using System.Text.Json;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Components;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexAgentPlatform.Tests;

public sealed class PlatformTests
{
    [Fact]
    public async Task MigrationSeedsAgentsAndProducesReadableBackup()
    {
        await using var fixture = await TestStorage.CreateAsync();
        await using var db = fixture.CreateDbContext();
        Assert.Equal(4, await db.Agents.CountAsync());
        Assert.Contains("20261117223000_ChatAutoApproval", await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Matches("^[0-9A-F]{64}$", (await File.ReadAllTextAsync(fixture.Paths.BootstrapFile)).Trim());

        var backup = await fixture.Startup.BackupAsync(default);
        await using var copy = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(fixture.Paths.Data, "backups", backup)};Pooling=False");
        await copy.OpenAsync();
        var command = copy.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task IdentityStoreOnlyAcceptsOneOwnerAndKeepsAHash()
    {
        await using var fixture = await TestStorage.CreateAsync();
        var owner = new PlatformUser { Id = PlatformUser.OwnerId, UserName = "operator", NormalizedUserName = "OPERATOR" };
        const string password = "Unit-test-only!2026";
        owner.PasswordHash = new PasswordHasher<PlatformUser>().HashPassword(owner, password);
        using var store = new OwnerStore(fixture);
        Assert.True((await store.CreateAsync(owner, default)).Succeeded);
        Assert.False((await store.CreateAsync(new PlatformUser { Id = PlatformUser.OwnerId, UserName = "admin" }, default)).Succeeded);
        var actual = Assert.IsType<PlatformUser>(await store.FindByNameAsync("OPERATOR", default));
        Assert.NotEqual(password, actual.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<PlatformUser>().VerifyHashedPassword(actual, actual.PasswordHash!, password));
        Assert.Null(await store.FindByNameAsync("ADMIN", default));
    }

    [Fact]
    public async Task FtsSearchFollowsNoteVersionsAndArchiveAndReadsSections()
    {
        await using var fixture = await TestStorage.CreateAsync();
        var memory = new MemoryStore(fixture, fixture.Paths);
        var draft = new MemoryDraft("Python utility", "Rapor", "python", "# List\na.py\n# Other\nHidden");
        var first = await memory.SaveAsync(null, draft, "developer", null, null, null, default);
        Assert.Equal(first.Id, Assert.Single(await memory.SearchAsync("Python", null, null, false, default)).Id);
        var second = await memory.SaveAsync(first.Id, draft with { Title = "Dosya boyutları" }, "developer", null, null, null, default);
        Assert.Equal(2, second.Version);
        Assert.Empty(await memory.SearchAsync("utility", null, null, false, default));
        Assert.Equal("# List\na.py", await memory.ReadAsync(first.Id, 1, "List", default));
        await memory.ArchiveAsync(first.Id, "developer", null, null, false, default);
        Assert.Empty(await memory.SearchAsync("boyutları", null, null, false, default));
        Assert.Single(await memory.SearchAsync("boyutları", null, null, true, default));
    }

    [Fact]
    public async Task TimelineUsesEventTimeAndLocalDateBoundaries()
    {
        await using var fixture = await TestStorage.CreateAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        var now = new DateTime(2026, 11, 17, 21, 30, 0, DateTimeKind.Utc);
        var range = Assert.IsType<TimeRange>(TemporalQuery.Parse("dün yaptığımız iş", now, zone));
        Assert.Equal(new DateTime(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc), range.FromUtc);
        await using (var db = fixture.CreateDbContext())
        {
            db.Events.AddRange(new Activity { Kind = "TestExecuted", EventOccurredAtUtc = range.FromUtc },
                new Activity { Kind = "GitPushed", EventOccurredAtUtc = range.UntilUtc });
            await db.SaveChangesAsync();
        }
        var memory = new MemoryStore(fixture, fixture.Paths);
        Assert.Equal("TestExecuted", Assert.Single(await memory.TimelineAsync(range, null, null, default)).Kind);
    }

    [Fact]
    public async Task RecoveryMarksExistingWorkInterruptedWithoutReplayingApprovedOperations()
    {
        await using var fixture = await TestStorage.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            db.Work.Add(new WorkItem { AgentId = "developer", Workspace = Guid.NewGuid().ToString("N"), Status = WorkStatus.Running });
            db.Runs.Add(new ChatRun { AgentId = "developer", ConversationId = "chat", Status = WorkStatus.Running });
            db.Approvals.AddRange(new Approval { Status = "Executing" }, new Approval { Status = "Approved" });
            await db.SaveChangesAsync();
        }
        await fixture.Startup.InitializeAsync(default);
        await using var recovered = fixture.CreateDbContext();
        Assert.Equal(WorkStatus.Interrupted, (await recovered.Runs.SingleAsync()).Status);
        Assert.Equal(WorkStatus.Interrupted, (await recovered.Work.SingleAsync()).Status);
        Assert.All(await recovered.Approvals.ToListAsync(), x => Assert.Equal("Interrupted", x.Status));
    }

