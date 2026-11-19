using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed partial class Operations
{
    public const string GitHubRepositoryApproval = "GitHubRepositoryCreate";
    public const string GitHubHttpClient = "GitHub";

    private sealed record GitHubRepositoryTarget(string ResourceId, string SourceBinding, string ProjectId,
        string Owner, string Name, bool Private, string Branch, string? WorkId, long? RepositoryId = null)
    {
        public string FullName => $"{Owner}/{Name}";
        public string Address => $"https://github.com/{FullName}.git";
    }

    internal async Task<Approval> RequestGitHubRepositoryAsync(ChatRun run, JsonElement args, CancellationToken ct)
    {
        var owner = (args.String("owner") ?? "").Trim();
        var name = (args.String("name") ?? "").Trim();
        if (!GitHubLogin().IsMatch(owner) || !GitHubRepositoryName().IsMatch(name) ||
            name is "." or ".." || name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide a valid GitHub login and repository name.");
        var isPrivate = args.String("visibility")?.Trim().ToLowerInvariant() switch
        {
            "private" => true,
            "public" => false,
            _ => throw new ArgumentException("Choose private or public visibility.")
        };
        var projectArgument = args.String("projectId")?.Trim() ?? run.ProjectId
            ?? throw new ArgumentException("Choose the platform project for the new repository.");

        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == run.AgentId, ct);
        if (!ToolAccess.AllowsProject(agent, run.ProjectId))
            throw new UnauthorizedAccessException("The current project is not available to this agent.");
        var projectId = await ResourceRegistration.AuthorizedProjectAsync(db, agent, projectArgument, ct);
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId, ct);
        var source = await GetGitHubConnectionAsync(db, agent, project.Id, args.String("sourceId")!, ct);
        var target = new GitHubRepositoryTarget(source.Id, Binding(source), project.Id, owner, name, isPrivate,
            SafeBranch(project.DefaultBranch), run.WorkId);
        ResourceRegistration.ValidateConnection(ProjectGitHubResource(target, source, agent.Id), source);
        var registration = await FindGitHubRegistrationAsync(db, target, ct);

        var history = await db.Approvals.FromSqlInterpolated($"""
            SELECT * FROM "Approvals"
            WHERE "Kind" = {GitHubRepositoryApproval} AND json_valid("Payload")
                AND json_extract("Payload", '$.ProjectId') = {projectId}
                AND json_extract("Payload", '$.Owner') COLLATE NOCASE = {owner}
                AND json_extract("Payload", '$.Name') COLLATE NOCASE = {name}
            """).AsNoTracking().OrderByDescending(a => a.CreatedAtUtc).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var prior in history.Where(a => a.Status == "Executing" ||
            (a.Status is "Pending" or "Approved" && a.ExpiresAtUtc > now)))
        {
            var other = ReadGitHubTarget(prior);
            if (prior.AgentId == agent.Id && prior.ConversationId == run.ConversationId &&
                (other with { Owner = owner, Name = name, RepositoryId = null, WorkId = null }) == (target with { WorkId = null }))
                return prior;
            throw new InvalidOperationException("A GitHub creation request for this repository is already pending or executing.");
        }
        // A retry can finish setup for a repository this platform already created and recorded.
        var receipt = history.Select(ReadGitHubTarget).FirstOrDefault(t => t.RepositoryId != null);
        if (receipt != null)
        {
            if (registration != null && history.Any(a => a.Status == "Completed" && ReadGitHubTarget(a).RepositoryId == receipt.RepositoryId))
                throw new InvalidOperationException("This GitHub repository is already linked to the project.");
            if (receipt.Private != target.Private)
                throw new InvalidOperationException("GitHub already created this repository with a different visibility. Review it on GitHub.");
            target = target with { RepositoryId = receipt.RepositoryId };
        }

        var visibility = isPrivate ? "Private" : "Public";
        var scope = $"{target.FullName} · {visibility} · Project {project.Name} · Branch {target.Branch} · Connection {source.Name}";
        var approval = new Approval
        {
            AgentId = agent.Id, ConversationId = run.ConversationId, Kind = GitHubRepositoryApproval,
            Scope = scope, Payload = JsonSerializer.Serialize(target), ContinuationRunId = run.Id
        };
        db.Approvals.Add(approval);
        db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = run.ConversationId, ProjectId = project.Id,
            WorkId = run.WorkId, Kind = "ApprovalRequested", Detail = scope });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return approval;
    }

    private async Task ExecuteGitHubRepositoryAsync(PlatformDb db, Approval approval, Agent agent, CancellationToken ct)
    {
        if (approval.DecidedBy != PlatformUser.OwnerId)
            throw new InvalidOperationException("Repository creation requires an explicit owner approval.");
        var target = ReadGitHubTarget(approval);
        var source = await RecheckGitHubApprovalAsync(db, target, agent, ct);
        using var http = httpClients?.CreateClient(GitHubHttpClient)
            ?? throw new InvalidOperationException("The GitHub client is not configured.");
        var token = await vault.ReadAsync(source.VaultId!, ct);
        if (string.IsNullOrWhiteSpace(token) || token.AsSpan().ContainsAny('\r', '\n'))
            throw new InvalidOperationException("The saved GitHub token is invalid.");
        var account = await SendGitHubAsync(http, token, HttpMethod.Get, "user", ct);
        if (!target.Owner.Equals(account.String("login"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The saved token does not belong to GitHub account {target.Owner}.");

        var apiPath = $"repos/{target.FullName}";
        var repository = target.RepositoryId.HasValue
            ? await SendGitHubAsync(http, token, HttpMethod.Get, apiPath, ct)
            : await SendGitHubAsync(http, token, HttpMethod.Post, "user/repos", ct,
                new { name = target.Name, @private = target.Private, auto_init = true });
        var repositoryId = repository.Number("id");
        var visibility = repository.Get("private").ValueKind;
        if (repositoryId <= 0 || (target.RepositoryId is { } priorId && priorId != repositoryId) ||
            !target.FullName.Equals(repository.String("full_name"), StringComparison.OrdinalIgnoreCase) ||
            visibility != (target.Private ? JsonValueKind.True : JsonValueKind.False))
            throw new InvalidOperationException("GitHub returned a repository that does not match the approved name, identity or visibility.");

        // Save the GitHub identity before creating refs or writing the local project link.
        target = target with { RepositoryId = repositoryId };
        approval.Payload = JsonSerializer.Serialize(target);
        approval.Result = $"GitHub created {target.FullName}. If setup fails, request the same repository again to continue.";
        await db.SaveChangesAsync(CancellationToken.None);

        var defaultBranch = SafeBranch(repository.String("default_branch"));
        await EnsureGitHubBranchAsync(http, token, apiPath, defaultBranch, target.Branch, ct);

        await using var registration = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await registration.Database.BeginTransactionAsync(ct);
        source = await RecheckGitHubApprovalAsync(registration, target, agent, ct);
        if (await FindGitHubRegistrationAsync(registration, target, ct) is null)
        {
            var resource = ProjectGitHubResource(target, source, agent.Id);
            registration.Resources.Add(resource);
            registration.Events.Add(new Activity { AgentId = agent.Id, ConversationId = approval.ConversationId,
                ProjectId = target.ProjectId, WorkId = target.WorkId, Kind = "ResourceCreated", Detail = resource.Name });
            await registration.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        approval.Result = $"{target.Address} was initialized and linked to the project on branch {target.Branch}.";
    }

    private static async Task<Resource> RecheckGitHubApprovalAsync(PlatformDb db, GitHubRepositoryTarget target, Agent agent, CancellationToken ct)
    {
        var source = await GetGitHubConnectionAsync(db, agent, target.ProjectId, target.ResourceId, ct);
        var branch = await db.Projects.Where(p => p.Id == target.ProjectId).Select(p => p.DefaultBranch).SingleOrDefaultAsync(ct);
        if (branch != target.Branch || Binding(source) != target.SourceBinding)
            throw new InvalidOperationException("The GitHub connection or project branch changed after the approval was requested.");
        ResourceRegistration.ValidateConnection(ProjectGitHubResource(target, source, agent.Id), source);
        await FindGitHubRegistrationAsync(db, target, ct);
        return source;
    }

    private static async Task<Resource> GetGitHubConnectionAsync(PlatformDb db, Agent agent, string projectId, string id, CancellationToken ct)
    {
        var current = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agent.Id, ct);
        if (current is null || !ToolAccess.Has(current, ToolAccess.CreateGitHubRepositories) ||
            !ToolAccess.Has(current, ToolAccess.GlobalResources) || !ToolAccess.AllowsProject(current, projectId))
            throw new InvalidOperationException("The agent needs Create GitHub repositories, Global resources and access to the target project.");
        var source = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.IsGlobal && r.ProjectId == null &&
            r.Kind == ResourceKind.GitRepository && r.ConnectionSourceId == null && r.VaultId != null, ct);
        if (source is null || new Uri(GitAddress(source)) is not { Host: "github.com", IsDefaultPort: true })
            throw new InvalidOperationException("Select a global GitHub resource with a saved HTTPS token.");
        return source;
    }

    private static async Task<Resource?> FindGitHubRegistrationAsync(PlatformDb db, GitHubRepositoryTarget target, CancellationToken ct)
    {
        var addressWithoutSuffix = target.Address[..^4];
        var matches = await db.Resources.AsNoTracking().Where(r => r.ProjectId == target.ProjectId &&
            (EF.Functions.Collate(r.Name, "NOCASE") == target.Name || (r.Kind == ResourceKind.GitRepository &&
                (EF.Functions.Collate(r.Address.TrimEnd('/'), "NOCASE") == target.Address ||
                 EF.Functions.Collate(r.Address.TrimEnd('/'), "NOCASE") == addressWithoutSuffix)))).Take(2).ToListAsync(ct);
        if (matches.Count == 0) return null;
        var resource = matches[0];
        var address = resource.Address.TrimEnd('/');
        if (matches.Count == 1 && resource.Kind == ResourceKind.GitRepository && !resource.IsGlobal &&
            resource.ConnectionSourceId == target.ResourceId && resource.VaultId is null &&
            (address.Equals(target.Address, StringComparison.OrdinalIgnoreCase) ||
                address.Equals(addressWithoutSuffix, StringComparison.OrdinalIgnoreCase)))
            return resource;
        throw new InvalidOperationException("The project already has a conflicting resource with this name or URL.");
    }

    private static Resource ProjectGitHubResource(GitHubRepositoryTarget target, Resource source, string agentId) =>
        new() { Name = target.Name, Address = target.Address, Kind = ResourceKind.GitRepository,
            ProjectId = target.ProjectId, CreatedByAgentId = agentId, ConnectionSourceId = source.Id,
            Username = source.Username, Production = source.Production };

    private static GitHubRepositoryTarget ReadGitHubTarget(Approval approval) =>
        JsonSerializer.Deserialize<GitHubRepositoryTarget>(approval.Payload)
        ?? throw new InvalidOperationException("The GitHub approval is incomplete.");

    private static async Task EnsureGitHubBranchAsync(HttpClient http, string token, string repo, string defaultBranch, string branch, CancellationToken ct)
    {
        string BranchPath(string name) => $"{repo}/git/ref/heads/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";
        JsonElement initial = default;
        for (var i = 0; i < 4; i++)
        {
            if (i != 0) await Task.Delay(TimeSpan.FromMilliseconds(350 * i), ct);
            initial = await SendGitHubAsync(http, token, HttpMethod.Get, BranchPath(defaultBranch), ct, refMayBePending: true);
            if (initial.ValueKind != JsonValueKind.Undefined) break;
        }
        var sha = initial.Get("object").String("sha");
        if (sha is null || !CommitHash().IsMatch(sha))
            throw new InvalidOperationException("GitHub did not return the initial commit. Check the token's Contents permission.");
        if (branch == defaultBranch) return;
        var existing = await SendGitHubAsync(http, token, HttpMethod.Get, BranchPath(branch), ct, refMayBePending: true);
        if (existing.ValueKind == JsonValueKind.Undefined)
            await SendGitHubAsync(http, token, HttpMethod.Post, $"{repo}/git/refs", ct, new { @ref = $"refs/heads/{branch}", sha });
    }

    private static async Task<JsonElement> SendGitHubAsync(HttpClient http, string token, HttpMethod method, string path,
        CancellationToken ct, object? content = null, bool refMayBePending = false)
    {
        using var request = new HttpRequestMessage(method, $"https://api.github.com/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("CodexAgentPlatform/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (content != null) request.Content = JsonContent.Create(content);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (refMayBePending && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return default;
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "GitHub rejected the saved token (401).",
                    HttpStatusCode.Forbidden => "GitHub denied this operation (403). Check the saved token's permissions.",
                    HttpStatusCode.UnprocessableEntity when path == "user/repos" =>
                        "GitHub rejected repository creation (422). Check the name and whether that repository already exists.",
                    _ => $"GitHub returned HTTP {(int)response.StatusCode} for the approved operation."
                });
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("GitHub timed out. Check the repository in GitHub before trying again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw new InvalidOperationException("The GitHub response could not be read. Check whether the repository was created before trying again.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex GitHubLogin();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex GitHubRepositoryName();
}
