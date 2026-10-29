using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace CsCode.Ui.Controls;

/// <summary>
/// A TextBlock that renders fuzzy-match highlights: characters at <see cref="Matches"/> are drawn bold in the list-highlight colour
/// (VS Code's <c>list.highlightForeground</c>). Runs are rebuilt only when the text or the match set changes.
/// </summary>
public sealed class HighlightedTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty = AvaloniaProperty.Register<HighlightedTextBlock, string?>(nameof(SourceText));
    public static readonly StyledProperty<int[]?> MatchesProperty = AvaloniaProperty.Register<HighlightedTextBlock, int[]?>(nameof(Matches));
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty = AvaloniaProperty.Register<HighlightedTextBlock, IBrush?>(nameof(HighlightBrush));

    static HighlightedTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
        MatchesProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
        HighlightBrushProperty.Changed.AddClassHandler<HighlightedTextBlock>((b, _) => b.Rebuild());
    }

    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public int[]? Matches { get => GetValue(MatchesProperty); set => SetValue(MatchesProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    private void Rebuild()
    {
        var text = SourceText ?? "";
        var matches = Matches;
        Inlines?.Clear();
        if (matches is null || matches.Length == 0)
        {
            Text = text;
            return;
        }
        Text = null;
        Inlines ??= new InlineCollection();
        var brush = HighlightBrush;
        var set = new HashSet<int>(matches);
        var i = 0;
        while (i < text.Length)
        {
            var hit = set.Contains(i);
            var j = i + 1;
            while (j < text.Length && set.Contains(j) == hit) j++;
            var run = new Run(text[i..j]);
            if (hit)
            {
                run.FontWeight = FontWeight.SemiBold;
                if (brush is not null) run.Foreground = brush;
            }
            Inlines.Add(run);
            i = j;
        }
    }
}
