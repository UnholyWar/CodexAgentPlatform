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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperationApprovalOnlyResumesTheSameTaskIfItWasNotDeclined(bool approve)
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var devops = await db.Agents.SingleAsync(a => a.Id == "devops");
        devops.AllowedTools += "," + ToolAccess.GlobalResources;
        var host = new Resource { Name = "Example host", Address = "host.invalid", Kind = ResourceKind.SshServer, IsGlobal = true };
        db.Resources.Add(host);
        await db.SaveChangesAsync();
        var codex = new FakeCodex { WorkspaceReady = true };
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        codex.NextToolCalls.Enqueue(("ssh_request_operation", new { id = host.Id, operation = "restart-docker" }));
        await worker.StartAsync(default);
        try
        {
            const string request = "Restart Docker and confirm the result.";
            var chat = await chats.SendAsync(null, request, devops.Id, null, default, autoApproveNewChat: true);
            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.WaitingForApproval);
            var waiting = await chats.ReadAsync(chat, 10, default);
            var approval = Assert.Single(waiting.Approvals);
            Assert.Equal("Pending", approval.Status);
            Assert.Equal(waiting.Run!.Id, approval.ContinuationRunId);
            Assert.Single(codex.TurnRequests);
            await storage.Startup.InitializeAsync(default);
            Assert.Equal(WorkStatus.WaitingForApproval, (await chats.ReadAsync(chat, 10, default)).Run?.Status);
            if (!approve)
            {
                var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
                await operations.DecideAsync(approval.Id, false, PlatformUser.OwnerId, default);
                await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run?.Status == WorkStatus.Cancelled);
                Assert.Single(await db.Runs.Where(r => r.ConversationId == chat).ToListAsync());
                Assert.Single(codex.TurnRequests);
                return;
            }

            // Simulate an approved job still running on its host, then its recorded final result.
            await db.Approvals.Where(a => a.Id == approval.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Executing"));
            Assert.Null(await ApprovalContinuations.QueueReadyAsync(db, default));
            await db.Approvals.Where(a => a.Id == approval.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(a => a.Status, "Completed").SetProperty(a => a.Result, "Docker is active."));

            await WaitAsync(async () => (await chats.ReadAsync(chat, 10, default)).Run is
                { ResumesRunId: not null, Status: WorkStatus.Completed });
            var finished = await chats.ReadAsync(chat, 10, default);
            Assert.Equal(waiting.Run.Id, finished.Run!.ResumesRunId);
            Assert.Equal((waiting.Run.UserMessageId, waiting.Run.Workspace, waiting.Run.AgentId),
                (finished.Run.UserMessageId, finished.Run.Workspace, finished.Run.AgentId));
            Assert.Equal(2, codex.TurnRequests.Count);
            var resumed = codex.TurnRequests[^1].Items("input").Single().String("text");
            Assert.Contains(request, resumed);
            Assert.Contains("Docker is active.", resumed);
            Assert.Single(finished.Messages, m => m.Role == "user");
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task ArchitectKeepsTheConversationAndDeveloperStartsTheSameRequestAfterApproval()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
        architect.AllowedTools += ",projects.create";
        await db.SaveChangesAsync();
        var codex = new FakeCodex { WorkspaceReady = true };
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var chat = await chats.SendAsync(null, "Mimarla görüşmek istiyorum.", null, null, default);
            Task Completed(int count) => WaitAsync(async () =>
                await db.Runs.CountAsync(r => r.ConversationId == chat && r.Status == WorkStatus.Completed) == count);
            await Completed(1);
            codex.NextToolCalls.Enqueue(("project_create", new { name = "Atlas" }));
            await chats.SendAsync(chat, "Atlas adında bir proje oluştur.", null, null, default);
            await Completed(2);
            var project = await db.Projects.SingleAsync();
            Assert.Equal(project.Id, (await chats.ReadAsync(chat, 10, default)).ProjectId);

            const string implementation = "Bu projeye Node.js ile basit bir uygulama yap.";
            await chats.SendAsync(chat, implementation, null, null, default);
            var waiting = await chats.ReadAsync(chat, 10, default);
            Assert.Equal("developer", waiting.Run?.AgentId);
            Assert.Equal(project.Id, waiting.Run?.ProjectId);
            Assert.Equal(WorkStatus.WaitingForApproval, waiting.Run?.Status);
            Assert.Equal("architect", waiting.CoordinatorAgentId);
            var approval = Assert.Single(waiting.Approvals);
            Assert.Equal(waiting.Run?.Id, approval.ContinuationRunId);
            Assert.Equal(2, codex.TurnRequests.Count);

            await new StorageStartup(storage, storage.Paths).InitializeAsync(default);
            Assert.Equal("Pending", await db.Approvals.AsNoTracking().Where(a => a.Id == approval.Id).Select(a => a.Status).SingleAsync());
            await operations.DecideAsync(approval.Id, true, PlatformUser.OwnerId, default);
            await Completed(3);
            var completed = await chats.ReadAsync(chat, 10, default);
            Assert.Equal(waiting.Run?.Id, completed.Run?.Id);
            Assert.Contains(implementation, codex.TurnRequests[^1].Items("input").Single().String("text"));
            Assert.Equal("platform_work", codex.TurnRequests[^1].String("permissions"));
            Assert.Contains(completed.Events, e => e.Kind == "AgentHandoff" && e.AgentId == "developer");

            await chats.SendAsync(chat, "Mimar, sonucu değerlendirir misin?", null, null, default);
            await Completed(4);
            Assert.Equal("architect", (await chats.ReadAsync(chat, 10, default)).Run?.AgentId);

            (await db.Agents.SingleAsync(a => a.Id == "qa")).AllowedProjects = project.Id;
            await db.SaveChangesAsync();
            codex.NextToolCalls.Enqueue(("task_delegate", new { agentId = "devops", brief = "Unauthorized follow-up" }));
            codex.NextToolCalls.Enqueue(("task_delegate", new { agentId = "qa", brief = "Verify the Node.js application." }));
            await chats.SendAsync(chat, "Uygulamanın çalıştığını doğrula.", null, null, default);
            await Completed(6);
            var verified = await chats.ReadAsync(chat, 15, default);
            Assert.Equal("qa", verified.Run?.AgentId);
            Assert.Equal("architect", verified.CoordinatorAgentId);
            Assert.Equal(completed.Run?.Workspace, verified.Run?.Workspace);
            Assert.Contains("Verify the Node.js application.", codex.TurnRequests[^1].Items("input").Single().String("text"));
            Assert.Contains(verified.Events, e => e.Kind == "AgentHandoff" && e.AgentId == "qa");
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task CombinedCreationAndImplementationQueuesBothStepsAndCanBeDeclined()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        var operations = new Operations(storage, storage.Paths, new Vault(storage.Paths, null));
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            var id = await chats.SendAsync(null, "Atlas adında bir proje oluştur ve Node.js ile basit bir uygulama yap.", null, null, default);
            var first = Assert.Single((await chats.ReadAsync(id, 10, default)).Approvals);
            Assert.Contains("Create projects", first.Scope);
            codex.NextToolCalls.Enqueue(("project_create", new { name = "Atlas" }));
            await operations.DecideAsync(first.Id, true, PlatformUser.OwnerId, default);

            await WaitAsync(() => db.Approvals.AnyAsync(a => a.ConversationId == id && a.Id != first.Id && a.Status == "Pending"));
            var staged = await chats.ReadAsync(id, 10, default);
            var project = await db.Projects.SingleAsync();
            var access = Assert.Single(staged.Approvals, a => a.Status == "Pending");
            Assert.Equal("developer", staged.Run?.AgentId);
            Assert.Equal(project.Id, staged.ProjectId);
            Assert.Equal(staged.Run?.Id, access.ContinuationRunId);
            Assert.Single(staged.Messages, m => m.Role == "user");
            await operations.DecideAsync(access.Id, false, PlatformUser.OwnerId, default);
            Assert.Equal(WorkStatus.Cancelled, (await chats.ReadAsync(id, 10, default)).Run?.Status);
            Assert.Single(codex.TurnRequests);
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
        await using (var db = storage.CreateDbContext())
        {
            (await db.Agents.SingleAsync(a => a.Id == "qa")).Voice = "Test device voice";
            await db.SaveChangesAsync();
        }
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
                var replies = new System.Collections.Concurrent.ConcurrentQueue<VoiceReply>();
                voice.ConversationOpened += id => opened.SetResult(id);
                voice.ReplyReady += replies.Enqueue;
                Assert.Equal("v=0 fake answer", await voice.StartAsync("v=0 fake offer"));
                const string text = "Please run the regression tests.";
                var request = new { type = "handoff_request", item_id = "handoff-1", input_transcript = text };
                codex.Emit("thread/realtime/itemAdded", new { threadId = "t1", item = request });
                codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text = " " + text });
                codex.Emit("thread/realtime/itemAdded", new { threadId = "t1", item = request });

                var id = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitAsync(() => Task.FromResult(!replies.IsEmpty));
                var reply = Assert.Single(replies);
                Assert.Equal("Test yanıtı", reply.Text);
                Assert.Equal("Test device voice", reply.Speaker.Voice);
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
        (await db.Agents.SingleAsync(a => a.Id == "architect")).AllowedProjects = project.Id;
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
            var replies = new System.Collections.Concurrent.ConcurrentQueue<VoiceReply>();
            voice.ConversationOpened += id => opened.SetResult(id);
            voice.ReplyReady += replies.Enqueue;
            await voice.StartAsync("v=0 fake offer");
            Assert.DoesNotContain(project.Name, codex.VoicePrompt);

            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user",
                text = "Atlas Portal projesi hakkında konuşalım." });
            var conversation = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => Task.FromResult(replies.Count == 1));
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "developer" }));
            codex.NextToolCalls.Enqueue(("handoff_to_agent", new { agentId = "qa" }));
            const string followUp = "Bu projede hangi testleri çalıştırdık?";
            codex.Emit("thread/realtime/itemAdded", new { threadId = "t1",
                item = new { type = "handoff_request", item_id = "follow-up", input_transcript = followUp } });

            await WaitAsync(() => Task.FromResult(replies.Count == 2));
            var snapshot = await chats.ReadAsync(conversation, 10, default);
            Assert.Equal("qa", snapshot.Run?.AgentId);
            Assert.Equal("qa", snapshot.CoordinatorAgentId);
            Assert.Equal("qa", voice.Speaker.Id);
            Assert.Equal(project.Id, snapshot.Run?.ProjectId);
            Assert.All(snapshot.Messages.Where(m => m.Role == "user"), m => Assert.Equal(project.Id, m.VoiceProjectId));
            Assert.Collection(codex.ToolReplies, r => Assert.False(r.True("success")), r => Assert.True(r.True("success")));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task SelectedProjectVoiceTranscriptReadsItsResourceWithoutANativeHandoff()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Compass" };
        var other = new Project { Name = "Other" };
        db.Projects.AddRange(project, other);
        var repo = new Resource { ProjectId = project.Id, Name = "Compass services", Kind = ResourceKind.GitRepository };
        db.Resources.AddRange(repo, new Resource { ProjectId = other.Id, Name = "Unrelated resource" });
        (await db.Agents.SingleAsync(a => a.Id == "developer")).AllowedProjects = project.Id;
        await db.SaveChangesAsync();

        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            await using var voice = new ChatVoiceCall(codex, chats, worker, NullLogger<ChatVoiceCall>.Instance,
                conversationId: null, projectId: project.Id, selectedAgent: null, autoApprove: false);
            var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replies = new System.Collections.Concurrent.ConcurrentQueue<VoiceReply>();
            voice.ConversationOpened += id => opened.SetResult(id);
            voice.ReplyReady += replies.Enqueue;
            await voice.StartAsync("v=0 fake offer");
            codex.NextToolCalls.Enqueue(("resource_get", new { id = repo.Id }));
            const string question = "Seçili projenin içinde hangi repo var?";
            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text = question });
            var conversation = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => Task.FromResult(replies.Count == 1));
            var prompt = Assert.Single(codex.TurnRequests).Get("input")[0].String("text");
            Assert.Contains(repo.Name, prompt);
            Assert.DoesNotContain("Unrelated resource", prompt);
            var lookup = Assert.Single(codex.ToolReplies);
            Assert.True(lookup.True("success"));
            Assert.Contains(repo.Name, lookup.Get("contentItems")[0].String("text"));

            // A late native handoff must not submit the first request twice.
            codex.Emit("thread/realtime/itemAdded", new { threadId = "t1",
                item = new { type = "handoff_request", item_id = "late", input_transcript = question.TrimEnd('?') } });
            (await db.Agents.SingleAsync(a => a.Id == "qa")).AllowedProjects = project.Id;
            await db.SaveChangesAsync();
            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text = "Peki varsayılan dalı nedir?" });
            await WaitAsync(() => Task.FromResult(replies.Count == 2));
            var runs = await db.Runs.AsNoTracking().Where(r => r.ConversationId == conversation).ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, r => { Assert.Equal("developer", r.AgentId); Assert.Equal(project.Id, r.ProjectId); });
            Assert.Equal(2, await db.Messages.CountAsync(m => m.ConversationId == conversation && m.Role == "user"));
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task VoiceSwitchKeepsOneConversationPartnerWhileADelegatedTaskReturnsItsOwnResult()
    {
        await using var storage = await TestStorage.CreateAsync();
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
            var replies = new System.Collections.Concurrent.ConcurrentQueue<VoiceReply>();
            var switches = new System.Collections.Concurrent.ConcurrentQueue<string>();
            voice.ConversationOpened += id => opened.SetResult(id);
            voice.ReplyReady += replies.Enqueue;
            voice.SpeakerChanged += speaker => switches.Enqueue(speaker.Id);
            var voiceFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            voice.Failed += reason => voiceFailure.TrySetResult(reason);
            await voice.StartAsync("v=0 fake offer");

            const string architect = "Mimarla görüşmek istiyorum";
            User(architect);
            codex.Emit("thread/realtime/itemAdded", new { threadId = "t1",
                item = new { type = "handoff_request", item_id = "first", input_transcript = architect + "." } });
            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "assistant",
                text = "Tabii, mimarla görüşmek için randevu mu istiyorsun?" });
            var id = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => Task.FromResult(replies.Count == 1));
            Assert.Equal("architect", replies.Last().Speaker.Id);

            codex.NextToolCalls.Enqueue(("task_delegate", new { agentId = "developer", brief = "Review the solution." }));
            User("Developer'a kodu inceleme görevini ver.");
            await WaitAsync(() => Task.FromResult(replies.Count == 2));
            Assert.Equal("developer", replies.Last().Speaker.Id);
            Assert.Equal("Test yanıtı", replies.Last().Text);
            Assert.Equal("architect", (await chats.ReadAsync(id, 30, default)).CoordinatorAgentId);

            User("Yok hangi ajanla görüşüyorum ben? Hangi ajanla görüşüyorum?");
            await WaitAsync(() => Task.FromResult(replies.Count == 3));
            Assert.Equal("architect", replies.Last().Speaker.Id);
            User("Görevi kim devraldı?");
            await WaitAsync(() => Task.FromResult(replies.Count == 4 || voiceFailure.Task.IsCompleted));
            if (voiceFailure.Task.IsCompleted) Assert.Fail(await voiceFailure.Task);
            Assert.Equal("architect", replies.Last().Speaker.Id);
            Assert.Contains("Developer", replies.Last().Text);

            User("Bunu Developer ajanına geçiş yapabilir miyiz şimdi?");
            await WaitAsync(() => Task.FromResult(replies.Count == 5));
            Assert.Equal("developer", replies.Last().Speaker.Id);

            User("Merhaba");
            await WaitAsync(() => Task.FromResult(replies.Count == 6));
            var snapshot = await chats.ReadAsync(id, 30, default);
            Assert.Equal(new[] { "developer" }, switches.ToArray());
            Assert.Equal("developer", replies.Last().Speaker.Id);
            Assert.Equal("developer", snapshot.CoordinatorAgentId);
            Assert.Equal("developer", snapshot.Run?.AgentId);
            Assert.Equal(6, snapshot.Messages.Count(m => m.Role == "user"));
            Assert.DoesNotContain(snapshot.Messages, m => m.Text.Contains("randevu"));
            Assert.DoesNotContain("thread/realtime/appendSpeech", codex.Methods);

            await using var db = storage.CreateDbContext();
            var delegated = await db.Runs.SingleAsync(r => r.DelegationDepth == 1);
            Assert.Equal(snapshot.Messages.Single(m => m.Role == "user" && m.Text.Contains("görevini")).Id, delegated.OriginMessageId);
            Assert.NotEqual(delegated.OriginMessageId, delegated.UserMessageId);

            User("Please switch to normal chat.");
            await WaitAsync(() => Task.FromResult(replies.Count == 7));
            Assert.Equal("architect", replies.Last().Speaker.Id);
            Assert.Equal("architect", (await chats.ReadAsync(id, 30, default)).CoordinatorAgentId);

            void User(string text) => codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text });
        }
        finally { await worker.StopAsync(default); }
    }

    [Fact]
    public async Task VoiceStartsWithArchitectAndTransfersGlobalInventoryToAnAuthorizedTeammate()
    {
        await using var storage = await TestStorage.CreateAsync();
        await using var db = storage.CreateDbContext();
        var project = new Project { Name = "Project scope" };
        db.Projects.Add(project);
        db.Resources.AddRange(
            new Resource { Name = "Shared infrastructure", IsGlobal = true, Address = "private.example", Kind = ResourceKind.SshServer },
            new Resource { Name = "Project-only repository", ProjectId = project.Id, Kind = ResourceKind.GitRepository });
        await db.SaveChangesAsync();

        var codex = new FakeCodex();
        var updates = new ChatUpdates();
        var chats = new ChatStore(storage, updates);
        using var worker = Worker(storage, codex, updates);
        await worker.StartAsync(default);
        try
        {
            await using var voice = new ChatVoiceCall(codex, chats, worker, NullLogger<ChatVoiceCall>.Instance,
                conversationId: null, projectId: null, selectedAgent: PlatformChat.Id, autoApprove: false);
            var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replies = new System.Collections.Concurrent.ConcurrentQueue<VoiceReply>();
            voice.ConversationOpened += id => opened.SetResult(id);
            voice.ReplyReady += replies.Enqueue;
            await voice.StartAsync("v=0 fake offer");
            Assert.Equal("architect", voice.Speaker.Id);
            User("Global kaynaklarıma erişebiliyor musun?");
            var id = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => Task.FromResult(replies.Count == 1));
            Assert.Contains("Global resources", Assert.Single(replies).Text);
            Assert.DoesNotContain("Shared infrastructure", Assert.Single(replies).Text);

            var architect = await db.Agents.SingleAsync(a => a.Id == "architect");
            architect.AllowedTools += "," + ToolAccess.GlobalResources;
            await db.SaveChangesAsync();
            User("Global kaynakları şu. Global kaynaklara erişebiliyor musun?");
            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "assistant", text = "Unverified native voice reply." });
            await WaitAsync(() => Task.FromResult(replies.Count == 2));
            var reply = replies.Last();
            Assert.Equal("architect", reply.Speaker.Id);
            Assert.Contains("Shared infrastructure", reply.Text);
            Assert.DoesNotContain("Project-only repository", reply.Text);
            Assert.DoesNotContain("private.example", reply.Text);

            architect.AllowedTools = "memory,resources,delegate";
            (await db.Agents.SingleAsync(a => a.Id == "developer")).AllowedTools = ToolAccess.GlobalResources;
            await db.SaveChangesAsync();
            User("Neler var bir baksana benim global kaynaklarımda");
            await WaitAsync(() => Task.FromResult(replies.Count == 3));
            Assert.Equal("developer", replies.Last().Speaker.Id);
            Assert.Equal("developer", voice.Speaker.Id);
            Assert.Empty(codex.TurnRequests);
            User("Peki, devam edelim.");
            await WaitAsync(() => Task.FromResult(replies.Count == 4));
            var snapshot = await chats.ReadAsync(id, 20, default);
            Assert.Equal("developer", snapshot.Run?.AgentId);
            Assert.Equal("developer", snapshot.CoordinatorAgentId);
            Assert.All(snapshot.Messages.Where(m => m.Role == "assistant"), m => Assert.NotEqual(PlatformChat.Id, m.AgentId));
            Assert.DoesNotContain(snapshot.Messages, m => m.Text == "Unverified native voice reply.");
            Assert.Contains("Shared infrastructure", Assert.Single(codex.TurnRequests).Get("input")[0].String("text"));

            var typedId = await chats.SendAsync(null, "What global resources can you see?", PlatformChat.Id, null, default);
            var typed = Assert.Single((await chats.ReadAsync(typedId, 10, default)).Messages, m => m.Role == "assistant");
            Assert.Equal("developer", typed.AgentId);
            Assert.Contains("Shared infrastructure", typed.Text);

            void User(string text) => codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "user", text });
        }
        finally { await worker.StopAsync(default); }
    }

    [Theory]
    [InlineData("juniper", "Merhaba Architect.", true)]
    [InlineData("maple", "Here's a different answer.", false)]
    public async Task VoicePlaybackUsesTheSavedToneAndOnlyAcceptsTheAppReply(string voice, string rendered, bool matches)
    {
        var codex = new FakeCodex { RenderedVoice = rendered };
        await using (var playback = new CodexVoicePlayback(codex,
            new(new("architect", "Architect", voice), "Merhaba **Architect**.")))
        {
            Assert.Equal("v=0 fake answer", await playback.ConnectAsync("v=0 playback offer"));
            var start = Assert.Single(codex.VoiceRequests);
            Assert.Equal(voice, start.String("voice"));
            Assert.Contains("Merhaba Architect.", start.Get("initialItems")[0].String("text"));
            codex.Emit("thread/realtime/transcript/done", new { threadId = "other-call", role = "assistant", text = "Unrelated reply" });
            Assert.Equal(matches, await playback.RenderAsync());
            Assert.Equal(matches, playback.CanPlay);
            codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "assistant", text = "An extra answer." });
            Assert.False(playback.CanPlay);
            Assert.False(await playback.RenderAsync());
            Assert.Single(codex.Methods, method => method == "thread/realtime/appendSpeech");
        }
        Assert.Contains("thread/realtime/stop", codex.Methods);
    }

    [Fact]
    public async Task VoicePlaybackAcceptsTurkishCasingAcrossTranscriptParts()
    {
        var codex = new FakeCodex();
        await using var playback = new CodexVoicePlayback(codex,
            new(new("architect", "Architect", "juniper"), "İSTANBUL hakkında bilgi."));
        await playback.ConnectAsync("v=0 playback offer");
        var render = playback.RenderAsync();
        codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "assistant", text = "İstanbul hakkında" });
        Assert.False(playback.CanPlay);
        codex.Emit("thread/realtime/transcript/done", new { threadId = "t1", role = "assistant", text = "BİLGİ." });
        Assert.True(await render.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task VoicePlaybackReportsAnUnavailableSavedToneWithoutSubstitutingAnother()
    {
        var codex = new FakeCodex();
        await using var playback = new CodexVoicePlayback(codex, new(new("architect", "Architect", "Old device voice"), "Hello."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => playback.ConnectAsync("v=0 playback offer"));
        Assert.Empty(codex.VoiceRequests);
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
        public bool WorkspaceReady { get; set; }
        public List<string> Methods { get; } = [];
        public List<JsonElement> ThreadRequests { get; } = [];
        public List<JsonElement> TurnRequests { get; } = [];
        public List<JsonElement> VoiceRequests { get; } = [];
        public Queue<(string Name, object Arguments)> NextToolCalls { get; } = [];
        public List<JsonElement> ToolReplies { get; } = [];
        public string? TurnModel { get; private set; }
        public string? VoicePrompt { get; private set; }
        public string? RenderedVoice { get; set; }
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
            if (method == "thread/realtime/listVoices")
                return Task.FromResult(Args(new { voices = new { v3 = new[] { "juniper", "maple" } } }));
            if (method == "thread/realtime/start")
            {
                var request = Args(parameters!);
                VoiceRequests.Add(request);
                VoicePrompt = request.String("prompt");
                Emit("thread/realtime/sdp", new { threadId = request.String("threadId"), sdp = "v=0 fake answer" });
            }
            if (method == "thread/realtime/appendSpeech" && RenderedVoice is { } rendered)
                Emit("thread/realtime/transcript/done", new { threadId = Args(parameters!).String("threadId"), role = "assistant", text = rendered });
            if (method == "windowsSandbox/readiness" && (WorkspaceReady || DenyWorkspaceNamespace))
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
