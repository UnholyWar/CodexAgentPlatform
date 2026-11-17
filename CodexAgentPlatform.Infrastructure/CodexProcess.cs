using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAgentPlatform.Application;
using Microsoft.Extensions.Logging;

namespace CodexAgentPlatform.Infrastructure;

public sealed class CodexProcess(PlatformPaths paths, string executable, ILogger<CodexProcess> log)
    : ICodexConnection, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly SemaphoreSlim lifecycle = new(1);
    private Session? ready;
    private bool disposed;
    public event Action<CodexEvent>? Received;
    public bool Connected => Volatile.Read(ref ready) is { Closed: false };

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var proc = Start("--version");
        try
        {
            var output = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            await stderr;
            if (proc.ExitCode != 0)
                throw new CodexRpcException("Codex CLI sürümü okunamadı.");
            return (await output).Trim();
        }
        finally { Kill(proc); }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        var session = await EnsureAsync(ct);
        return await RequestAsync(session, method, parameters, ct);
    }

    public Task ReplyAsync(JsonElement id, object result, CancellationToken ct) =>
        WriteAsync(Volatile.Read(ref ready) ?? throw new IOException("Codex bağlantısı kapalı."),
            new { id, result }, ct);

    public Task RejectAsync(JsonElement id, string reason, CancellationToken ct) =>
        WriteAsync(Volatile.Read(ref ready) ?? throw new IOException("Codex bağlantısı kapalı."),
            new { id, error = new { code = -32601, message = reason } }, ct);

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            await StopAsync();
            await StartAsync(ct);
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            disposed = true;
            await StopAsync();
        }
        finally { lifecycle.Release(); }
    }

    private async Task<Session> EnsureAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref ready);
        if (current is { Closed: false })
            return current;
        await lifecycle.WaitAsync(ct);
        try
        {
            if (ready is { Closed: false } running)
                return running;
            await StopAsync();
            return await StartAsync(ct);
        }
        finally { lifecycle.Release(); }
    }

    private async Task<Session> StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await WriteConfigAsync(ct);
        var session = new Session(Start("app-server", "--listen", "stdio://"));
        session.Reader = ReadAsync(session);
        session.Stderr = DrainErrorsAsync(session);
        try
        {
            await RequestAsync(session, "initialize", new
            {
                clientInfo = new { name = "codex_agent_platform", title = "Codex Agent Platform", version = "0.1.0" },
                capabilities = new { experimentalApi = true }
            }, ct);
            await WriteAsync(session, new { method = "initialized" }, ct);
            Volatile.Write(ref ready, session);
            return session;
        }
        catch
        {
            await CloseAsync(session);
            throw;
        }
    }

    private Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = paths.Workspaces, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "SystemRoot", "windir", "ComSpec", "PATHEXT", "ProgramFiles", "ProgramFiles(x86)",
            "ProgramData", "LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "TEMP", "TMP", "TMPDIR",
            "LANG", "LC_ALL", "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY", "SSL_CERT_FILE",
            "SSL_CERT_DIR", "CODEX_CA_CERTIFICATE"
        };
        foreach (var key in info.Environment.Keys.ToArray())
            if (!allowed.Contains(key)) info.Environment.Remove(key);
        info.Environment["CODEX_HOME"] = paths.CodexHome;
        return Process.Start(info) ?? throw new IOException("Codex CLI başlatılamadı.");
    }

    private async Task WriteConfigAsync(CancellationToken ct)
    {
        const string config = """
            default_permissions = "platform_chat"
            approval_policy = "never"
            cli_auth_credentials_store = "file"
            web_search = "live"

            [features]
            apps = false
            use_legacy_landlock = false
            prefer_mxc = true

            [permissions.platform_chat.filesystem]
            ":root" = "deny"
            ":minimal" = "read"

            [permissions.platform_chat.network]
            enabled = false

            [permissions.platform_work]
            extends = ":workspace"

            [permissions.platform_work.filesystem]
            ":root" = "deny"
            ":minimal" = "read"
            ":tmpdir" = "deny"
            ":slash_tmp" = "deny"
            glob_scan_max_depth = 4

            [permissions.platform_work.filesystem.":workspace_roots"]
            "**/.env" = "deny"

            [permissions.platform_work.network]
            enabled = false

            [shell_environment_policy]
            inherit = "core"
            exclude = ["CODEX_*", "OPENAI_*", "*TOKEN*", "*SECRET*", "*PASSWORD*", "*API_KEY*"]
            """;
        // Paths themselves carry no credentials. Codex's TOML string syntax accepts JSON's escapes.
        var rules = config.Replace("\":minimal\" = \"read\"", "\":minimal\" = \"read\"\n" +
            JsonSerializer.Serialize(paths.Data) + " = \"deny\"", StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
            rules += "\n\n[windows]\nsandbox = \"elevated\"\n";
        await File.WriteAllTextAsync(Path.Combine(paths.CodexHome, "config.toml"), rules, ct);
    }

    private async Task<JsonElement> RequestAsync(Session session, string method, object? parameters, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(method == "account/login/start" ? 60 : 35));
        var id = Interlocked.Increment(ref session.NextId);
        var promise = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = promise;
        try
        {
            await WriteAsync(session, new { id, method, @params = parameters }, timeout.Token);
            return await promise.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Codex {method} yanıtı zaman aşımına uğradı.");
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private static async Task WriteAsync(Session session, object message, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(message, Json);
        await session.Writer.WaitAsync(ct);
        try
        {
            if (session.Closed) throw new IOException("Codex bağlantısı kapandı.");
            await session.Process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await session.Process.StandardInput.FlushAsync(ct);
        }
        finally { session.Writer.Release(); }
    }

    private async Task ReadAsync(Session session)
    {
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 16_000_000) throw new IOException("Codex yanıtı çok büyük.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.Get("id");
                if (root.String("method") is { } method)
                {
                    var parameters = root.Get("params");
                    Publish(new CodexEvent(method, parameters.Clone(),
                        id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id.Clone() : null));
                }
                else if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) && session.Pending.TryGetValue(n, out var answer))
                {
                    var error = root.Get("error");
                    if (error.ValueKind == JsonValueKind.Object)
                        answer.TrySetException(new CodexRpcException(TextLimits.Clip(error.String("message") ?? "Codex RPC error", 500)));
                    else
                        answer.TrySetResult(root.Get("result").Clone());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            log.LogWarning("Codex transport closed ({Type}).", ex.GetType().Name);
        }
        finally
        {
            session.Closed = true;
            foreach (var promise in session.Pending.Values)
                promise.TrySetException(new IOException("Codex App Server bağlantısı kapandı."));
            if (ReferenceEquals(Volatile.Read(ref ready), session))
                Publish(new CodexEvent("transport/closed", default));
        }
    }

    private void Publish(CodexEvent item)
    {
        if (Received is not { } listeners) return;
        foreach (Action<CodexEvent> listener in listeners.GetInvocationList())
        {
            try { listener(item); }
            catch (Exception ex) { log.LogWarning("Codex notification consumer failed ({Type}).", ex.GetType().Name); }
        }
    }

    private static async Task DrainErrorsAsync(Session session)
    {
        try
        {
            while (await session.Process.StandardError.ReadLineAsync() is not null) { }
        }
        catch (IOException) { }
    }

    private async Task StopAsync()
    {
        if (Volatile.Read(ref ready) is not { } session) return;
        await CloseAsync(session);
        Volatile.Write(ref ready, null);
    }

    private static async Task CloseAsync(Session session)
    {
        try
        {
            session.Process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await session.Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { Kill(session.Process); }
        catch (IOException) { Kill(session.Process); }
        finally
        {
            await Task.WhenAll(session.Reader, session.Stderr).WaitAsync(TimeSpan.FromSeconds(5));
            session.Process.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private sealed class Session(Process process)
    {
        public Process Process { get; } = process;
        public SemaphoreSlim Writer { get; } = new(1);
        public ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public volatile bool Closed;
        public long NextId;
        public Task Reader = Task.CompletedTask;
        public Task Stderr = Task.CompletedTask;
    }
}
