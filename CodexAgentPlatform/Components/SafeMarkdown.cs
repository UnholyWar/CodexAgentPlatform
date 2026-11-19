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
        var rows = source.Replace("\r", "").Split('\n');
        for (var index = 0; index < rows.Length; index++)
        {
            var raw = rows[index];
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
            if (raw.Contains('|') && TryRenderTable(rows, ref index, html)) continue;
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

    private static bool TryRenderTable(string[] rows, ref int index, StringBuilder html)
    {
        var separator = NextNonBlank(rows, index + 1);
        if (separator == rows.Length || !rows[separator].Contains('-')) return false;
        var header = TableCells(rows[index]);
        var markers = TableCells(rows[separator]);
        if (header.Count == 0 || markers.Count != header.Count || markers.Any(m => !TableSeparator().IsMatch(m)))
            return false;
        var alignment = markers.Select(m => m.EndsWith(':')
            ? (m.StartsWith(':') ? " class=\"align-center\"" : " class=\"align-right\"") : "").ToArray();
        html.Append("<div class=\"markdown-table\" role=\"region\" aria-label=\"Table\" tabindex=\"0\"><table><thead><tr>");
        for (var col = 0; col < header.Count; col++)
            html.Append("<th scope=\"col\"").Append(alignment[col]).Append('>').Append(Inline(header[col])).Append("</th>");
        html.Append("</tr></thead><tbody>");
        index = separator;
        for (var next = NextNonBlank(rows, index + 1); next < rows.Length; next = NextNonBlank(rows, index + 1))
        {
            var line = rows[next].TrimStart();
            if (line.StartsWith("```") || !line.Contains('|')) break;
            // Some chat replies separate every explicitly piped row with a blank line.
            if (next > index + 1 && !line.StartsWith('|') && !line.StartsWith(@"\|")) break;
            var cells = TableCells(line);
            if (cells.Count == 0 || cells.Count > header.Count) break;
            html.Append("<tr>");
            for (var col = 0; col < cells.Count; col++)
                html.Append("<td").Append(alignment[col]).Append('>').Append(Inline(cells[col])).Append("</td>");
            html.Append("</tr>");
            index = next;
        }
        html.Append("</tbody></table></div>");
        return true;
    }

    private static int NextNonBlank(string[] rows, int index)
    {
        while (index < rows.Length && string.IsNullOrWhiteSpace(rows[index])) index++;
        return index;
    }

    private static List<string> TableCells(string raw)
    {
        var line = raw.Trim();
        if (line.StartsWith(@"\|")) line = line[1..];
        List<string> cells = [];
        var value = new StringBuilder();
        bool inCode = false, delimited = false, endedAtPipe = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            endedAtPipe = false;
            if (c == '\\' && i + 1 < line.Length && line[i + 1] is '|' or '\\') value.Append(line[++i]);
            else if (c == '|' && !inCode)
            {
                if (i > 0) cells.Add(value.ToString().Trim());
                value.Clear();
                delimited = endedAtPipe = true;
            }
            else
            {
                if (c == '`') inCode = !inCode;
                value.Append(c);
            }
        }
        if (!delimited) return [];
        if (!endedAtPipe) cells.Add(value.ToString().Trim());
        return cells;
    }

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex TableSeparator();

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
