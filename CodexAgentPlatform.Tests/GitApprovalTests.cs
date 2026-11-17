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
            Kind = ResourceKind.GitRepository, Address = "https://example.invalid/repo.git", Production = true };
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
        Assert.StartsWith("ÜRETİM", approval.Scope);
        Assert.False(await operations.ExecuteNextAsync(default));
        await operations.DecideAsync(approval.Id, true, PlatformUser.OwnerId, default);
        await GitAsync(worktree, "-c", "user.name=Platform test", "-c", "user.email=testing@localhost",
            "commit", "--allow-empty", "-m", "unapproved commit");

        Assert.True(await operations.ExecuteNextAsync(default));
        var result = await db.Approvals.AsNoTracking().SingleAsync(x => x.Id == approval.Id);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("onaydan sonra değişti", result.Result);
        Assert.False(await operations.ExecuteNextAsync(default));
    }

    private static async Task GitAsync(string directory, params string[] arguments)
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
