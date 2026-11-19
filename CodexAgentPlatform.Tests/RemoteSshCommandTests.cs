using System.Diagnostics;
using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class RemoteSshCommandTests
{
    [Fact]
    public async Task RemoteShellUsesOneExplicitApprovalAndRechecksTheHostBeforeDispatch()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var key = Path.Combine(storage.Paths.Data, "test-vault-key");
        await File.WriteAllTextAsync(key, Convert.ToBase64String(new byte[32]));
        var vault = new Vault(storage.Paths, key);
        var operations = new Operations(storage, storage.Paths, vault);
        var tools = new AgentTools(storage, new MemoryStore(storage, storage.Paths), operations, storage.Paths);
        var host = new Resource { Kind = ResourceKind.SshServer, Name = "Sunucum", IsGlobal = true, Address = "host.invalid",
            Username = "deployer", Port = 2222, VaultId = await vault.StoreAsync("fixture-key", default) };
        var conversation = new Conversation { AutoApprove = true };
        var devops = await db.Agents.SingleAsync(a => a.Id == "devops");
        db.AddRange(host, conversation);
        await db.SaveChangesAsync();
        var run = new ChatRun { AgentId = devops.Id, ConversationId = conversation.Id };
        const string script = "cd /srv/site\ndocker compose pull\ndocker compose up -d";
        var args = JsonSerializer.SerializeToElement(new { id = host.Id, command = script });
        Task<object?> Propose() => tools.RunAsync("ssh_request_command", args, run, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(Propose);
        devops.AllowedTools += "," + ToolAccess.GlobalResources;
        await db.SaveChangesAsync();
        Assert.Contains("ssh_request_command", JsonSerializer.Serialize(AgentTools.Describe(devops)));
        var reply = Assert.IsType<ApprovalReply>(await Propose());
        Assert.Equal(reply.Id, Assert.IsType<ApprovalReply>(await Propose()).Id);
        Assert.Equal("Pending", reply.Status);
        var approval = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.False(approval.AutoEligible);
        Assert.Equal(script, Operations.SshCommandScript(approval));
        Assert.Contains("deployer@host.invalid:2222", reply.Scope);
        Assert.False(await operations.ExecuteNextAsync(default));

        await operations.DecideAsync(reply.Id, true, PlatformUser.OwnerId, default);
        host.Port = 22;
        await db.SaveChangesAsync();
        Assert.True(await operations.ExecuteNextAsync(default));
        var rejected = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.Equal("Failed", rejected.Status);
        Assert.Contains("SSH connection changed", rejected.Result);
    }

    [Fact]
    public async Task StartupRetainsRemoteCommandTrackingAndAnExpiredJobIsNeverDispatchedAgain()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var approval = new Approval { Kind = Operations.SshCommandApproval, Status = "Executing", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync();
        await storage.Startup.InitializeAsync(default);
        Assert.Equal("Executing", (await db.Approvals.AsNoTracking().SingleAsync()).Status);
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        Assert.True(await operations.ExecuteNextAsync(default));
        Assert.Equal("Interrupted", (await db.Approvals.AsNoTracking().SingleAsync()).Status);
    }

    [CodexFact("CAP_TEST_BASH_EXE")]
    public async Task DetachedCommandPreservesExactQuotingAndReportsTheExitCode()
    {
        await using var storage = await TestStorage.CreateAsync();
        var id = Guid.NewGuid().ToString("N");
        var launch = Operations.SshLaunchCommand(id, "printf '%s\\n' \"it's working\"\nprintf '%s\\n' '$(literal)'\nexit 23");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0, (await BashAsync(launch)).ExitCode);
        (int ExitCode, string Output) result;
        do
        {
            await Task.Delay(100, timeout.Token);
            result = await BashAsync(Operations.SshStatusCommand(id));
        } while (!result.Output.StartsWith("finished:", StringComparison.Ordinal));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("finished:23\nit's working\n$(literal)\n", result.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.NotEqual(0, (await BashAsync(launch)).ExitCode);

        async Task<(int ExitCode, string Output)> BashAsync(string command)
        {
            var options = new ProcessStartInfo(Environment.GetEnvironmentVariable("CAP_TEST_BASH_EXE")!)
            {
                WorkingDirectory = storage.Paths.Data, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            options.Environment["CAP_SSH_JOB_TEST_HOME"] = storage.Paths.Data.Replace('\\', '/');
            var isolated = command.Replace("$HOME", "$CAP_SSH_JOB_TEST_HOME", StringComparison.Ordinal);
            foreach (var arg in new[] { "--noprofile", "--norc", "-c", "export PATH=/usr/bin:/bin:$PATH\n" + isolated })
                options.ArgumentList.Add(arg);
            using var process = Process.Start(options)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await error;
                return (process.ExitCode, await output);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }
}
