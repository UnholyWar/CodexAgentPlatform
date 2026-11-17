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
    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate()) await Task.Delay(100, deadline.Token);
    }

    private sealed class FakeCodex : ICodexConnection
    {
        public bool Connected { get; private set; }
        public bool DropNext { get; set; }
        public List<string> Methods { get; } = [];
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
            if (method is "thread/start" or "thread/resume")
                return Task.FromResult(Args(new { thread = new { id = threadId } }));
            if (method == "turn/start")
            {
                TurnModel = Args(parameters!).String("model");
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
