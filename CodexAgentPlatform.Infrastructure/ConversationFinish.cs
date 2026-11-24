using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

internal static class ConversationFinish
{
    internal const string Kind = "FinishConversation";

    internal static async Task<ApprovalReply> RequestAsync(PlatformDb db, ChatRun caller, string id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var runs = db.Runs.Where(r => r.ConversationId == id);
        var work = db.Work.Where(w => w.ConversationId == id);
        if ((id != caller.ConversationId && !await runs.AnyAsync(r => r.AgentId == caller.AgentId && r.ProjectId == caller.ProjectId, ct))
            || await runs.AnyAsync(r => r.ProjectId != caller.ProjectId, ct)
            || await work.AnyAsync(w => w.ProjectId != caller.ProjectId, ct))
            throw new UnauthorizedAccessException("Choose a conversation available to this agent in the current project.");
        var chat = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException("Conversation not found.");
        var now = DateTime.UtcNow;
        var request = await db.Approvals.FirstOrDefaultAsync(a => a.Kind == Kind && a.Payload == id &&
            a.ConversationId == caller.ConversationId && a.AgentId == caller.AgentId &&
            a.Status == "Pending" && a.ExpiresAtUtc > now, ct);
        if (request is null)
        {
            request = new Approval { Kind = Kind, Payload = id, ConversationId = caller.ConversationId,
                AgentId = caller.AgentId, Scope = $"Finish “{chat.Title}” ({id}) and cancel its outstanding work and approvals. Keep chat history." };
            db.Approvals.Add(request);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(request.Id, request.Scope, request.Status, request.ExpiresAtUtc);
    }

    // Both owner entry points hold a SQLite write transaction through the worker claim checks and updates.
    internal static async Task<string> ApplyAsync(PlatformDb db, string id, string? decisionId, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == id, ct))
            throw new InvalidOperationException("Conversation no longer exists.");
        var runs = db.Runs.Where(r => r.ConversationId == id);
        var approvals = db.Approvals.Where(a => a.ConversationId == id && a.Id != decisionId);
        if (await runs.AnyAsync(r => r.Status == WorkStatus.Running, ct))
            throw new InvalidOperationException("A response is running. Stop it in the chat or wait for it to finish, then retry.");
        if (await approvals.AnyAsync(a => a.Status == "Executing", ct))
            throw new InvalidOperationException("An approved operation is executing. Wait for its result, then retry.");

        var openTasks = db.Work.Where(w => w.ConversationId == id && Enumerable.Contains(ChatStore.Active, w.Status));
        if (await db.Runs.AnyAsync(r => r.ConversationId != id && Enumerable.Contains(ChatStore.Active, r.Status)
                && openTasks.Any(w => w.Id == r.WorkId), ct))
            throw new InvalidOperationException("A task from this conversation is active in another chat. Finish that response first.");

        var now = DateTime.UtcNow;
        const string reason = "Cancelled by the owner when finishing the conversation.";
        var responses = await runs.Where(r => Enumerable.Contains(ChatStore.Active, r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkStatus.Cancelled)
                .SetProperty(r => r.Error, reason).SetProperty(r => r.UpdatedAtUtc, now), ct);
        var tasks = await openTasks
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkStatus.Cancelled)
                .SetProperty(w => w.UpdatedAtUtc, now), ct);
        var declined = await approvals.Where(a => a.Status == "Pending" || a.Status == "Approved")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Declined")
                .SetProperty(a => a.DecidedBy, PlatformUser.OwnerId).SetProperty(a => a.Result, reason), ct);
        var result = $"Cancelled tasks: {tasks}. Cancelled responses: {responses}. Withdrawn approvals: {declined}.";
        if (tasks + responses + declined > 0)
            db.Events.Add(new Activity { ConversationId = id, Kind = "ConversationFinished", Detail = result });
        return result;
    }
}
