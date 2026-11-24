using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class ConversationLifecycleTests
{
    [Fact]
    public async Task OwnerFinishesAbandonedWorkAndCanThenDeleteTheChat()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var chats = new ChatStore(storage, new ChatUpdates());
        var chat = new Conversation();
        var other = new Conversation();
        var task = new WorkItem { ConversationId = chat.Id, Status = WorkStatus.WaitingForUser };
        var unrelated = new WorkItem { ConversationId = other.Id, Status = WorkStatus.Running };
        var run = new ChatRun { ConversationId = chat.Id, WorkId = task.Id, Status = WorkStatus.WaitingForApproval };
        var approval = new Approval { ConversationId = chat.Id, ContinuationRunId = run.Id, Status = "Approved" };
        db.AddRange(chat, other, task, unrelated, run, approval, new Message { ConversationId = chat.Id, Text = "history" });
        await db.SaveChangesAsync();
        Task<string> Finish() => chats.FinishConversationAsync(chat.Id, PlatformUser.OwnerId, default);
        Assert.NotNull(await chats.DeleteConversationAsync(chat.Id, storage.Paths, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chats.FinishConversationAsync(chat.Id, "agent", default));
        Assert.Contains("Cancelled tasks: 1", await Finish());
        Assert.Contains("Cancelled tasks: 0", await Finish());
        db.ChangeTracker.Clear();
        Assert.Equal(WorkStatus.Cancelled, (await db.Work.FindAsync(task.Id))!.Status);
        Assert.Equal(WorkStatus.Running, (await db.Work.FindAsync(unrelated.Id))!.Status);
        Assert.Equal(WorkStatus.Cancelled, (await db.Runs.FindAsync(run.Id))!.Status);
        Assert.Equal("Declined", (await db.Approvals.FindAsync(approval.Id))!.Status);
        Assert.Null(await ApprovalContinuations.QueueReadyAsync(db, default));
        Assert.Equal("history", Assert.Single((await chats.ReadAsync(chat.Id, 20, default)).Messages).Text);
        Assert.Null(await chats.DeleteConversationAsync(chat.Id, storage.Paths, default));
    }

    [Fact]
    public async Task AgentRequestNeedsOwnerDecisionAndCannotTargetAnUnrelatedChat()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var callerChat = new Conversation();
        var target = new Conversation { AutoApprove = true };
        var unrelated = new Conversation();
        var caller = new ChatRun { AgentId = "developer", ConversationId = callerChat.Id };
        var work = new WorkItem { ConversationId = target.Id, Status = WorkStatus.WaitingForUser };
        db.AddRange(callerChat, target, unrelated, work, new ChatRun { ConversationId = target.Id,
            AgentId = caller.AgentId, Status = WorkStatus.Completed });
        await db.SaveChangesAsync();
        var ops = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        var tools = new AgentTools(storage, new MemoryStore(storage, storage.Paths), ops, storage.Paths);
        Task<object?> Request(string id) => tools.RunAsync("conversation_request_finish",
            System.Text.Json.JsonSerializer.SerializeToElement(new { id }), caller, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Request(unrelated.Id));
        var request = Assert.IsType<ApprovalReply>(await Request(target.Id));
        Assert.Equal(request.Id, Assert.IsType<ApprovalReply>(await Request(target.Id)).Id);
        Assert.Equal("Pending", request.Status);
        Assert.Equal(WorkStatus.WaitingForUser, (await db.Work.AsNoTracking().SingleAsync(w => w.Id == work.Id)).Status);
        await ops.DecideAsync(request.Id, true, PlatformUser.OwnerId, default);
        Assert.Equal(WorkStatus.Cancelled, (await db.Work.AsNoTracking().SingleAsync(w => w.Id == work.Id)).Status);
        Assert.Equal("Completed", (await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == request.Id)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinishingBlocksWithoutPartialChangesWhenExecutionIsInProgress(bool runningResponse)
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var chats = new ChatStore(storage, new ChatUpdates());
        var chat = new Conversation();
        var work = new WorkItem { ConversationId = chat.Id, Status = WorkStatus.Running };
        db.AddRange(chat, work, new ChatRun { ConversationId = chat.Id,
            Status = runningResponse ? WorkStatus.Running : WorkStatus.Pending },
            new Approval { ConversationId = chat.Id, Status = runningResponse ? "Pending" : "Executing" });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chats.FinishConversationAsync(chat.Id, PlatformUser.OwnerId, default));
        Assert.Contains(runningResponse ? "running" : "executing", error.Message);
        Assert.Equal(WorkStatus.Running, (await db.Work.AsNoTracking().SingleAsync(w => w.Id == work.Id)).Status);
    }

    [Fact]
    public async Task ArchivingKeepsHistoryReadableAndDeletingPreservesProjectData()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var chats = new ChatStore(storage, new ChatUpdates());
        var chat = new Conversation { Title = "Conversation to archive" };
        var other = new Conversation { Title = "Keep" };
        var project = new Project { Name = "Shared" };
        var resource = new Resource { ProjectId = project.Id };
        var note = new MemoryNote { ProjectId = project.Id, Title = "Keep note" };
        var workspace = Guid.NewGuid().ToString("N");
        var run = new ChatRun { ConversationId = chat.Id, Workspace = workspace, Status = WorkStatus.Completed };
        var message = new Message { ConversationId = chat.Id, Role = "user", Text = "needle" };
        db.AddRange(chat, other, project, resource, note, run, message);
        await db.SaveChangesAsync();
        var photo = new MessagePhoto { MessageId = message.Id, Name = "image.png", ContentType = "image/png", Bytes = [1] };
        db.Add(photo);
        await db.SaveChangesAsync();
        var root = storage.Paths.Workspace(workspace);
        var folder = Path.Combine(root, "chat-images", photo.Id);
        Directory.CreateDirectory(folder);
        var staged = Path.Combine(folder, "photo-image.png");
        await File.WriteAllBytesAsync(staged, photo.Bytes);
        var keep = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(keep, "project edits");

        await chats.SetArchivedAsync(chat.Id, true, default);
        Assert.Empty(await chats.RecentAsync("needle", default));
        Assert.Equal(chat.Id, Assert.Single(await chats.RecentAsync("needle", default, true)).Id);
        Assert.Single((await chats.ReadAsync(chat.Id, 20, default)).Messages);
        await chats.SetArchivedAsync(chat.Id, false, default);
        Assert.Equal(chat.Id, Assert.Single(await chats.RecentAsync("needle", default)).Id);

        Assert.Null(await chats.DeleteConversationAsync(chat.Id, storage.Paths, default));
        Assert.False((await chats.ReadAsync(chat.Id, 20, default)).Exists);
        Assert.False(await db.MessagePhotos.AnyAsync(p => p.Id == photo.Id));
        Assert.False(File.Exists(staged));
        Assert.Equal("project edits", await File.ReadAllTextAsync(keep));
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == other.Id));
        Assert.True(await db.Resources.AnyAsync(r => r.Id == resource.Id));
        Assert.True(await db.Notes.AnyAsync(n => n.Id == note.Id));
    }

    [Fact]
    public async Task ActiveWorkRunAndOutstandingApprovalsIndependentlyBlockDeletion()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var chats = new ChatStore(storage, new ChatUpdates());
        var chat = new Conversation();
        var work = new WorkItem { ConversationId = chat.Id, Status = WorkStatus.Running };
        db.AddRange(chat, work);
        await db.SaveChangesAsync();
        Task<string?> Delete() => chats.DeleteConversationAsync(chat.Id, storage.Paths, default);
        Assert.Contains("active task", await Delete());
        work.Status = WorkStatus.Completed;
        var run = new ChatRun { ConversationId = chat.Id, Status = WorkStatus.Pending };
        db.Add(run);
        await db.SaveChangesAsync();
        Assert.Contains("active task", await Delete());
        run.Status = WorkStatus.Completed;
        var approval = new Approval { ConversationId = chat.Id };
        db.Add(approval);
        foreach (var status in new[] { "Pending", "Approved", "Executing" })
        {
            approval.Status = status;
            await db.SaveChangesAsync();
            Assert.Contains("approval", await Delete());
            Assert.True(await db.Conversations.AnyAsync(c => c.Id == chat.Id));
        }
        approval.Status = "Completed";
        await db.SaveChangesAsync();
        Assert.Null(await Delete());
    }
}
