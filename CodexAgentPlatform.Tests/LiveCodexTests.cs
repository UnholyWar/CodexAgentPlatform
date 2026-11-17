using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class LiveCodexTests
{
    [CodexFact("CAP_TEST_CODEX_EXE")]
    public async Task InstalledCliSupportsInitializeAccountAndReconnectOverStdio()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var codex = Process(storage.Paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        Assert.Contains("codex", await codex.VersionAsync(timeout.Token), StringComparison.OrdinalIgnoreCase);
        var accounts = new CodexAccounts(codex);
        Assert.Equal(JsonValueKind.Object, (await accounts.ReadAsync(timeout.Token)).ValueKind);
        Assert.NotEmpty(await accounts.ModelsAsync(timeout.Token));
        var workspace = storage.Paths.Workspace(Guid.NewGuid().ToString("N"));
        var workspaceReady = !OperatingSystem.IsWindows() || (await accounts.WindowsReadinessAsync(timeout.Token)).String("status") == "ready";
        var thread = await codex.CallAsync("thread/start", new
        {
            cwd = workspace, permissions = workspaceReady ? "platform_work" : "platform_chat", approvalPolicy = "never",
            runtimeWorkspaceRoots = new[] { workspace },
            environments = workspaceReady ? null : Array.Empty<object>(),
            dynamicTools = AgentTools.Describe(new Agent { AllowedTools = "memory" }).ToArray()
        }, timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(thread.Get("thread").String("id")));
        Assert.True(codex.Connected);
        await codex.ReconnectAsync(timeout.Token);
        Assert.Equal(JsonValueKind.Object, (await accounts.ReadAsync(timeout.Token)).ValueKind);
    }

    [CodexFact("CAP_TEST_CODEX_EXE", "CAP_TEST_AUTH_DATA")]
    public async Task AuthenticatedThreadCanStreamARealTurn()
    {
        await using var temp = await TestStorage.CreateAsync();
        var data = Environment.GetEnvironmentVariable("CAP_TEST_AUTH_DATA")!;
        var paths = new PlatformPaths(data, temp.Paths.Workspaces);
        await using var codex = Process(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var accounts = new CodexAccounts(codex);
        Assert.Equal(JsonValueKind.Object, (await accounts.ReadAsync(ct)).Get("account").ValueKind);
        Assert.NotEmpty(await accounts.ModelsAsync(ct));
        var workspace = paths.Workspace(Guid.NewGuid().ToString("N"));
        var threadId = (await codex.CallAsync("thread/start", new
        {
            cwd = workspace, permissions = "platform_chat", approvalPolicy = "never", runtimeWorkspaceRoots = new[] { workspace },
            environments = Array.Empty<object>()
        }, ct)).Get("thread").String("id");
        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new System.Text.StringBuilder();
        codex.Received += item =>
        {
            if (item.Parameters.String("threadId") != threadId) return;
            if (item.Method == "item/agentMessage/delta") output.Append(item.Parameters.String("delta"));
            if (item.Method == "turn/completed") finished.TrySetResult(item.Parameters.Get("turn").String("status") ?? "");
        };
        await codex.CallAsync("turn/start", new
        {
            threadId, cwd = workspace, permissions = "platform_chat", approvalPolicy = "never",
            environments = Array.Empty<object>(),
            input = new[] { new { type = "text", text = "Reply with a short greeting. Use no tools." } }
        }, ct);
        Assert.Equal("completed", await finished.Task.WaitAsync(ct));
        Assert.NotEmpty(output.ToString().Trim());
    }

    private static CodexProcess Process(PlatformPaths paths) =>
        new(paths, Environment.GetEnvironmentVariable("CAP_TEST_CODEX_EXE")!, NullLogger<CodexProcess>.Instance);
}

public sealed class CodexFactAttribute : FactAttribute
{
    public CodexFactAttribute(params string[] settings)
    {
        if (settings.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(x))))
            Skip = "Opt-in with " + string.Join(" and ", settings);
    }
}
