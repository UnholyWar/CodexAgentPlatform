using System.Text.Json;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed record Delegation(string AgentId, string Brief);

public sealed class AgentTools(IDbContextFactory<PlatformDb> dbs, MemoryStore memory, Operations operations, PlatformPaths paths)
{
    private sealed record Tool(string Name, string Permission, string Description, string[] Required, string[] Optional);
    private static readonly Tool[] Registry =
    [
        new("memory_search", "memory", "Find Markdown notes by keyword, project and date.", [], ["query", "projectId", "when"]),
        new("memory_list", "memory", "List recent note summaries.", [], ["projectId"]),
        new("memory_read", "memory", "Read a selected Markdown note or heading.", ["id"], ["section"]),
        new("memory_create", "memory", "Create a concise Markdown knowledge note.", ["title", "summary", "markdown"], ["tags", "verification", "sources"]),
        new("memory_update", "memory", "Revise one of your existing knowledge notes.", ["id", "title", "summary", "markdown"], ["tags", "verification", "sources"]),
        new("memory_archive", "memory", "Archive one of your old notes.", ["id"], []),
        new("memory_consolidate", "memory", "Replace one note with a consolidated note and archive the duplicate.", ["id", "mergeId", "title", "summary", "markdown"], ["tags", "verification", "sources"]),
        new("memory_timeline", "memory", "Find time-stamped work events. Use a date phrase in Turkish/English or from/until dates (ISO YYYY-MM-DD, end exclusive).", [], ["projectId", "when", "from", "until"]),
        new("conversation_search", "memory", "Find past messages by keyword. Set scope to all to search your older authorized conversations. Filter dates with when, or from and until.", ["query"], ["scope", "when", "from", "until"]),
        new("task_begin", "", "Open or resume a durable work item. Set mode to new for a separate request, or resume for prior work.", ["title", "description"], ["mode"]),
        new("task_get_current", "", "Read structured context about the current work.", [], []),
        new("task_progress", "", "Record one verified step and the next step. Set event to CodeModified, TestExecuted or ResearchCompleted when appropriate.", ["step"], ["next", "event", "branch", "commit"]),
        new("task_finish", "", "Set work status to completed, paused, waiting, failed, or cancelled.", ["status", "step"], ["next"]),
        new("task_delegate", "delegate", "Ask another agent for one follow-up after this turn. List agents using agent_list.", ["agentId", "brief"], []),
        new("agent_list", "delegate", "List agents eligible for a follow-up on this project.", [], []),
        new("resource_search", "resources", "Find approved project resources by name.", [], ["query"]),
        new("resource_get", "resources", "Read metadata of an authorized resource.", ["id"], []),
        new("git_workspace", "git", "Prepare or operate on the registered repository. Operations: prepare, status, diff, fetch, pull, branch, commit. The resulting worktree is project/.", ["id", "operation"], ["argument"]),
        new("git_request_push", "git", "Request explicit owner approval for a non-force push of the current commit.", ["id"], ["destinationBranch"]),
        new("ssh_inspect", "ssh", "Run one fixed SSH inspection: test, health, docker-status, docker-services, linux-services, docker-logs, sftp-read (configured file).", ["id", "operation"], []),
        new("ssh_request_operation", "ssh", "Request explicit approval for restart-docker on an SSH server, or deploy/rollback on a registered Swarm target. Deploy requires an image digest.", ["id", "operation"], ["image"])
    ];

    public static IEnumerable<object> Describe(Agent agent) => Registry
        .Where(tool => tool.Permission.Length == 0 || ToolAccess.Has(agent, tool.Permission))
        .Select(tool => new
        {
            type = "function", name = tool.Name, description = tool.Description, deferLoading = false,
            inputSchema = new
            {
                type = "object",
                properties = tool.Required.Concat(tool.Optional).ToDictionary(x => x, _ => new { type = "string" }),
                required = tool.Required,
                additionalProperties = false
            }
        });

