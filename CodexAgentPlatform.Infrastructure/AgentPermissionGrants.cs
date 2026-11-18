using System.Text.Json;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public static class AgentPermissionGrants
{
    public const string ApprovalKind = "AgentPermissionGrant";
    private sealed record Grant(string TargetAgentId, string PermissionKind, string PermissionId);

    public static async Task<ApprovalReply> ProposeAsync(PlatformDb db, ChatRun run, string agent, string kind,
        string permission, CancellationToken ct)
    {
        agent = agent.Trim();
        permission = permission.Trim();
        kind = kind.Trim().ToLowerInvariant();
        if (agent.Length == 0 || permission.Length == 0 || kind is not ("tool" or "project"))
            throw new ArgumentException("Specify an agent, a permission type (tool or project), and a permission.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (run.DelegationDepth != 0 || !await db.Messages.AnyAsync(m => m.Id == run.UserMessageId &&
            m.ConversationId == run.ConversationId && m.Role == "user", ct))
            throw new UnauthorizedAccessException("Permission requests must originate from an owner message in this chat.");
        var candidates = await db.Agents.AsNoTracking().Where(a => !a.Archived && (a.Id == agent || a.Name == agent))
            .Take(2).ToListAsync(ct);
        if (candidates.Count != 1)
            throw new InvalidOperationException("Specify the exact name or ID of one active agent.");
        var target = candidates[0];
        string key, label;
        if (kind == "tool")
        {
            var match = ToolAccess.Permissions.FirstOrDefault(p => p.Key.Equals(permission, StringComparison.OrdinalIgnoreCase)
                || p.Value.Equals(permission, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null) throw new ArgumentException("Unknown tool permission. Use its key or its name from Tool permissions.");
            (key, label) = (match.Key, match.Value);
        }
        else
        {
            var projects = await db.Projects.AsNoTracking().Where(p => p.Id == permission || p.Name == permission)
                .Select(p => new { p.Id, p.Name }).Take(2).ToListAsync(ct);
            if (projects.Count != 1) throw new ArgumentException("Specify the exact name or ID of one existing project.");
            (key, label) = (projects[0].Id, projects[0].Name);
        }
        if (ToolAccess.Split(kind == "tool" ? target.AllowedTools : target.AllowedProjects).Contains(key))
            throw new InvalidOperationException("The agent already has this permission.");

        var payload = JsonSerializer.Serialize(new Grant(target.Id, kind, key));
        var scope = Scope(target.Name, kind, label);
        var now = DateTime.UtcNow;
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == ApprovalKind &&
            a.ConversationId == run.ConversationId && a.Payload == payload && a.Scope == scope &&
            a.Status == "Pending" && a.ExpiresAtUtc > now, ct);
        if (approval is null)
        {
            approval = new Approval { Kind = ApprovalKind, AgentId = run.AgentId, ConversationId = run.ConversationId,
                Scope = scope, Payload = payload };
            db.Approvals.Add(approval);
            db.Events.Add(new Activity { Kind = "PermissionGrantRequested", AgentId = run.AgentId,
                ConversationId = run.ConversationId, ProjectId = run.ProjectId, WorkId = run.WorkId, Detail = scope });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new ApprovalReply(approval.Id, approval.Scope, approval.Status, approval.ExpiresAtUtc);
    }

    // Operations.DecideAsync loads this pending request under the same write transaction.
    internal static async Task DecideAsync(PlatformDb db, Approval approval, bool allow, string ownerId, CancellationToken ct)
    {
        if (ownerId != PlatformUser.OwnerId) throw new UnauthorizedAccessException();
        var grant = JsonSerializer.Deserialize<Grant>(approval.Payload)
            ?? throw new InvalidOperationException("Invalid agent permission request.");
        approval.DecidedBy = ownerId;
        approval.Status = "Declined";
        if (allow)
        {
            var target = await db.Agents.SingleOrDefaultAsync(a => a.Id == grant.TargetAgentId && !a.Archived, ct);
            var label = grant.PermissionKind switch
            {
                "tool" => ToolAccess.Permissions.GetValueOrDefault(grant.PermissionId),
                "project" => await db.Projects.Where(p => p.Id == grant.PermissionId).Select(p => p.Name).SingleOrDefaultAsync(ct),
                _ => null
            };
            if (target is null || label is null || Scope(target.Name, grant.PermissionKind, label) != approval.Scope)
            {
                approval.Status = "Failed";
                approval.Result = "The agent or permission changed. Request a new grant.";
            }
            else
            {
                if (grant.PermissionKind == "tool") target.AllowedTools = Add(target.AllowedTools, grant.PermissionId);
                else target.AllowedProjects = Add(target.AllowedProjects, grant.PermissionId);
                approval.Status = "Completed";
                approval.Result = "Permission granted.";
            }
        }
        db.Events.Add(new Activity { Kind = approval.Status switch { "Completed" => "PermissionGranted",
            "Declined" => "PermissionGrantDeclined", _ => "PermissionGrantFailed" }, AgentId = grant.TargetAgentId,
            ConversationId = approval.ConversationId, Detail = approval.Scope });
        await db.SaveChangesAsync(ct);
    }

    private static string Scope(string agentName, string kind, string label) =>
        $"Grant {agentName} {(kind == "tool" ? "the tool permission" : "access to the project")} “{label}”.";

    private static string Add(string existing, string key) =>
        string.Join(',', ToolAccess.Split(existing).Append(key).Distinct(StringComparer.Ordinal));
}
