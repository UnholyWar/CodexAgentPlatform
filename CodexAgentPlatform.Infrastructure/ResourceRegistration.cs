using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;

namespace CodexAgentPlatform.Infrastructure;

public static partial class ResourceRegistration
{
    internal static async Task<object> CreateAsync(PlatformDb db, ChatRun run, JsonElement args, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == run.AgentId, ct);
        if (!ToolAccess.Has(agent, ToolAccess.CreateResources) || !ToolAccess.AllowsProject(agent, run.ProjectId))
            throw new UnauthorizedAccessException("The agent cannot create resources in this conversation.");

        var scope = args.String("scope")?.Trim().ToLowerInvariant() ?? "project";
        var requested = args.String("projectId")?.Trim();
        string? project = null;
        if (scope == "project")
        {
            requested ??= run.ProjectId;
            if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("Specify the target project.");
            project = await AuthorizedProjectAsync(db, agent, requested, ct);
        }
        else if (scope == "global")
        {
            if (requested is not null || !ToolAccess.Has(agent, ToolAccess.GlobalResources))
                throw new UnauthorizedAccessException("Global creation requires Global resources permission and no project argument.");
        }
        else throw new ArgumentException("Select project or global scope.");

        var name = ResourceName(args.String("name")!);
        Resource? source = null;
        if (args.String("sourceId") is { } sourceId)
            source = await VisibleSourceAsync(db, agent, project, sourceId, ct);
        var kind = source?.Kind;
        if (args.String("kind") is { } label)
        {
            if (!Enum.TryParse<ResourceKind>(label, true, out var parsed) || !Enum.GetNames<ResourceKind>().Contains(label, StringComparer.OrdinalIgnoreCase)
                || (source != null && source.Kind != parsed))
                throw new ArgumentException("Choose a supported resource type matching the source.");
            kind = parsed;
        }
        if (kind is null) throw new ArgumentException("Provide a resource type or choose a source.");
        var address = ResourceAddress(args.String("address") ?? source?.Address);
        var item = new Resource { Name = name, Kind = kind.Value, Address = address, ProjectId = project, IsGlobal = project is null, CreatedByAgentId = agent.Id,
            Production = args.True("production") || source?.Production == true };
        if (source != null) await CopyConnectionAsync(db, agent, item, source, ct);
        await ValidateTargetAsync(db, agent, item, args.String("sshServerId"), ct);
        var existing = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.IsGlobal == item.IsGlobal && r.ProjectId == project &&
            EF.Functions.Collate(r.Name, "NOCASE") == name, ct);
        if (existing != null)
        {
            if (existing.Kind != item.Kind || existing.Address != item.Address || existing.ConnectionSourceId != item.ConnectionSourceId ||
                existing.VaultId != null || existing.Production != item.Production || existing.SshServerId != item.SshServerId)
                throw new ArgumentException("A different resource already uses this name in the target scope.");
            return new { existing.Id, existing.Name, existing.ProjectId, existing.IsGlobal, created = false };
        }
        db.Resources.Add(item);
        db.Events.Add(new Activity { AgentId = agent.Id, ProjectId = project, ConversationId = run.ConversationId,
            WorkId = run.WorkId, Kind = "ResourceCreated", Detail = item.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { item.Id, item.Name, item.ProjectId, item.IsGlobal, created = true };
    }

    internal static async Task<object> UpdateAsync(PlatformDb db, ChatRun run, JsonElement args, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == run.AgentId, ct);
        var targetId = args.String("id");
        var item = await db.Resources.SingleOrDefaultAsync(r => r.Id == targetId, ct);
        var project = args.String("projectId") is { } requested ? await AuthorizedProjectAsync(db, agent, requested.Trim(), ct) : run.ProjectId;
        if (!ToolAccess.Has(agent, ToolAccess.CreateResources) || !ToolAccess.AllowsProject(agent, run.ProjectId) ||
            item is null || item.IsGlobal || item.ProjectId is null ||
            item.CreatedByAgentId != agent.Id || !ToolAccess.AllowsProject(agent, item.ProjectId) ||
            (project != null && project != item.ProjectId))
            throw new UnauthorizedAccessException("Choose a project resource created by this agent in an authorized project.");
        var newName = ResourceName(args.String("name") ?? item.Name);
        var address = ResourceAddress(args.String("address") ?? item.Address);
        if (await db.Resources.AnyAsync(r => r.Id != item.Id && r.ProjectId == item.ProjectId &&
            EF.Functions.Collate(r.Name, "NOCASE") == newName, ct))
            throw new ArgumentException("A different resource already uses this name in the target scope.");
        var newSource = args.String("sourceId");
        if ((address != item.Address || newSource != null) && item.VaultId != null)
            throw new InvalidOperationException("The owner configured a credential for this exact address. Choose a saved connection for a new address.");
        item.Name = newName;
        item.Address = address;
        if (newSource != null)
            await CopyConnectionAsync(db, agent, item, await VisibleSourceAsync(db, agent, item.ProjectId, newSource, ct), ct);
        else if (item.ConnectionSourceId is { } sourceId)
            ValidateConnection(item, await VisibleSourceAsync(db, agent, item.ProjectId, sourceId, ct));
        await ValidateTargetAsync(db, agent, item, args.String("sshServerId"), ct);
        if (!db.ChangeTracker.HasChanges()) return new { item.Id, item.Name, item.Address, updated = false };
        db.Events.Add(new Activity { AgentId = agent.Id, ProjectId = item.ProjectId, ConversationId = run.ConversationId,
            WorkId = run.WorkId, Kind = "ResourceUpdated", Detail = item.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { item.Id, item.Name, item.Address, updated = true };
    }

    private static async Task CopyConnectionAsync(PlatformDb db, Agent agent, Resource item, Resource source, CancellationToken ct)
    {
        if (item.Id == source.Id || item.Kind != source.Kind)
            throw new ArgumentException("Choose a separate source of the same resource type.");
        item.Username = source.Username;
        item.HostFingerprint = source.HostFingerprint;
        item.Port = source.Port;
        item.SftpFile = source.SftpFile;
        item.SshServerId = source.SshServerId;
        item.Production |= source.Production;
        item.ConnectionSourceId = null;
        if (source.ConnectionSourceId != null)
        {
            var connection = await VisibleSourceAsync(db, agent, item.ProjectId, source.ConnectionSourceId, ct);
            ValidateConnection(source, connection);
            source = connection;
        }
        if (source.VaultId != null)
        {
            ValidateConnection(item, source);
            item.ConnectionSourceId = source.Id;
            item.Production |= source.Production;
        }
    }

    private static async Task ValidateTargetAsync(PlatformDb db, Agent agent, Resource item, string? sshServerId, CancellationToken ct)
    {
        if (item.Kind == ResourceKind.GitRepository) _ = Operations.GitAddress(item);
        if (item.Kind == ResourceKind.SshServer)
        {
            if (item.ConnectionSourceId == null && item.VaultId == null)
                throw new ArgumentException("Choose a saved SSH server with a key, or configure a new SSH key in Resources.");
            Operations.ValidateSshServer(item);
        }
        if (item.Kind == ResourceKind.DeploymentTarget)
        {
            if (!Operations.IsSwarmServiceName(item.Address)) throw new ArgumentException("Enter a valid Docker Swarm service name.");
            var host = sshServerId ?? item.SshServerId ?? throw new ArgumentException("Choose an SSH server for the deployment target.");
            if ((await VisibleSourceAsync(db, agent, item.ProjectId, host, ct)).Kind != ResourceKind.SshServer)
                throw new ArgumentException("Choose an SSH server for the deployment target.");
            item.SshServerId = host;
        }
    }

    private static async Task<string> AuthorizedProjectAsync(PlatformDb db, Agent agent, string requested, CancellationToken ct)
    {
        var projects = await db.Projects.Where(p => p.Id == requested || p.Name == requested).Select(p => p.Id).Take(2).ToListAsync(ct);
        if (projects.Count != 1 || !ToolAccess.AllowsProject(agent, projects[0]))
            throw new UnauthorizedAccessException("Choose a project this agent is allowed to access.");
        return projects[0];
    }

    private static string ResourceName(string name) => name.Trim() is { Length: > 0 and <= 100 } value
        ? value : throw new ArgumentException("Enter a resource name between 1 and 100 characters.");

    private static string ResourceAddress(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 1024)
            throw new ArgumentException("Provide a resource address up to 1,024 characters.");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            throw new ArgumentException("Keep credentials out of the address.");
        return value;
    }

    private static async Task<Resource> VisibleSourceAsync(PlatformDb db, Agent agent, string? project, string id, CancellationToken ct)
    {
        var source = await db.Resources.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (source is null || !(source.IsGlobal && source.ProjectId is null ? ToolAccess.Has(agent, ToolAccess.GlobalResources) :
            !source.IsGlobal && project != null && source.ProjectId == project && ToolAccess.Has(agent, ToolAccess.ProjectResources)))
            throw new UnauthorizedAccessException("The source resource is not available in this scope.");
        return source;
    }

    public static void ValidateConnection(Resource item, Resource? source)
    {
        if (source is null || source.Id == item.Id || source.Kind != item.Kind || source.VaultId is null ||
            source.ConnectionSourceId != null || !(source.IsGlobal && source.ProjectId is null || item.ProjectId != null && item.ProjectId == source.ProjectId))
            throw new InvalidOperationException("The saved connection is unavailable for this resource.");
        if (item.Kind == ResourceKind.GitRepository)
        {
            var origin = new Uri(Operations.GitAddress(source));
            var target = new Uri(Operations.GitAddress(item));
            if (origin == target) return;
            var path = target.AbsolutePath.TrimEnd('/');
            var sourceOwner = origin.AbsolutePath.Trim('/').Split('/')[0];
            if (origin.Host == "github.com" && origin.IsDefaultPort)
            {
                if (target.Host == origin.Host && target.IsDefaultPort && GitHubRepositoryPath().IsMatch(path) &&
                    (sourceOwner.Length == 0 || path.Split('/')[1].Equals(sourceOwner, StringComparison.OrdinalIgnoreCase))) return;
            }
            else if (IsHttpsChild(origin, target)) return;
            throw new InvalidOperationException("Use a repository within the saved connection's HTTPS host and namespace.");
        }
        if (item.Kind is ResourceKind.ApiConnection or ResourceKind.ExternalService &&
            Uri.TryCreate(source.Address, UriKind.Absolute, out var baseUri) && Uri.TryCreate(item.Address, UriKind.Absolute, out var childUri) &&
            IsHttpsChild(baseUri, childUri)) return;
        if (item.Address != source.Address || item.Kind == ResourceKind.SshServer &&
            (item.Port != source.Port || item.Username != source.Username || item.HostFingerprint != source.HostFingerprint || item.SftpFile != source.SftpFile))
            throw new InvalidOperationException("The connection details must match the saved source.");
    }

    private static bool IsHttpsChild(Uri origin, Uri target) => origin.Scheme == Uri.UriSchemeHttps && target.Scheme == origin.Scheme &&
        target.IdnHost == origin.IdnHost && target.Port == origin.Port && target.UserInfo.Length == 0 && target.Query.Length == 0 &&
        target.Fragment.Length == 0 && !target.AbsolutePath.Contains('%') &&
        target.AbsolutePath.StartsWith(origin.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);

    [GeneratedRegex(@"^/[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex GitHubRepositoryPath();
}
