using System.Diagnostics;
using CodexAgentPlatform.Application;
using CodexAgentPlatform.Infrastructure;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CodexAgentPlatform;

internal sealed class LicenseCircuit(ILicenseAccess access) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
        => ctx => access.IsValid ? next(ctx) : Task.FromException(new InvalidOperationException("Geçerli lisans gerekli."));
}

internal sealed class LicenseService(PlatformPaths paths, ILogger<LicenseService> log) : BackgroundService, ILicenseAccess
{
    private readonly SemaphoreSlim gate = new(1);
    private long expiration;
    public bool IsValid => Interlocked.Read(ref expiration) > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Task<bool> ActivateAsync(string code, CancellationToken ct) => RefreshAsync(code, ct);

    private async Task<bool> RefreshAsync(string? code, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Licensing", "license.mjs"));
            start.ArgumentList.Add(Path.Combine(paths.Data, "credentials"));
            using var process = Process.Start(start) ?? throw new IOException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await process.StandardInput.WriteAsync(code ?? "");
                process.StandardInput.Close();
                var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                long exp = 0;
                if (process.ExitCode == 0) long.TryParse(output.Trim(), out exp);
                Interlocked.Exchange(ref expiration, exp);
                return IsValid;
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref expiration, 0);
            log.LogWarning("Lisans kontrolü tamamlanamadı ({Type}).", ex.GetType().Name);
            return false;
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(null, ct);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
