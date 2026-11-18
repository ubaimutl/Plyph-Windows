using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Plyph.Windows.UI;

internal static class ActionIcons
{
    internal sealed record Choice(string Key, string Label, string Glyph);

    public static IReadOnlyList<Choice> Choices { get; } =
    [
        new("custom", "Lightning", "\uE945"),
        new("ask", "Question", "\uE897"),
        new("correct", "Magic wand", "\uE73E"),
        new("rewrite", "Edit document", "\uE70F"),
        new("prompt", "Play", "\uE768"),
        new("summary", "Document", "\uE8A5"),
        new("list", "List", "\uE8FD"),
        new("language", "Globe", "\uE909"),
        new("mail", "Mail", "\uE715"),
        new("code", "Code", "\uE943"),
        new("idea", "Star", "\uE734"),
        new("calendar", "Calendar", "\uE787"),
        new("chat", "Comment", "\uE90A"),
        new("search", "Search", "\uE721"),
        new("format", "Align left", "\uE8E4"),
        new("flag", "Flag", "\uE7C1")
    ];

    private static string GetGlyph(string key) => Choices.FirstOrDefault(choice => choice.Key == key)?.Glyph ?? Choices[0].Glyph;

    public static IconElement Create(string key, double size = 16)
    {
        if (key is "ask" or "correct" or "rewrite" or "prompt")
        {
            // Monochrome renders of assets/icons/plyph-*-symbolic.svg, centered on a common canvas.
            return new BitmapIcon
            {
                UriSource = new Uri($"ms-appx:///Assets/ActionIcons/{key}.png"),
                ShowAsMonochrome = true,
                Width = size,
                Height = size
            };
        }

        return CreateGlyph(GetGlyph(key), size);
    }

    public static void FitPath(PathIcon icon, double size)
    {
        // PathIcon does not scale its geometry when Width and Height change.
        var bounds = icon.Data.Bounds;
        var scale = size * 0.9 / Math.Max(bounds.Width, bounds.Height);
        icon.Data.Transform = new MatrixTransform
        {
            Matrix = new Matrix(scale, 0, 0, scale,
                (size - bounds.Width * scale) / 2 - bounds.X * scale,
                (size - bounds.Height * scale) / 2 - bounds.Y * scale)
        };
        icon.Width = size;
        icon.Height = size;
    }

    public static FontIcon CreateGlyph(string glyph, double size = 16) => new()
    {
        FontFamily = Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily,
        Glyph = glyph,
        FontSize = size
    };
}
