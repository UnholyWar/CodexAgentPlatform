using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAgentPlatform;

public sealed class ReleaseCatalog(IHttpClientFactory clients, ILogger<ReleaseCatalog> log)
{
    private const string Repository = "unholywar/codexagentplatform";
    private int checking;
    private volatile string? available;
    public string? Available => available;
    public event Action? Changed;

    public void CheckAfterLogin()
    {
        if (Interlocked.Exchange(ref checking, 1) == 0) _ = CheckAsync();
    }

    internal static Version? StableVersion(string tag) =>
        Regex.IsMatch(tag, @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") &&
        Version.TryParse(tag[1..], out var version) ? version : null;

    private async Task CheckAsync()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "platform-version");
            if (!File.Exists(path) || StableVersion(File.ReadAllText(path).Trim()) is not { } current) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var http = clients.CreateClient("ghcr");
            using var auth = JsonDocument.Parse(await http.GetByteArrayAsync(
                $"https://ghcr.io/token?service=ghcr.io&scope=repository:{Repository}:pull", deadline.Token));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                auth.RootElement.GetProperty("token").GetString());
            var newest = current;
            string? last = null;
            while (true)
            {
                var url = $"https://ghcr.io/v2/{Repository}/tags/list?n=100";
                if (last is not null) url += "&last=" + Uri.EscapeDataString(last);
                using var response = await http.GetAsync(url, deadline.Token);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
                var tags = document.RootElement.GetProperty("tags").EnumerateArray().ToArray();
                foreach (var tag in tags)
                    if (StableVersion(tag.GetString() ?? "") is { } stable && stable > newest) newest = stable;
                if (!response.Headers.Contains("Link") || tags.Length == 0) break;
                var next = tags[^1].GetString();
                if (string.IsNullOrEmpty(next) || next == last) return;
                last = next;
            }
            available = newest > current ? $"v{newest}" : null;
            Changed?.Invoke();
        }
        catch (Exception ex) { log.LogDebug("Sürüm kontrolü tamamlanamadı ({Reason}).", ex.GetType().Name); }
        finally { Volatile.Write(ref checking, 0); }
    }
}
