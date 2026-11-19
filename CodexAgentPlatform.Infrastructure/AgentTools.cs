using System.Text.Json;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed record Delegation(string AgentId, string Brief, bool TransferConversation = false, string? RequiredPermission = null);
public sealed record ApprovalReply(string Id, string Scope, string Status, DateTime ExpiresAtUtc);

public sealed class AgentTools(IDbContextFactory<PlatformDb> dbs, MemoryStore memory, Operations operations, PlatformPaths paths)
{
    private sealed record Tool(string Name, string Permission, string Description, string[] Required, string[] Optional);
    internal const string GrantToolName = "request_agent_permission";
    private static readonly Tool GrantRequest = new(GrantToolName, "",
        "On the owner's explicit request, propose one additional permission for an agent (exact name or ID). " +
        "For kind 'project', permission is the exact project name or ID. For kind 'tool', use one of " +
        string.Join(", ", ToolAccess.Permissions.Keys) + ". The owner reviews the proposal in chat.",
        ["agent", "kind", "permission"], []);
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
        new("task_delegate", "delegate", "Assign one task to another agent after this turn. List eligible agents using agent_list.", ["agentId", "brief"], []),
        new("agent_list", "delegate", "List agents eligible for a follow-up on this project.", [], []),
        new("resource_search", ToolAccess.ProjectResources, "Find resources by name within your granted scope. Set scope to global for the global inventory.", [], ["query", "scope"]),
        new("project_create", ToolAccess.CreateProjects, "Create a platform project and give the creating agent access to it. The default branch is main unless specified.", ["name"], ["defaultBranch"]),
        new("resource_get", ToolAccess.ProjectResources, "Read metadata of an authorized resource.", ["id"], []),
        new("resource_create", ToolAccess.CreateResources,
            "Register a platform resource in the current project, or provide projectId (ID or exact name). " +
            "Use scope=global only for an explicit global request. For a new resource provide kind and address. " +
            "Kinds: GitRepository, SshServer, ApiConnection, LocalWorkspace, Environment, DeploymentTarget, ExternalService. " +
            "Use resource_search then sourceId to copy a visible resource, optionally changing the repository URL. " +
            "A saved HTTPS Git credential is linked by the server. For SSH use an existing SSH source. " +
            "DeploymentTarget requires sshServerId or a source target. Never provide keys or tokens.",
            ["name"], ["kind", "address", "scope", "projectId", "sourceId", "sshServerId", "production"]),
        new("resource_update", ToolAccess.CreateResources,
            "Edit the name or address/path of a project resource you created, using its project resource ID. " +
            "Pass sourceId to use another visible connection of the same type, or sshServerId for a deployment target. " +
            "Specify projectId when the destination differs from the active project.",
            ["id"], ["name", "address", "projectId", "sourceId", "sshServerId"]),
        new("git_workspace", "git", "Use prepare to check out the registered repository. Use list or read for committed files at HEAD, passing a relative directory or file path as argument. Supports status, diff, fetch, pull, branch and commit. Pass nextOffset as offset for paged output.", ["id", "operation"], ["argument", "offset"]),
        new("git_request_push", "git", "Request explicit owner approval for a non-force push of the current commit.", ["id"], ["destinationBranch"]),
        new("github_request_create_repository", ToolAccess.CreateGitHubRepositories,
            "Request owner approval to create a GitHub repository for a platform project using a saved global GitHub connection. " +
            "Find its ID with resource_search. Provide the personal GitHub account login, repository name and visibility (private or public). " +
            "Use the active project or pass projectId (ID or exact name). After approval the platform initializes and links the repository.",
            ["sourceId", "owner", "name", "visibility"], ["projectId"]),
        new("ssh_inspect", "ssh", "Inspect test, health, docker-status, docker-services, linux-services, docker-logs or sftp-read. For more live command output pass nextOffset as offset.", ["id", "operation"], ["offset"]),
        new("ssh_request_operation", "ssh", "Request explicit approval for restart-docker on an SSH server, or deploy/rollback on a registered Swarm target. Deploy requires an image digest.", ["id", "operation"], ["image"])
    ];

    public static IEnumerable<object> Describe(Agent agent, bool projectSelected = false, bool voice = false)
    {
        var deferred = new List<object> { DescribeFunction(GrantRequest, true) };
        if (agent.Id == PlatformChat.Id)
        {
            yield return DescribeFunction(new Tool("handoff_to_agent", "", "Transfer the current user request to one listed specialist.", ["agentId"], []), false);
            yield return ToolNamespace(deferred);
            yield break;
        }
        if (voice)
            yield return DescribeFunction(new Tool("handoff_to_agent", "",
                "Transfer this conversation and the caller's current request to a listed teammate when it is outside your scope. " +
                "Include the tool permission needed for access. Use task_delegate for work you want to assign while keeping the conversation.",
                ["agentId"], ["permission"]), false);
        foreach (var tool in Registry.Where(tool => HasToolPermission(agent, tool)))
        {
            bool onDemand = tool.Permission is "git" or "ssh" or ToolAccess.ProjectResources ||
                tool.Name is "memory_archive" or "memory_consolidate" or "memory_update" or "memory_create";
            if ((projectSelected || voice) && tool.Name is ("resource_search" or "resource_get" or "git_workspace"))
                onDemand = false;
            var spec = DescribeFunction(tool, onDemand);
            if (onDemand) deferred.Add(spec);
            else yield return spec;
        }
        yield return ToolNamespace(deferred);
    }

    private static bool HasToolPermission(Agent agent, Tool tool) => tool.Permission switch
    {
        ToolAccess.ProjectResources => ToolAccess.HasResourcePermission(agent),
        ToolAccess.CreateGitHubRepositories => ToolAccess.Has(agent, tool.Permission) && ToolAccess.Has(agent, ToolAccess.GlobalResources),
        "" => true,
        _ => ToolAccess.Has(agent, tool.Permission)
    };

    private static object ToolNamespace(List<object> tools) =>
        new { type = "namespace", name = "platform", description = "Platform actions and owner approval requests.", tools };

    private static object DescribeFunction(Tool tool, bool deferLoading) => new
    {
        type = "function", name = tool.Name, description = tool.Description, deferLoading,
        inputSchema = new
        {
            type = "object",
            properties = tool.Required.Concat(tool.Optional).ToDictionary(x => x, x => new { type = x switch
                { "offset" => "integer", "production" => "boolean", _ => "string" } }),
            required = tool.Required,
            additionalProperties = false
        }
    };

    public async Task<object?> RunAsync(string name, JsonElement args, ChatRun run, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var normal = run.AgentId == PlatformChat.Id;
        var agent = normal ? PlatformChat.CreateAgent() : await db.Agents.AsNoTracking().SingleAsync(x => x.Id == run.AgentId, ct);
        if (normal && name != GrantToolName)
            throw new UnauthorizedAccessException("This tool is not available in normal chat.");
        var tool = name == GrantToolName ? GrantRequest : Registry.FirstOrDefault(x => x.Name == name);
        if (agent.Archived || tool is null || !HasToolPermission(agent, tool)
            || (!normal && !ToolAccess.AllowsProject(agent, run.ProjectId)))
            throw new UnauthorizedAccessException("This tool is not permitted.");
        if (args.ValueKind != JsonValueKind.Object || tool.Required.Any(x => string.IsNullOrWhiteSpace(args.String(x))))
            throw new ArgumentException("Required tool arguments are missing.");

        if (name == GrantToolName)
            return await AgentPermissionGrants.ProposeAsync(db, run, args.String("agent")!, args.String("kind")!, args.String("permission")!, ct);
        if (name == "resource_create")
            return await ResourceRegistration.CreateAsync(db, run, args, ct);
        if (name == "resource_update")
            return await ResourceRegistration.UpdateAsync(db, run, args, ct);
        if (name == "github_request_create_repository")
        {
            var approval = await operations.RequestGitHubRepositoryAsync(run, args, ct);
            return new ApprovalReply(approval.Id, approval.Scope, approval.Status, approval.ExpiresAtUtc);
        }

        var id = args.String("id");
        var project = args.String("projectId") ?? run.ProjectId;
        if (!ToolAccess.AllowsProject(agent, project)) throw new UnauthorizedAccessException("Access to this project is not permitted.");
        switch (name)
        {
            case "project_create":
                var created = await ProjectStore.CreateAsync(db, args.String("name")!, args.String("defaultBranch"), run, ct);
                return new { created.Id, created.Name, created.DefaultBranch };
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
                List<Resource> visible;
                if (id is not null)
                {
                    var allowedProjects = ToolAccess.Split(agent.AllowedProjects);
                    var canReadGlobal = ToolAccess.Has(agent, ToolAccess.GlobalResources);
                    var canReadProject = ToolAccess.Has(agent, ToolAccess.ProjectResources);
                    visible = await db.Resources.AsNoTracking().Where(x => x.Id == id &&
                        (x.ProjectId == null ? canReadGlobal && x.IsGlobal : canReadProject && Enumerable.Contains(allowedProjects, x.ProjectId) &&
                            (run.ProjectId == null || x.ProjectId == run.ProjectId))).ToListAsync(ct);
                }
                else
                {
                    var globalOnly = string.Equals(args.String("scope"), "global", StringComparison.OrdinalIgnoreCase);
                    if (globalOnly && !ToolAccess.Has(agent, ToolAccess.GlobalResources))
                        throw new UnauthorizedAccessException("The Global resources permission is required.");
                    visible = await operations.VisibleResourcesAsync(agent, args.String("query"), ct, run.ProjectId, globalOnly);
                }
                return visible.Select(x => new { x.Id, x.Name, Kind = x.Kind.ToString(), x.ProjectId, x.IsGlobal, x.Address, x.Production,
                    hasSavedConnection = x.VaultId != null || x.ConnectionSourceId != null,
                    canEdit = !x.IsGlobal && x.CreatedByAgentId == agent.Id && ToolAccess.Has(agent, ToolAccess.CreateResources) }).ToArray();
            case "git_workspace":
                if (!ToolAccess.Has(agent, "workspace")) throw new UnauthorizedAccessException("Workspace permission is required.");
                await RequireResourceProjectAsync(db, id!, run, ct);
                return await operations.GitAsync(agent, id!, run.Workspace!, args.String("operation")!, args.String("argument"), ct,
                    run.ConversationId, run.WorkId, OutputOffset(args));
            case "git_request_push":
            case "ssh_request_operation":
                await RequireResourceProjectAsync(db, id!, run, ct);
                var approval = await operations.RequestAsync(agent, run.ConversationId, id!,
                    name == "git_request_push" ? "GitPush" : args.String("operation") == "restart-docker" ? "SshChange" : "SshRelease",
                    args.String("operation") ?? "push", run.Workspace, args.String("destinationBranch"), ct, args.String("image"), run.WorkId);
                return new ApprovalReply(approval.Id, approval.Scope, approval.Status, approval.ExpiresAtUtc);
            case "ssh_inspect":
                await RequireResourceProjectAsync(db, id!, run, ct);
                return await operations.InspectSshAsync(agent, id!, args.String("operation")!, ct, run.ConversationId, run.WorkId, OutputOffset(args));
            case "agent_list":
                var agents = await db.Agents.AsNoTracking().Where(x => !x.Archived && x.Id != agent.Id).ToListAsync(ct);
                return agents.Where(a => ToolAccess.AllowsProject(a, run.ProjectId))
                    .Select(a => new { a.Id, a.Name, a.Title, workspace = ToolAccess.Has(a, "workspace") }).ToArray();
            case "task_delegate":
                if (run.DelegationDepth != 0) throw new InvalidOperationException("Further delegation is not permitted for this work.");
                var target = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == args.String("agentId"), ct);
                if (target is null || target.Id == agent.Id || !ToolAccess.AllowsProject(target, run.ProjectId))
                    throw new UnauthorizedAccessException("The target agent is not available.");
                var brief = TextLimits.Clip(args.String("brief")?.Trim(), 2000);
                if (brief.Length == 0) throw new ArgumentException("Provide a task brief for the receiving agent.");
                return new Delegation(target.Id, brief);
            default:
                return await WorkAsync(db, name, args, run, ct);
        }
    }

    private static int OutputOffset(JsonElement args)
    {
        var value = args.Get("offset");
        if (value.ValueKind == JsonValueKind.Undefined) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var offset) && offset >= 0) return offset;
        throw new ArgumentException("A valid output offset is required.");
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
                throw new InvalidOperationException("Call task_begin first.");
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
                    "cancelled" => WorkStatus.Cancelled, _ => throw new ArgumentException("Invalid work status.")
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
                throw new ArgumentException("Date expression not recognized. Specify from and until dates.");
        if (!DateOnly.TryParseExact(args.String("from"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var from) ||
            !DateOnly.TryParseExact(args.String("until"), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var until) || until <= from)
            throw new ArgumentException("Valid start and end dates are required.");
        return new(TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone),
            TimeZoneInfo.ConvertTimeToUtc(until.ToDateTime(TimeOnly.MinValue), zone));
    }

    private static bool CanRead(Agent agent, MemoryNote note) =>
        note.ProjectId is not null ? ToolAccess.AllowsProject(agent, note.ProjectId) : note.AgentId is null || note.AgentId == agent.Id;

    private static async Task RequireNoteAsync(PlatformDb db, Agent agent, string id, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (note is null || !CanRead(agent, note)) throw new UnauthorizedAccessException("The memory note is not accessible.");
    }

    private static async Task RequireResourceProjectAsync(PlatformDb db, string id, ChatRun run, CancellationToken ct)
    {
        if (!await db.Resources.AnyAsync(x => x.Id == id && (x.ProjectId == null ? x.IsGlobal : x.ProjectId == run.ProjectId), ct))
            throw new UnauthorizedAccessException("Select this resource's project in the chat settings.");
    }
}
