using System.Text.Json;
using CodexAgentPlatform.Application;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet.Common;

namespace CodexAgentPlatform.Infrastructure;

public sealed partial class Operations
{
    public const string SshCommandApproval = "SshCommand";
    private static readonly TimeSpan SshTrackingWindow = TimeSpan.FromHours(1);
    private DateTime nextSshStatusCheck;
    private int sshStatusIndex;

    private sealed record SshCommandTarget(string ResourceId, string ResourceBinding, string Script);

    internal async Task<Approval> RequestSshCommandAsync(Agent agent, ChatRun run, string resourceId, string script, CancellationToken ct)
    {
        script = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(script) || script.Length > 12_000 ||
            script.Any(c => (char.IsControl(c) && c is not ('\n' or '\t')) ||
                (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069')))
            throw new ArgumentException("Enter a Bash script of up to 12,000 characters with plain text, spaces and line breaks.");
        var host = await ResolveAsync(agent, resourceId, ResourceKind.SshServer, "ssh", ct);
        if (host.ProjectId is not null && host.ProjectId != run.ProjectId)
            throw new UnauthorizedAccessException("The host is not available in this project.");
        if (host.VaultId is null || !vault.Configured)
            throw new InvalidOperationException("Configure the Vault and save an SSH key for this host first.");

        var payload = JsonSerializer.Serialize(new SshCommandTarget(host.Id, Binding(host), script));
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var existing = await db.Approvals.FirstOrDefaultAsync(a => a.Kind == SshCommandApproval && a.AgentId == agent.Id &&
            a.ConversationId == run.ConversationId && a.Payload == payload &&
            (a.Status == "Executing" || ((a.Status == "Pending" || a.Status == "Approved") && a.ExpiresAtUtc > now)), ct);
        if (existing != null) return existing;
        var scope = $"SSH · {host.Name} · {host.Username}@{host.Address}:{host.Port}";
        var approval = new Approval { Kind = SshCommandApproval, Scope = host.Production ? $"PRODUCTION · {scope}" : scope,
            AgentId = agent.Id, ConversationId = run.ConversationId, Payload = payload, ContinuationRunId = run.Id };
        db.Approvals.Add(approval);
        db.Events.Add(new Activity { AgentId = agent.Id, ConversationId = run.ConversationId, WorkId = run.WorkId,
            ProjectId = run.ProjectId ?? host.ProjectId, Kind = "ApprovalRequested", Detail = approval.Scope });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return approval;
    }

    internal async Task<object> ReadSshCommandResultAsync(Agent agent, ChatRun run, string id, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var approval = await db.Approvals.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.Kind == SshCommandApproval &&
            a.AgentId == agent.Id && a.ConversationId == run.ConversationId, ct)
            ?? throw new UnauthorizedAccessException("This SSH command is not available in this conversation.");
        var target = ReadSshCommandTarget(approval);
        var host = await ResolveAsync(agent, target.ResourceId, ResourceKind.SshServer, "ssh", ct);
        if ((host.ProjectId is not null && host.ProjectId != run.ProjectId) || Binding(host) != target.ResourceBinding)
            throw new UnauthorizedAccessException("The SSH resource is no longer available for this command.");
        return new { approval.Id, approval.Status, approval.Result };
    }

    public static string? SshCommandScript(Approval approval) =>
        approval.Kind == SshCommandApproval ? JsonSerializer.Deserialize<SshCommandTarget>(approval.Payload)?.Script : null;

    private async Task StartSshCommandAsync(PlatformDb db, Approval approval, CancellationToken ct)
    {
        Resource host;
        string launch;
        try
        {
            var target = ReadSshCommandTarget(approval);
            host = await ResolveSshCommandHostAsync(db, approval, target, ct);
            if (!vault.Configured || host.VaultId is null)
                throw new InvalidOperationException("The Vault or the saved SSH key is unavailable.");
            launch = SshLaunchCommand(approval.Id, target.Script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            await FinishSshCommandAsync(db, approval, "Failed",
                ex is JsonException ? "The SSH approval could not be read." : ex.Message);
            return;
        }

        // Record tracking before dispatch. The remote script may replace the application container.
        approval.ExpiresAtUtc = DateTime.UtcNow + SshTrackingWindow;
        approval.Result = "Waiting for the host to report the command result.";
        await db.SaveChangesAsync(CancellationToken.None);
        using var launchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        launchTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await SshAsync(host, SshCommandApproval, launchTimeout.Token, launch); }
        catch (SshAuthenticationException)
        {
            await FinishSshCommandAsync(db, approval, "Failed", "The SSH server rejected the saved username or key.");
        }
        catch (Exception ex) when (SshCommunicationError(ex))
        {
            // The request could have reached the host before SSH dropped. Poll its existing job.
        }
    }

    private async Task<bool> CheckSshCommandAsync(PlatformDb db, CancellationToken ct)
    {
        if (DateTime.UtcNow < nextSshStatusCheck) return false;
        nextSshStatusCheck = DateTime.UtcNow.AddSeconds(5);
        var jobs = await db.Approvals.Where(a => a.Kind == SshCommandApproval && a.Status == "Executing")
            .OrderBy(a => a.CreatedAtUtc).ToListAsync(ct);
        if (jobs.Count == 0) return false;
        var approval = jobs[sshStatusIndex % jobs.Count];
        sshStatusIndex = (sshStatusIndex + 1) % jobs.Count;
        if (DateTime.UtcNow >= approval.ExpiresAtUtc)
        {
            await FinishSshCommandAsync(db, approval, "Interrupted", "The host did not confirm the result within an hour. The command may still be running.");
            return true;
        }

        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        poll.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var target = ReadSshCommandTarget(approval);
            var host = await ResolveSshCommandHostAsync(db, approval, target, poll.Token);
            var response = await SshAsync(host, SshCommandApproval, poll.Token, SshStatusCommand(approval.Id));
            var output = JsonSerializer.SerializeToElement(response).String("output") ?? "";
            var newline = output.IndexOf('\n');
            var state = (newline < 0 ? output : output[..newline]).Trim();
            if (state.StartsWith("finished:", StringComparison.Ordinal) && int.TryParse(state.AsSpan(9), out var exitCode) &&
                exitCode is >= 0 and <= 255)
            {
                var result = newline < 0 ? "" : output[(newline + 1)..];
                await FinishSshCommandAsync(db, approval, exitCode == 0 ? "Completed" : "Failed",
                    $"Exit code {exitCode}\n{result}".TrimEnd());
                return true;
            }
            if (state == "missing" && DateTime.UtcNow > approval.ExpiresAtUtc - SshTrackingWindow + TimeSpan.FromSeconds(45))
            {
                await FinishSshCommandAsync(db, approval, "Interrupted", "The host has no record of this command. Check the SSH user's home directory and Bash access.");
                return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or JsonException)
        {
            await FinishSshCommandAsync(db, approval, "Interrupted", "The SSH approval or the host is no longer available.");
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && SshCommunicationError(ex)) { }
        return false;
    }

    private async Task<Resource> ResolveSshCommandHostAsync(PlatformDb db, Approval approval, SshCommandTarget target, CancellationToken ct)
    {
        if (approval.DecidedBy != PlatformUser.OwnerId)
            throw new UnauthorizedAccessException("This SSH command requires an explicit owner approval.");
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == approval.AgentId, ct)
            ?? throw new UnauthorizedAccessException("The requesting agent no longer exists.");
        var host = await ResolveAsync(agent, target.ResourceId, ResourceKind.SshServer, "ssh", ct);
        if (Binding(host) != target.ResourceBinding)
            throw new UnauthorizedAccessException("The SSH connection changed after the approval was requested.");
        return host;
    }

