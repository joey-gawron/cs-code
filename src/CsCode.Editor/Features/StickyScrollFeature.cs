using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Sticky scroll (VS Code's <c>editor.stickyScroll.enabled</c>, OFF by default in CsCode; docs/TREE-SITTER.md, "Structure"): while the first
/// visible line is inside a foldable region whose first line has scrolled away, that first line stays pinned at the top of the text, outermost
/// region first, at most <see cref="MaxLines"/>. The regions are the ones folding uses (<see cref="FoldingFeature.RegionsContaining"/>): the
/// syntax tree's folds for a document tree-sitter colours, a language server's folding ranges, or indentation — so it works for every language,
/// and exactly by syntax where there is a tree. Clicking a pinned line goes to it.
///
/// Threading: everything here is UI work bounded by the region count around one line (the folding manager's segment tree), a few
/// microseconds per scroll; a pinned row's text is built only when the set of pinned lines changes.
/// </summary>
internal sealed class StickyScrollFeature : ISettingsAware, IThemeAware, IDisposable
{
    public const int MaxLines = 5;

    private readonly CodeEditorControl _v;
    private readonly StackPanel _panel;
    private readonly DisposableStore _d = new();
    private readonly List<int> _lines = new();
    private readonly List<string> _texts = new(); // what each pinned row shows (the line's text when it was built)
    private bool _textsStale;
    private bool _updatePosted;
    private IBrush _background = Brushes.Transparent;
    private IBrush _hover = Brushes.Transparent;
    private IBrush _border = Brushes.Transparent;
    private bool _disposed;

    public StickyScrollFeature(CodeEditorControl view)
    {
        _v = view;
        _panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsVisible = false,
            ClipToBounds = true,
        };
        if (view.Content is Grid grid) grid.Children.Add(_panel);
        view.TextView.ScrollOffsetChanged += OnViewChanged;
        view.TextView.VisualLinesChanged += OnViewChanged;
        _d.Add(Disposable.Create(() =>
        {
            view.TextView.ScrollOffsetChanged -= OnViewChanged;
            view.TextView.VisualLinesChanged -= OnViewChanged;
        }));
        // A pinned header is above the first visible line, so it is edited from elsewhere (another split view, Replace All, a rename, undo, a
        // macro) without the set of pinned line numbers changing: after an edit the rows compare their text too. The typing path only sets a
        // flag; the check is posted once (coalesced) and reads at most MaxLines lines.
        _d.Add(view.Document.Changed.Subscribe(_ => OnDocumentChanged()));
        ApplyBrushes();
        Update();
    }

    private void OnDocumentChanged()
    {
        if (_disposed || _lines.Count == 0) return;
        _textsStale = true;
        if (_updatePosted) return;
        _updatePosted = true;
        _v.Post(() =>
        {
            _updatePosted = false;
            Update();
        });
    }

    /// <summary>The document lines pinned right now, outermost first (tests, diagnostics).</summary>
    internal IReadOnlyList<int> PinnedLines => _lines;

    /// <summary>The text shown for each pinned line (tests).</summary>
    internal IEnumerable<string> PinnedTexts => _panel.Children.OfType<Border>().Select(b => ((TextBlock)b.Child!).Text ?? "");

    private void OnViewChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (_disposed) return;
        var wanted = Compute();
        if (wanted.SequenceEqual(_lines) && !TextsChanged())
        {
            Place();
            return;
        }
        _lines.Clear();
        _lines.AddRange(wanted);
        Rebuild();
    }

    /// <summary>The pinned lines for the current scroll position: the headers of the regions around the first visible line that start above it.</summary>
    private List<int> Compute()
    {
        var result = new List<int>();
        if (!_v.Settings.StickyScroll || _v.Feature<FoldingFeature>() is not { } folding) return result;
        var textView = _v.TextView;
        if (!textView.VisualLinesValid || textView.DocumentHeight <= 0) return result;
        var top = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y);
        if (top is null) return result;
        var first = top.LineNumber - 1;
        if (first <= 0) return result;
        foreach (var (start, end) in folding.RegionsContaining(first))
        {
            if (result.Count >= MaxLines) break;
            if (start >= first + result.Count) break; // its first line is still visible below the pinned rows
            if (end <= first + result.Count) continue; // the region ends under the pinned rows: nothing of its body is left to read
            var header = HeaderLine(start);
            if (result.Count > 0 && result[^1] >= header) continue;
            result.Add(header);
        }
        return result;
    }

    /// <summary>
    /// The line that names a region: its first line, or — when that is only an opening bracket (Allman-style <c>{</c> under
    /// <c>class Big</c>, where indentation folding starts) — the declaration just above it.
    /// </summary>
    private int HeaderLine(int start)
    {
        var text = _v.Document.GetLineText(start).AsSpan().Trim();
        if (start == 0 || text.Length != 1 || text[0] is not ('{' or '(' or '[')) return start;
        return _v.Document.GetLineText(start - 1).AsSpan().Trim().Length > 0 ? start - 1 : start;
    }

    /// <summary>After an edit: whether a pinned row's line reads differently now (the flag is cleared; at most <see cref="MaxLines"/> lines read).</summary>
    private bool TextsChanged()
    {
        if (!_textsStale) return false;
        _textsStale = false;
        var lineCount = _v.Document.LineCount;
        for (var i = 0; i < _lines.Count; i++)
            if (i >= _texts.Count || _lines[i] >= lineCount || !string.Equals(_texts[i], _v.Document.GetLineText(_lines[i]), StringComparison.Ordinal)) return true;
        return false;
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        _texts.Clear();
        _textsStale = false;
        if (_lines.Count == 0)
        {
            _panel.IsVisible = false;
            return;
        }
        var editor = _v.Editor;
        var tab = Math.Max(1, _v.Settings.TabSize);
        var lineHeight = _v.TextView.DefaultLineHeight;
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var raw = _v.Document.GetLineText(line);
            _texts.Add(raw);
            var text = raw.Replace("\t", new string(' ', tab), StringComparison.Ordinal);
            var block = new TextBlock
            {
                Text = text,
                FontFamily = editor.FontFamily,
                FontSize = editor.FontSize,
                FontWeight = editor.FontWeight,
                Foreground = _v.Palette.Foreground,
                TextWrapping = TextWrapping.NoWrap,
                Height = lineHeight,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new Border
            {
                Child = block,
                Background = _background,
                BorderBrush = _border,
                BorderThickness = i == _lines.Count - 1 ? new Thickness(0, 0, 0, 1) : default,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = line,
            };
            row.PointerEntered += (_, _) => row.Background = _hover;
            row.PointerExited += (_, _) => row.Background = _background;
            row.PointerPressed += OnRowPressed;
            _panel.Children.Add(row);
        }
        _panel.IsVisible = true;
        Place();
    }

    /// <summary>Lines the rows up with the text: past the line numbers and folding margins, scrolled horizontally with it.</summary>
    private void Place()
    {
        if (!_panel.IsVisible) return;
        var origin = _v.TextView.TranslatePoint(default, _v) ?? default;
        _panel.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        var shift = -_v.TextView.ScrollOffset.X;
        foreach (var row in _panel.Children.OfType<Border>()) row.Child!.Margin = new Thickness(shift, 0, 0, 0);
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int line }) return;
        e.Handled = true;
        GoTo(line);
    }

    /// <summary>What clicking a pinned line does: the caret to that line, the line brought into view.</summary>
    internal void GoTo(int line)
    {
        _v.Controller.SetSelection(TextRange.At(line, 0), reveal: false);
        _v.Controller.Reveal(TextRange.At(line, 0), center: true);
        _v.Controller.Focus();
    }

    private void ApplyBrushes()
    {
        var themes = _v.Factory.Themes;
        IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
        _background = B("editorStickyScroll.background");
        _hover = B("editorStickyScrollHover.background");
        _border = B("editorLineNumber.foreground");
    }

    public void OnThemeChanged()
    {
        ApplyBrushes();
        Rebuild();
    }

    public void OnSettingsChanged()
    {
        _lines.Clear();
        _lines.AddRange(Compute());
        Rebuild();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d.Dispose();
        foreach (var row in _panel.Children.OfType<Border>()) row.PointerPressed -= OnRowPressed;
        _panel.Children.Clear();
        if (_v.Content is Grid grid) grid.Children.Remove(_panel);
    }
}
