using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class AgentPermissionGrantTests
{
    [Fact]
    public async Task DuplicateProposalsStayPendingUntilAnOwnerDecisionAndPreserveOtherGrants()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var ops = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        var run = await StartAsync(db);
        Task<ApprovalReply> Request(string permission) =>
            AgentPermissionGrants.ProposeAsync(db, run, "QA Engineer", "tool", permission, default);
        var first = await Request("projects.create");
        Assert.Equal(first.Id, (await Request("Create projects")).Id);
        Assert.Equal(1, await db.Approvals.CountAsync());
        await ops.SetAutoApprovalAsync(run.ConversationId, true, PlatformUser.OwnerId, default);
        Assert.False(await ops.ExecuteNextAsync(default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ops.DecideAsync(first.Id, true, "other", default));
        await db.Agents.Where(a => a.Id == "qa").ExecuteUpdateAsync(s => s.SetProperty(a => a.AllowedTools, "ssh,workspace"));
        await ops.DecideAsync(first.Id, true, PlatformUser.OwnerId, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.DecideAsync(first.Id, true, PlatformUser.OwnerId, default));
        var permitted = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == "qa");
        Assert.Equal(new[] { "ssh", "workspace", "projects.create" }, ToolAccess.Split(permitted.AllowedTools));
        var approval = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.Equal("Completed", approval.Status);
        Assert.Equal(PlatformUser.OwnerId, approval.DecidedBy);
    }

    [Fact]
    public async Task DeclinedExpiredAndRenamedTargetsDoNotGrantAccess()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var run = await StartAsync(db);
        var ops = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        var project = new Project { Name = "Operations" };
        db.Add(project);
        await db.SaveChangesAsync();
        Task<ApprovalReply> Request() => AgentPermissionGrants.ProposeAsync(db, run, "qa", "project", project.Id, default);

        var declined = await Request();
        await ops.DecideAsync(declined.Id, false, PlatformUser.OwnerId, default);
        var expired = await Request();
        await db.Approvals.Where(a => a.Id == expired.Id).ExecuteUpdateAsync(s =>
            s.SetProperty(a => a.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.DecideAsync(expired.Id, true, PlatformUser.OwnerId, default));
        var renamed = await Request();
        project.Name = "Renamed operations";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.DecideAsync(renamed.Id, true, PlatformUser.OwnerId, default));
        Assert.Equal("Failed", await db.Approvals.Where(a => a.Id == renamed.Id).Select(a => a.Status).SingleAsync());
        Assert.False(ToolAccess.AllowsProject(await db.Agents.AsNoTracking().SingleAsync(a => a.Id == "qa"), project.Id));
    }

    [Fact]
    public async Task UnknownPermissionsAndDelegatedMessagesCannotCreateRequests()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var run = await StartAsync(db);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AgentPermissionGrants.ProposeAsync(db, run, "qa", "tool", "superuser", default));
        run.DelegationDepth = 1;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AgentPermissionGrants.ProposeAsync(db, run, "qa", "tool", "git", default));
        Assert.False(await db.Approvals.AnyAsync());
    }

    private static async Task<ChatRun> StartAsync(PlatformDb db)
    {
        var chat = new Conversation { AutoApprove = true };
        var message = new Message { ConversationId = chat.Id, Role = "user", Text = "Give QA this permission." };
        db.AddRange(chat, message);
        await db.SaveChangesAsync();
        return new ChatRun { ConversationId = chat.Id, UserMessageId = message.Id, AgentId = PlatformChat.Id };
    }
}
