using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Search.ViewModels;

namespace CsCode.Panels.Search.Views;

/// <summary>
/// One result line: the preview text with the match highlighted (find-match colour) and, while a replacement is typed, the match struck
/// through followed by the text it would become. Single line, character ellipsis. Runs are rebuilt only when an input changes; while attached
/// it listens to the shared <see cref="ReplacePreviewState"/>, so a keystroke in the replace box touches only the realised rows.
/// </summary>
public sealed class MatchPreviewText : TextBlock
{
    public static readonly StyledProperty<string?> PreviewProperty = AvaloniaProperty.Register<MatchPreviewText, string?>(nameof(Preview));
    public static readonly StyledProperty<int> MatchStartProperty = AvaloniaProperty.Register<MatchPreviewText, int>(nameof(MatchStart));
    public static readonly StyledProperty<int> MatchLengthProperty = AvaloniaProperty.Register<MatchPreviewText, int>(nameof(MatchLength));
    public static readonly StyledProperty<string?> MatchedTextProperty = AvaloniaProperty.Register<MatchPreviewText, string?>(nameof(MatchedText));
    public static readonly StyledProperty<ReplacePreviewState?> PreviewsProperty = AvaloniaProperty.Register<MatchPreviewText, ReplacePreviewState?>(nameof(Previews));

    private IDisposable? _sub;

    public MatchPreviewText()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        TextWrapping = TextWrapping.NoWrap;
        MaxLines = 1;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public int MatchStart { get => GetValue(MatchStartProperty); set => SetValue(MatchStartProperty, value); }
    public int MatchLength { get => GetValue(MatchLengthProperty); set => SetValue(MatchLengthProperty, value); }
    public string? MatchedText { get => GetValue(MatchedTextProperty); set => SetValue(MatchedTextProperty, value); }
    public ReplacePreviewState? Previews { get => GetValue(PreviewsProperty); set => SetValue(PreviewsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PreviewProperty || change.Property == MatchStartProperty || change.Property == MatchLengthProperty
            || change.Property == MatchedTextProperty || change.Property == PreviewsProperty)
        {
            if (change.Property == PreviewsProperty && IsAttachedToVisualTree()) Resubscribe();
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Resubscribe();
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _sub?.Dispose();
        _sub = null;
        base.OnDetachedFromVisualTree(e);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    private void Resubscribe()
    {
        _sub?.Dispose();
        _sub = Previews?.Changed.Subscribe(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild));
    }

    private IBrush? Res(string key) => this.TryFindResource(key, ActualThemeVariant, out var r) ? r as IBrush : null;

    private void Rebuild()
    {
        Inlines?.Clear();
        var text = Preview ?? "";
        if (text.Length == 0) return;
        var start = Math.Clamp(MatchStart, 0, text.Length);
        var len = Math.Clamp(MatchLength, 0, text.Length - start);
        var inlines = Inlines ??= new InlineCollection();
        if (start > 0) inlines.Add(new Run(text[..start]));

        var replacement = len > 0 && MatchedText is { } matched ? Previews?.Preview(matched) : null;
        if (len > 0)
        {
            var hit = new Run(text.Substring(start, len)) { Background = Res("vsc:editor.findMatchHighlightBackground") };
            if (replacement is not null)
            {
                hit.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                hit.Foreground = Res("vsc:gitDecoration.deletedResourceForeground");
                hit.Background = Res("vsc:diffEditor.removedTextBackground") ?? Res("vsc:editor.findMatchHighlightBackground");
            }
            inlines.Add(hit);
            if (replacement is { Length: > 0 })
                inlines.Add(new Run(Visible(replacement)) { Foreground = Res("vsc:gitDecoration.addedResourceForeground"), Background = Res("vsc:diffEditor.insertedTextBackground") });
        }
        if (start + len < text.Length) inlines.Add(new Run(text[(start + len)..]));
    }

    /// <summary>Line breaks and tabs of a replacement cannot show in one line: they are made visible.</summary>
    private static string Visible(string s) => s.Replace("\r\n", "↵").Replace("\n", "↵").Replace("\r", "↵").Replace('\t', '→');
}
