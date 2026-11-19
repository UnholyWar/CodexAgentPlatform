using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed class ChatUpdates
{
    private readonly ConcurrentDictionary<string, (long Id, string Text)> answers = new();
    public event Action<string>? Changed;
    public event Action<string>? AnswerChanged;
    public void Publish(string id) => Notify(Changed, id);

    public void PublishAnswer(string chat, long id, string text)
    {
        answers[chat] = (id, text);
        Notify(AnswerChanged, chat);
    }

    public void ApplyAnswer(string chat, ChatSnapshot snapshot)
    {
        if (answers.TryGetValue(chat, out var answer) && snapshot.Messages.FindLast(m => m.Id == answer.Id) is { } message)
            message.Text = answer.Text;
    }

    public void ClearAnswer(string chat, long id)
    {
        if (answers.TryGetValue(chat, out var answer) && answer.Id == id)
            answers.TryRemove(new KeyValuePair<string, (long, string)>(chat, answer));
    }

    private static void Notify(Action<string>? subscribers, string id)
    {
        if (subscribers is null) return;
        foreach (Action<string> callback in subscribers.GetInvocationList())
            try { callback(id); } catch (ObjectDisposedException) { }
    }
}

public sealed record ChatSnapshot(List<Message> Messages, List<Activity> Events, ChatRun? Run, List<Approval> Approvals,
    bool AutoApprove = false, string? ProjectId = null, string? CoordinatorAgentId = null);
internal sealed record ProjectReference(string Id, string Name);

public sealed partial class ChatStore(IDbContextFactory<PlatformDb> dbs, ChatUpdates updates)
{
    public static readonly WorkStatus[] Active = [WorkStatus.Pending, WorkStatus.Running, WorkStatus.WaitingForApproval, WorkStatus.WaitingForUser];

    public static bool IsPauseRequest(string text) =>
        text.Trim().TrimEnd('.', '!').ToLower(CultureInfo.GetCultureInfo("tr-TR")) is
            "dur" or "şimdilik dur" or "duraklat" or "stop" or "pause" or "stop for now";

    public async Task RecordPauseAsync(string id, string text, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Messages.Add(new Message { ConversationId = id, Role = "user", Text = text });
        await db.SaveChangesAsync(ct);
        updates.Publish(id);
    }

    public async Task<string> SendAsync(string? chatId, string text, string? selectedAgent, string? projectId, CancellationToken ct,
        bool autoApproveNewChat = false, long? voiceMessageId = null) =>
        (await SubmitAsync(chatId, text, selectedAgent, projectId, ct, autoApproveNewChat, voiceMessageId)).ChatId;

    internal async Task<VoiceReply?> SubmitVoiceAsync(Message user, string? selectedAgent, CancellationToken ct) =>
        (await SubmitAsync(user.ConversationId, user.Text, selectedAgent, user.VoiceProjectId, ct, voiceMessageId: user.Id)).Reply;