    [Fact]
    public void SshSourceAcceptsPrivateKeyAndRejectsPublicKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        Operations.ValidateSshPrivateKey(rsa.ExportRSAPrivateKeyPem());
        Assert.Throws<ArgumentException>(() => Operations.ValidateSshPrivateKey(rsa.ExportRSAPublicKeyPem()));
    }

    [Fact]
    public void SshSourceAcceptsAHostFingerprintOrTheCompleteOpenSshFingerprintLine()
    {
        var digest = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("test host key"u8));
        var fingerprint = "SHA256:" + digest.TrimEnd('=');
        var resource = new Resource
        {
            Kind = ResourceKind.SshServer, IsGlobal = true, Address = "host.example", Port = 2222, Username = " deploy ",
            HostFingerprint = $"256 SHA256:{digest} host.example (ED25519)"
        };

        Operations.ValidateSshServer(resource);
        Assert.Equal(fingerprint, resource.HostFingerprint);
        Assert.Equal("deploy", resource.Username);
        Operations.ValidateSshServer(resource);

        resource.HostFingerprint = $"{fingerprint} {fingerprint}";
        Assert.Contains("host key fingerprint", Assert.Throws<ArgumentException>(() => Operations.ValidateSshServer(resource)).Message);
        resource.Address = "ssh://host.example:2222";
        Assert.Contains("hostname or IP", Assert.Throws<ArgumentException>(() => Operations.ValidateSshServer(resource)).Message);
    }

    [Fact]
    public async Task VaultAndBrokerEnforceScopeBeforeConnecting()
    {
        await using var fixture = await TestStorage.CreateAsync();
        var key = Path.Combine(fixture.Paths.Data, "test-vault-key");
        await File.WriteAllTextAsync(key, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var vault = new Vault(fixture.Paths, key);
        const string secret = "test secret value";
        var vaultId = await vault.StoreAsync(secret, default);
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(fixture.Paths.Data, "credentials", $"{vaultId}.vault")));
        var operations = new Operations(fixture, fixture.Paths, vault);
        await using var db = fixture.CreateDbContext();
        var resource = new Resource { Name = "test only", Kind = ResourceKind.SshServer, ProjectId = "project", VaultId = vaultId };
        db.Resources.Add(resource);
        await db.SaveChangesAsync();
        var developer = await db.Agents.SingleAsync(x => x.Id == "developer");
        var devops = await db.Agents.SingleAsync(x => x.Id == "devops");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.InspectSshAsync(developer, resource.Id, "test", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.InspectSshAsync(devops, resource.Id, "test", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.DecideAsync("nonexistent", true, "other", default));
        devops.AllowedProjects = "project";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.InspectSshAsync(devops, resource.Id, "anything", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => operations.RequestAsync(devops, "c", resource.Id, "GitPush", "push", null, null, default));
    }

    [Fact]
    public async Task GlobalResourcesWorkWithoutProjectAndKeepToolPermissions()
    {
        await using var fixture = await TestStorage.CreateAsync();
        await using var db = fixture.CreateDbContext();
        var ops = new Operations(fixture, fixture.Paths, new Vault(fixture.Paths, null));
        var tools = new AgentTools(fixture, new MemoryStore(fixture, fixture.Paths), ops, fixture.Paths);
        var global = new Resource { IsGlobal = true, Kind = ResourceKind.SshServer, Name = "Ortak sunucu" };
        var project = new Resource { ProjectId = "private", Kind = ResourceKind.SshServer, Name = "Özel sunucu" };
        db.Resources.AddRange(global, project, new Resource { Name = "Eski atanmamış kaynak" });
        await db.SaveChangesAsync();
        var devops = await db.Agents.SingleAsync(a => a.Id == "devops");
        var run = new ChatRun { AgentId = devops.Id, ConversationId = "chat" };
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new { id = global.Id, operation = "restart-docker" });
        var globalSearch = System.Text.Json.JsonSerializer.SerializeToElement(new { scope = "global" });
        var globalGet = System.Text.Json.JsonSerializer.SerializeToElement(new { id = global.Id });
        devops.AllowedProjects = "private";
        await db.SaveChangesAsync();
        Assert.Equal(project.Id, Assert.Single(await ops.VisibleResourcesAsync(devops, null, default)).Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync("resource_search", globalSearch, run, default));
        Assert.Equal("[]", System.Text.Json.JsonSerializer.Serialize(await tools.RunAsync("resource_get", globalGet, run, default)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync("ssh_request_operation", args, run, default));

        devops.AllowedTools = $"ssh,{ToolAccess.GlobalResources}";
        await db.SaveChangesAsync();
        Assert.Equal(global.Id, Assert.Single(await ops.VisibleResourcesAsync(devops, null, default)).Id);
        Assert.Contains(global.Id, System.Text.Json.JsonSerializer.Serialize(await tools.RunAsync("resource_search", globalSearch, run, default)));
        Assert.Contains(global.Id, System.Text.Json.JsonSerializer.Serialize(await tools.RunAsync("resource_get", globalGet, run, default)));
        var approval = Assert.IsType<ApprovalReply>(await tools.RunAsync("ssh_request_operation", args, run, default));
        Assert.Equal("Pending", approval.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync("ssh_request_operation",
            System.Text.Json.JsonSerializer.SerializeToElement(new { id = project.Id, operation = "restart-docker" }), run, default));
        run.AgentId = "developer";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.RunAsync("ssh_request_operation", args, run, default));
    }

    [Fact]
    public async Task ChatSwitchOnlyApprovesEligibleRequestsAndRevokesUnclaimedDecisions()
    {
        await using var fixture = await TestStorage.CreateAsync();
        await using var db = fixture.CreateDbContext();
        var ops = new Operations(fixture, fixture.Paths, new Vault(fixture.Paths, null));
        var agent = await db.Agents.SingleAsync(a => a.Id == "devops");
        agent.AllowedProjects = "p";
        var host = new Resource { Name = "Dev", Kind = ResourceKind.SshServer, ProjectId = "p", Address = "test.invalid" };
        var chat = new Conversation();
        db.AddRange(host, chat);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ops.SetAutoApprovalAsync(chat.Id, true, "other", default));
        await ops.SetAutoApprovalAsync(chat.Id, true, PlatformUser.OwnerId, default);
        Task<Approval> Request() => ops.RequestAsync(agent, chat.Id, host.Id, "SshChange", "restart-docker", null, null, default);
        var first = await Request();
        Assert.Equal("Pending", first.Status);
        await ops.DecideAsync(first.Id, true, PlatformUser.OwnerId, default);
        // Record the successful prior operation without connecting to a server.
        await db.Approvals.Where(a => a.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, "Completed"));
        var repeated = await Request();
        Assert.Equal("Approved", repeated.Status);
        Assert.Equal(Operations.AutomaticDecision, repeated.DecidedBy);
        await ops.SetAutoApprovalAsync(chat.Id, false, PlatformUser.OwnerId, default);
        Assert.Equal("Pending", (await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == repeated.Id)).Status);
        await ops.SetAutoApprovalAsync(chat.Id, true, PlatformUser.OwnerId, default);
        Assert.Equal("Approved", (await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == repeated.Id)).Status);
        host.Production = true;
        await db.SaveChangesAsync();
        Assert.Equal("Pending", (await Request()).Status);
    }

    [Fact]
    public void ProductionApprovalsKeepTheirWarningWithEitherStoredLanguage()
    {
        Assert.True(ApprovalDisplay.IsProduction("ÜRETİM · repository"));
        Assert.True(ApprovalDisplay.IsProduction("PRODUCTION · repository"));
        Assert.False(ApprovalDisplay.IsProduction("repository"));
        Assert.Equal("PRODUCTION · repository", ApprovalDisplay.Scope("ÜRETİM · repository"));
        Assert.Equal("PRODUCTION · repository", ApprovalDisplay.Scope("PRODUCTION · repository"));
    }

    [Fact]
    public void MarkdownAlwaysEncodesUntrustedHtmlAndRejectsUnsafeLinks()
    {
        var html = SafeMarkdown.Render("<img src=x onerror=alert(1)>\n[link](javascript:alert)\n[docs](https://example.org/docs)");
        Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.Contains("href=\"https://example.org/docs\"", html);
    }
}

internal sealed class TestStorage : IDbContextFactory<PlatformDb>, IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexAgentPlatform.Tests", Guid.NewGuid().ToString("N"));
    public PlatformPaths Paths { get; }
    public StorageStartup Startup { get; }
    private readonly DbContextOptions<PlatformDb> options;
    private TestStorage()
    {
        Paths = new PlatformPaths(Path.Combine(root, "data"), Path.Combine(root, "workspaces"));
        options = new DbContextOptionsBuilder<PlatformDb>().UseSqlite(Paths.ConnectionString + ";Pooling=False").Options;
        Startup = new StorageStartup(this, Paths);
    }
    public static async Task<TestStorage> CreateAsync()
    {
        var storage = new TestStorage();
        await storage.Startup.InitializeAsync(default);
        return storage;
    }
    public PlatformDb CreateDbContext() => new(options);
    public ValueTask DisposeAsync()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Paths.ConnectionString);
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        var resolved = Path.GetFullPath(root);
        var expected = Path.Combine(Path.GetTempPath(), "CodexAgentPlatform.Tests") + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(expected, StringComparison.Ordinal) && Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            if (OperatingSystem.IsWindows())
            {
                var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(resolved, "*", enumeration))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            Directory.Delete(resolved, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}
