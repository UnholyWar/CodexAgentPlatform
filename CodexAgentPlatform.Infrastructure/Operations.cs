using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Activity = CodexAgentPlatform.Application.Activity;

namespace CodexAgentPlatform.Infrastructure;

public sealed record OperationTarget(string ResourceId, string Operation, string? WorkspaceId = null,
    string? Branch = null, string? Commit = null, string? ResourceBinding = null, string? Image = null, string? WorkId = null);

public sealed partial class Operations(IDbContextFactory<PlatformDb> dbs, PlatformPaths paths, Vault vault)
{
    public const string AutomaticDecision = "owner:auto";

    public async Task SetAutoApprovalAsync(string conversation, bool enabled, string ownerId, CancellationToken ct)
    {
        if (ownerId != PlatformUser.OwnerId) throw new UnauthorizedAccessException();
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var count = await db.Conversations.Where(x => x.Id == conversation)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AutoApprove, enabled), ct);
        if (count == 0) throw new InvalidOperationException("Conversation not found.");
        var now = DateTime.UtcNow;
        var requests = db.Approvals.Where(x => x.ConversationId == conversation && x.Kind != AgentPermissionGrants.ApprovalKind &&
            x.AutoEligible && x.ExpiresAtUtc > now);
        if (enabled)
            await requests.Where(x => x.Status == "Pending").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Approved")
                .SetProperty(x => x.DecidedBy, AutomaticDecision), ct);
        else
            await requests.Where(x => x.Status == "Approved" && x.DecidedBy == AutomaticDecision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Pending").SetProperty(x => x.DecidedBy, (string?)null), ct);
        db.Events.Add(new Activity { ConversationId = conversation, Kind = enabled ? "AutoApprovalEnabled" : "AutoApprovalDisabled" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static readonly Dictionary<string, string> SshCommands = new(StringComparer.Ordinal)
    {
        ["health"] = "uptime && df -hP && free -m",
        ["docker-status"] = "docker ps --format '{{.Names}} {{.Status}}'",
        ["docker-services"] = "docker service ls",
        ["linux-services"] = "systemctl --failed --no-pager --plain",
        ["docker-logs"] = "journalctl -u docker -n 100 --no-pager",
        ["restart-docker"] = "systemctl restart docker"
    };

    public async Task<List<Resource>> VisibleResourcesAsync(Agent agent, string? query, CancellationToken ct, string? projectId = null)
    {
        if (!ToolAccess.Has(agent, "resources")) return [];
        await using var db = await dbs.CreateDbContextAsync(ct);
        var projects = ToolAccess.Split(agent.AllowedProjects);
        var resources = db.Resources.AsNoTracking().Where(x => x.ProjectId == null ? x.IsGlobal : Enumerable.Contains(projects, x.ProjectId));
        if (projectId is not null) resources = resources.Where(r => r.ProjectId == projectId || r.ProjectId == null);
        if (!string.IsNullOrWhiteSpace(query)) resources = resources.Where(x => x.Name.Contains(query));
        return await resources.OrderByDescending(x => projectId != null && x.ProjectId == projectId).ThenBy(x => x.Name).Take(25).ToListAsync(ct);
    }

    public async Task<object> InspectSshAsync(Agent agent, string resourceId, string command, CancellationToken ct,
        string? conversationId = null, string? workId = null, int offset = 0)
    {
        var resource = await ResolveAsync(agent, resourceId, ResourceKind.SshServer, "ssh", ct);
        if (command is "restart-docker" || (command is not ("test" or "sftp-read") && !SshCommands.ContainsKey(command)))
            throw new InvalidOperationException("This SSH operation requires approval.");
        var result = await SshAsync(resource, command, ct, offset: offset);
        await AuditAsync(agent.Id, resource.ProjectId, conversationId, workId, "SSHExecuted", $"{resource.Name} · {command}", ct);
        return result;
    }

    public async Task<object> GitAsync(Agent agent, string resourceId, string workspaceId,
        string operation, string? argument, CancellationToken ct, string? conversationId = null, string? workId = null, int offset = 0)
    {
        if (offset != 0 && operation is not ("status" or "diff" or "list" or "read"))
            throw new ArgumentException("This Git operation does not support pagination.");
        var resource = await ResolveAsync(agent, resourceId, ResourceKind.GitRepository, "git", ct);
        var root = paths.Workspace(workspaceId);
        var repo = Path.Combine(root, "project");
        if (operation == "prepare")
        {
            if (Directory.Exists(repo))
            {
                await VerifyRepoAsync(resource, repo, ct);
                return new { path = "project", branch = (await RunGitAsync(repo, ["branch", "--show-current"], null, ct)).Trim() };
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var branch = resource.IsGlobal ? null : await db.Projects.Where(x => x.Id == resource.ProjectId).Select(x => x.DefaultBranch).SingleAsync(ct);
            if (branch is not null) ValidateBranch(branch);
            var cacheRoot = Path.Combine(paths.Workspaces, ".git-cache");
            Directory.CreateDirectory(cacheRoot);
            PlatformPaths.CheckLinks(cacheRoot);
            var clone = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
            var url = GitAddress(resource);
            var cloneArgs = new List<string> { "clone", "--no-checkout", "--single-branch" };
            if (branch is not null) cloneArgs.AddRange(["--branch", branch]);
            cloneArgs.AddRange(["--", url, clone]);
            await RunGitAsync(root, cloneArgs.ToArray(), resource, ct);
            await RunGitAsync(clone, ["worktree", "add", "-b", $"agent/{workspaceId}", "--", repo, branch is null ? "HEAD" : $"refs/remotes/origin/{branch}"], null, ct);
            await File.WriteAllTextAsync(Path.Combine(clone, ".git", "platform-resource"), resource.Id, ct);
            await AuditAsync(agent.Id, resource.ProjectId, conversationId, workId, "GitWorkspacePrepared", $"{resource.Name} · {branch}", ct);
            return new { path = "project", branch = $"agent/{workspaceId}" };
        }

        await VerifyRepoAsync(resource, repo, ct);
        var output = operation switch
        {
            "list" => await ListTreeAsync(repo, argument, ct),
            "read" => await ReadBlobAsync(repo, argument, ct),
            "status" => await RunGitAsync(repo, ["status", "--short", "--branch"], null, ct),
            "diff" => await RunGitAsync(repo, ["diff", "--no-ext-diff", "--no-textconv", "HEAD", "--"], null, ct),
            "fetch" => await RunGitAsync(repo, ["fetch", "--no-recurse-submodules", GitAddress(resource)], resource, ct),
            "pull" => await RunGitAsync(repo, ["pull", "--ff-only", "--no-rebase", "--no-recurse-submodules", GitAddress(resource), SafeBranch(argument)], resource, ct),
            "branch" => await RunGitAsync(repo, ["switch", "-c", SafeBranch(argument)], null, ct),
            "commit" => await CommitAsync(repo, argument, ct),
            _ => throw new InvalidOperationException("Unknown Git operation.")
        };
        await AuditAsync(agent.Id, resource.ProjectId, conversationId, workId, operation == "commit" ? "GitCommitted" : "GitOperation",
            $"{resource.Name} · {operation}", ct);
        if (operation is "diff" or "status" or "list" or "read") return ToolOutput.Page(output, offset, 16_000);
        return new { output = TextLimits.Clip(output, 3000), truncated = output.Length > 3000 };
    }

    public async Task<Approval> RequestAsync(Agent agent, string conversationId, string resourceId, string kind,
        string operation, string? workspaceId, string? branch, CancellationToken ct, string? image = null, string? workId = null)
    {
        OperationTarget target;
        string scope;
        Resource resource;
        var globalServer = false;
        if (kind == "GitPush")
        {
            resource = await ResolveAsync(agent, resourceId, ResourceKind.GitRepository, "git", ct);
            var repo = Path.Combine(paths.Workspace(workspaceId!), "project");
            await VerifyRepoAsync(resource, repo, ct);
            var local = SafeBranch((await RunGitAsync(repo, ["branch", "--show-current"], null, ct)).Trim());
            var commit = (await RunGitAsync(repo, ["rev-parse", "--verify", "HEAD^{commit}"], null, ct)).Trim();
            var destination = SafeBranch(branch ?? local);
            target = new(resourceId, local, workspaceId, destination, commit, Binding(resource));
            scope = $"{resource.Name} · {local} → {destination} · {commit} · {GitAddress(resource)}";
        }
        else if (kind == "SshRelease")
        {
            var deployment = await ReleaseAsync(agent, resourceId, operation, image, ct);
            resource = deployment.Target;
            globalServer = deployment.Server.IsGlobal;
            target = new(resourceId, operation, ResourceBinding: deployment.Binding, Image: image);
            scope = $"{deployment.Target.Name} · {deployment.Server.Address}:{deployment.Server.Port} · {deployment.Command}";
            if (deployment.Server.Production) resource.Production = true;
        }
        else
        {
            resource = await ResolveAsync(agent, resourceId, ResourceKind.SshServer, "ssh", ct);
            if (kind != "SshChange" || operation != "restart-docker")
                throw new InvalidOperationException("This change operation is not registered.");
            target = new(resourceId, operation, ResourceBinding: Binding(resource));
            scope = $"{resource.Name} · {resource.Address}:{resource.Port} · {SshCommands[operation]}";
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payload = JsonSerializer.Serialize(target with { WorkId = workId });
        var now = DateTime.UtcNow;
        var existing = await db.Approvals.FirstOrDefaultAsync(x => (x.Status == "Pending" || x.Status == "Approved" || x.Status == "Executing") && x.AgentId == agent.Id &&
            x.ConversationId == conversationId && x.Payload == payload && x.ExpiresAtUtc > now, ct);
        if (existing is not null) return existing;
        var approval = new Approval { AgentId = agent.Id, ConversationId = conversationId, Kind = kind,
            Scope = resource.Production ? $"PRODUCTION · {scope}" : scope, Payload = payload };
        if (!resource.Production && !resource.IsGlobal && !globalServer)
        {
            if (kind == "GitPush")
                approval.AutoEligible = target.Operation == $"agent/{workspaceId}" && target.Branch == target.Operation &&
                    !await db.Projects.AnyAsync(p => p.Id == resource.ProjectId && p.DefaultBranch == target.Branch, ct);
            else if (operation is "restart-docker" or "deploy")
            {
                var approvedScopes = await db.Approvals.Where(a => a.ConversationId == conversationId && a.AgentId == agent.Id &&
                    a.Scope == scope && a.Kind == kind && a.DecidedBy == PlatformUser.OwnerId && a.Status == "Completed")
                    .Select(a => a.Payload).ToListAsync(ct);
                approval.AutoEligible = approvedScopes.Any(p => JsonSerializer.Deserialize<OperationTarget>(p) is { } prior &&
                    (prior with { WorkId = null }) == target);
            }
        }
        if (approval.AutoEligible && await db.Conversations.AnyAsync(c => c.Id == conversationId && c.AutoApprove, ct))
        {
            approval.Status = "Approved";
            approval.DecidedBy = AutomaticDecision;
        }
        db.Approvals.Add(approval);
        db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = conversationId, ProjectId = resource.ProjectId, WorkId = workId,
            Kind = approval.DecidedBy == AutomaticDecision ? "AutoApproved" : "ApprovalRequested", Detail = approval.Scope });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return approval;
    }

    public async Task DecideAsync(string id, bool allow, string ownerId, CancellationToken ct)
    {
        if (ownerId != PlatformUser.OwnerId) throw new UnauthorizedAccessException();
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var approval = await db.Approvals.SingleOrDefaultAsync(x => x.Id == id && x.Status == "Pending" && x.ExpiresAtUtc > now, ct)
            ?? throw new InvalidOperationException("The approval has expired or a decision has already been made.");
        if (approval.Kind == AgentPermissionGrants.ApprovalKind)
            await AgentPermissionGrants.DecideAsync(db, approval, allow, ownerId, ct);
        else
        {
            approval.Status = allow ? "Approved" : "Declined";
            approval.DecidedBy = ownerId;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (approval.Status == "Failed")
            throw new InvalidOperationException(approval.Result);
    }

    public async Task<bool> ExecuteNextAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        await db.Approvals.Where(x => (x.Status == "Pending" || x.Status == "Approved") && x.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Expired"), ct);
        var expiredRequests = await db.Runs.Where(r => r.Status == WorkStatus.WaitingForApproval &&
            db.Approvals.Any(a => a.ContinuationRunId == r.Id && a.Status == "Expired"))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkStatus.Cancelled).SetProperty(r => r.UpdatedAtUtc, now)
                .SetProperty(r => r.Error, "The permission request expired. The task was cancelled."), ct);
        var approval = await db.Approvals.AsNoTracking().Where(x => x.Status == "Approved" && x.Kind != AgentPermissionGrants.ApprovalKind)
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (approval is null) return expiredRequests != 0;
        var claimed = await db.Approvals.Where(x => x.Id == approval.Id && x.Status == "Approved" && x.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Executing"), ct);
        if (claimed != 1) return true;
        db.Attach(approval);
        try
        {
            var target = JsonSerializer.Deserialize<OperationTarget>(approval.Payload)
                ?? throw new InvalidOperationException("Invalid approval scope.");
            var agent = await db.Agents.AsNoTracking().SingleAsync(x => x.Id == approval.AgentId, ct);
            if (approval.Kind == "GitPush")
            {
                var resource = await ResolveAsync(agent, target.ResourceId, ResourceKind.GitRepository, "git", ct);
                if (approval.DecidedBy == AutomaticDecision && await db.Projects.AnyAsync(p =>
                    p.Id == resource.ProjectId && p.DefaultBranch == target.Branch, ct))
                    throw new InvalidOperationException("The default branch requires administrator approval.");
                if (Binding(resource) != target.ResourceBinding) throw new InvalidOperationException("The resource changed after approval.");
                var repo = Path.Combine(paths.Workspace(target.WorkspaceId!), "project");
                await VerifyRepoAsync(resource, repo, ct);
                var head = (await RunGitAsync(repo, ["rev-parse", "--verify", "HEAD^{commit}"], null, ct)).Trim();
                var local = (await RunGitAsync(repo, ["branch", "--show-current"], null, ct)).Trim();
                if (head != target.Commit || local != target.Operation || !CommitHash().IsMatch(head))
                    throw new InvalidOperationException("The commit or branch changed after approval.");
                approval.Result = TextLimits.Clip(await RunGitAsync(repo,
                    ["push", "--porcelain", "--no-verify", "--", GitAddress(resource), $"{head}:refs/heads/{SafeBranch(target.Branch)}"], resource, ct), 2000);
                db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = approval.ConversationId,
                    ProjectId = resource.ProjectId, WorkId = target.WorkId, Kind = "GitPushed", Detail = approval.Scope });
            }
            else if (approval.Kind == "SshRelease")
            {
                var deployment = await ReleaseAsync(agent, target.ResourceId, target.Operation, target.Image, ct);
                if (deployment.Binding != target.ResourceBinding) throw new InvalidOperationException("The deployment resource changed after approval.");
                approval.Result = JsonSerializer.Serialize(await SshAsync(deployment.Server, target.Operation, ct, deployment.Command));
                db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = approval.ConversationId,
                    ProjectId = deployment.Target.ProjectId, WorkId = target.WorkId, Kind = target.Operation == "deploy" ? "DeploymentExecuted" : "RollbackExecuted",
                    Detail = approval.Scope });
            }
            else if (approval.Kind == "SshChange" && target.Operation == "restart-docker")
            {
                var resource = await ResolveAsync(agent, target.ResourceId, ResourceKind.SshServer, "ssh", ct);
                if (Binding(resource) != target.ResourceBinding) throw new InvalidOperationException("The resource changed after approval.");
                approval.Result = JsonSerializer.Serialize(await SshAsync(resource, target.Operation, ct));
                db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = approval.ConversationId,
                    ProjectId = resource.ProjectId, WorkId = target.WorkId, Kind = "SSHExecuted", Detail = approval.Scope });
            }
            else throw new InvalidOperationException("This approval type is not supported.");
            approval.Status = "Completed";
        }
        catch (OperationCanceledException) { approval.Status = "Interrupted"; }
        catch (Exception ex)
        {
            approval.Status = "Failed";
            approval.Result = ex is InvalidOperationException ? TextLimits.Clip(ex.Message, 500) : "The operation failed.";
        }
        db.Entry(approval).Property(x => x.Status).IsModified = true;
        db.Events.Add(new Activity { AgentId = approval.AgentId, ConversationId = approval.ConversationId,
            Kind = $"Approval{approval.Status}", Detail = approval.Scope });
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private async Task<Resource> ResolveAsync(Agent agent, string id, ResourceKind kind, string tool, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var current = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agent.Id, ct);
        var resource = await db.Resources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Kind == kind, ct);
        if (current is null || !ToolAccess.Has(current, tool) || resource is null || (resource.ProjectId is null && !resource.IsGlobal) ||
            !ToolAccess.AllowsProject(current, resource.ProjectId))
            throw new UnauthorizedAccessException("The agent is not authorized to use this resource or tool.");
        return resource;
    }

    private async Task<(Resource Target, Resource Server, string Binding, string Command)> ReleaseAsync(
        Agent agent, string resourceId, string operation, string? image, CancellationToken ct)
    {
        var target = await ResolveAsync(agent, resourceId, ResourceKind.DeploymentTarget, "ssh", ct);
        if (!IsSwarmServiceName(target.Address) || target.SshServerId is null)
            throw new InvalidOperationException("A valid Docker Swarm service and SSH resource are required.");
        var server = await ResolveAsync(agent, target.SshServerId, ResourceKind.SshServer, "ssh", ct);
        if (!server.IsGlobal && server.ProjectId != target.ProjectId)
            throw new UnauthorizedAccessException("The deployment target and SSH resource must belong to the same project.");
        var command = operation switch
        {
            "deploy" when image is not null && ImageDigest().IsMatch(image) => $"docker service update --detach=false --image {image} {target.Address}",
            "rollback" => $"docker service rollback --detach=false {target.Address}",
            _ => throw new InvalidOperationException("Deployment requires an image pinned to a SHA256 digest or a rollback operation.")
        };
        return (target, server, Binding(target) + Binding(server), command);
    }

    private async Task<object> SshAsync(Resource resource, string operation, CancellationToken ct, string? approvedCommand = null, int offset = 0)
    {
        if (resource is not { Username.Length: > 0, HostFingerprint.Length: > 0, VaultId.Length: > 0, Port: > 0 and <= 65535 })
            throw new InvalidOperationException("An SSH username, host key, and vault entry are required.");
        var secret = await vault.ReadAsync(resource.VaultId, ct);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var keyStream = new MemoryStream(bytes, writable: false);
            using var key = new PrivateKeyFile(keyStream);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(approvedCommand is null ? TimeSpan.FromSeconds(35) : TimeSpan.FromMinutes(3));
            if (operation == "sftp-read")
                return await ReadSftpAsync(resource, key, secret, timeout.Token);
            using var client = new SshClient(resource.Address, resource.Port, resource.Username, key);
            PinHost(client, resource);
            await client.ConnectAsync(timeout.Token);
            if (operation == "test") return new { connected = client.IsConnected };
            using var command = client.CreateCommand(approvedCommand ?? SshCommands[operation]);
            command.CommandTimeout = approvedCommand is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(2);
            await command.ExecuteAsync(timeout.Token);
            if (command.ExitStatus != 0)
                throw new InvalidOperationException($"The SSH command exited with code {command.ExitStatus}.");
            var output = command.Result.Replace(secret, "[credential]", StringComparison.Ordinal);
            if (SshCommands.ContainsKey(operation) && operation != "restart-docker")
                return ToolOutput.Page(output, offset, 8000);
            return new { output = TextLimits.Clip(output, 8000) };
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void ValidateSshPrivateKey(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var key = new PrivateKeyFile(stream);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or CryptographicException or
            ArgumentException or FormatException or NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException("Enter a valid SSH private key without a passphrase.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void ValidateSshServer(Resource resource)
    {
        if (Uri.CheckHostName(resource.Address) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter only the hostname or IP address. Use the separate fields for the SSH username and port.");
        if (resource.Port is < 1 or > 65535)
            throw new ArgumentException("Enter an SSH port between 1 and 65535.");
        resource.Username = resource.Username?.Trim();
        if (string.IsNullOrWhiteSpace(resource.Username))
            throw new ArgumentException("Enter the SSH username.");

        var fingerprints = (resource.HostFingerprint ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        Span<byte> digest = stackalloc byte[32];
        if (fingerprints.Length != 1 || !SshFingerprint().IsMatch(fingerprints[0]) ||
            !Convert.TryFromBase64String(fingerprints[0][7..].TrimEnd('=') + "=", digest, out var length) || length != digest.Length)
            throw new ArgumentException("Enter the server's verified SHA256 host key fingerprint, or paste one line from ssh-keygen -lf.");
        resource.HostFingerprint = "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    private static async Task<object> ReadSftpAsync(Resource resource, PrivateKeyFile key, string secret, CancellationToken ct)
    {
        var path = resource.SftpFile;
        if (path is null || !IsSftpFile(path))
            throw new InvalidOperationException("No valid SFTP file is configured for this SSH resource.");
        using var client = new SftpClient(resource.Address, resource.Port, resource.Username!, key);
        PinHost(client, resource);
        client.OperationTimeout = TimeSpan.FromSeconds(12);
        await client.ConnectAsync(ct);
        using var stream = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, ct);
        var buffer = new byte[8_193];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, count, buffer.Length - count, ct);
            if (read == 0) break;
            count += read;
        }
        var content = Encoding.UTF8.GetString(buffer, 0, Math.Min(count, 8_192));
        return new { file = path, truncated = count > 8_192, content = content.Replace(secret, "[credential]", StringComparison.Ordinal) };
    }

    private static void PinHost(BaseClient client, Resource resource)
    {
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(12);
        var expected = resource.HostFingerprint!.Trim().Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
        client.HostKeyReceived += (_, args) =>
        {
            var actual = Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
            args.CanTrust = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
        };
    }

    private async Task<string> RunGitAsync(string cwd, string[] args, Resource? authentication, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var inherited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "PATH", "SystemRoot", "windir", "PATHEXT", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR" };
        foreach (var key in start.Environment.Keys.ToArray())
            if (!inherited.Contains(key)) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        var options = new Dictionary<string, string>
        {
            ["core.hooksPath"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["core.fsmonitor"] = "false", ["credential.helper"] = "",
            ["http.followRedirects"] = "false", ["protocol.allow"] = "never", ["protocol.https.allow"] = "always",
            ["user.name"] = "Codex Agent Platform", ["user.email"] = "codex-agent-platform@localhost",
            ["commit.gpgSign"] = "false"
        };
        string? secret = null;
        if (authentication?.VaultId is { } vaultId)
        {
            secret = await vault.ReadAsync(vaultId, ct);
            if (secret.Contains('\r') || secret.Contains('\n')) throw new InvalidOperationException("Invalid Git credential.");
            var username = string.IsNullOrWhiteSpace(authentication.Username) ? "x-access-token" : authentication.Username.Trim();
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{secret}"));
            options[$"http.{GitAddress(authentication)}.extraHeader"] = "Authorization: Basic " + base64;
        }
        start.Environment["GIT_CONFIG_COUNT"] = options.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var i = 0;
        foreach (var option in options)
        {
            start.Environment[$"GIT_CONFIG_KEY_{i}"] = option.Key;
            start.Environment[$"GIT_CONFIG_VALUE_{i++}"] = option.Value;
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        try
        {
            var stdout = DrainAsync(process.StandardOutput, timeout.Token);
            var stderr = DrainAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The Git operation ({args[0]}) exited with code {process.ExitCode}.");
            if (string.IsNullOrWhiteSpace(output)) output = error;
            return secret is null ? output : output.Replace(secret, "[credential]", StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> DrainAsync(StreamReader stream, CancellationToken ct)
    {
        var buffer = new char[4096];
        var preview = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            preview.Append(buffer, 0, Math.Max(0, Math.Min(read, 32_000 - preview.Length)));
        return preview.ToString();
    }

    private async Task<string> CommitAsync(string repo, string? message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500) throw new InvalidOperationException("A commit message is required.");
        await RunGitAsync(repo, ["add", "-A", "--", "."], null, ct);
        return await RunGitAsync(repo, ["commit", "--no-verify", "-m", message], null, ct);
    }

    private async Task<string> ListTreeAsync(string repo, string? directory, CancellationToken ct)
    {
        var tree = string.IsNullOrEmpty(directory) || directory == "." ? "HEAD" : "HEAD:" + RepositoryPath(directory);
        var names = await RunGitAsync(repo, ["ls-tree", "-z", "--name-only", tree], null, ct);
        return string.Join("\n", names.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => !name.Equals(".env", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<string> ReadBlobAsync(string repo, string? argument, CancellationToken ct)
    {
        var path = RepositoryPath(argument);
        var entry = await RunGitAsync(repo, ["--literal-pathspecs", "ls-tree", "HEAD", "--", path], null, ct);
        var header = entry.Split('\t')[0].Split(' ');
        if (header.Length != 3 || header[0] is not ("100644" or "100755") || !CommitHash().IsMatch(header[2]))
            throw new InvalidOperationException("The path must name a regular file in the current commit.");
        var body = await RunGitAsync(repo, ["cat-file", "blob", header[2]], null, ct);
        if (body.Contains('\0')) throw new InvalidOperationException("This repository file contains binary data.");
        return body;
    }

    private static string RepositoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                p.Equals(".env", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use a permitted path relative to the repository.");
        return path;
    }

    private async Task VerifyRepoAsync(Resource resource, string repo, CancellationToken ct)
    {
        if (!Directory.Exists(repo)) throw new InvalidOperationException("Prepare the Git workspace first.");
        PlatformPaths.CheckLinks(repo);
        var common = (await RunGitAsync(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"], null, ct)).Trim();
        var baseDir = Path.GetFullPath(Path.Combine(paths.Workspaces, ".git-cache")) + Path.DirectorySeparatorChar;
        var marker = Path.Combine(common, "platform-resource");
        if (!Path.GetFullPath(common).StartsWith(baseDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != resource.Id)
            throw new InvalidOperationException("The workspace is not linked to this Git resource.");
    }

    private async Task AuditAsync(string agentId, string? project, string? conversation, string? workId, string kind, string detail, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Events.Add(new Activity { AgentId = agentId, ProjectId = project, ConversationId = conversation, WorkId = workId, Kind = kind, Detail = detail });
        await db.SaveChangesAsync(ct);
    }

    public static string GitAddress(Resource resource)
    {
        if (!Uri.TryCreate(resource.Address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo + uri.Query + uri.Fragment))
            throw new InvalidOperationException("The Git repository requires an HTTPS URL without credentials.");
        return uri.AbsoluteUri;
    }

    private static string Binding(Resource resource) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resource))));

    private static string SafeBranch(string? branch)
    {
        if (branch is null) throw new InvalidOperationException("A branch is required.");
        ValidateBranch(branch);
        return branch;
    }

    private static void ValidateBranch(string branch)
    {
        if (!IsSupportedGitBranch(branch))
            throw new InvalidOperationException("Invalid branch name.");
    }

    internal static bool IsSupportedGitBranch(string branch) =>
        BranchShape().IsMatch(branch) && !branch.Contains("..", StringComparison.Ordinal) &&
        branch.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith('.') &&
            !part.EndsWith(".lock", StringComparison.Ordinal));

    public static bool IsSwarmServiceName(string name) => ServiceName().IsMatch(name);

    public static bool IsSftpFile(string path) => SftpPath().IsMatch(path) && !path.Split('/').Any(part => part is "." or "..");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,150}$")]
    private static partial Regex BranchShape();

    [GeneratedRegex("^(?:[a-f0-9]{40}|[a-f0-9]{64})$")]
    private static partial Regex CommitHash();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}\z")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.:/_-]{0,254}@sha256:[a-f0-9]{64}\z")]
    private static partial Regex ImageDigest();

    [GeneratedRegex(@"^/(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+\z")]
    private static partial Regex SftpPath();

    [GeneratedRegex(@"\ASHA256:[A-Za-z0-9+/]{43}=?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SshFingerprint();
}
