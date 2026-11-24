using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class ConversationLifecycleTests
{
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