    private async Task<(string ChatId, VoiceReply? Reply)> SubmitAsync(string? chatId, string text, string? selectedAgent, string? projectId, CancellationToken ct,
        bool autoApproveNewChat = false, long? voiceMessageId = null)
    {
        text = text.Trim();
        var inputProject = projectId ?? "";
        var inheritProject = projectId is null;
        if (string.IsNullOrWhiteSpace(projectId)) projectId = null;
        if (text.Length is 0 or > 16_000) throw new ArgumentException("Messages must contain between 1 and 16,000 characters.");
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var chat = chatId is null ? new Conversation { Title = TextLimits.Clip(text.Replace('\n', ' '), 65), AutoApprove = autoApproveNewChat }
            : await db.Conversations.SingleAsync(x => x.Id == chatId, ct);
        if (await db.Runs.AnyAsync(x => x.ConversationId == chat.Id && Enumerable.Contains(Active, x.Status), ct))
            throw new InvalidOperationException("A response is already being prepared in this chat.");
        var agents = await db.Agents.AsNoTracking().Where(x => !x.Archived)
            .Select(a => new Agent { Id = a.Id, Name = a.Name, Title = a.Title, AllowedProjects = a.AllowedProjects,
                AllowedTools = a.AllowedTools, Model = a.Model, Effort = a.Effort, Voice = a.Voice })
            .ToListAsync(ct);
        var settings = await db.Preferences.SingleAsync(ct);
        var isVoice = voiceMessageId.HasValue;
        var voiceEntry = isVoice ? PickTeamAgent(agents, selectedAgent)
            ?? throw new InvalidOperationException("Add or unarchive a team agent to start a voice conversation.") : null;
        var normal = PlatformChat.CreateAgent();
        var explicitAgent = Mention(agents, text) ?? voiceEntry;
        if (explicitAgent is null && !string.IsNullOrWhiteSpace(selectedAgent))
            explicitAgent = selectedAgent == PlatformChat.Id ? normal : agents.Find(a => a.Id == selectedAgent)
                ?? throw new InvalidOperationException("The selected agent is no longer available.");
        if (ProjectRouting.RequestsArchitect(text))
            explicitAgent = agents.Find(a => a.Id == "architect") ?? throw new InvalidOperationException("Architect is not available.");
        var accessRequest = IsAccessRequest(text);
        if (!accessRequest && ProjectRouting.GlobalResourceInventory(text))
        {
            var current = PickTeamAgent(agents, explicitAgent?.Id ?? chat.CoordinatorAgentId);
            if (current is not null)
                return await GlobalResourcesAsync(db, tx, chat, chatId, agents, current, text, isVoice ? inputProject : null, voiceMessageId, ct);
        }
        if (explicitAgent?.Id == "architect" && voiceMessageId is null) chat.CoordinatorAgentId = explicitAgent.Id;
        else if (explicitAgent?.Id == PlatformChat.Id && voiceMessageId is null) chat.CoordinatorAgentId = null;
        var last = chatId is null ? null : await db.Runs.AsNoTracking().Where(x => x.ConversationId == chat.Id)
            .OrderByDescending(x => x.AssistantMessageId).Select(x => new { x.AgentId, x.ProjectId, x.CreatedAtUtc }).FirstOrDefaultAsync(ct);
        var currentProject = chatId is null ? null :
            await ProjectRouting.FocusedProjectAsync(db, chatId, last?.ProjectId, last?.CreatedAtUtc, ct);
        // A project named in an access request is the proposed grant target, not a workspace switch.
        var intent = accessRequest ? default : ProjectRouting.Intent(text);
        var automatic = explicitAgent is null || explicitAgent.Id is PlatformChat.Id or "architect" ||
            voiceEntry is not null && (intent.Create && !ToolAccess.Has(voiceEntry, ToolAccess.CreateProjects) ||
                intent.Implement && !ToolAccess.Has(voiceEntry, "workspace"));
        if (intent.Create)
            projectId = null;
        else if (projectId is null && inheritProject && accessRequest)
            projectId = currentProject;
        else if (projectId is null && inheritProject)
        {
            var mentions = await FindNamedProjectsAsync(db, text, spoken: false, ct);
            if (mentions.Length == 1) projectId = mentions[0].Id;
            else if (mentions.Length > 1)
                return await SaveExchangeAsync(db, tx, chat, chatId, explicitAgent ?? normal, text,
                    $"This request includes multiple projects. Which should we start with, {mentions[0].Name} or {mentions[1].Name}?", voiceMessageId, ct);
            else projectId = currentProject;
        }
        var previousAgent = last is not null && last.ProjectId == projectId
            ? agents.Find(a => a.Id == last.AgentId && ToolAccess.AllowsProject(a, projectId)) : null;
        var coordinator = chat.CoordinatorAgentId == PlatformChat.Id ? normal :
            agents.Find(a => a.Id == chat.CoordinatorAgentId && ToolAccess.AllowsProject(a, projectId));
        var agent = explicitAgent ?? coordinator ?? previousAgent;
        (string Kind, string Permission)? requiredGrant = null;
        var handoff = false;
        if (automatic && intent.Create && (explicitAgent?.Id == "architect" ? explicitAgent : ProjectRouting.Creator(agents)) is { } creator)
        {
            agent = creator;
            if (creator.Id == "architect" && (voiceMessageId is null || chat.CoordinatorAgentId is null))
                chat.CoordinatorAgentId = creator.Id;
            handoff = true;
            if (!ToolAccess.Has(creator, ToolAccess.CreateProjects))
                requiredGrant = ("tool", ToolAccess.CreateProjects);
        }
        else if (automatic && intent.Implement && projectId is { } targetProject && ProjectRouting.Developer(agents, targetProject) is { } developer)
        {
            agent = developer;
            handoff = previousAgent?.Id != developer.Id;
            if (!ToolAccess.AllowsProject(developer, targetProject))
                requiredGrant = ("project", targetProject);
        }
        else if (explicitAgent?.Id == "architect" && projectId is not null && !ToolAccess.AllowsProject(explicitAgent, projectId) &&
            ProjectRouting.RequestsArchitect(text))
            requiredGrant = ("project", projectId);
        if (voiceEntry is not null && agent is not null && requiredGrant is null && !ToolAccess.AllowsProject(agent, projectId))
        {
            var eligible = agents.Where(a => ToolAccess.AllowsProject(a, projectId))
                .OrderByDescending(a => ToolAccess.Has(a, ToolAccess.ProjectResources))
                .ThenByDescending(a => a.Id == "architect").ThenBy(a => a.Name).FirstOrDefault();
            if (eligible is null)
            {
                var answerText = ProjectRouting.IsTurkish(text) ? "Seçili projeye erişim izni olan bir ekip ajanı yok."
                    : "No team agent has permission to access the selected project.";
                return await SaveExchangeAsync(db, tx, chat, chatId, voiceEntry, text, answerText, voiceMessageId, ct, inputProject);
            }
            agent = eligible;
            handoff = true;
        }
        agent ??= normal;
        if (voiceEntry is not null && requiredGrant is null)
            chat.CoordinatorAgentId = agent.Id;
        if ((agent.Id != PlatformChat.Id && requiredGrant is null && !ToolAccess.AllowsProject(agent, projectId)) ||
            (projectId is not null && !await db.Projects.AnyAsync(x => x.Id == projectId, ct)))
            throw new UnauthorizedAccessException("The agent does not have access to the selected project.");
        WorkItem? candidate = null;
        if (agent.Id != PlatformChat.Id && !accessRequest && !intent.Create && requiredGrant is null)
        {
            var range = TemporalQuery.Parse(text, DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone));
            var previous = await FindWorkAsync(db, chat.Id, text, projectId, agent, range, !inheritProject, ct);
            if (previous.Count > 1)
            {
                var choices = string.Join("\n", previous.Select(x => $"- {x.Title}"));
                var reply = $"I found multiple work items in this period. Which would you like to open?\n\n{choices}";
                return await SaveExchangeAsync(db, tx, chat, chatId, agent, text, reply, voiceMessageId, ct);
            }
            candidate = previous.SingleOrDefault();
            projectId ??= candidate?.ProjectId;
        }
        if (chatId is null) db.Conversations.Add(chat);
        chat.UpdatedAtUtc = DateTime.UtcNow;
        var user = voiceMessageId is { } messageId
            ? await db.Messages.SingleAsync(m => m.ConversationId == chat.Id && m.Id == messageId &&
                m.Role == "user" && m.VoiceProjectId == inputProject && m.Text == text, ct)
            : new Message { ConversationId = chat.Id, Role = "user", Text = text };
        var answer = new Message { ConversationId = chat.Id, AgentId = agent.Id, Role = "assistant" };
        if (voiceMessageId is null) db.Messages.Add(user);
        db.Messages.Add(answer);
        await db.SaveChangesAsync(ct);
        var run = new ChatRun
        {
            ConversationId = chat.Id, AgentId = agent.Id, UserMessageId = user.Id, AssistantMessageId = answer.Id,
            Model = agent.Model ?? settings.Model, Effort = agent.Effort ?? (agent.Model is null ? settings.Effort : null),
            Status = requiredGrant is null ? WorkStatus.Pending : WorkStatus.WaitingForApproval,
            ProjectId = projectId, WorkId = candidate?.Id, Workspace = candidate?.Workspace ??
                await RecentWorkspaceAsync(db, chat.Id, agent.Id, projectId, ct) ?? Guid.NewGuid().ToString("N")
        };
        db.Runs.Add(run);
        if (handoff && requiredGrant is null)
            db.Events.Add(new Activity { ConversationId = chat.Id, AgentId = agent.Id, ProjectId = projectId,
                Kind = "AgentHandoff", Detail = $"{agent.Name} took over." });
        if (projectId is not null && candidate is null)
            db.Events.Add(new Activity { ConversationId = chat.Id, AgentId = agent.Id, ProjectId = projectId,
                Kind = "ProjectSelected", Detail = projectId });
        else if (projectId is null && !inheritProject && !intent.Create && voiceMessageId is null)
            db.Events.Add(new Activity { ConversationId = chat.Id, AgentId = agent.Id, Kind = "ProjectCleared", Detail = "No project selected." });
        await db.SaveChangesAsync(ct);
        if (requiredGrant is { } grant)
            await AgentPermissionGrants.ForQueuedRunAsync(db, run, grant.Kind, grant.Permission, ct);
        await tx.CommitAsync(ct);
        updates.Publish(chat.Id);
        return (chat.Id, null);
    }

    public async Task<List<Conversation>> RecentAsync(string? query, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        IQueryable<Conversation> chats = db.Conversations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var expression = MemoryStore.SearchExpression(query);
            if (expression is null) return [];
            chats = db.Conversations.FromSqlInterpolated($"""
                SELECT c.* FROM Conversations c
                WHERE c.Title LIKE {'%' + query + '%'}
                   OR EXISTS (SELECT 1 FROM MessageSearch JOIN Messages m ON m.Id = MessageSearch.rowid
                              WHERE MessageSearch MATCH {expression} AND m.ConversationId = c.Id)
                """).AsNoTracking();
        }
        return await chats.OrderByDescending(x => x.UpdatedAtUtc).Take(70).ToListAsync(ct);
    }

    internal async Task<Message> SaveVoiceUserAsync(string? conversationId, string? projectId, string text,
        bool autoApprove, string? initialAgent, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (projectId is not null && !await db.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new InvalidOperationException("The project selected for this call no longer exists.");
        var chat = conversationId is null
            ? new Conversation { Title = TextLimits.Clip(text.Replace('\n', ' '), 65),
                AutoApprove = autoApprove }
            : await db.Conversations.SingleAsync(c => c.Id == conversationId, ct);
        if (conversationId is null) db.Conversations.Add(chat);
        if (initialAgent is not null) chat.CoordinatorAgentId = initialAgent;
        chat.UpdatedAtUtc = DateTime.UtcNow;
        var message = new Message { ConversationId = chat.Id, Role = "user", Text = text, VoiceProjectId = projectId ?? "" };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        updates.Publish(chat.Id);
        return message;
    }

    internal async Task<ProjectReference[]> FindSpokenProjectsAsync(string text, CancellationToken ct)
    {
        if (IsAccessRequest(text)) return [];
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await FindNamedProjectsAsync(db, text, spoken: true, ct);
    }

    internal async Task<Message> ScopeVoiceUserAsync(Message user, string projectId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.Messages.Where(m => m.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.VoiceProjectId, projectId), ct);
        user.VoiceProjectId = projectId;
        updates.Publish(user.ConversationId);
        return user;
    }

    private static async Task<ProjectReference[]> FindNamedProjectsAsync(PlatformDb db, string text, bool spoken, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().Select(p => new ProjectReference(p.Id, p.Name)).ToListAsync(ct);
        // Transcription can insert spaces inside names such as AtlasPortal.
        var speech = spoken ? new string(text.Where(char.IsLetterOrDigit).ToArray()) : null;
        return projects.Where(p =>
        {
            if (p.Name.Length >= 3 && text.Contains(p.Name, StringComparison.OrdinalIgnoreCase)) return true;
            if (speech is null) return false;
            var name = new string(p.Name.Where(char.IsLetterOrDigit).ToArray());
            return name.Length >= 5 && speech.Contains(name, StringComparison.OrdinalIgnoreCase);
        }).Take(2).ToArray();
    }

    internal async Task<VoiceSpeaker?> VoiceSpeakerAsync(string agentId, CancellationToken ct)
    {
        if (agentId == PlatformChat.Id) return null;
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Agents.AsNoTracking().Where(a => a.Id == agentId)
            .Select(a => new VoiceSpeaker(a.Id, a.Name, a.Voice)).SingleOrDefaultAsync(ct);
    }

    internal async Task<VoiceSpeaker> VoiceEntryAsync(string? requestedAgent, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agents = await db.Agents.AsNoTracking().Where(a => !a.Archived).ToListAsync(ct);
        var agent = PickTeamAgent(agents, requestedAgent)
            ?? throw new InvalidOperationException("Add or unarchive a team agent to start a voice conversation.");
        return new(agent.Id, agent.Name, agent.Voice);
    }

    private static Agent? PickTeamAgent(IEnumerable<Agent> agents, string? requestedAgent) =>
        agents.Where(a => !a.Archived && a.Id != PlatformChat.Id)
            .OrderByDescending(a => a.Id == requestedAgent)
            .ThenByDescending(a => a.Id == "architect").ThenBy(a => a.Name).FirstOrDefault();

    internal async Task<(VoiceSpeaker? Changed, VoiceReply? Reply)> VoiceControlAsync(
        Message user, VoiceSpeaker current, CancellationToken ct)
    {
        var command = ProjectRouting.VoiceControl(user.Text);
        if (!command.Identity && !command.TaskStatus && command.Target is null) return default;
        await using var db = await dbs.CreateDbContextAsync(ct);
        VoiceSpeaker? changed = null;
        string answer;
        if (command.Target is { } target)
        {
            var agents = await db.Agents.AsNoTracking()
                .Select(a => new Agent { Id = a.Id, Name = a.Name, Title = a.Title, Voice = a.Voice, Archived = a.Archived })
                .ToListAsync(ct);
            agents.Add(PlatformChat.CreateAgent());
            var agent = ProjectRouting.VoiceTarget(agents, target);
            if (agent is null) return default;
            if (agent.Id == PlatformChat.Id) agent = PickTeamAgent(agents, null);
            if (agent is null) return default;
            if (agent.Archived)
                answer = command.Turkish ? $"{agent.Name} şu anda görüşmeye açık değil." : $"{agent.Name} is currently unavailable.";
            else
            {
                changed = new(agent.Id, agent.Name, agent.Voice);
                answer = command.Turkish ? $"Merhaba, ben {agent.Name}. Nasıl yardımcı olabilirim?"
                    : $"Hi, I'm {agent.Name}. How can I help?";
            }
        }
        else if (command.TaskStatus)
            answer = await VoiceTaskStatusAsync(db, user, command.Turkish, ct);
        else
            answer = command.Turkish ? $"Şu anda {current.Name} ile görüşüyorsun." : $"You're speaking with {current.Name}.";

        var chat = await db.Conversations.SingleAsync(c => c.Id == user.ConversationId, ct);
        if (changed is not null) chat.CoordinatorAgentId = changed.Id;
        var reply = new VoiceReply(changed ?? current, answer);
        AddVoiceReply(db, user, reply);
        await db.SaveChangesAsync(ct);
        updates.Publish(user.ConversationId);
        return (changed, reply);
    }

    private static async Task<string> VoiceTaskStatusAsync(PlatformDb db, Message user, bool turkish, CancellationToken ct)
    {
        var task = await db.Runs.AsNoTracking().Where(r => r.ConversationId == user.ConversationId &&
                (r.WorkId != null || r.OriginMessageId != null || Enumerable.Contains(Active, r.Status)))
            .OrderByDescending(r => r.AssistantMessageId)
            .Select(r => new { r.AgentId, r.Status,
                PendingApproval = db.Approvals.Any(a => a.ConversationId == r.ConversationId && a.AgentId == r.AgentId &&
                    a.Status == "Pending" && a.ExpiresAtUtc > DateTime.UtcNow && a.CreatedAtUtc >= r.CreatedAtUtc),
                Name = db.Agents.Where(a => a.Id == r.AgentId).Select(a => a.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (task is null) return turkish ? "Bu görüşmede takip edilen bir görev yok." : "There is no tracked task in this conversation.";
        var name = task.Name ?? task.AgentId;
        return (task.PendingApproval ? WorkStatus.WaitingForApproval : task.Status, turkish) switch
        {
            (WorkStatus.Pending, true) => $"{name} görevi devraldı. Görev sırada.",
            (WorkStatus.Running, true) => $"{name} görev üzerinde çalışıyor.",
            (WorkStatus.WaitingForApproval, true) => $"{name} devam edebilmek için sohbet ekranında onayını bekliyor.",
            (WorkStatus.WaitingForUser, true) => $"{name} devam etmek için yanıtını bekliyor.",
            (WorkStatus.Completed, true) => $"{name} görevle ilgili yanıtını sohbet ekranına yazdı.",
            (_, true) => $"{name} şu anda bu görev üzerinde çalışmıyor. Ayrıntılar sohbet ekranında.",
            (WorkStatus.Pending, false) => $"{name} has the task. It is queued.",
            (WorkStatus.Running, false) => $"{name} is working on the task.",
            (WorkStatus.WaitingForApproval, false) => $"{name} is waiting for your approval in chat.",
            (WorkStatus.WaitingForUser, false) => $"{name} is waiting for your reply.",
            (WorkStatus.Completed, false) => $"{name} posted the result in chat.",
            _ => $"{name} is no longer working on the task. See the chat for details."
        };
    }

    internal async Task RecordVoiceReplyAsync(Message user, VoiceReply reply, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        AddVoiceReply(db, user, reply);
        await db.SaveChangesAsync(ct);
        updates.Publish(user.ConversationId);
    }

    private static void AddVoiceReply(PlatformDb db, Message user, VoiceReply reply) =>
        db.Messages.Add(new Message { ConversationId = user.ConversationId, Role = "assistant", AgentId = reply.Speaker.Id,
            Text = reply.Text, VoiceProjectId = reply.Speaker.Id == PlatformChat.Id ? null : user.VoiceProjectId });

    internal async Task<(WorkStatus Status, string Text, bool PendingApproval, string? ProjectId, string AgentId, string? ConversationAgentId)?> VoiceWorkAsync(
        string conversationId, long messageId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var result = await db.Runs.AsNoTracking().Where(r => r.ConversationId == conversationId &&
                (r.UserMessageId == messageId || r.OriginMessageId == messageId))
            .OrderByDescending(r => r.AssistantMessageId)
            .Select(r => new { r.Status, r.Error, r.ProjectId, r.CreatedAtUtc, r.AgentId, Text = db.Messages.Where(m => m.Id == r.AssistantMessageId).Select(m => m.Text).FirstOrDefault(),
                PendingApproval = db.Approvals.Any(a => a.ConversationId == conversationId && a.AgentId == r.AgentId &&
                    a.Status == "Pending" && a.ExpiresAtUtc > DateTime.UtcNow && a.CreatedAtUtc >= r.CreatedAtUtc) })
            .FirstOrDefaultAsync(ct);
        if (result is null) return null;
        var projectId = await ProjectRouting.CreatedProjectAsync(db, conversationId, result.CreatedAtUtc, ct) ?? result.ProjectId;
        var conversationAgent = await db.Conversations.Where(c => c.Id == conversationId)
            .Select(c => c.CoordinatorAgentId).SingleOrDefaultAsync(ct);
        return (result.Status, result.Status == WorkStatus.Completed ? result.Text ?? "" : result.Error ?? "",
            result.PendingApproval, projectId, result.AgentId, conversationAgent);
    }

    public async Task<ChatSnapshot> ReadAsync(string id, int messageLimit, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == id).OrderByDescending(x => x.Id)
            .Take(messageLimit).ToListAsync(ct);
        messages.Reverse();
        var events = await db.Events.AsNoTracking().Where(x => x.ConversationId == id)
            .OrderByDescending(x => x.Id).Take(45).ToListAsync(ct);
        var run = await db.Runs.AsNoTracking().Where(x => x.ConversationId == id).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var conversationApprovals = db.Approvals.AsNoTracking().Where(x => x.ConversationId == id);
        var recentIds = conversationApprovals.OrderByDescending(x => x.CreatedAtUtc).Take(12).Select(x => x.Id);
        var now = DateTime.UtcNow;
        var approvals = await conversationApprovals.Where(x => recentIds.Contains(x.Id) ||
                (x.Kind == AgentPermissionGrants.ApprovalKind && x.Status == "Pending" && x.ExpiresAtUtc > now))
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
        var chat = await db.Conversations.AsNoTracking().Where(c => c.Id == id)
            .Select(c => new { c.AutoApprove, c.CoordinatorAgentId }).FirstOrDefaultAsync(ct);
        var projectId = await ProjectRouting.FocusedProjectAsync(db, id, run?.ProjectId, run?.CreatedAtUtc, ct);
        return new(messages, events, run, approvals, chat?.AutoApprove == true, projectId, chat?.CoordinatorAgentId);
    }

    public async Task TouchAsync(string id, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.Conversations.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastAccessedAtUtc, DateTime.UtcNow), ct);
    }

    private static Agent? Mention(List<Agent> agents, string text) =>
        agents.OrderByDescending(a => a.Name.Length).FirstOrDefault(a =>
            new[] { a.Name, a.Name.Split(' ')[0] }.Any(name =>
                text.StartsWith(name + ",", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("@" + name, StringComparison.OrdinalIgnoreCase) &&
                (text.Length == name.Length + 1 || !char.IsLetterOrDigit(text[name.Length + 1]))));

    [GeneratedRegex(@"\b(?:permissions?|access|grant|yetki\p{L}*|[iİ]z[iİ]n\p{L}*|[iİ]zn\p{L}*|erişim\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrantReference();

    [GeneratedRegex(@"\b(?:give|grant|add|allow|enable|ver|verir|verebilir|ekle\p{L}*|tanı\p{L}*|yetkilendir\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrantVerb();

    private static bool IsAccessRequest(string text) => GrantReference().IsMatch(text) && GrantVerb().IsMatch(text);

    private static async Task<List<WorkItem>> FindWorkAsync(PlatformDb db, string conversation, string input,
        string? project, Agent agent, TimeRange? range, bool projectSelected, CancellationToken ct)
    {
        var allowedProjects = ToolAccess.Split(agent.AllowedProjects);
        var work = db.Work.AsNoTracking().Where(x => x.Status != WorkStatus.Cancelled && x.AgentId == agent.Id &&
            (x.ProjectId == null || Enumerable.Contains(allowedProjects, x.ProjectId)));
        if (projectSelected || project is not null) work = work.Where(x => x.ProjectId == project);
        if (range is not null)
            work = work.Where(x => db.Events.Any(e => e.WorkId == x.Id &&
                e.EventOccurredAtUtc >= range.FromUtc && e.EventOccurredAtUtc < range.UntilUtc));
        else
        {
            var local = await work.Where(x => x.ConversationId == conversation).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);
            if (local is not null && (local.Status != WorkStatus.Completed || RequestsContinuation(input) ||
                (local.Title.Length > 0 && input.Contains(local.Title, StringComparison.OrdinalIgnoreCase)))) return [local];
            work = work.Where(x => x.Title.Length >= 4 && input.Contains(x.Title));
        }
        var matches = await work.OrderByDescending(x => x.UpdatedAtUtc).Take(8).ToListAsync(ct);
        var title = matches.Where(x => input.Contains(x.Title, StringComparison.OrdinalIgnoreCase)).ToList();
        return title.Count == 1 ? title : matches;
    }

    internal static async Task<string?> RecentWorkspaceAsync(PlatformDb db, string conversation, string agent, string? project, CancellationToken ct)
    {
        var recent = await db.Runs.Where(x => x.ConversationId == conversation && x.AgentId == agent &&
                x.ProjectId == project && x.Workspace != null)
            .OrderByDescending(x => x.UserMessageId).Select(x => x.Workspace).FirstOrDefaultAsync(ct);
        if (recent is not null || project is null) return recent;
        return await db.Work.Where(x => x.ConversationId == conversation && x.AgentId == agent && x.ProjectId == project)
            .OrderByDescending(x => x.UpdatedAtUtc).Select(x => x.Workspace).FirstOrDefaultAsync(ct);
    }

    private static bool RequestsContinuation(string input) =>
        input.Trim().TrimEnd('.', '!').ToLowerInvariant() is
            "devam" or "devam et" or "kaldığımız yerden devam et" or "continue" or "resume";

    private async Task<(string ChatId, VoiceReply? Reply)> GlobalResourcesAsync(PlatformDb db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Conversation chat, string? chatId, List<Agent> agents, Agent current, string input, string? inputProject, long? voiceMessageId, CancellationToken ct)
    {
        var agent = PickTeamAgent(agents.Where(a => ToolAccess.Has(a, ToolAccess.GlobalResources)), current.Id);
        var turkish = ProjectRouting.IsTurkish(input);
        if (agent is null)
            return await SaveExchangeAsync(db, tx, chat, chatId, current, input, turkish
                ? "Global kaynakları okumak için bir ekip ajanına Global resources izni verilmesi gerekiyor."
                : "A team agent needs the Global resources permission to view this inventory.", voiceMessageId, ct, inputProject);

        if (agent.Id != current.Id)
            db.Events.Add(new Activity { ConversationId = chat.Id, AgentId = agent.Id,
                Kind = "AgentHandoff", Detail = $"{agent.Name} took over the conversation to check global resources." });
        chat.CoordinatorAgentId = agent.Id;
        var names = await db.Resources.AsNoTracking().Where(r => r.IsGlobal && r.ProjectId == null)
            .OrderBy(r => r.Name).Select(r => r.Name).Take(21).ToListAsync(ct);
        var answer = names.Count switch
        {
            0 => turkish ? "Kayıtlı global kaynak yok." : "There are no registered global resources.",
            > 20 => turkish ? "Kayıtlı global kaynaklardan ilk 20 tanesi." : "The first 20 registered global resources.",
            _ => turkish ? "Kayıtlı global kaynaklar." : "Registered global resources."
        };
        if (names.Count > 0) answer += " " + string.Join(", ", names.Take(20)) + ".";
        return await SaveExchangeAsync(db, tx, chat, chatId, agent, input, answer, voiceMessageId, ct, inputProject);
    }

    private async Task<(string ChatId, VoiceReply? Reply)> SaveExchangeAsync(PlatformDb db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Conversation chat, string? chatId, Agent agent, string input, string answer, long? voiceMessageId, CancellationToken ct,
        string? voiceProjectId = null)
    {
        if (chatId is null) db.Conversations.Add(chat);
        chat.UpdatedAtUtc = DateTime.UtcNow;
        if (voiceMessageId is null) db.Messages.Add(new Message { ConversationId = chat.Id, Role = "user", Text = input });
        db.Messages.Add(new Message { ConversationId = chat.Id, Role = "assistant", AgentId = agent.Id, Text = answer, VoiceProjectId = voiceProjectId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        updates.Publish(chat.Id);
        return (chat.Id, new(new(agent.Id, agent.Name, agent.Voice), answer));
    }
}
