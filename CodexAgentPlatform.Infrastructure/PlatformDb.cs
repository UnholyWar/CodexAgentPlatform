using CodexAgentPlatform.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public sealed class PlatformUser : IdentityUser
{
    public const string OwnerId = "owner";
}

public sealed class PlatformDb(DbContextOptions<PlatformDb> options) : DbContext(options)
{
    public DbSet<PlatformUser> Users => Set<PlatformUser>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ConversationThread> Threads => Set<ConversationThread>();
    public DbSet<ChatRun> Runs => Set<ChatRun>();
    public DbSet<WorkItem> Work => Set<WorkItem>();
    public DbSet<Activity> Events => Set<Activity>();
    public DbSet<MemoryNote> Notes => Set<MemoryNote>();
    public DbSet<MemoryRevision> Revisions => Set<MemoryRevision>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<TokenUsage> Usage => Set<TokenUsage>();
    public DbSet<Preferences> Preferences => Set<Preferences>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PlatformUser>().HasIndex(x => x.NormalizedUserName).IsUnique();
        model.Entity<PlatformUser>().Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
        model.Entity<ConversationThread>().HasKey(x => new { x.ConversationId, x.AgentId, x.Workspace });
        model.Entity<MemoryRevision>().HasKey(x => new { x.NoteId, x.Version });
        model.Entity<MemoryNote>().Property(x => x.Version).IsConcurrencyToken();
        model.Entity<TokenUsage>().HasKey(x => new { x.ThreadId, x.TurnId });
        model.Entity<Agent>().Property(x => x.Name).UseCollation("NOCASE");
        model.Entity<Agent>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Project>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Message>().HasIndex(x => new { x.ConversationId, x.Id });
        model.Entity<ChatRun>().HasIndex(x => new { x.ConversationId, x.Status });
        model.Entity<ChatRun>().HasIndex(x => x.ConversationId).IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 2, 3)");
        model.Entity<WorkItem>().HasIndex(x => x.ConversationId);
        model.Entity<Approval>().HasIndex(x => x.ContinuationRunId);
        model.Entity<Activity>().HasIndex(x => x.EventOccurredAtUtc);
        model.Entity<Activity>().HasIndex(x => new { x.ConversationId, x.Id });
        model.Entity<Resource>().HasIndex(x => x.ProjectId);
        model.Entity<Resource>().HasOne<Resource>().WithMany().HasForeignKey(x => x.ConnectionSourceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PlatformPaths
{
    public PlatformPaths(string data, string workspaces)
    {
        Data = Path.GetFullPath(data);
        Workspaces = Path.GetFullPath(workspaces);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Data.Equals(Workspaces, comparison) ||
            Data.StartsWith(Path.TrimEndingDirectorySeparator(Workspaces) + Path.DirectorySeparatorChar, comparison) ||
            Workspaces.StartsWith(Path.TrimEndingDirectorySeparator(Data) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Data and workspace paths must be separate.");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Workspaces);
        CheckLinks(Data);
        CheckLinks(Workspaces);
        foreach (var name in new[] { "database", "memory", "tasks", "codex", "dataprotection-keys", "backups", "credentials", "logs" })
            Directory.CreateDirectory(Path.Combine(Data, name));
    }

    public string Data { get; }
    public string Workspaces { get; }
    public string Database => Path.Combine(Data, "database", "agents.db");
    public string CodexHome => Path.Combine(Data, "codex");
    public string BootstrapFile => Path.Combine(Data, "credentials", "bootstrap-code");
    public string KeyDirectory => Path.Combine(Data, "dataprotection-keys");
    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Database, ForeignKeys = true, DefaultTimeout = 15
    }.ToString();

    public string Workspace(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid workspace ID.");
        var path = Path.Combine(Workspaces, id);
        Directory.CreateDirectory(path);
        CheckLinks(path);
        return path;
    }

    public static void CheckLinks(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links are not allowed in managed paths.");
        }
    }
}
