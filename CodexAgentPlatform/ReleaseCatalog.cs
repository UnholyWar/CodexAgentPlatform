using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAgentPlatform;

public enum ReleaseCheckStatus { NotChecked, Checking, Completed, NoRelease, AccessDenied, Unavailable }

public sealed class ReleaseCatalog(IHttpClientFactory clients, ILogger<ReleaseCatalog> log)
{
    private const string LatestRelease = "https://lisansapi.onurozgultekin.com/v1/releases/latest";
    private int checking;
    private volatile string? available;
    private volatile ReleaseCheckStatus status;
    public string? Current { get; } = ReadCurrentVersion();
    public string? Available => available;
    public ReleaseCheckStatus Status => status;
    public event Action? Changed;

    public void CheckForUpdates()
    {
        if (Interlocked.Exchange(ref checking, 1) == 0) _ = CheckAsync();
    }

    internal static Version? StableVersion(string tag) =>
        Regex.IsMatch(tag, @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") &&
        Version.TryParse(tag[1..], out var version) ? version : null;

    private static string? ReadCurrentVersion()
    {
        try
        {
            var tag = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "platform-version")).Trim();
            return StableVersion(tag) is null ? null : tag;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task CheckAsync()
    {
        try
        {
            status = ReleaseCheckStatus.Checking;
            Changed?.Invoke();
            if (Current is null || StableVersion(Current) is not { } current)
            {
                status = ReleaseCheckStatus.Unavailable;
                return;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var http = clients.CreateClient("release-catalog");
            using var response = await http.GetAsync(LatestRelease, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                available = null;
                status = ReleaseCheckStatus.NoRelease;
                return;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            var tag = document.RootElement.GetProperty("version").GetString();
            if (tag is null || StableVersion(tag) is not { } newest)
                throw new InvalidDataException("The release service returned an invalid version.");
            available = newest > current ? tag : null;
            status = ReleaseCheckStatus.Completed;
        }
        catch (Exception ex)
        {
            var statusCode = (ex as HttpRequestException)?.StatusCode;
            status = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ReleaseCheckStatus.AccessDenied : ReleaseCheckStatus.Unavailable;
            log.LogWarning("Sürüm kontrolü tamamlanamadı ({Reason}, HTTP {StatusCode}).",
                ex.GetType().Name, statusCode);
        }
        finally
        {
            Volatile.Write(ref checking, 0);
            Changed?.Invoke();
        }
    }
}
