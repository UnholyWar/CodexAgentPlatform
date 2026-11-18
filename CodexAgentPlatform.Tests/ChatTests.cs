using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CodexAgentPlatform.Tests;

public sealed class ChatTests(ITestOutputHelper output)
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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chats.SendAsync(chat, "@Architect bu raporu değerlendir", null, null, default));
        await chats.SendAsync(chat, "Sonucu değerlendir", architect.Id, "", default);
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
        Assert.Equal(9, deferred.Length);
        Assert.Contains(deferred, t => t.String("name") == "request_agent_permission");
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
            var chat = await chats.SendAsync(null, "Merhaba", "developer", null, default);
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
    public async Task NormalChatOnlyReceivesEligibleTitlesAndRejectsOtherToolsAndAgents()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Shared dashboard" };
        db.Projects.Add(project);
        var developer = await db.Agents.SingleAsync(a => a.Id == "developer");
        developer.AllowedProjects = project.Id;
        developer.Title = "Dashboard developer";
        developer.Instructions = "Private dashboard maintenance instructions";
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        architect.AllowedProjects = project.Id;
        architect.Archived = true;
        var devops = await db.Agents.SingleAsync(a => a.Id == "devops");
        await db.SaveChangesAsync();

        var codex = new FakeCodex();
        codex.NextToolCalls.Enqueue(("task_begin", new { title = "Unexpected", description = "Test" }));
        codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = devops.Id }));
        codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = architect.Id }));
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var id = await chats.SendAsync(null, "Could you explain this?", null, project.Id, default);
            await WaitAsync(async () => (await chats.ReadAsync(id, 10, default)).Run?.Status == WorkStatus.Completed);
            var thread = Assert.Single(codex.ThreadRequests);
            AssertChatOnly(thread);
            var offered = thread.Items("dynamicTools").ToArray();
            Assert.Equal("handoff_to_agent", Assert.Single(offered, t => t.String("type") == "function").String("name"));
            Assert.Equal("request_agent_permission", Assert.Single(Assert.Single(offered, t => t.String("type") == "namespace").Items("tools")).String("name"));
            var input = Assert.Single(codex.TurnRequests).Items("input").Single().String("text");
            Assert.Contains(developer.Title, input);
            Assert.DoesNotContain(architect.Title, input);
            Assert.DoesNotContain(devops.Title, input);
            Assert.DoesNotContain(developer.Instructions, input + thread.String("developerInstructions"));
            Assert.DoesNotContain("memory_search", thread.GetRawText());
            Assert.Equal(3, codex.ToolReplies.Count);
            Assert.All(codex.ToolReplies, r => Assert.False(r.True("success")));
            Assert.Equal(PlatformChat.Id, (await db.Runs.SingleAsync()).AgentId);
            Assert.False(await db.Work.AnyAsync());
            Assert.False(await db.Events.AnyAsync(e => e.Kind == "AgentHandoff"));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task ChatGrantEnablesProjectCreationInAnExistingSpecialistChat()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var id = await chats.SendAsync(null, "Review this plan", "qa", null, default, autoApproveNewChat: true);
            Task WaitForReplies(int count) => WaitAsync(async () =>
                await db.Runs.CountAsync(r => r.ConversationId == id && r.Status == WorkStatus.Completed) == count);
            await WaitForReplies(1);
            Assert.DoesNotContain("project_create", codex.ThreadRequests[0].Get("dynamicTools").GetRawText());

            codex.NextToolCalls.Enqueue(("request_agent_permission", new { agent = "QA Engineer", kind = "tool", permission = "projects.create" }));
            await chats.SendAsync(id, "QA ajanına proje oluşturma yetkisi ver", PlatformChat.Id, null, default);
            await WaitForReplies(2);
            var grant = Assert.Single((await chats.ReadAsync(id, 10, default)).Approvals);
            Assert.Equal("Pending", grant.Status);
            Assert.DoesNotContain("projects.create", await db.Agents.Where(a => a.Id == "qa").Select(a => a.AllowedTools).SingleAsync());

            var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
            await operations.DecideAsync(grant.Id, true, PlatformUser.OwnerId, default);
            codex.NextToolCalls.Enqueue(("project_create", new { name = "Atlas" }));
            await chats.SendAsync(id, "Atlas projesini oluştur", "qa", null, default);
            await WaitForReplies(3);
            Assert.Contains("project_create", codex.ThreadRequests[^1].Get("dynamicTools").GetRawText());
            Assert.NotEqual(codex.TurnRequests[0].String("threadId"), codex.TurnRequests[^1].String("threadId"));
            var atlas = await db.Projects.SingleAsync(p => p.Name == "Atlas");
            Assert.True(ToolAccess.AllowsProject(await db.Agents.AsNoTracking().SingleAsync(a => a.Id == "qa"), atlas.Id));

            codex.NextToolCalls.Enqueue(("request_agent_permission", new { agent = "Developer", kind = "project", permission = "Atlas" }));
            await chats.SendAsync(id, "Developer'a Atlas projesi için erişim ver", null, null, default);
            await WaitForReplies(4);
            var pending = await db.Approvals.SingleAsync(a => a.ConversationId == id && a.Status == "Pending");
            await operations.DecideAsync(pending.Id, true, PlatformUser.OwnerId, default);
            Assert.True(ToolAccess.AllowsProject(await db.Agents.AsNoTracking().SingleAsync(a => a.Id == "developer"), atlas.Id));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task NormalChatHandsTheOriginalRequestToOneSpecialistAndKeepsTheFollowUp()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            const string background = "We deployed the staging dashboard yesterday.";
            var id = await chats.SendAsync(null, background, null, null, default);
            await WaitAsync(async () => (await chats.ReadAsync(id, 10, default)).Run?.Status == WorkStatus.Completed);
            Assert.Equal(PlatformChat.Id, (await db.Runs.SingleAsync()).AgentId);

            const string request = "Can you inspect that release's logs?";
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "devops" }));
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "qa" }));
            await chats.SendAsync(id, request, null, null, default);
            await WaitAsync(() => db.Runs.AnyAsync(r => r.AgentId == "devops" && r.Status == WorkStatus.Completed));
            Assert.Equal(3, await db.Runs.CountAsync());
            Assert.Equal(1, await db.Messages.CountAsync(m => m.Role == "user" && m.Text == request));
            Assert.Single(await db.Events.Where(e => e.Kind == "AgentHandoff" && e.ConversationId == id).ToListAsync());
            Assert.Collection(codex.ToolReplies, r => Assert.True(r.True("success")), r => Assert.False(r.True("success")));
            var specialistInput = codex.TurnRequests[^1].Items("input").Single().String("text");
            Assert.Contains(request, specialistInput);
            Assert.Contains(background, specialistInput);
            Assert.Equal(2, await db.Usage.CountAsync(u => u.AgentId == PlatformChat.Id));
            Assert.Equal(1, await db.Usage.CountAsync(u => u.AgentId == "devops"));

            await chats.SendAsync(id, "Why did that happen?", null, null, default);
            await WaitAsync(async () => await db.Runs.CountAsync(r => r.AgentId == "devops" && r.Status == WorkStatus.Completed) == 2);
            Assert.Equal(codex.TurnRequests[^2].String("threadId"), codex.TurnRequests[^1].String("threadId"));

            await chats.SendAsync(id, "Let's chat.", PlatformChat.Id, null, default);
            await WaitAsync(async () => await db.Runs.CountAsync(r => r.AgentId == PlatformChat.Id && r.Status == WorkStatus.Completed) == 3);
            Assert.Equal(codex.TurnRequests[0].String("threadId"), codex.TurnRequests[^1].String("threadId"));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task AnExplicitMultiwordMentionOverridesTheSelectedAgent()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var chats = new ChatStore(storage, new ChatUpdates());
        var specialist = new Agent { Name = "Release Engineer", Title = "Release operations" };
        db.Agents.Add(specialist);
        await db.SaveChangesAsync();

        var id = await chats.SendAsync(null, "@Release Engineer inspect this build", "developer", null, default);

        var run = Assert.Single(await db.Runs.Where(r => r.ConversationId == id).ToListAsync());
        Assert.Equal(specialist.Id, run.AgentId);
    }

    [Fact]
    public async Task NormalChatSendsLessPlatformContextThanTheDeveloperForTheSameGreeting()
    {
        await using var storage = await TestStorage.CreateAsync();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            foreach (var selection in new[] { PlatformChat.Id, "developer" })
            {
                var id = await chats.SendAsync(null, "Merhaba", selection, null, default);
                await WaitAsync(async () => (await chats.ReadAsync(id, 10, default)).Run?.Status == WorkStatus.Completed);
            }

            var sizes = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                var thread = codex.ThreadRequests[i];
                var tools = thread.Items("dynamicTools").ToArray();
                var functions = tools.Sum(t => t.String("type") == "namespace" ? t.Items("tools").Count() : 1);
                var instructionChars = (thread.String("developerInstructions") ?? "").Length + (thread.String("baseInstructions") ?? "").Length;
                var schemaChars = thread.Get("dynamicTools").GetRawText().Length;
                var inputChars = codex.TurnRequests[i].Items("input").Sum(item => item.String("text")?.Length ?? 0);
                sizes.Add(instructionChars + schemaChars + inputChars);
                output.WriteLine($"{(i == 0 ? "Normal chat" : "Developer")}: {functions} functions, {instructionChars} instruction characters, " +
                    $"{schemaChars} serialized tool characters, {inputChars} turn-input characters.");
            }
            Assert.True(sizes[0] < sizes[1]);
        }
        finally { await worker.StopAsync(default); }
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
            Assert.StartsWith(result.Run!.Workspace + ":chat:", thread.Workspace, StringComparison.Ordinal);
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

    [Fact]
    public async Task VoiceHandoffUsesOneTranscriptAndSpeaksTheSelectedAgentsResult()
    {
        await using var storage = await TestStorage.CreateAsync();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            await using (var voice = new ChatVoiceCall(codex, chats, worker, NullLogger<ChatVoiceCall>.Instance,
                conversationId: null, projectId: null, selectedAgent: "qa", autoApprove: false))
            {
                var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                voice.ConversationOpened += id => opened.SetResult(id);
                Assert.Equal("v=0 fake answer", await voice.StartAsync("v=0 fake offer"));
                const string text = "Please run the regression tests.";
                var request = new { type = "handoff_request", item_id = "handoff-1", input_transcript = text };
                codex.Emit("thread/realtime/itemAdded", new { threadId = "t1", item = request });
                codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text = " " + text });
                codex.Emit("thread/realtime/itemAdded", new { threadId = "t1", item = request });

                var id = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitAsync(() => Task.FromResult(codex.SpokenResults.Contains("Agent result: Test yanıtı")));
                var snapshot = await chats.ReadAsync(id, 10, default);
                var user = Assert.Single(snapshot.Messages, m => m.Role == "user");
                Assert.Equal(text, user.Text);
                Assert.Equal("", user.VoiceProjectId);
                Assert.Equal(user.Id, snapshot.Run?.UserMessageId);
                Assert.Equal("qa", snapshot.Run?.AgentId);
                Assert.Equal(WorkStatus.Completed, snapshot.Run?.Status);
            }
            Assert.Contains("thread/realtime/stop", codex.Methods);
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task VoiceFollowUpUsesTheSpokenProjectAndOnlyAnEligibleAgent()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "AtlasPortal" };
        db.Projects.Add(project);
        var qa = await db.Agents.SingleAsync(a => a.Id == "qa");
        qa.AllowedProjects = project.Id;
        await db.SaveChangesAsync();

        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            await using var voice = new ChatVoiceCall(codex, chats, worker, NullLogger<ChatVoiceCall>.Instance,
                conversationId: null, projectId: null, selectedAgent: null, autoApprove: false);
            var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            voice.ConversationOpened += id => opened.SetResult(id);
            await voice.StartAsync("v=0 fake offer");
            Assert.Contains(project.Name, codex.VoicePrompt);

            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user",
                text = "Atlas Portal projesi hakkında konuşalım." });
            var conversation = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "developer" }));
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "qa" }));
            const string followUp = "Bu projede hangi testleri çalıştırdık?";
            codex.Emit("thread/realtime/itemAdded", new { threadId = "t1",
                item = new { type = "handoff_request", item_id = "follow-up", input_transcript = followUp } });

            await WaitAsync(() => Task.FromResult(codex.SpokenResults.Contains("Agent result: Test yanıtı")));
            var snapshot = await chats.ReadAsync(conversation, 10, default);
            Assert.Equal("qa", snapshot.Run?.AgentId);
            Assert.Equal(project.Id, snapshot.Run?.ProjectId);
            Assert.All(snapshot.Messages.Where(m => m.Role == "user"), m => Assert.Equal(project.Id, m.VoiceProjectId));
            Assert.Collection(codex.ToolReplies, r => Assert.False(r.True("success")), r => Assert.True(r.True("success")));
        }
        finally { await worker.StopAsync(default); }
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
        public Queue<(string Name, object Arguments)> NextToolCalls { get; } = [];
        public List<JsonElement> ToolReplies { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> SpokenResults { get; } = new();
        public string? TurnModel { get; private set; }
        public string? VoicePrompt { get; private set; }
        private int count;
        public event Action<CodexEvent>? Received;
        public void Emit(string method, object parameters) => Received?.Invoke(new CodexEvent(method, Args(parameters)));
        public Task<string> VersionAsync(CancellationToken ct) => Task.FromResult("fake-codex");
        public Task ReconnectAsync(CancellationToken ct) { Connected = true; return Task.CompletedTask; }
        public Task ReplyAsync(JsonElement id, object result, CancellationToken ct)
        {
            ToolReplies.Add(Args(result));
            return Task.CompletedTask;
        }
        public Task RejectAsync(JsonElement id, string reason, CancellationToken ct) => Task.CompletedTask;
        public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
        {
            Connected = true;
            Methods.Add(method);
            if (method == "account/read")
                return Task.FromResult(Args(new { account = new { type = "chatgpt" } }));
            if (method == "thread/realtime/start")
            {
                var request = Args(parameters!);
                VoicePrompt = request.String("prompt");
                Emit("thread/realtime/sdp", new { threadId = request.String("threadId"), sdp = "v=0 fake answer" });
            }
            if (method == "thread/realtime/appendSpeech")
                SpokenResults.Enqueue(Args(parameters!).String("text")!);
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
                return Task.FromResult(Args(new { thread = new { id = request.String("threadId") ?? $"t{ThreadRequests.Count}" } }));
            }
            if (method == "turn/start")
            {
                var request = Args(parameters!);
                var threadId = request.String("threadId")!;
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
                    while (NextToolCalls.TryDequeue(out var tool))
                    {
                        var arguments = Args(new { threadId, turnId, tool = tool.Name, arguments = tool.Arguments });
                        Received?.Invoke(new CodexEvent("item/tool/call", arguments, Args(NextToolCalls.Count + 1)));
                    }
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
