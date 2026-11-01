using Avalonia.Media;
using Avalonia.Media.Immutable;
using CsCode.Core.Theming;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>
/// Fonts and colours of the rendered preview. The colours come from the current colour theme's VS Code ids (<c>editor.background</c>,
/// <c>editor.foreground</c>, <c>textLink.foreground</c>, <c>textCodeBlock.background</c>, <c>textPreformat.foreground</c>,
/// <c>textBlockQuote.*</c>, <c>textSeparator.foreground</c>), which every theme has (defaults fill the gaps), so the preview follows light, dark,
/// high-contrast and user themes. Immutable brushes: safe to share between the controls of one render.
/// </summary>
public sealed record MarkdownPreviewStyle(
    FontFamily Body,
    FontFamily Mono,
    double FontSize,
    double LineHeight,
    IBrush Background,
    IBrush Foreground,
    IBrush Link,
    IBrush CodeBackground,
    IBrush InlineCode,
    IBrush QuoteBackground,
    IBrush QuoteBorder,
    IBrush Separator,
    IBrush Muted,
    IBrush TableHeaderBackground,
    IBrush FindMatch,
    ThemeKind Kind)
{
    /// <summary>The code font's size: VS Code's preview uses the editor font at 1em of the body text (14 px with the defaults).</summary>
    public double CodeFontSize => FontSize * 0.93;

    /// <summary>The space under every top-level block.</summary>
    public double BlockSpacing => Math.Round(FontSize * 0.85);

    public static MarkdownPreviewStyle From(IColorThemeService themes, FontFamily body, FontFamily mono, double fontSize, double lineHeight)
    {
        IBrush B(string key) => new ImmutableSolidColorBrush(ToColor(themes.GetColor(key)));
        var fg = ToColor(themes.GetColor("editor.foreground"));
        return new MarkdownPreviewStyle(
            body, mono, Math.Clamp(fontSize, 6, 100), Math.Clamp(lineHeight, 1, 3),
            B("editor.background"),
            new ImmutableSolidColorBrush(fg),
            B("textLink.foreground"),
            B("textCodeBlock.background"),
            B("textPreformat.foreground"),
            B("textBlockQuote.background"),
            B("textBlockQuote.border"),
            B("textSeparator.foreground"),
            B("descriptionForeground"),
            new ImmutableSolidColorBrush(Color.FromArgb(0x1A, fg.R, fg.G, fg.B)),
            B("editor.findMatchHighlightBackground"),
            themes.Current.Kind);
    }

    private static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
