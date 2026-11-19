using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class ProjectCreationTests
{
    [Fact]
    public async Task ProjectCreationIsOptInAndAConflictingNameDoesNotGrantAccess()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var prior = await ProjectStore.CreateAsync(db, "Existing", " release ", null, default);
        Assert.Equal("release", prior.DefaultBranch);
        var branchError = await Assert.ThrowsAsync<ArgumentException>(() =>
            ProjectStore.CreateAsync(db, "Invalid default branch", "release/../main", null, default));
        Assert.Contains("default Git branch", branchError.Message);
        var developers = db.Agents.Where(a => a.Id == "developer");
        var developer = await developers.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("project_create", JsonSerializer.Serialize(AgentTools.Describe(developer)));
        var tools = new AgentTools(storage, new MemoryStore(storage, storage.Paths),
            new Operations(storage, storage.Paths, new Vault(storage.Paths, null)), storage.Paths);
        var run = new ChatRun { AgentId = developer.Id, ConversationId = "project-creation-chat" };
        Task<object?> Create(string name) =>
            tools.RunAsync("project_create", JsonSerializer.SerializeToElement(new { name }), run, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create("Not allowed"));
        await developers.ExecuteUpdateAsync(s => s.SetProperty(a => a.AllowedTools, ToolAccess.CreateProjects));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(" Existing "));
        Assert.Empty(ToolAccess.Split((await developers.AsNoTracking().SingleAsync()).AllowedProjects));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(new string('x', 81)));
        Assert.Empty(await db.Events.Where(e => e.Kind == "ProjectCreated").ToListAsync());

        await developers.ExecuteUpdateAsync(s => s.SetProperty(a => a.AllowedProjects, prior.Id));
        await Create(" New project ");
        var created = await db.Projects.SingleAsync(p => p.Name == "New project");
        Assert.Equal("main", created.DefaultBranch);
        Assert.Equal(new[] { prior.Id, created.Id }, ToolAccess.Split((await developers.AsNoTracking().SingleAsync()).AllowedProjects));
        var evt = await db.Events.SingleAsync(e => e.Kind == "ProjectCreated");
        Assert.Equal((created.Id, developer.Id, run.ConversationId), (evt.ProjectId, evt.AgentId, evt.ConversationId));
        Assert.All(await db.Agents.Where(a => a.Id != developer.Id).ToListAsync(),
            a => Assert.DoesNotContain(created.Id, ToolAccess.Split(a.AllowedProjects)));
    }

    [Fact]
    public async Task AgentCreatedProjectCanBeDeletedWhenItOnlyHasPermissionsAndHistory()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var other = await ProjectStore.CreateAsync(db, "Keep", null, null, default);
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        architect.AllowedTools = ToolAccess.CreateProjects;
        architect.AllowedProjects = other.Id;
        var conversation = new Conversation();
        db.Conversations.Add(conversation);
        var message = new Message { ConversationId = conversation.Id, Role = "user", Text = "Give QA access." };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var run = new ChatRun { AgentId = architect.Id, ConversationId = conversation.Id, UserMessageId = message.Id };
        var project = await ProjectStore.CreateAsync(db, "Temporary project", null, run, default);
        await AgentPermissionGrants.ProposeAsync(db, run, "qa", "project", project.Id, default);

        Assert.Null(await ProjectStore.DeleteAsync(db, project.Id, project.Name, default));
        Assert.False(await db.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.Equal(new[] { other.Id }, ToolAccess.Split(architect.AllowedProjects));
        Assert.Contains(await db.Events.ToListAsync(), e => e.Kind == "ProjectCreated" && e.ProjectId == project.Id);
        Assert.Equal("Declined", await db.Approvals.Select(a => a.Status).SingleAsync());
        Assert.Null((await new ChatStore(storage, new ChatUpdates()).ReadAsync(conversation.Id, 10, default)).ProjectId);
    }

    [Fact]
    public async Task ProjectDeletionReportsLiveLinksAndKeepsPermissions()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = await ProjectStore.CreateAsync(db, "Project with work", null, null, default);
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        architect.AllowedProjects = project.Id;
        db.Resources.Add(new Resource { Name = "Repository", ProjectId = project.Id });
        db.Work.Add(new WorkItem { Title = "Implementation", ProjectId = project.Id });
        await db.SaveChangesAsync();

        var reason = await ProjectStore.DeleteAsync(db, project.Id, project.Name, default);
        Assert.Contains("linked resources (1)", reason);
        Assert.Contains("work items (1)", reason);
        Assert.True(await db.Projects.AnyAsync(p => p.Id == project.Id));
        Assert.Contains(project.Id, ToolAccess.Split(architect.AllowedProjects));
    }

    [Fact]
    public void SavingAStalePermissionSelectionPreservesConcurrentChanges()
    {
        var merged = ToolAccess.ApplySelectionChanges("prior,from-chat", "prior,revoked", new HashSet<string> { "revoked", "manual" });
        Assert.Equal(new[] { "from-chat", "manual" }, ToolAccess.Split(merged).Order(StringComparer.Ordinal));
    }
}
