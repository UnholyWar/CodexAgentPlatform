using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace CodexAgentPlatform.Infrastructure;

internal static class ToolOutput
{
    private const int PageSize = 3000;
    private const int JsonLimit = 20_000;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static object Page(string text, int offset, int captureLimit)
    {
        var length = Math.Min(text.Length, captureLimit);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var start = Math.Min(offset, length);
        var end = Math.Min(start + PageSize, length);
        if (end < length && char.IsHighSurrogate(text[end - 1])) end--;
        return new { output = text[start..end], nextOffset = end < length ? (int?)end : null, truncated = text.Length > length };
    }

    public static string Serialize(object? value)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (json.Length <= JsonLimit) return json;
        if (value is Array rows)
        {
            var items = new List<JsonElement>();
            var length = 100;
            foreach (var row in rows)
            {
                var item = JsonSerializer.SerializeToElement(row, Json);
                length += item.GetRawText().Length + 1;
                if (length > JsonLimit) break;
                items.Add(item);
            }
            return JsonSerializer.Serialize(new { items, truncated = true, totalItems = rows.Length }, Json);
        }
        return JsonSerializer.Serialize(new { truncated = true, preview = json[..PageSize], totalCharacters = json.Length }, Json);
    }
}
