using System.Text.Json;

namespace CodexAgentPlatform.Application;

public sealed record CodexEvent(string Method, JsonElement Parameters, JsonElement? RequestId = null);

public interface ICodexConnection
{
    event Action<CodexEvent>? Received;
    bool Connected { get; }
    Task<string> VersionAsync(CancellationToken ct);
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct);
    Task ReplyAsync(JsonElement id, object result, CancellationToken ct);
    Task RejectAsync(JsonElement id, string reason, CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
}

public sealed class CodexRpcException(string message) : Exception(message);

public static class CodexJson
{
    public static JsonElement Get(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var result) ? result : default;

    public static string? String(this JsonElement value, string key) =>
        value.Get(key) is { ValueKind: JsonValueKind.String } item ? item.GetString() : null;

    public static long Number(this JsonElement value, string key) =>
        value.Get(key) is { ValueKind: JsonValueKind.Number } item && item.TryGetInt64(out var number) ? number : 0;

    public static bool True(this JsonElement value, string key) => value.Get(key).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(this JsonElement value, string key) =>
        value.Get(key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
}