    private static SshCommandTarget ReadSshCommandTarget(Approval approval) =>
        JsonSerializer.Deserialize<SshCommandTarget>(approval.Payload) ?? throw new JsonException("Invalid SSH command approval.");

    private static bool SshCommunicationError(Exception ex) => ex is SshException or IOException or InvalidOperationException or
        OperationCanceledException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException;

    private static Task FinishSshCommandAsync(PlatformDb db, Approval approval, string status, string result)
    {
        approval.Status = status;
        db.Entry(approval).Property(a => a.Status).IsModified = true;
        approval.Result = result;
        db.Events.Add(new Activity { AgentId = approval.AgentId, ConversationId = approval.ConversationId,
            Kind = $"Approval{status}", Detail = approval.Scope });
        return db.SaveChangesAsync(CancellationToken.None);
    }

    internal static string SshLaunchCommand(string id, string script)
    {
        const string run = """
            job=$1
            printf 'running\n' >"$job/state"
            /bin/bash "$job/script" </dev/null 2>&1 | tail -c 7000 >"$job/output"
            code=${PIPESTATUS[0]}
            printf 'finished:%d\n' "$code" >"$job/state.tmp"
            mv -- "$job/state.tmp" "$job/state"
            rm -f -- "$job/script"
            """;
        var launch = """
            set -eu
            umask 077
            root="$HOME/.local/state/codex-agent-platform/ssh-jobs"
            job="$root/$1"
            mkdir -p -- "$root"
            mkdir -- "$job"
            printf '%s' "$2" >"$job/script"
            nohup /bin/bash -c "$3" _ "$job" </dev/null >"$job/launcher.log" 2>&1 &
            """;
        return $"/bin/bash -c {ShellQuote(launch)} _ {ShellQuote(SshJobId(id))} {ShellQuote(script)} {ShellQuote(run)}";
    }

    internal static string SshStatusCommand(string id)
    {
        const string status = """
            job="$HOME/.local/state/codex-agent-platform/ssh-jobs/$1"
            if [[ ! -f "$job/state" ]]; then
              printf 'missing\n'
              exit 0
            fi
            IFS= read -r state <"$job/state"
            printf '%s\n' "$state"
            if [[ "$state" == finished:* ]]; then cat -- "$job/output"; fi
            """;
        return $"/bin/bash -c {ShellQuote(status)} _ {ShellQuote(SshJobId(id))}";
    }

    private static string SshJobId(string id) => Guid.TryParseExact(id, "N", out _) ? id
        : throw new InvalidOperationException("Invalid SSH command approval ID.");

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
