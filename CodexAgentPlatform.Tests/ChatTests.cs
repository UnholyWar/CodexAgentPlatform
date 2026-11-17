using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class ChatTests
{
    [Fact]
    public async Task ProjectChatReusesWorkspaceAndOnlyResumesCompletedWorkOnReference()
    {
        await using var storage = await TestStorage.CreateAsync();
        var chats = new ChatStore(storage, new ChatUpdates());
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Deneme projesi" };
        db.Projects.Add(project);
        var developer = await db.Agents.SingleAsync(x => x.Id == "developer");
        developer.AllowedProjects = project.Id;
        await db.SaveChangesAsync();

        var chat = await chats.SendAsync(null, "Birinci soru", developer.Id, project.Id, default);
        var first = await db.Runs.AsNoTracking().SingleAsync();
        await db.Runs.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WorkStatus.Completed));
        await chats.SendAsync(chat, "İkinci soru", developer.Id, project.Id, default);
        var second = await db.Runs.AsNoTracking().SingleAsync(x => x.Id != first.Id);
        Assert.Equal(first.Workspace, second.Workspace);
        var work = new WorkItem { ConversationId = chat, ProjectId = project.Id, AgentId = developer.Id,
            Workspace = first.Workspace!, Status = WorkStatus.Completed, Title = "Veri aktarımı" };
        db.Work.Add(work);
        await db.SaveChangesAsync();
        await db.Runs.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WorkStatus.Completed));
        await chats.SendAsync(chat, "Teşekkürler", developer.Id, project.Id, default);
        var thanks = await db.Runs.AsNoTracking().SingleAsync(x => x.Status == WorkStatus.Pending);
        Assert.Null(thanks.WorkId);
        Assert.Equal(first.Workspace, thanks.Workspace);
        await db.Runs.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, WorkStatus.Completed));
        await chats.SendAsync(chat, "Veri aktarımı işini yeniden aç", developer.Id, project.Id, default);
        Assert.Equal(work.Id, (await db.Runs.AsNoTracking().SingleAsync(x => x.Status == WorkStatus.Pending)).WorkId);
    }

    [Fact]
    public async Task ResumedAgentReceivesOnlyNewAuthorizedExcerpts()
    {
        await using var storage = await TestStorage.CreateAsync();
        var chats = new ChatStore(storage, new ChatUpdates());
        await using var db = storage.CreateDbContext();
        var architect = await db.Agents.SingleAsync(x => x.Id == "architect");
        var qa = await db.Agents.SingleAsync(x => x.Id == "qa");
        var restricted = new Project { Name = "Başka bir proje" };
        qa.AllowedProjects = restricted.Id;
        db.Projects.Add(restricted);
        await db.SaveChangesAsync();
        var chat = await chats.SendAsync(null, "İlk tasarım", architect.Id, null, default);

        async Task Complete(string thread, string answer)
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(x => x.Status == WorkStatus.Pending);
            await db.Messages.Where(x => x.Id == run.AssistantMessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, answer));
            await db.Runs.Where(x => x.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ThreadId, thread)
                .SetProperty(x => x.TurnId, run.Id).SetProperty(x => x.Status, WorkStatus.Completed));
        }

        await Complete("design", "Önceki yanıt");
        await chats.SendAsync(chat, "Planı incele", qa.Id, null, default);
        await Complete("qa", "Doğrulama sonucu");
        await chats.SendAsync(chat, "Özel rapor", qa.Id, restricted.Id, default);
        await Complete("restricted", "Gizli sonuç");
        await chats.SendAsync(chat, "Sonucu değerlendir", architect.Id, null, default);
        var next = await db.Runs.AsNoTracking().SingleAsync(x => x.Status == WorkStatus.Pending);
        next.ThreadId = "design";
        var references = await new ContextBudget(storage).BuildAsync(next, architect, true, default);
        Assert.Contains("Doğrulama sonucu", references);
        Assert.DoesNotContain("Önceki yanıt", references);
        Assert.DoesNotContain("Özel rapor", references);
        Assert.DoesNotContain("Gizli sonuç", references);
    }

    [Fact]
    public void ToolRepliesPreserveUnicodePageBoundariesAndValidJson()
    {
        var text = new string('ğ', 2999) + "🌍" + new string('ş', 700);
        var first = Args(ToolOutput.Page(text, 0, 8000));
        var next = Args(ToolOutput.Page(text, (int)first.Number("nextOffset"), 8000));
        Assert.Equal(text, first.String("output") + next.String("output"));
        Assert.Equal(JsonValueKind.Null, next.Get("nextOffset").ValueKind);
        var rows = Enumerable.Repeat(new { detail = new string('ö', 900) }, 100).ToArray();
        var serialized = ToolOutput.Serialize(rows);
        Assert.InRange(serialized.Length, 1, 20_000);
        using var result = JsonDocument.Parse(serialized);
        Assert.True(result.RootElement.True("truncated"));
        Assert.NotEmpty(result.RootElement.Items("items"));
    }

    [Fact]
    public void NewDeveloperThreadsDeferMaintenanceToolsAndKeepWorkToolsAvailable()
    {
        var developer = new Agent { AllowedTools = "memory,resources,workspace,git" };
        var specs = AgentTools.Describe(developer).Select(Args).ToArray();
        Assert.Equal(9, specs.Count(t => t.String("type") == "function"));
        Assert.Contains(specs, t => t.String("name") == "task_begin" && !t.True("deferLoading"));
        var deferred = Assert.Single(specs, t => t.String("type") == "namespace").Items("tools").ToArray();
        Assert.Equal(8, deferred.Length);
        Assert.All(deferred, t => Assert.True(t.True("deferLoading")));
        Assert.DoesNotContain(deferred, t => t.String("name") == "ssh_inspect");
    }

    [Fact]
    public void StreamingUpdatesOnlyPatchTheMatchingMessage()
    {
        var updates = new ChatUpdates();
        var reloads = 0;
        updates.Changed += _ => reloads++;
        var snapshot = new ChatSnapshot([new Message { Id = 9 }, new Message { Id = 10 }], [], null, []);
        updates.PublishAnswer("chat", 10, "yeni yanıt");
        updates.ClearAnswer("chat", 9);
        updates.ApplyAnswer("chat", snapshot);
        Assert.Equal("", snapshot.Messages[0].Text);
        Assert.Equal("yeni yanıt", snapshot.Messages[1].Text);
        Assert.Equal(0, reloads);
    }

    [Fact]
    public async Task StreamingPersistenceThreadResumeModelChangeAndTokenAccounting()
    {
        await using var storage = await TestStorage.CreateAsync();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var chat = await chats.SendAsync(null, "Merhaba", null, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Completed);
            var first = await chats.ReadAsync(chat, 10, default);
            Assert.Collection(first.Messages, x => Assert.Equal("Merhaba", x.Text), x => Assert.Equal("Test yanıtı", x.Text));
            Assert.Equal(chat, Assert.Single(await chats.RecentAsync("yanıtı", default)).Id);
            await using (var db = storage.CreateDbContext())
                await db.Agents.Where(x => x.Id == "developer").ExecuteUpdateAsync(s => s.SetProperty(x => x.Model, "test-model-b"));
            await chats.SendAsync(chat, "Devam et", null, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Messages.Count == 4 &&
                (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Completed);
            Assert.Contains("thread/resume", codex.Methods);
            Assert.Equal("test-model-b", codex.TurnModel);
            await using var check = storage.CreateDbContext();
            Assert.Empty(await check.Work.ToListAsync());
            Assert.Equal(2, await check.Usage.CountAsync());
            Assert.Equal(32, await check.Usage.SumAsync(x => x.Total));
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
    }

    [Fact]
    public async Task ADisconnectedServerIsRecoveredOnNextMessageWithoutReplayingInterruptedTurn()
    {
        await using var storage = await TestStorage.CreateAsync();
        var codex = new FakeCodex { DropNext = true };
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var chat = await chats.SendAsync(null, "İlk mesaj", null, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Interrupted);
            Assert.False(codex.Connected);
            await chats.SendAsync(chat, "Tekrar dene", null, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Completed);
            Assert.True(codex.Connected);
            Assert.Equal(2, codex.Methods.Count(x => x == "turn/start"));
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
    }

    [Theory]
    [InlineData("developer", true)]
    [InlineData("architect", false)]
    public async Task ChatOnlyThreadsHaveExplicitCapabilitiesAndNeverReplayTheTurn(string agentId, bool workspaceDenied)
    {
        await using var storage = await TestStorage.CreateAsync();
        var codex = new FakeCodex { DenyWorkspaceNamespace = workspaceDenied };
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var chat = await chats.SendAsync(null, "İlk mesaj", agentId, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Completed);
            var result = await chats.ReadAsync(chat, 10, default);
            Assert.Collection(result.Messages, x => Assert.Equal("İlk mesaj", x.Text), x => Assert.Equal("Test yanıtı", x.Text));
            Assert.Equal(workspaceDenied ? 2 : 1, codex.ThreadRequests.Count);
            if (workspaceDenied) Assert.Equal("platform_work", codex.ThreadRequests[0].String("permissions"));
            var chatRequest = codex.ThreadRequests[^1];
            AssertChatOnly(chatRequest);
            Assert.Contains("You do not have terminal or direct filesystem access in this conversation.",
                chatRequest.String("developerInstructions"));
            AssertChatOnly(Assert.Single(codex.TurnRequests));

            await using var db = storage.CreateDbContext();
            var thread = Assert.Single(await db.Threads.Where(x => x.ConversationId == chat).ToListAsync());
            Assert.Equal(result.Run!.Workspace + ":chat", thread.Workspace);
            Assert.Equal(workspaceDenied ? 1 : 0,
                await db.Events.CountAsync(x => x.ConversationId == chat && x.Kind == "WorkspaceUnavailable"));
        }
        finally { await worker.StopAsync(default); worker.Dispose(); }
    }

    [Fact]
    public async Task TaskAndMemoryToolsPersistStructuredWork()
    {
        await using var storage = await TestStorage.CreateAsync();
        var memory = new MemoryStore(storage, storage.Paths);
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        var tools = new AgentTools(storage, memory, operations, storage.Paths);
        var run = new ChatRun { AgentId = "developer", ConversationId = Guid.NewGuid().ToString("N"), Workspace = Guid.NewGuid().ToString("N") };
        await using (var db = storage.CreateDbContext()) { db.Runs.Add(run); await db.SaveChangesAsync(); }
        await tools.RunAsync("task_begin", Args(new { title = "Files", description = "List files and sizes" }), run, default);
        await tools.RunAsync("task_progress", Args(new { step = "Local tests passed", @event = "TestExecuted" }), run, default);
        await tools.RunAsync("memory_create", Args(new { title = "Files", summary = "Python", markdown = "# List files\nUse pathlib." }), run, default);
        await tools.RunAsync("task_finish", Args(new { status = "completed", step = "Saved" }), run, default);

        await using var check = storage.CreateDbContext();
        Assert.Equal(WorkStatus.Completed, (await check.Work.SingleAsync()).Status);
        var note = Assert.Single(await memory.SearchAsync("Python", null, null, false, default));
        Assert.Equal(run.WorkId, note.SourceTaskId);
        Assert.True(File.Exists(Path.Combine(storage.Paths.Data, "tasks", run.WorkId!, "work.json")));
        Assert.True(await check.Events.AnyAsync(x => x.WorkId == run.WorkId && x.Kind == "TestExecuted"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            tools.RunAsync("ssh_inspect", Args(new { id = "server", operation = "test" }), run, default));
    }

    private static ChatWorker Worker(TestStorage storage, FakeCodex codex, ChatUpdates updates)
    {
        var memory = new MemoryStore(storage, storage.Paths);
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        return new ChatWorker(storage, codex, new AgentTools(storage, memory, operations, storage.Paths),
            new ContextBudget(storage), storage.Paths, operations, updates, NullLogger<ChatWorker>.Instance);
    }

    private static JsonElement Args(object payload) => JsonSerializer.SerializeToElement(payload);

    private static void AssertChatOnly(JsonElement request)
    {
        Assert.Equal("platform_chat", request.String("permissions"));
        var environments = request.Get("environments");
        Assert.Equal(JsonValueKind.Array, environments.ValueKind);
        Assert.Equal(0, environments.GetArrayLength());
    }
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate()) await Task.Delay(100, deadline.Token);
    }

    private sealed class FakeCodex : ICodexConnection
    {
        public bool Connected { get; private set; }
        public bool DropNext { get; set; }
        public bool DenyWorkspaceNamespace { get; set; }
        public List<string> Methods { get; } = [];
        public List<JsonElement> ThreadRequests { get; } = [];
        public List<JsonElement> TurnRequests { get; } = [];
        public string? TurnModel { get; private set; }
        private int count;
        public event Action<CodexEvent>? Received;
        public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult("fake-codex");
        public Task ReconnectAsync(CancellationToken ct) { Connected = true; return Task.CompletedTask; }
        public Task ReplyAsync(JsonElement id, object result, CancellationToken ct) => Task.CompletedTask;
        public Task RejectAsync(JsonElement id, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
        {
            Connected = true;
            Methods.Add(method);
            const string threadId = "t1";
            if (method == "windowsSandbox/readiness" && DenyWorkspaceNamespace)
                return Task.FromResult(Args(new { status = "ready" }));
            if (method is "thread/start" or "thread/resume")
            {
                var request = Args(parameters!);
                ThreadRequests.Add(request);
                if (DenyWorkspaceNamespace && request.String("permissions") == "platform_work")
                    return Task.FromException<JsonElement>(new CodexRpcException(
                        "error creating thread: failed to load AGENTS.md instructions for environment `local`: " +
                        "fs sandbox helper failed with status exit status: 1: bwrap: No permissions to create new namespace"));
                return Task.FromResult(Args(new { thread = new { id = threadId } }));
            }
            if (method == "turn/start")
            {
                var request = Args(parameters!);
                TurnRequests.Add(request);
                TurnModel = request.String("model");
                var turnId = $"turn-{++count}";
                if (DropNext)
                {
                    DropNext = false;
                    Connected = false;
                    Received?.Invoke(new CodexEvent("transport/closed", default));
                }
                else
                {
                    Received?.Invoke(new CodexEvent("item/agentMessage/delta", Args(new { threadId, turnId, itemId = "msg", delta = "Test " })));
                    Received?.Invoke(new CodexEvent("item/agentMessage/delta", Args(new { threadId, turnId, itemId = "msg", delta = "yanıtı" })));
                    var total = new { inputTokens = count * 10, cachedInputTokens = count * 2, outputTokens = count * 6, totalTokens = count * 16 };
                    for (var i = 0; i < 2; i++)
                        Received?.Invoke(new CodexEvent("thread/tokenUsage/updated", Args(new { threadId, turnId, tokenUsage = new { total } })));
                    Received?.Invoke(new CodexEvent("turn/completed", Args(new { threadId, turn = new { id = turnId, status = "completed" } })));
                }
                return Task.FromResult(Args(new { turn = new { id = turnId } }));
            }
            return Task.FromResult(Args(new { }));
        }
    }
}
