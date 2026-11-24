using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class GitHubRepositoryTests
{
    private const string ToolName = "github_request_create_repository";
    private const string Token = "fake-test-token";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedGitHubToolInitializesTheConfiguredBranchAndLinksTheVaultConnection(bool alreadyRegistered)
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var (agent, project, source, vault) = await SetupAsync(storage, db);
        var chat = new Conversation { AutoApprove = true };
        db.Conversations.Add(chat);
        if (alreadyRegistered)
        {
            db.Resources.Add(new Resource { Name = "Existing link", WorkingBranch = "master", Kind = ResourceKind.GitRepository,
                Address = "https://github.com/octocat/website", ProjectId = project.Id,
                ConnectionSourceId = source.Id, CreatedByAgentId = agent.Id });
        }
        await db.SaveChangesAsync();
        var api = new GitHubApi();
        var operations = new Operations(storage, storage.Paths, vault, api);
        var tools = new AgentTools(storage, new MemoryStore(storage, storage.Paths), operations, storage.Paths);
        var run = new ChatRun { AgentId = agent.Id, ConversationId = chat.Id, ProjectId = project.Id };
        Task<object?> Call() => tools.RunAsync(ToolName, Args(source), run, default);

        Assert.DoesNotContain(ToolName, JsonSerializer.Serialize(AgentTools.Describe(agent)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Call);
        agent.AllowedTools += "," + ToolAccess.CreateGitHubRepositories;
        await db.SaveChangesAsync();
        Assert.Contains(ToolName, JsonSerializer.Serialize(AgentTools.Describe(agent)));
        var request = Assert.IsType<ApprovalReply>(await Call());
        Assert.Equal(request.Id, Assert.IsType<ApprovalReply>(await Call()).Id);
        Assert.Equal("Pending", request.Status);
        Assert.Contains("octocat/website · Private · Project Website · Branch master", request.Scope);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(await db.Approvals.AsNoTracking().SingleAsync()));
        Assert.False(await operations.ExecuteNextAsync(default));
        Assert.Empty(api.Calls);

        await operations.DecideAsync(request.Id, true, PlatformUser.OwnerId, default);
        Assert.True(await operations.ExecuteNextAsync(default));
        Assert.Equal("Completed", (await db.Approvals.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(new[] { "GET /user", "POST /user/repos", "GET /repos/octocat/website/git/ref/heads/main",
            "GET /repos/octocat/website/git/ref/heads/master", "POST /repos/octocat/website/git/refs" }, api.Calls);
        var registered = await db.Resources.AsNoTracking().SingleAsync(r => r.ProjectId == project.Id);
        var expectedAddress = alreadyRegistered ? "https://github.com/octocat/website" : "https://github.com/octocat/website.git";
        Assert.Equal((expectedAddress, source.Id, agent.Id),
            (registered.Address, registered.ConnectionSourceId, registered.CreatedByAgentId));
        Assert.Null(registered.VaultId);
        await Assert.ThrowsAsync<InvalidOperationException>(Call);
        Assert.Equal(1, api.CreateCount);
    }

    [Fact]
    public async Task BranchFailureCanBeRetriedAndAnAccountMismatchCannotCreateARepository()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var (agent, project, source, vault) = await SetupAsync(storage, db);
        agent.AllowedTools += "," + ToolAccess.CreateGitHubRepositories;
        await db.SaveChangesAsync();
        var api = new GitHubApi { Login = "another-user", RejectBranch = true };
        var operations = new Operations(storage, storage.Paths, vault, api);
        var run = new ChatRun { AgentId = agent.Id, ConversationId = "chat", ProjectId = project.Id };
        async Task<Approval> ApproveAndExecuteAsync()
        {
            var request = await operations.RequestGitHubRepositoryAsync(run, Args(source), default);
            await operations.DecideAsync(request.Id, true, PlatformUser.OwnerId, default);
            Assert.True(await operations.ExecuteNextAsync(default));
            return await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == request.Id);
        }

        var revoked = await operations.RequestGitHubRepositoryAsync(run, Args(source), default);
        await operations.DecideAsync(revoked.Id, true, PlatformUser.OwnerId, default);
        agent.AllowedTools = ToolAccess.GlobalResources;
        await db.SaveChangesAsync();
        Assert.True(await operations.ExecuteNextAsync(default));
        Assert.Empty(api.Calls);
        Assert.Equal("Failed", (await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == revoked.Id)).Status);
        agent.AllowedTools += "," + ToolAccess.CreateGitHubRepositories;
        await db.SaveChangesAsync();

        Assert.Equal("Failed", (await ApproveAndExecuteAsync()).Status);
        Assert.Equal(new[] { "GET /user" }, api.Calls);
        api.Login = "octocat";
        var incomplete = await ApproveAndExecuteAsync();
        Assert.Equal("Failed", incomplete.Status);
        Assert.Contains("GitHub created octocat/website", incomplete.Result);
        Assert.False(await db.Resources.AnyAsync(r => r.ProjectId == project.Id));

        api.RejectBranch = false;
        Assert.Equal("Completed", (await ApproveAndExecuteAsync()).Status);
        Assert.Equal(1, api.CreateCount);
        Assert.Contains("GET /repos/octocat/website", api.Calls);
        Assert.Single(await db.Resources.Where(r => r.ProjectId == project.Id).ToListAsync());
    }

    private static JsonElement Args(Resource source) => JsonSerializer.SerializeToElement(
        new { workingBranch = "master", sourceId = source.Id, owner = "octocat", name = "website", visibility = "private" });

    private static async Task<(Agent Agent, Project Project, Resource Source, Vault Vault)> SetupAsync(TestStorage storage, PlatformDb db)
    {
        var key = Path.Combine(storage.Paths.Data, "vault-test-key");
        await File.WriteAllTextAsync(key, Convert.ToBase64String(new byte[32]));
        var vault = new Vault(storage.Paths, key);
        var source = new Resource { IsGlobal = true, Name = "GitHub", Kind = ResourceKind.GitRepository,
            Address = "https://github.com/octocat", VaultId = await vault.StoreAsync(Token, default) };
        var project = new Project { Name = "Website" };
        var agent = await db.Agents.SingleAsync(a => a.Id == "architect");
        agent.AllowedTools = ToolAccess.GlobalResources;
        agent.AllowedProjects = project.Id;
        db.AddRange(source, project);
        await db.SaveChangesAsync();
        return (agent, project, source, vault);
    }

    private sealed class GitHubApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Calls { get; } = [];
        public int CreateCount { get; private set; }
        public string Login { get; set; } = "octocat";
        public bool RejectBranch { get; set; }
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(Operations.GitHubHttpClient, name);
            return new HttpClient(this, disposeHandler: false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.github.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            var call = $"{request.Method} {request.RequestUri.AbsolutePath}";
            Calls.Add(call);
            var sha = new string('a', 40);
            var repository = new { id = 123, full_name = "octocat/website", @private = true, default_branch = "main" };

            switch (call)
            {
                case "GET /user": return Response(HttpStatusCode.OK, new { login = Login });
                case "POST /user/repos":
                    var create = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.True(create.True("auto_init"));
                    Assert.True(create.True("private"));
                    Assert.Equal("website", create.String("name"));
                    CreateCount++;
                    return Response(HttpStatusCode.Created, repository);
                case "GET /repos/octocat/website": return Response(HttpStatusCode.OK, repository);
                case "GET /repos/octocat/website/git/ref/heads/main":
                    return Response(HttpStatusCode.OK, new { @ref = "refs/heads/main", @object = new { sha } });
                case "GET /repos/octocat/website/git/ref/heads/master": return new HttpResponseMessage(HttpStatusCode.NotFound);
                case "POST /repos/octocat/website/git/refs":
                    var branch = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.Equal("refs/heads/master", branch.String("ref"));
                    Assert.Equal(sha, branch.String("sha"));
                    return RejectBranch ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                        : Response(HttpStatusCode.Created, new { @ref = "refs/heads/master" });
                default: throw new InvalidOperationException($"Unexpected request {call}");
            }
        }

        private static HttpResponseMessage Response(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body) };
    }
}
