using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexAgentPlatform.Components;

public static partial class SafeMarkdown
{
    public static string Render(string source, bool compactCode = false)
    {
        var html = new StringBuilder();
        int? codeStart = null;
        var lines = 0;
        var diff = false;
        var list = false;
        void CloseCode()
        {
            html.Append("</code></pre>");
            if (compactCode && (diff || lines > 6 || html.Length - codeStart!.Value > 650))
            {
                var label = diff ? "File diff" : "Code block";
                html.Insert(codeStart!.Value, $"<details class=\"code-fold\"><summary>{label}<small>{lines} lines</small></summary>");
                html.Append("</details>");
            }
            codeStart = null;
        }
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                CloseList(html, ref list);
                if (codeStart.HasValue) CloseCode();
                else
                {
                    codeStart = html.Length;
                    diff = raw[3..].Trim().ToLowerInvariant() is "diff" or "patch";
                    lines = 0;
                    html.Append("<pre><code>");
                }
                continue;
            }
            if (codeStart.HasValue) { html.AppendLine(WebUtility.HtmlEncode(raw)); lines++; continue; }
            if (raw.StartsWith("- ", StringComparison.Ordinal) || raw.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!list) html.Append("<ul>");
                list = true;
                html.Append("<li>").Append(Inline(raw[2..])).Append("</li>");
                continue;
            }
            CloseList(html, ref list);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var heading = raw.TakeWhile(c => c == '#').Count();
            if (heading is >= 1 and <= 6 && raw.Length > heading && raw[heading] == ' ')
            {
                var level = Math.Min(heading + 2, 6);
                html.Append($"<h{level}>").Append(Inline(raw[(heading + 1)..])).Append($"</h{level}>");
            }
            else html.Append("<p>").Append(Inline(raw)).Append("</p>");
        }
        CloseList(html, ref list);
        if (codeStart.HasValue) CloseCode();
        return html.ToString();
    }

    private static void CloseList(StringBuilder builder, ref bool list)
    {
        if (list) builder.Append("</ul>");
        list = false;
    }

    private static string Inline(string value)
    {
        var output = new StringBuilder();
        var position = 0;
        foreach (Match token in InlineTokens().Matches(value))
        {
            output.Append(WebUtility.HtmlEncode(value[position..token.Index]));
            if (token.Groups[1].Success)
                output.Append("<code>").Append(WebUtility.HtmlEncode(token.Groups[1].Value)).Append("</code>");
            else if (token.Groups[2].Success)
                output.Append("<strong>").Append(WebUtility.HtmlEncode(token.Groups[2].Value)).Append("</strong>");
            else if (Uri.TryCreate(token.Groups[4].Value, UriKind.Absolute, out var url) &&
                url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0)
                output.Append("<a rel=\"noopener noreferrer\" target=\"_blank\" href=\"")
                    .Append(WebUtility.HtmlEncode(url.AbsoluteUri)).Append("\">")
                    .Append(WebUtility.HtmlEncode(token.Groups[3].Value)).Append("</a>");
            else output.Append(WebUtility.HtmlEncode(token.Value));
            position = token.Index + token.Length;
        }
        return output.Append(WebUtility.HtmlEncode(value[position..])).ToString();
    }

    [GeneratedRegex(@"`([^`]+)`|\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex InlineTokens();
}
