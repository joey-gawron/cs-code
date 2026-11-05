using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Languages;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Git.Views;

/// <summary>
/// Spell checking of the commit message box (docs/SPELLCHECK.md) with the app's spell checker (Core's <see cref="ITextSpellChecker"/>): a
/// transparent layer over the text box draws a wavy underline under each misspelt word, and right-clicking one offers its replacements and
/// Add to User Dictionary (with Cut / Copy / Paste) instead of the box's own menu. The text is checked 300 ms after typing stops, on the
/// checker's lane (language id <c>scminput</c>, cspell's for this box); an answer for older text is dropped, and the underlines go as soon as
/// the text changes. Nothing is checked or held while the box is not in a window.
/// </summary>
internal sealed class CommitMessageSpelling : Control
{
    public const string LanguageId = "scminput";
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    private readonly TextBox _box;
    private readonly ITextSpellChecker _checker;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<SpellingIssue> _issues = [];
    private string? _checkedText;
    private CancellationTokenSource? _cts;
    private IDisposable? _changed;
    private bool _attached;

    public CommitMessageSpelling(TextBox box, ITextSpellChecker checker)
    {
        _box = box;
        _checker = checker;
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += OnTick;
        box.TextChanged += OnTextChanged;
        box.ContextRequested += OnContextRequested;
        box.PropertyChanged += OnBoxPropertyChanged;
        box.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled);
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_issues.Count > 0) InvalidateVisual();
    }

    /// <summary>The misspelt words of the text as last checked (empty while the text differs from it).</summary>
    internal IReadOnlyList<SpellingIssue> Issues => _issues;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _changed = _checker.Changed.Subscribe(_ => Dispatcher.UIThread.Post(Schedule));
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _changed?.Dispose();
        _changed = null;
        _timer.Stop();
        _cts?.Cancel();
        _issues = [];
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_issues.Count > 0)
        {
            _issues = [];
            InvalidateVisual();
        }
        Schedule();
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_issues.Count > 0 && e.Property == BoundsProperty) InvalidateVisual();
    }

    private void Schedule()
    {
        if (!_attached) return;
        _timer.Stop();
        _timer.Start();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var text = _box.Text ?? "";
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IReadOnlyList<SpellingIssue> issues;
        try
        {
            issues = text.Length == 0 ? [] : await _checker.CheckTextAsync(text, LanguageId, cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // cancelled by newer text, or the checker is gone (shutting down): no underlines
        }
        if (cts.IsCancellationRequested || (_box.Text ?? "") != text) return; // typed meanwhile: the next check answers
        _checkedText = text;
        _issues = issues;
        InvalidateVisual();
    }

    /// <summary>The underline rectangles, in this layer's coordinates (each word's text rows; tests).</summary>
    internal IReadOnlyList<Rect> Underlines()
    {
        if (_issues.Count == 0 || _checkedText != (_box.Text ?? "")) return [];
        if (_box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { TextLayout: { } layout } presenter) return [];
        var viewport = presenter.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroller && scroller.TranslatePoint(default, this) is { } origin
            ? new Rect(origin, scroller.Bounds.Size)
            : new Rect(Bounds.Size);
        var rects = new List<Rect>();
        foreach (var issue in _issues)
        {
            if (issue.Start + issue.Length > (_box.Text?.Length ?? 0)) continue;
            foreach (var r in layout.HitTestTextRange(issue.Start, issue.Length))
            {
                if (presenter.TranslatePoint(r.TopLeft, this) is not { } at) continue;
                var rect = new Rect(at, r.Size);
                if (rect.Width > 0 && rect.Intersects(viewport)) rects.Add(rect);
            }
        }
        return rects;
    }

    public override void Render(DrawingContext context)
    {
        var rects = Underlines();
        if (rects.Count == 0) return;
        var pen = new Pen(ThemeBrushes.Get("editorInfo.foreground"), 1);
        foreach (var r in rects)
        {
            // a wave of 2 px amplitude under the word, as the editor draws its squiggles
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var y = r.Bottom - 1;
                g.BeginFigure(new Point(r.Left, y), false);
                var up = true;
                for (var x = r.Left + 2; x <= r.Right + 0.5; x += 2, up = !up) g.LineTo(new Point(x, up ? y - 2 : y));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    // ---- the right-click menu over a misspelt word ----

    private async void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IssueAt(e) is not { } issue) return; // anywhere else: the box's own menu
        e.Handled = true;
        try
        {
            if (await MenuForAsync(issue) is { } entries) ContextMenus.Open(entries, _box, accessibleName: "Spelling");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the checker is gone (shutting down): no menu
        }
    }

    private SpellingIssue? IssueAt(ContextRequestedEventArgs e)
    {
        if (_issues.Count == 0 || _checkedText != (_box.Text ?? "")) return null;
        int offset;
        if (_box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is { TextLayout: { } layout } presenter && e.TryGetPosition(presenter, out var p))
            offset = layout.HitTestPoint(p).TextPosition;
        else offset = _box.CaretIndex; // the keyboard's menu key: the word at the caret
        return _issues.FirstOrDefault(i => offset >= i.Start && offset <= i.Start + i.Length);
    }

    /// <summary>The replacements for <paramref name="issue"/>, Add to User Dictionary, then Cut / Copy / Paste (null when the text moved on).</summary>
    internal async Task<IReadOnlyList<MenuEntry>?> MenuForAsync(SpellingIssue issue)
    {
        var text = _box.Text ?? "";
        var suggestions = await _checker.SuggestAsync(issue.Word);
        if ((_box.Text ?? "") != text) return null;
        IReadOnlyList<MenuEntry> replace = suggestions.Count == 0
            ? [new MenuEntry("(no suggestions)", () => Task.CompletedTask) { IsEnabled = false }]
            : suggestions.Select(s => new MenuEntry(s.Replace("_", "__", StringComparison.Ordinal), () => Replace(issue, s, text))).ToArray();
        return MenuSections.Join(
            replace,
            [new MenuEntry($"Add \"{issue.Word.Replace("_", "__", StringComparison.Ordinal)}\" to User Dictionary", () => _checker.AddToUserDictionaryAsync(issue.Word))],
            [
                new MenuEntry("Cu_t", () => { _box.Cut(); return Task.CompletedTask; }),
                new MenuEntry("_Copy", () => { _box.Copy(); return Task.CompletedTask; }),
                new MenuEntry("_Paste", () => { _box.Paste(); return Task.CompletedTask; }),
            ]);
    }

    /// <summary>Replaces the word with <paramref name="replacement"/> when the text is still the one the menu was made for.</summary>
    internal Task Replace(SpellingIssue issue, string replacement, string expectedText)
    {
        var text = _box.Text ?? "";
        if (text != expectedText || issue.Start + issue.Length > text.Length) return Task.CompletedTask;
        _box.Text = string.Concat(text.AsSpan(0, issue.Start), replacement, text.AsSpan(issue.Start + issue.Length));
        _box.CaretIndex = issue.Start + replacement.Length;
        return Task.CompletedTask;
    }

    /// <summary>Unhooks from the text box (the view's disposal).</summary>
    public void Detach()
    {
        _box.TextChanged -= OnTextChanged;
        _box.ContextRequested -= OnContextRequested;
        _box.PropertyChanged -= OnBoxPropertyChanged;
        _box.RemoveHandler(ScrollViewer.ScrollChangedEvent, OnScrolled);
        _timer.Tick -= OnTick;
        _timer.Stop();
        _changed?.Dispose();
        _changed = null;
        _cts?.Cancel();
    }
}