    public async Task<object?> RunAsync(string name, JsonElement args, ChatRun run, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == run.AgentId, ct);
        var tool = Registry.FirstOrDefault(x => x.Name == name);
        if (agent.Archived || tool is null || (tool.Permission.Length > 0 && !ToolAccess.Has(agent, tool.Permission))
            || !ToolAccess.AllowsProject(agent, run.ProjectId))
            throw new UnauthorizedAccessException("Araç yetkisi yok.");
        if (args.ValueKind != JsonValueKind.Object || tool.Required.Any(x => string.IsNullOrWhiteSpace(args.String(x))))
            throw new ArgumentException("Araç için gerekli argümanlar eksik.");

        var id = args.String("id");
        var project = args.String("projectId") ?? run.ProjectId;
        if (!ToolAccess.AllowsProject(agent, project)) throw new UnauthorizedAccessException("Proje yetkisi yok.");
        switch (name)
        {
            case "memory_search":
            case "memory_list":
                var notes = await memory.SearchAsync(args.String("query"), project, await RangeAsync(db, args, ct), false, ct);
                return notes.Where(n => CanRead(agent, n)).Take(10).Select(n =>
                    new { n.Id, n.Title, n.Summary, n.Tags, n.ProjectId, n.UpdatedAtUtc, n.Verification }).ToArray();
            case "memory_read":
                await RequireNoteAsync(db, agent, id!, ct);
                return new { markdown = TextLimits.Clip(await memory.ReadAsync(id!, null, args.String("section"), ct), 6500) };
            case "memory_create":
            case "memory_update":
            case "memory_consolidate":
                if (id is not null) await RequireNoteAsync(db, agent, id, ct);
                var draft = new MemoryDraft(args.String("title")!, args.String("summary")!, args.String("tags") ?? "",
                    args.String("markdown")!, args.String("verification") ?? "hypothesis", args.String("sources"));
                var saved = await memory.SaveAsync(id, draft, agent.Id, run.ProjectId, run.WorkId,
                    run.ConversationId, ct, args.String("mergeId"));
                return new { saved.Id, saved.Version };
            case "memory_archive":
                await RequireNoteAsync(db, agent, id!, ct);
                await memory.ArchiveAsync(id!, agent.Id, run.WorkId, run.ConversationId, false, ct);
                return new { archived = id };
            case "memory_timeline":
                var timeline = await memory.TimelineAsync(await RangeAsync(db, args, ct), project, null, ct);
                return timeline.Where(x => ToolAccess.AllowsProject(agent, x.ProjectId)).Take(15).Select(x =>
                    new { x.Kind, Detail = TextLimits.Clip(x.Detail, 350), x.WorkId, x.ProjectId, x.EventOccurredAtUtc }).ToArray();
            case "conversation_search":
                var query = MemoryStore.SearchExpression(args.String("query")!);
                if (query is null) return Array.Empty<object>();
                var matches = db.Messages.FromSqlInterpolated($"""
                    SELECT m.* FROM Messages m JOIN MessageSearch ON MessageSearch.rowid = m.Id
                    WHERE MessageSearch MATCH {query}
                    """).AsNoTracking();
                var scopes = ToolAccess.Split(agent.AllowedProjects);
                var all = args.String("scope") == "all";
                if (!all) matches = matches.Where(x => x.ConversationId == run.ConversationId);
                matches = matches.Where(x => db.Runs.Any(r => (r.UserMessageId == x.Id || r.AssistantMessageId == x.Id) &&
                    (!all || r.AgentId == agent.Id) && (r.ProjectId == null || Enumerable.Contains(scopes, r.ProjectId))));
                if (await RangeAsync(db, args, ct) is { } interval)
                    matches = matches.Where(x => x.CreatedAtUtc >= interval.FromUtc && x.CreatedAtUtc < interval.UntilUtc);
                return (await matches.OrderByDescending(m => m.Id).Take(8).ToListAsync(ct))
                    .Select(x => new { x.Role, Text = TextLimits.Clip(x.Text, 600), x.CreatedAtUtc }).ToArray();
            case "resource_search":
            case "resource_get":
                var visible = await operations.VisibleResourcesAsync(agent, args.String("query"), ct);
                if (id is not null)
                {
                    var allowedProjects = ToolAccess.Split(agent.AllowedProjects);
                    visible = await db.Resources.AsNoTracking().Where(x => x.Id == id &&
                        x.ProjectId != null && Enumerable.Contains(allowedProjects, x.ProjectId)).ToListAsync(ct);
                }
                return visible.Select(x => new { x.Id, x.Name, Kind = x.Kind.ToString(), x.ProjectId, x.Address, x.Production }).ToArray();
            case "git_workspace":
                if (!ToolAccess.Has(agent, "workspace")) throw new UnauthorizedAccessException("Çalışma alanı izni gerekli.");
                await RequireResourceProjectAsync(db, id!, run, ct);
                return await operations.GitAsync(agent, id!, run.Workspace!, args.String("operation")!, args.String("argument"), ct,
                    run.ConversationId, run.WorkId);
            case "git_request_push":
            case "ssh_request_operation":
                await RequireResourceProjectAsync(db, id!, run, ct);
                var approval = await operations.RequestAsync(agent, run.ConversationId, id!,
                    name == "git_request_push" ? "GitPush" : args.String("operation") == "restart-docker" ? "SshChange" : "SshRelease",
                    args.String("operation") ?? "push", run.Workspace, args.String("destinationBranch"), ct, args.String("image"), run.WorkId);
                return new { approval.Id, approval.Scope, approval.Status, approval.ExpiresAtUtc };
            case "ssh_inspect":
                await RequireResourceProjectAsync(db, id!, run, ct);
                return await operations.InspectSshAsync(agent, id!, args.String("operation")!, ct, run.ConversationId, run.WorkId);
            case "agent_list":
                var agents = await db.Agents.AsNoTracking().Where(x => !x.Archived && x.Id != agent.Id).ToListAsync(ct);
                return agents.Where(a => ToolAccess.AllowsProject(a, run.ProjectId)).Select(a => new { a.Id, a.Name, a.Title }).ToArray();
            case "task_delegate":
                if (run.DelegationDepth != 0) throw new InvalidOperationException("Bu iş devri son seviye.");
                var target = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == args.String("agentId"), ct);
                if (target is null || target.Id == agent.Id || !ToolAccess.AllowsProject(target, run.ProjectId))
                    throw new UnauthorizedAccessException("Hedef ajan erişilebilir değil.");
                return new Delegation(target.Id, TextLimits.Clip(args.String("brief"), 2000));
            default:
                return await WorkAsync(db, name, args, run, ct);
        }
    }

    private async Task<object?> WorkAsync(PlatformDb db, string operation, JsonElement args, ChatRun run, CancellationToken ct)
    {
        var work = run.WorkId is null ? null : await db.Work.SingleOrDefaultAsync(x => x.Id == run.WorkId &&
            x.AgentId == run.AgentId && x.ProjectId == run.ProjectId, ct);
        if (operation == "task_get_current")
            return work is null ? null : new { work.Id, work.Title, work.Description, work.LastStep, work.NextStep,
                work.Branch, work.Commit, Status = work.Status.ToString(), path = "." };

        var kind = "WorkProgress";
        if (operation == "task_begin")
        {
            if (args.String("mode") == "new") work = null;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (work is null)
            {
                work = new WorkItem { AgentId = run.AgentId, ConversationId = run.ConversationId, ProjectId = run.ProjectId,
                    Workspace = run.Workspace!, Title = TextLimits.Clip(args.String("title"), 160),
                    Description = TextLimits.Clip(args.String("description"), 4000) };
                db.Work.Add(work);
                kind = "WorkCreated";
            }
            else kind = "WorkResumed";
            work.Status = WorkStatus.Running;
            work.StartedAtUtc ??= DateTime.UtcNow;
            work.CompletedAtUtc = null;
            work.CodexThreadId = run.ThreadId;
            await db.Runs.Where(x => x.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkId, work.Id), ct);
            work.UpdatedAtUtc = DateTime.UtcNow;
            db.Events.Add(new Activity { AgentId = run.AgentId, WorkId = work.Id, ConversationId = run.ConversationId,
                ProjectId = work.ProjectId, Kind = kind, Detail = work.Title });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            run.WorkId = work.Id;
            await WorkspaceMemory.WriteWorkAsync(paths, work, ct);
            return new { work.Id, Status = work.Status.ToString(), work.Title };
        }
        else
        {
            if (work is null || work.Status != WorkStatus.Running)
                throw new InvalidOperationException("Önce task_begin çağırın.");
            work.LastStep = TextLimits.Clip(args.String("step"), 1000);
            work.NextStep = TextLimits.Clip(args.String("next"), 800);
            work.Branch = args.String("branch") is { } branch ? TextLimits.Clip(branch, 151) : work.Branch;
            work.Commit = args.String("commit") is { } commit ? TextLimits.Clip(commit, 64) : work.Commit;
            if (operation == "task_finish")
            {
                work.Status = args.String("status") switch
                {
                    "completed" => WorkStatus.Completed, "paused" => WorkStatus.Paused,
                    "waiting" => WorkStatus.WaitingForUser, "failed" => WorkStatus.Failed,
                    "cancelled" => WorkStatus.Cancelled, _ => throw new ArgumentException("Geçersiz iş durumu.")
                };
                if (work.Status == WorkStatus.Completed) work.CompletedAtUtc = DateTime.UtcNow;
                kind = $"Work{work.Status}";
            }
            else if (args.String("event") is { } classification &&
                classification is "CodeModified" or "TestExecuted" or "ResearchCompleted")
                kind = classification;
        }
        work.UpdatedAtUtc = DateTime.UtcNow;
        db.Events.Add(new Activity { AgentId = run.AgentId, WorkId = work.Id, ConversationId = run.ConversationId,
            ProjectId = work.ProjectId, Kind = kind, Detail = work.LastStep ?? work.Title });
        await db.SaveChangesAsync(ct);
        await WorkspaceMemory.WriteWorkAsync(paths, work, ct);
        return new { work.Id, Status = work.Status.ToString(), work.Title };
    }


    private static async Task<TimeRange?> RangeAsync(PlatformDb db, JsonElement args, CancellationToken ct)
    {
        if (args.String("when") is null && args.String("from") is null && args.String("until") is null) return null;
        var zoneId = await db.Preferences.Select(x => x.TimeZone).SingleAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        if (args.String("when") is { } when)
            return TemporalQuery.Parse(when, DateTime.UtcNow, zone) ??
                throw new ArgumentException("Tarih ifadesi tanınmadı. from ve until tarihleri kullanın.");
        if (!DateOnly.TryParseExact(args.String("from"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(args.String("until"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var until) || until <= from)
            throw new ArgumentException("Geçerli bir başlangıç ve bitiş tarihi gerekli.");
        return new(TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone),
            TimeZoneInfo.ConvertTimeToUtc(until.ToDateTime(TimeOnly.MinValue), zone));
    }

    private static bool CanRead(Agent agent, MemoryNote note) =>
        note.ProjectId is not null ? ToolAccess.AllowsProject(agent, note.ProjectId) : note.AgentId is null || note.AgentId == agent.Id;

    private static async Task RequireNoteAsync(PlatformDb db, Agent agent, string id, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (note is null || !CanRead(agent, note)) throw new UnauthorizedAccessException("Hafıza kaydı erişilebilir değil.");
    }

    private static async Task RequireResourceProjectAsync(PlatformDb db, string id, ChatRun run, CancellationToken ct)
    {
        if (run.ProjectId is null || !await db.Resources.AnyAsync(x => x.Id == id && x.ProjectId == run.ProjectId, ct))
            throw new UnauthorizedAccessException("Bu kaynağın projesini sohbet kutusundan seçin.");
    }
}
