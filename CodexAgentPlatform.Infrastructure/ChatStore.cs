using System.Globalization;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed class ChatUpdates
{
    public event Action<string>? Changed;
    public void Publish(string id)
    {
        if (Changed is not { } subscribers) return;
        foreach (Action<string> callback in subscribers.GetInvocationList())
            try { callback(id); } catch (ObjectDisposedException) { }
    }
}

public sealed record ChatSnapshot(List<Message> Messages, List<Activity> Events, ChatRun? Run, List<Approval> Approvals);

public sealed class ChatStore(IDbContextFactory<PlatformDb> dbs, ChatUpdates updates)
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

    public async Task<string> SendAsync(string? chatId, string text, string? selectedAgent, string? projectId, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length is 0 or > 16_000) throw new ArgumentException("Mesaj 1–16.000 karakter olmalı.");
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var chat = chatId is null ? new Conversation { Title = TextLimits.Clip(text.Replace('\n', ' '), 65) }
            : await db.Conversations.SingleAsync(x => x.Id == chatId, ct);
        if (await db.Runs.AnyAsync(x => x.ConversationId == chat.Id && Enumerable.Contains(Active, x.Status), ct))
            throw new InvalidOperationException("Bu sohbette bir yanıt hazırlanıyor.");
        var agents = await db.Agents.Where(x => !x.Archived).ToListAsync(ct);
        if (agents.Count == 0) throw new InvalidOperationException("Etkin ajan bulunamadı.");
        var settings = await db.Preferences.SingleAsync(ct);
        var explicitAgent = Mention(agents, text) ?? agents.Find(a => a.Id == selectedAgent);
        if (projectId is null)
        {
            var projects = await db.Projects.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            var mentions = projects.Where(x => x.Name.Length >= 3 && text.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (mentions.Length == 1) projectId = mentions[0].Id;
            else if (mentions.Length > 1)
                return await SaveExchangeAsync(db, tx, chat, chatId, explicitAgent ?? DefaultAgent(agents, text), text,
                    $"Bu istek birden fazla projeyi içeriyor. Önce {mentions[0].Name} veya {mentions[1].Name} adını seçebilir misiniz?", ct);
        }
        var range = TemporalQuery.Parse(text, DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone));
        var previous = await FindWorkAsync(db, chat.Id, text, projectId, explicitAgent?.Id, range, ct);
        var candidate = previous.Count == 1 ? previous[0] : null;
        var agent = explicitAgent ?? agents.Find(a => a.Id == candidate?.AgentId) ?? DefaultAgent(agents, text);
        projectId ??= candidate?.ProjectId;
        if (!ToolAccess.AllowsProject(agent, projectId) || (projectId is not null && !await db.Projects.AnyAsync(x => x.Id == projectId, ct)))
            throw new UnauthorizedAccessException("Ajanın seçilen projeye erişimi yok.");
        if (previous.Count > 1)
        {
            var choices = string.Join("\n", previous.Select(x => $"- {x.Title}"));
            var reply = $"Bu zaman aralığında birden fazla çalışma buldum. Hangisini açalım?\n\n{choices}";
            return await SaveExchangeAsync(db, tx, chat, chatId, agent, text, reply, ct);
        }
        if (chatId is null) db.Conversations.Add(chat);
        chat.UpdatedAtUtc = DateTime.UtcNow;
        var user = new Message { ConversationId = chat.Id, Role = "user", Text = text };
        var answer = new Message { ConversationId = chat.Id, AgentId = agent.Id, Role = "assistant" };
        db.Messages.AddRange(user, answer);
        await db.SaveChangesAsync(ct);
        db.Runs.Add(new ChatRun
        {
            ConversationId = chat.Id, AgentId = agent.Id, UserMessageId = user.Id, AssistantMessageId = answer.Id,
            Model = agent.Model ?? settings.Model, Effort = agent.Effort ?? (agent.Model is null ? settings.Effort : null),
            ProjectId = projectId, WorkId = candidate?.Id, Workspace = candidate?.Workspace ??
                await RecentWorkspaceAsync(db, chat.Id, agent.Id, projectId, ct) ?? Guid.NewGuid().ToString("N")
        });
        if (projectId is not null && candidate is null)
            db.Events.Add(new Activity { ConversationId = chat.Id, AgentId = agent.Id, ProjectId = projectId,
                Kind = "ProjectSelected", Detail = projectId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        updates.Publish(chat.Id);
        return chat.Id;
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

    public async Task<ChatSnapshot> ReadAsync(string id, int messageLimit, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => x.ConversationId == id).OrderByDescending(x => x.Id)
            .Take(messageLimit).ToListAsync(ct);
        messages.Reverse();
        var events = await db.Events.AsNoTracking().Where(x => x.ConversationId == id)
            .OrderByDescending(x => x.Id).Take(45).ToListAsync(ct);
        var run = await db.Runs.AsNoTracking().Where(x => x.ConversationId == id).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var approvals = await db.Approvals.AsNoTracking().Where(x => x.ConversationId == id)
            .OrderByDescending(x => x.CreatedAtUtc).Take(12).ToListAsync(ct);
        return new(messages, events, run, approvals);
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

    private static Agent DefaultAgent(List<Agent> agents, string text)
    {
        var lower = text.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var id = lower.Contains("ssh") || lower.Contains("deploy") || lower.Contains("sunucu") ? "devops"
            : lower.Contains("regresyon") || lower.Contains("qa ") ? "qa"
            : lower.Contains("mimari") || lower.Contains("architecture") ? "architect" : "developer";
        return agents.Find(x => x.Id == id) ?? agents[0];
    }

    private static async Task<List<WorkItem>> FindWorkAsync(PlatformDb db, string conversation, string input,
        string? project, string? agent, TimeRange? range, CancellationToken ct)
    {
        var work = db.Work.AsNoTracking().Where(x => x.Status != WorkStatus.Cancelled);
        if (project is not null) work = work.Where(x => x.ProjectId == project);
        if (agent is not null) work = work.Where(x => x.AgentId == agent);
        if (range is not null)
            work = work.Where(x => db.Events.Any(e => e.WorkId == x.Id &&
                e.EventOccurredAtUtc >= range.FromUtc && e.EventOccurredAtUtc < range.UntilUtc));
        else
        {
            var local = await work.Where(x => x.ConversationId == conversation).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync(ct);
            if (local is not null) return [local];
            work = work.Where(x => x.Title.Length >= 4 && input.Contains(x.Title));
        }
        var matches = await work.OrderByDescending(x => x.UpdatedAtUtc).Take(8).ToListAsync(ct);
        var title = matches.Where(x => input.Contains(x.Title, StringComparison.OrdinalIgnoreCase)).ToList();
        return title.Count == 1 ? title : matches;
    }

    private static async Task<string?> RecentWorkspaceAsync(PlatformDb db, string conversation, string agent, string? project, CancellationToken ct)
    {
        if (project is not null)
            return await db.Work.Where(x => x.ConversationId == conversation && x.AgentId == agent && x.ProjectId == project)
                .OrderByDescending(x => x.UpdatedAtUtc).Select(x => x.Workspace).FirstOrDefaultAsync(ct);
        return await db.Runs.Where(x => x.ConversationId == conversation && x.AgentId == agent && x.ProjectId == null)
            .OrderByDescending(x => x.CreatedAtUtc).Select(x => x.Workspace).FirstOrDefaultAsync(ct);
    }

    private async Task<string> SaveExchangeAsync(PlatformDb db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        Conversation chat, string? chatId, Agent agent, string input, string answer, CancellationToken ct)
    {
        if (chatId is null) db.Conversations.Add(chat);
        chat.UpdatedAtUtc = DateTime.UtcNow;
        db.Messages.AddRange(new Message { ConversationId = chat.Id, Role = "user", Text = input },
            new Message { ConversationId = chat.Id, Role = "assistant", AgentId = agent.Id, Text = answer });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        updates.Publish(chat.Id);
        return chat.Id;
    }
}
