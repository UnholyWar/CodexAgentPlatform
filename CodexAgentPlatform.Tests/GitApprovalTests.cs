using System.Diagnostics;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class GitApprovalTests
{
    [CodexFact("CAP_TEST_GIT_EXE")]
    public async Task ApprovedPushFailsBeforeNetworkWhenWorktreeHeadHasChanged()
    {
        await using var fixture = await TestStorage.CreateAsync();
        var operations = new Operations(fixture, fixture.Paths, new Vault(fixture.Paths, null));
        var workspaceId = Guid.NewGuid().ToString("N");
        var root = fixture.Paths.Workspace(workspaceId);
        var cache = Path.Combine(fixture.Paths.Workspaces, ".git-cache", "source");
        Directory.CreateDirectory(cache);
        var worktree = Path.Combine(root, "project");
        var project = new Project { Name = "Fake project" };
        var repository = new Resource { ProjectId = project.Id, Name = "Test repository",
            Kind = ResourceKind.GitRepository, WorkingBranch = "main", Address = "https://example.invalid/repo.git", Production = true };
        await using var db = fixture.CreateDbContext();
        var agent = await db.Agents.SingleAsync(x => x.Id == "developer");
        agent.AllowedProjects = project.Id;
        db.AddRange(project, repository);
        await db.SaveChangesAsync();

        await GitAsync(cache, "init", "-b", "main");
        await GitAsync(cache, "-c", "user.name=Platform test", "-c", "user.email=testing@localhost",
            "commit", "--allow-empty", "-m", "approved commit");
        await GitAsync(cache, "worktree", "add", "-b", "agent/test", worktree, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(cache, ".git", "platform-resource"), repository.Id);

        var approval = await operations.RequestAsync(agent, "test-chat", repository.Id, "GitPush",
            "push", workspaceId, "main", default);
        Assert.Equal("Pending", approval.Status);
        Assert.StartsWith("PRODUCTION", approval.Scope);
        Assert.False(await operations.ExecuteNextAsync(default));
        await operations.DecideAsync(approval.Id, true, PlatformUser.OwnerId, default);
        await GitAsync(worktree, "-c", "user.name=Platform test", "-c", "user.email=testing@localhost",
            "commit", "--allow-empty", "-m", "unapproved commit");

        Assert.True(await operations.ExecuteNextAsync(default));
        var result = await db.Approvals.AsNoTracking().SingleAsync(x => x.Id == approval.Id);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("changed after approval", result.Result);
        Assert.False(await operations.ExecuteNextAsync(default));

        var chat = new Conversation { AutoApprove = true };
        repository.Production = false;
        db.Conversations.Add(chat);
        await db.SaveChangesAsync();
        await GitAsync(worktree, "switch", "-c", $"agent/{workspaceId}");
        var scoped = await operations.RequestAsync(agent, chat.Id, repository.Id, "GitPush", "push", workspaceId, null, default);
        Assert.Equal("Approved", scoped.Status);
        Assert.Equal(Operations.AutomaticDecision, scoped.DecidedBy);
        var main = await operations.RequestAsync(agent, chat.Id, repository.Id, "GitPush", "push", workspaceId, "main", default);
        Assert.Equal("Pending", main.Status);
    }

    [CodexFact("CAP_TEST_GIT_EXE")]
    public async Task ProjectInspectionReadsOnlyCommittedFilesInTheRegisteredRepository()
    {
        await using var fixture = await TestStorage.CreateAsync();
        await using var db = fixture.CreateDbContext();
        var project = new Project { Name = "Inspection" };
        var resource = new Resource { Name = "Source", ProjectId = project.Id, Kind = ResourceKind.GitRepository };
        var developer = await db.Agents.SingleAsync(a => a.Id == "developer");
        developer.AllowedProjects = project.Id;
        db.AddRange(project, resource);
        await db.SaveChangesAsync();
        var workspace = Guid.NewGuid().ToString("N");
        var checkout = Path.Combine(fixture.Paths.Workspace(workspace), "project");
        var clone = Path.Combine(fixture.Paths.Workspaces, ".git-cache", "inspection");
        Directory.CreateDirectory(Path.Combine(clone, "docs"));
        await GitAsync(clone, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(clone, "docs", "notes.md"), "committed text");
        await File.WriteAllTextAsync(Path.Combine(clone, ".env"), "excluded");
        await GitAsync(clone, "add", "-f", "--", "docs/notes.md", ".env");
        await GitAsync(clone, "-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "Seed");
        await GitAsync(clone, "worktree", "add", "--detach", checkout, "HEAD");
        await File.WriteAllTextAsync(Path.Combine(clone, ".git", "platform-resource"), resource.Id);
        await File.WriteAllTextAsync(Path.Combine(checkout, "docs", "notes.md"), "working copy");

        var ops = new Operations(fixture, fixture.Paths, new Vault(fixture.Paths, null));
        Task<object> Inspect(string op, string? path) => ops.GitAsync(developer, resource.Id, workspace, op, path, default);
        await Inspect("prepare", null);
        string Output(object page) => System.Text.Json.JsonSerializer.SerializeToElement(page).GetProperty("output").GetString()!;
        Assert.Equal("docs", Output(await Inspect("list", null)));
        Assert.Equal("notes.md", Output(await Inspect("list", "docs")));
        Assert.Equal("committed text", Output(await Inspect("read", "docs/notes.md")));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect("read", ".env"));
        await Assert.ThrowsAsync<ArgumentException>(() => Inspect("read", "../outside"));
        await File.WriteAllTextAsync(Path.Combine(clone, ".git", "platform-resource"), "another-resource");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Inspect("read", "docs/notes.md"));
    }

    internal static async Task GitAsync(string directory, params string[] arguments)
    {
        var options = new ProcessStartInfo(Environment.GetEnvironmentVariable("CAP_TEST_GIT_EXE")!)
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) options.ArgumentList.Add(argument);
        using var process = Process.Start(options)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await stdout;
        Assert.True(process.ExitCode == 0, await stderr);
    }
}
