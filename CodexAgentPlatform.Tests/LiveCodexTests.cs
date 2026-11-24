using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class LiveCodexTests
{
    [CodexFact("CAP_TEST_CODEX_WITHOUT_PATH")]
    public async Task WindowsDevelopmentDiscoversDesktopCliAndStartsAppServerWithoutPathEntry()
    {
        Assert.True(OperatingSystem.IsWindows());
        await using var storage = await TestStorage.CreateAsync();
        await using var codex = new CodexProcess(storage.Paths, null, NullLogger<CodexProcess>.Instance,
            allowWindowsDesktopFallback: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        Assert.Contains("codex", await codex.VersionAsync(ct), StringComparison.OrdinalIgnoreCase);
        var accounts = new CodexAccounts(codex);
        Assert.Equal(JsonValueKind.Object, (await accounts.ReadAsync(ct)).ValueKind);
        Assert.NotEmpty(await accounts.ModelsAsync(ct));
        Assert.Contains((await accounts.WindowsReadinessAsync(ct)).String("status"),
            new[] { "ready", "notConfigured", "updateRequired" });
    }

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

    [CodexFact("CAP_TEST_CODEX_EXE", "CAP_TEST_AUTH_DATA", "CAP_TEST_GIT_EXE")]
    public async Task TextChatHandsOffAndReadsTheSelectedRepository()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Compass" };
        var repository = new Resource { ProjectId = project.Id, Name = "Compass repository",
            Kind = ResourceKind.GitRepository, Address = "https://example.invalid/compass.git" };
        var chat = new Conversation();
        var work = new WorkItem { ConversationId = chat.Id, AgentId = "developer", ProjectId = project.Id,
            Title = "Initial checkout", Workspace = Guid.NewGuid().ToString("N"), Status = WorkStatus.Completed };
        (await db.Agents.SingleAsync(a => a.Id == "developer")).AllowedProjects = project.Id;
        db.AddRange(project, repository, chat, work);
        await db.SaveChangesAsync();
        var clone = Path.Combine(storage.Paths.Workspaces, ".git-cache", "compass");
        Directory.CreateDirectory(clone);
        var marker = Guid.NewGuid().ToString("N");
        await GitApprovalTests.GitAsync(clone, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), $"Project verification code: {marker}");
        await GitApprovalTests.GitAsync(clone, "add", "--", "README.md");
        await GitApprovalTests.GitAsync(clone, "-c", "user.name=Test", "-c", "user.email=test@localhost", "commit", "-m", "Seed");
        await GitApprovalTests.GitAsync(clone, "worktree", "add", "--detach", Path.Combine(storage.Paths.Workspace(work.Workspace), "project"), "HEAD");
        await File.WriteAllTextAsync(Path.Combine(clone, ".git", "platform-resource"), repository.Id);

        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var vault = new Vault(storage.Paths, null);
        var operations = new Operations(storage, storage.Paths, vault);
        var tools = new AgentTools(storage, new MemoryStore(storage, storage.Paths), operations, storage.Paths);
        var auth = new PlatformPaths(Environment.GetEnvironmentVariable("CAP_TEST_AUTH_DATA")!, storage.Paths.Workspaces);
        await using var codex = Process(auth);
        using var worker = new ChatWorker(storage, codex, tools, new ContextBudget(storage), storage.Paths,
            operations, updates, NullLogger<ChatWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = deadline.Token;
            await chats.SendAsync(chat.Id, "Seçili projenin README.md dosyasında yazan doğrulama kodunu oku.", null, project.Id, ct);
            ChatSnapshot snapshot;
            do
            {
                await Task.Delay(300, ct);
                snapshot = await chats.ReadAsync(chat.Id, 12, ct);
            } while (snapshot.Run is null || ChatStore.Active.Contains(snapshot.Run.Status));
            Assert.Equal("developer", snapshot.Run.AgentId);
            Assert.Contains(marker, snapshot.Messages.Single(m => m.Id == snapshot.Run.AssistantMessageId).Text);
        }
        finally { await worker.StopAsync(default); }
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
