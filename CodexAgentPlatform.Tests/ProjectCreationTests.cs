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
    public void SavingAStalePermissionSelectionPreservesConcurrentChanges()
    {
        var merged = ToolAccess.ApplySelectionChanges("prior,from-chat", "prior,revoked", new HashSet<string> { "revoked", "manual" });
        Assert.Equal(new[] { "from-chat", "manual" }, ToolAccess.Split(merged).Order(StringComparer.Ordinal));
    }
}
