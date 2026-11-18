using System.Globalization;
using System.Text;

namespace Plyph.Core;

public sealed class DiffPart
{
    public string Type { get; set; } = "equal";
    public string Text { get; set; } = "";

    public DiffPart() { }
    public DiffPart(string type, string text)
    {
        Type = type;
        Text = text;
    }
}

public static class DiffEngine
{
    private static bool IsWordRune(Rune r)
    {
        if (r.Value == '_')
            return true;
        var cat = Rune.GetUnicodeCategory(r);
        return cat is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    public static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
            return result;

        var runes = text.EnumerateRunes().ToArray();
        var i = 0;
        while (i < runes.Length)
        {
            if (IsWordRune(runes[i]))
            {
                var sb = new StringBuilder();
                while (i < runes.Length && IsWordRune(runes[i]))
                {
                    sb.Append(runes[i].ToString());
                    i++;
                }
                result.Add(sb.ToString());
            }
            else if (Rune.IsWhiteSpace(runes[i]))
            {
                var sb = new StringBuilder();
                while (i < runes.Length && Rune.IsWhiteSpace(runes[i]))
                {
                    sb.Append(runes[i].ToString());
                    i++;
                }
                result.Add(sb.ToString());
            }
            else
            {
                result.Add(runes[i].ToString());
                i++;
            }
        }
        return result;
    }

    public static List<DiffPart> DiffText(string original, string result)
    {
        var before = Tokenize(original ?? "");
        var after = Tokenize(result ?? "");

        var start = 0;
        while (start < before.Count && start < after.Count && before[start] == after[start])
            start++;

        var endBefore = before.Count;
        var endAfter = after.Count;
        while (endBefore > start && endAfter > start && before[endBefore - 1] == after[endAfter - 1])
        {
            endBefore--;
            endAfter--;
        }

        var parts = new List<DiffPart>();
        void Append(string type, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (parts.Count > 0 && parts[^1].Type == type)
                parts[^1].Text += text;
            else
                parts.Add(new DiffPart(type, text));
        }

        if (start > 0)
            Append("equal", string.Concat(before.Take(start)));

        var rows = endBefore - start;
        var columns = endAfter - start;

        if ((long)(rows + 1) * (columns + 1) > 1_000_000L)
        {
            if (rows > 0)
                Append("removed", string.Concat(before.Skip(start).Take(rows)));
            if (columns > 0)
                Append("added", string.Concat(after.Skip(start).Take(columns)));
        }
        else
        {
            var stride = columns + 1;
            var lengths = new uint[(rows + 1) * stride];
            for (var r = rows - 1; r >= 0; r--)
            {
                for (var c = columns - 1; c >= 0; c--)
                {
                    lengths[r * stride + c] = before[start + r] == after[start + c]
                        ? lengths[(r + 1) * stride + c + 1] + 1
                        : Math.Max(lengths[(r + 1) * stride + c], lengths[r * stride + c + 1]);
                }
            }

            var i = 0;
            var j = 0;
            while (i < rows || j < columns)
            {
                if (i < rows && j < columns && before[start + i] == after[start + j])
                {
                    Append("equal", before[start + i]);
                    i++;
                    j++;
                }
                else if (i < rows && (j == columns || lengths[(i + 1) * stride + j] >= lengths[i * stride + j + 1]))
                {
                    Append("removed", before[start + i]);
                    i++;
                }
                else
                {
                    Append("added", after[start + j]);
                    j++;
                }
            }
        }

        if (endBefore < before.Count)
            Append("equal", string.Concat(before.Skip(endBefore)));

        return parts;
    }

    public static string DiffMarkup(string original, string result)
    {
        var sb = new StringBuilder();
        foreach (var part in DiffText(original, result))
        {
            var escaped = part.Text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

            if (part.Type == "added")
                sb.Append($"<span foreground=\"#b9f6ca\" background=\"#19452b\" underline=\"single\">{escaped}</span>");
            else if (part.Type == "removed")
                sb.Append($"<span foreground=\"#ffcdd2\" background=\"#58252b\" strikethrough=\"true\">{escaped}</span>");
            else
                sb.Append(escaped);
        }
        return sb.ToString();
    }
}
