using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class ResourceCreationTests
{
    [Fact]
    public void WorkingBranchIsOptionalAndValidatedForGitResources()
    {
        Assert.Null(ResourceRegistration.NormalizeWorkingBranch(ResourceKind.GitRepository, " "));
        Assert.Equal("release/v1", ResourceRegistration.NormalizeWorkingBranch(ResourceKind.GitRepository, " release/v1 "));
        Assert.Throws<ArgumentException>(() => ResourceRegistration.NormalizeWorkingBranch(ResourceKind.GitRepository, "release/../main"));
        Assert.Throws<ArgumentException>(() => ResourceRegistration.NormalizeWorkingBranch(ResourceKind.SshServer, "main"));
    }

    [Fact]
    public async Task RegisteringGitUsesTheSavedConnectionAndIsIdempotent()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "yaseminwebsite" };
        var source = new Resource { Name = "Github Adresim", Kind = ResourceKind.GitRepository, IsGlobal = true,
            Address = "https://github.com/UnholyWar", Username = "UnholyWar", VaultId = Guid.NewGuid().ToString("N") };
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        architect.AllowedProjects = project.Id;
        db.AddRange(project, source);
        await db.SaveChangesAsync();
        var tools = Tools(storage);
        var run = new ChatRun { AgentId = architect.Id, ConversationId = "chat" };
        var args = JsonSerializer.SerializeToElement(new { projectId = project.Name, name = "Project repository",
            sourceId = source.Id, workingBranch = " master ", address = "https://github.com/UnholyWar/yaseminwebsite" });
        Task<object?> Create() => tools.RunAsync("resource_create", args, run, default);
        Assert.DoesNotContain("resource_create", JsonSerializer.Serialize(AgentTools.Describe(architect)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Create);
        architect.AllowedTools += "," + ToolAccess.CreateResources;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Create);
        architect.AllowedTools += "," + ToolAccess.GlobalResources;
        await db.SaveChangesAsync();
        Assert.Contains("resource_create", JsonSerializer.Serialize(AgentTools.Describe(architect, true, true)));
        Assert.True(JsonSerializer.SerializeToElement(await Create()).True("created"));
        Assert.False(JsonSerializer.SerializeToElement(await Create()).True("created"));
        var resource = await db.Resources.SingleAsync(r => r.ProjectId == project.Id);
        Assert.Equal("master", resource.WorkingBranch);
        await tools.RunAsync("resource_create", JsonSerializer.SerializeToElement(new { projectId = project.Id,
            name = "Second repository", sourceId = source.Id, workingBranch = "main",
            address = "https://github.com/UnholyWar/second" }), run, default);
        Assert.Equal("main", (await db.Resources.SingleAsync(r => r.Name == "Second repository")).WorkingBranch);
        Assert.Equal(source.Id, resource.ConnectionSourceId);
        Assert.Null(resource.VaultId);
        Assert.Equal(architect.Id, (await db.Events.SingleAsync(e => e.Kind == "ResourceCreated" && e.Detail == resource.Name)).AgentId);
        var metadata = await tools.RunAsync("resource_get", JsonSerializer.SerializeToElement(new { id = resource.Id }), run, default);
        Assert.DoesNotContain(source.VaultId!, JsonSerializer.Serialize(metadata));

        var developer = await db.Agents.SingleAsync(a => a.Id == "developer");
        developer.AllowedProjects = project.Id;
        await db.SaveChangesAsync();
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        // Reaching the vault proves a project agent uses the saved token, without making a network request.
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.GitAsync(developer, resource.Id, Guid.NewGuid().ToString("N"), "prepare", null, default));
        Assert.Contains("vault key", unavailable.Message);
        source.VaultId = null;
        await db.SaveChangesAsync();
        var revoked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.GitAsync(developer, resource.Id, Guid.NewGuid().ToString("N"), "prepare", null, default));
        Assert.Contains("saved connection is unavailable", revoked.Message);
    }

    [Theory]
    [InlineData("https://example.org/UnholyWar/yaseminwebsite")]
    [InlineData("https://github.com:8443/UnholyWar/yaseminwebsite")]
    [InlineData("https://github.com/AnotherOwner/yaseminwebsite")]
    [InlineData("https://github.com/UnholyWar/repo/extra")]
    public void GitHubConnectionsStayWithinTheirRepositoryScope(string address)
    {
        var source = new Resource { Kind = ResourceKind.GitRepository, IsGlobal = true, VaultId = "stored",
            Address = "https://github.com/UnholyWar" };
        Assert.Throws<InvalidOperationException>(() => ResourceRegistration.ValidateConnection(
            new Resource { Kind = ResourceKind.GitRepository, ProjectId = "project", Address = address }, source));
    }

    [Fact]
    public async Task StandaloneResourcesRespectTheActiveProjectAndGlobalPermissions()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Primary" };
        var other = new Project { Name = "Other" };
        var agent = await db.Agents.SingleAsync(a => a.Id == "developer");
        agent.AllowedProjects = project.Id;
        agent.AllowedTools = ToolAccess.CreateResources;
        db.AddRange(project, other);
        await db.SaveChangesAsync();
        var tools = Tools(storage);
        var run = new ChatRun { AgentId = agent.Id, ProjectId = project.Id, ConversationId = "chat" };
        Task<object?> Create(object args) => tools.RunAsync("resource_create", JsonSerializer.SerializeToElement(args), run, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(new { name = "Wrong project", projectId = other.Id }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create(new { name = "Global", scope = "global" }));
        await Create(new { name = "API", kind = "ApiConnection", address = "https://api.example.org" });
        Assert.Equal(project.Id, (await db.Resources.SingleAsync()).ProjectId);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(new { name = "API", kind = "Environment", address = "staging" }));
        Assert.Single(await db.Resources.ToListAsync());
    }

    private static AgentTools Tools(TestStorage storage) => new(storage, new MemoryStore(storage, storage.Paths),
        new Operations(storage, storage.Paths, new Vault(storage.Paths, null)), storage.Paths);

    [Theory]
    [InlineData(ResourceKind.GitRepository)]
    [InlineData(ResourceKind.ApiConnection)]
    public async Task AnAgentCanCopyANamedConnectionToTwoProjectsAndRetargetOnlyItsOwnCopy(ResourceKind kind)
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var one = new Project { Name = "Atlas" };
        var two = new Project { Name = "Borealis" };
        var origin = new Resource { Name = "Team connection", Kind = kind, Address = "https://team.example.org/groups/team",
            IsGlobal = true, VaultId = Guid.NewGuid().ToString("N") };
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        var other = await db.Agents.SingleAsync(a => a.Id == "developer");
        architect.AllowedTools = other.AllowedTools = $"{ToolAccess.CreateResources},{ToolAccess.GlobalResources},{ToolAccess.ProjectResources}";
        architect.AllowedProjects = other.AllowedProjects = $"{one.Id},{two.Id}";
        db.AddRange(one, two, origin);
        await db.SaveChangesAsync();
        var tools = Tools(storage);
        var run = new ChatRun { AgentId = architect.Id, ConversationId = "chat" };
        Task<object?> Call(string tool, object args) => tools.RunAsync(tool, JsonSerializer.SerializeToElement(args), run, default);
        run.ProjectId = one.Id;
        foreach (var project in new[] { one, two })
        {
            await Call("resource_create", new { name = "App", projectId = project.Name, sourceId = origin.Id, address = origin.Address + "/" + project.Name });
        }
        run.ProjectId = two.Id;
        var copy = await db.Resources.AsNoTracking().SingleAsync(r => r.ProjectId == one.Id);
        var previousFolder = kind == ResourceKind.GitRepository ? Operations.CheckoutFolder(copy) : null;
        var newAddress = origin.Address + "/renamed";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call("resource_update", new { id = copy.Id, address = newAddress }));
        run.ProjectId = one.Id;
        run.AgentId = other.Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call("resource_update", new { id = copy.Id, address = newAddress }));
        run.AgentId = architect.Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Call("resource_update", new { id = origin.Id, address = newAddress }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call("resource_update", new { id = copy.Id, address = "https://elsewhere.example/project" }));
        Assert.True(JsonSerializer.SerializeToElement(await Call("resource_update", new { id = copy.Id, name = "Renamed", address = newAddress })).True("updated"));
        copy = await db.Resources.AsNoTracking().SingleAsync(r => r.Id == copy.Id);
        Assert.Equal(("Renamed", newAddress, origin.Id, architect.Id), (copy.Name, copy.Address, copy.ConnectionSourceId, copy.CreatedByAgentId));
        if (kind == ResourceKind.GitRepository) Assert.NotEqual(previousFolder, Operations.CheckoutFolder(copy));
        Assert.Equal(origin.Address + "/Borealis", await db.Resources.Where(r => r.ProjectId == two.Id).Select(r => r.Address).SingleAsync());
        Assert.Equal("https://team.example.org/groups/team", await db.Resources.Where(r => r.Id == origin.Id).Select(r => r.Address).SingleAsync());
        Assert.Single(await db.Events.Where(e => e.Kind == "ResourceUpdated").ToListAsync());
        var secondConnection = new Resource { Name = "Another authorized account", Kind = kind, IsGlobal = true,
            Address = origin.Address, VaultId = Guid.NewGuid().ToString("N") };
        db.Resources.Add(secondConnection);
        await db.SaveChangesAsync();
        var change = new { id = copy.Id, sourceId = secondConnection.Id };
        Assert.True(JsonSerializer.SerializeToElement(await Call("resource_update", change)).True("updated"));
        Assert.False(JsonSerializer.SerializeToElement(await Call("resource_update", change)).True("updated"));
        Assert.Equal(secondConnection.Id, await db.Resources.Where(r => r.Id == copy.Id).Select(r => r.ConnectionSourceId).SingleAsync());
    }
}
