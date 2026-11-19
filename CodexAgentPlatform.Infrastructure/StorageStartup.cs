using System.Security.Cryptography;
using CodexAgentPlatform.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed class StorageStartup(IDbContextFactory<PlatformDb> dbs, PlatformPaths paths)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL", ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Preferences.AnyAsync(ct))
        {
            db.Preferences.Add(new Preferences());
            db.Agents.AddRange(
                CreateAgent("architect", "Architect", "Senior Software Architect", "Design architecture, analyze trade-offs and coordinate work.", "memory,resources,delegate"),
                CreateAgent("developer", "Developer", "Senior Full-Stack Developer", "Implement, refactor and test real code. Verify results from the workspace.", "memory,resources,workspace,git"),
                CreateAgent("qa", "QA Engineer", "Software Quality Engineer", "Investigate regressions and verify changes with tests.", "memory,resources,workspace"),
                CreateAgent("devops", "DevOps Engineer", "DevOps & Infrastructure Engineer", "Inspect authorized infrastructure and request approval for changes.", "memory,resources,workspace,git,ssh"));
            await db.SaveChangesAsync(ct);
        }

        var now = DateTime.UtcNow;
        var stale = await db.Runs.Where(x => x.Status == WorkStatus.Running || x.Status == WorkStatus.WaitingForUser ||
            (x.Status == WorkStatus.Pending || x.Status == WorkStatus.WaitingForApproval) &&
            !db.Approvals.Any(a => a.ContinuationRunId == x.Id && (a.Status == "Pending" || a.Status == "Completed"))).ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Status = WorkStatus.Interrupted;
            run.UpdatedAtUtc = now;
            run.Error = "The application restarted. Resume safely from the chat.";
            db.Events.Add(new Activity
            {
                ConversationId = run.ConversationId, AgentId = run.AgentId, WorkId = run.WorkId,
                Kind = "RunInterrupted", Detail = run.Error
            });
        }
        var interrupted = await db.Work.Where(x => x.Status == WorkStatus.Running ||
            x.Status == WorkStatus.WaitingForApproval).ToListAsync(ct);
        foreach (var work in interrupted)
        {
            work.Status = WorkStatus.Interrupted;
            work.UpdatedAtUtc = now;
        }
        await db.Approvals.Where(x => x.Status == "Executing" || x.Status == "Approved" ||
            x.Status == "Pending" && x.ContinuationRunId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Interrupted"), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var work in interrupted) await WorkspaceMemory.WriteWorkAsync(paths, work, ct);
        foreach (var agent in await db.Agents.AsNoTracking().ToListAsync(ct))
            await WorkspaceMemory.WriteAgentAsync(paths, agent, ct);

        if (!await db.Users.AnyAsync(ct) && !File.Exists(paths.BootstrapFile))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var temp = paths.BootstrapFile + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (var file = new FileStream(temp, options))
                    await file.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))), ct);
                File.Move(temp, paths.BootstrapFile);
            }
            catch (IOException) when (File.Exists(paths.BootstrapFile)) { }
            finally { File.Delete(temp); }
        }
    }

    public async Task<string> BackupAsync(CancellationToken ct)
    {
        var name = $"agents-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
        var destination = Path.Combine(paths.Data, "backups", name);
        await using var src = new SqliteConnection(new SqliteConnectionStringBuilder(paths.ConnectionString) { Pooling = false }.ToString());
        await using var dst = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination, Pooling = false
        }.ToString());
        await src.OpenAsync(ct);
        await dst.OpenAsync(ct);
        src.BackupDatabase(dst);
        return name;
    }

    private static Agent CreateAgent(string id, string name, string title, string instructions, string tools) =>
        new() { Id = id, Name = name, Title = title, Instructions = instructions, AllowedTools = tools };
}
