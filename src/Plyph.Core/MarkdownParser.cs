using System.Text;
using System.Text.RegularExpressions;

namespace Plyph.Core;

public sealed record MarkdownSpan(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Monospace = false);

public static class MarkdownParser
{
    private static readonly Regex EscapableCharRegex = new(@"^[\\`*_{}\[\]()#+.!>~-]$", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"^(!?)\[([^\]\n]+)\]\(([^\s)]+)\)", RegexOptions.Compiled);
    private static readonly Regex WordOrNumberCharRegex = new(@"[\p{L}\p{N}]", RegexOptions.Compiled);
    private static readonly Regex FenceOpenRegex = new(@"^ {0,3}(`{3,}|~{3,})[^\n]*$", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^ {0,3}#{1,6}\s+(.+?)(?:\s+#+)?$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(\s*)[-+*]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex HorizontalRuleRegex = new(@"^\s*([-*_])(?:\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex BlockquotePrefixRegex = new(@"^ {0,3}>\s?", RegexOptions.Compiled);

    private static readonly string[] EmphasisMarkers = { "***", "___", "**", "__", "~~", "*", "_" };

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Inline(string text, int depth = 0)
    {
        if (depth > 6)
            return Escape(text);

        var result = new StringBuilder();
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length &&
                EscapableCharRegex.IsMatch(text[index + 1].ToString()))
            {
                result.Append(Escape(text[index + 1].ToString()));
                index += 2;
                continue;
            }

            if (text[index] == '`')
            {
                var length = 1;
                while (index + length < text.Length && text[index + length] == '`')
                    length++;
                var marker = new string('`', length);
                var end = text.IndexOf(marker, index + length, StringComparison.Ordinal);
                if (end != -1)
                {
                    result.Append($"<tt>{Escape(text[(index + length)..end])}</tt>");
                    index = end + length;
                    continue;
                }
            }

            Match? link = null;
            if (text[index] == '[' || (index + 1 < text.Length && text[index] == '!' && text[index + 1] == '['))
            {
                var m = LinkRegex.Match(text[index..]);
                if (m.Success)
                    link = m;
            }

            if (link != null)
            {
                var isImage = !string.IsNullOrEmpty(link.Groups[1].Value);
                var label = link.Groups[2].Value;
                var target = link.Groups[3].Value;
                result.Append(isImage
                    ? Escape(label)
                    : $"{Inline(label, depth + 1)} ({Escape(target)})");
                index += link.Value.Length;
                continue;
            }

            var empMarker = EmphasisMarkers.FirstOrDefault(m =>
                index + m.Length <= text.Length &&
                string.CompareOrdinal(text, index, m, 0, m.Length) == 0);

            if (empMarker != null &&
                !(empMarker.Contains('_') && index > 0 && WordOrNumberCharRegex.IsMatch(text[index - 1].ToString())))
            {
                var end = text.IndexOf(empMarker, index + empMarker.Length, StringComparison.Ordinal);
                if (end > index + empMarker.Length)
                {
                    var (openTag, closeTag) = empMarker.Length == 3
                        ? ("<b><i>", "</i></b>")
                        : empMarker == "~~"
                            ? ("<s>", "</s>")
                            : empMarker.Length == 2
                                ? ("<b>", "</b>")
                                : ("<i>", "</i>");

                    result.Append(openTag)
                          .Append(Inline(text[(index + empMarker.Length)..end], depth + 1))
                          .Append(closeTag);
                    index = end + empMarker.Length;
                    continue;
                }
            }

            result.Append(Escape(text[index].ToString()));
            index++;
        }

        return result.ToString();
    }

    public static string MarkdownMarkup(string text)
    {
        var normalized = Regex.Replace(text ?? "", @"\r\n?", "\n");
        var lines = normalized.Split('\n');
        var output = new List<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var fence = FenceOpenRegex.Match(line);
            if (fence.Success)
            {
                var code = new List<string>();
                var fenceChar = fence.Groups[1].Value[0];
                var fenceLen = fence.Groups[1].Value.Length;
                var closing = new Regex($@"^ {{0,3}}{Regex.Escape(fenceChar.ToString())}{{{fenceLen},}}\s*$");
                while (++index < lines.Length && !closing.IsMatch(lines[index]))
                    code.Add(lines[index]);
                output.Add($"<tt>{Escape(string.Join("\n", code))}</tt>");
                continue;
            }

            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                output.Add($"<b>{Inline(heading.Groups[1].Value)}</b>");
                continue;
            }

            var bullet = BulletRegex.Match(line);
            if (bullet.Success)
            {
                output.Add($"{bullet.Groups[1].Value}• {Inline(bullet.Groups[2].Value)}");
                continue;
            }

            if (HorizontalRuleRegex.IsMatch(line))
            {
                output.Add("────────");
                continue;
            }

            var quoted = BlockquotePrefixRegex.Replace(line, "│ ");
            output.Add(Inline(quoted));
        }

        return string.Join("\n", output);
    }

    /// <summary>
    /// Converts the Pango-like markup produced by MarkdownMarkup into styled spans for native Windows RichTextBox rendering.
    /// </summary>
    public static List<MarkdownSpan> ParseToSpans(string markdownText)
    {
        var markup = MarkdownMarkup(markdownText);
        var spans = new List<MarkdownSpan>();
        var bold = 0;
        var italic = 0;
        var strike = 0;
        var mono = 0;
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
                return;
            spans.Add(new MarkdownSpan(
                current.ToString(),
                Bold: bold > 0,
                Italic: italic > 0,
                Strikethrough: strike > 0,
                Monospace: mono > 0));
            current.Clear();
        }

        for (var i = 0; i < markup.Length;)
        {
            if (markup.AsSpan(i).StartsWith("<b>", StringComparison.Ordinal))
            { Flush(); bold++; i += 3; continue; }
            if (markup.AsSpan(i).StartsWith("</b>", StringComparison.Ordinal))
            { Flush(); bold = Math.Max(0, bold - 1); i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("<i>", StringComparison.Ordinal))
            { Flush(); italic++; i += 3; continue; }
            if (markup.AsSpan(i).StartsWith("</i>", StringComparison.Ordinal))
            { Flush(); italic = Math.Max(0, italic - 1); i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("<s>", StringComparison.Ordinal))
            { Flush(); strike++; i += 3; continue; }
            if (markup.AsSpan(i).StartsWith("</s>", StringComparison.Ordinal))
            { Flush(); strike = Math.Max(0, strike - 1); i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("<tt>", StringComparison.Ordinal))
            { Flush(); mono++; i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("</tt>", StringComparison.Ordinal))
            { Flush(); mono = Math.Max(0, mono - 1); i += 5; continue; }

            if (markup.AsSpan(i).StartsWith("&lt;", StringComparison.Ordinal))
            { current.Append('<'); i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("&gt;", StringComparison.Ordinal))
            { current.Append('>'); i += 4; continue; }
            if (markup.AsSpan(i).StartsWith("&amp;", StringComparison.Ordinal))
            { current.Append('&'); i += 5; continue; }

            current.Append(markup[i]);
            i++;
        }

        Flush();
        return spans;
    }
}
