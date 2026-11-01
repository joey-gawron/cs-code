using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Diff;

/// <summary>
/// The diff editor: two read-only editor views (full syntax colouring, find, copy, Vim motions) side by side, or one inline (unified) view, over
/// two texts compared with <see cref="LineDiff"/>. Changed lines and changed characters are highlighted, the panes scroll together through a line
/// mapping that interpolates inside changes, and Previous / Next Change (Alt+F5 / Shift+Alt+F5 in the keymaps) step through the changes. A side
/// given as a file follows it: edits in its open editor, a save, or a change on disk recompute the diff (debounced, on the editor worker).
/// Disposed with its tab; recreated from the request when the tab's view is rebuilt.
/// </summary>
public sealed class DiffEditorControl : UserControl, IDisposable
{
    /// <summary>Setting: side by side (true, default) or inline.</summary>
    public const string SideBySideSetting = "diffEditor.renderSideBySide";
    /// <summary>Setting: leading/trailing whitespace changes are not differences (VS Code's default: true).</summary>
    public const string IgnoreTrimWhitespaceSetting = "diffEditor.ignoreTrimWhitespace";

    private static int s_counter;

    private readonly EditorViewFactory _factory;
    private readonly DisposableStore _store = new();
    private readonly List<IDisposable> _paneStore = new();
    private readonly Grid _body;
    private readonly TextBlock _summary;
    private readonly TextBlock _leftLabel, _rightLabel;
    private readonly Button _toggle;
    private readonly string _languageId;
    private readonly int _id = Interlocked.Increment(ref s_counter);
    private CancellationTokenSource? _reloadCts;
    private System.Threading.Timer? _debounce;
    private EditorDocument? _leftDoc, _rightDoc, _inlineDoc;
    private CodeEditorControl? _left, _right, _inline;
    private readonly DiffBackgroundRenderer _leftRenderer = new() { GapKind = DiffLineKind.Inserted };
    private readonly DiffBackgroundRenderer _rightRenderer = new() { GapKind = DiffLineKind.Removed };
    private readonly DiffBackgroundRenderer _inlineRenderer = new();
    private DualLineNumberMargin? _numbers;
    private DiffBrushes _brushes;
    private DiffModel? _model;
    private UnifiedDiff? _unified;
    private string _originalText = "", _modifiedText = "";
    private bool _sideBySide;
    private bool _syncing, _disposed;
    private readonly Dictionary<CodeEditorControl, (double Target, long Until)> _pendingScroll = new();
    private int? _pendingReveal;

    internal DiffEditorControl(EditorViewFactory factory, DiffEditorRequest request)
    {
        EditorStyles.Ensure();
        _factory = factory;
        Request = request;
        _brushes = DiffBrushes.From(factory.Themes);
        // nullable reads: the settings are registered by the source-control module, which may be absent
        _sideBySide = factory.Services.Config.Get<bool?>(SideBySideSetting) ?? true;
        IsSingle = request.Original.Uri is null && request.Original.Text is null;
        if (IsSingle) _sideBySide = false;
        _pendingReveal = request.RevealLine;
        var hint = request.LanguageHint is { } h ? (Uri.TryCreate(h, UriKind.Absolute, out var u) ? u : new Uri("file:///" + h.TrimStart('/'))) : request.Modified.Uri ?? request.Original.Uri;
        _languageId = hint is null ? "plaintext" : factory.Languages.DetectLanguageId(hint);

        var p = EditorPalette.From(factory.Themes);
        _leftLabel = new TextBlock { Text = request.Original.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = p.DescriptionForeground };
        _rightLabel = new TextBlock { Text = request.Modified.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = p.DescriptionForeground };
        _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Foreground = p.DescriptionForeground };
        var prev = ToolButton("↑", "Previous Change (Shift+Alt+F5)", () => GoToChange(forward: false));
        var next = ToolButton("↓", "Next Change (Alt+F5)", () => GoToChange(forward: true));
        _toggle = ToolButton(_sideBySide ? "Inline" : "Side by Side", "Toggle Inline View", ToggleInline);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _summary } };
        if (!IsSingle) { tools.Children.Add(prev); tools.Children.Add(next); tools.Children.Add(_toggle); }
        if (request.Modified.Uri is { } file && factory.Services.Navigation is { } nav)
            tools.Children.Add(ToolButton("Open File", "Open the file in an editor", () => _ = nav.OpenAsync(file, CurrentModifiedLine() is var line and >= 0 ? new TextRange(new TextPosition(line, 0), new TextPosition(line, 0)) : null)));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), Height = 26, Background = p.WidgetBackground };
        header.Children.Add(_leftLabel);
        Grid.SetColumn(_rightLabel, 1);
        header.Children.Add(_rightLabel);
        Grid.SetColumn(tools, 2);
        header.Children.Add(tools);

        _body = new Grid();
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_body);
        Content = root;
        Background = p.Background;
        Avalonia.Automation.AutomationProperties.SetName(this, "Diff: " + request.Title);

        _store.Add(factory.Themes.Changed.Subscribe(_ => factory.Services.Ui.Post(OnThemeChanged)));
        _store.Add(factory.Services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects(IgnoreTrimWhitespaceSetting)) ScheduleReload(TimeSpan.Zero);
            if (e.Affects("editor") && !IsSingle) factory.Services.Ui.Post(() => { if (_inline is not null) _inline.Editor.ShowLineNumbers = false; });
        }));
        WatchSide(request.Original);
        WatchSide(request.Modified);
        BuildPanes();
        ScheduleReload(TimeSpan.Zero);
    }

    public DiffEditorRequest Request { get; }

    /// <summary>The current comparison (null until the first computation finished).</summary>
    public DiffModel? Model => _model;

    public bool IsSideBySide => _sideBySide;

    /// <summary>Nothing to compare with (the request's original has neither text nor file): one read-only document.</summary>
    public bool IsSingle { get; }

    /// <summary>Completes when the computation in flight (if any) has been applied (tests).</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    internal CodeEditorControl? OriginalView => _left;
    internal CodeEditorControl? ModifiedView => _right;
    internal CodeEditorControl? InlineView => _inline;
    internal string Summary => _summary.Text ?? "";

    private static Button ToolButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1), Margin = new Thickness(1, 2), MinHeight = 0, FontSize = 12, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------------------------- sources

    private void WatchSide(DiffSide side)
    {
        if (side.Uri is not { } uri) return;
        var docs = _factory.Services.Documents;
        _store.Add(docs.Changed.Subscribe(e => { if (e.Document.Uri == uri) ScheduleReload(TimeSpan.FromMilliseconds(300)); }));
        _store.Add(docs.Opened.Subscribe(e => { if (e.Document.Uri == uri) ScheduleReload(TimeSpan.FromMilliseconds(50)); }));
        _store.Add(docs.Closed.Subscribe(e => { if (e.Document.Uri == uri) ScheduleReload(TimeSpan.FromMilliseconds(50)); }));
        if (uri.IsFile && Path.GetDirectoryName(uri.LocalPath) is { } dir)
            _store.Add(_factory.Services.FileWatcher.Watch(new Uri(dir), recursive: false, excludeGlobs: null, changes =>
            {
                if (changes.Any(c => c.Uri.IsFile && string.Equals(c.Uri.LocalPath, uri.LocalPath, StringComparison.Ordinal)) && docs.Get(uri) is null)
                    ScheduleReload(TimeSpan.FromMilliseconds(200));
            }));
    }

    private async Task<string> LoadAsync(DiffSide side, CancellationToken ct)
    {
        if (side.Text is { } text) return text;
        if (side.Uri is not { } uri) return "";
        string? open = null;
        await _factory.Services.Ui.InvokeAsync(() => open = _factory.Services.Documents.Get(uri)?.GetText()).ConfigureAwait(false);
        if (open is not null) return open;
        try
        {
            var bytes = await _factory.Services.FileSystem.ReadFileAsync(uri, ct).ConfigureAwait(false);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) return "(binary file)";
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private void ScheduleReload(TimeSpan delay)
    {
        if (_disposed) return;
        if (delay == TimeSpan.Zero) { _factory.Services.Ui.Post(() => Ready = ReloadAsync()); return; }
        _debounce ??= new System.Threading.Timer(_ => _factory.Services.Ui.Post(() => Ready = ReloadAsync()));
        _debounce.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async Task ReloadAsync()
    {
        if (_disposed) return;
        _reloadCts?.Cancel();
        var cts = _reloadCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var original = await LoadAsync(Request.Original, ct).ConfigureAwait(false);
            var modified = await LoadAsync(Request.Modified, ct).ConfigureAwait(false);
            if (IsSingle) original = modified;
            var ignore = _factory.Services.Config.Get<bool?>(IgnoreTrimWhitespaceSetting) ?? true;
            var model = await _factory.Worker.RunAsync(WorkPriority.Background, t => DiffModel.Compute(original, modified, ignore, t), ct, key: this).ConfigureAwait(false);
            var unified = _sideBySide ? null : UnifiedDiff.From(model);
            await _factory.Services.Ui.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                Apply(original, modified, model, unified);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------------------------- panes

    private EditorDocument Snapshot(string side, string text)
    {
        var name = Request.LanguageHint is { } h ? Path.GetFileName(h) : "text";
        var uri = new Uri($"cscode-diff:/{_id}/{side}/{Uri.EscapeDataString(name)}");
        return _factory.CreateSnapshotDocument(uri, text, _languageId);
    }

    private void BuildPanes()
    {
        ReleasePanes();
        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        if (_sideBySide)
        {
            _leftDoc = Snapshot("original", _originalText);
            _rightDoc = Snapshot("modified", _modifiedText);
            _left = new CodeEditorControl(_factory, _leftDoc) { AllowMinimap = false }; // no minimap in diff panes (VS Code)
            _right = new CodeEditorControl(_factory, _rightDoc) { AllowMinimap = false };
            _body.ColumnDefinitions = new ColumnDefinitions("*,4,*");
            _body.Children.Add(_left);
            var splitter = new GridSplitter { Width = 4, Background = EditorPalette.From(_factory.Themes).WidgetBorder, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(splitter, 1);
            _body.Children.Add(splitter);
            Grid.SetColumn(_right, 2);
            _body.Children.Add(_right);
            Attach(_left, _leftRenderer);
            Attach(_right, _rightRenderer);
            _left.TextView.ScrollOffsetChanged += OnLeftScrolled;
            _right.TextView.ScrollOffsetChanged += OnRightScrolled;
            _paneStore.Add(Disposable.Create(() =>
            {
                if (_left is not null) _left.TextView.ScrollOffsetChanged -= OnLeftScrolled;
                if (_right is not null) _right.TextView.ScrollOffsetChanged -= OnRightScrolled;
            }));
            _leftLabel.IsVisible = true;
            _rightLabel.Text = Request.Modified.Label;
        }
        else
        {
            _inlineDoc = Snapshot("inline", _unified?.Text ?? _modifiedText);
            _inline = new CodeEditorControl(_factory, _inlineDoc) { AllowMinimap = false };
            _body.Children.Add(_inline);
            Attach(_inline, _inlineRenderer);
            if (!IsSingle)
            {
                _inline.Editor.ShowLineNumbers = false;
                var numbers = _numbers = new DualLineNumberMargin { Brushes = _brushes, Typeface = new Typeface(_inline.Editor.FontFamily), FontSize = _inline.Editor.FontSize, Diff = _unified };
                var inline = _inline;
                inline.TextArea.LeftMargins.Insert(0, numbers);
                _paneStore.Add(Disposable.Create(() => inline.TextArea.LeftMargins.Remove(numbers)));
            }
            _leftLabel.IsVisible = false;
            _rightLabel.Text = IsSingle ? Request.Modified.Label : $"{Request.Original.Label} ↔ {Request.Modified.Label}";
        }
        _toggle.Content = _sideBySide ? "Inline" : "Side by Side";
    }

    private void Attach(CodeEditorControl view, DiffBackgroundRenderer renderer)
    {
        renderer.Brushes = _brushes;
        view.TextView.BackgroundRenderers.Add(renderer);
        _paneStore.Add(Disposable.Create(() => view.TextView.BackgroundRenderers.Remove(renderer)));
    }

    private void ReleasePanes()
    {
        for (var i = _paneStore.Count - 1; i >= 0; i--) _paneStore[i].Dispose();
        _paneStore.Clear();
        foreach (var v in new[] { _left, _right, _inline }) v?.Dispose();
        foreach (var d in new[] { _leftDoc, _rightDoc, _inlineDoc }) if (d is not null) _factory.ReleaseSnapshotDocument(d);
        _left = _right = _inline = null;
        _leftDoc = _rightDoc = _inlineDoc = null;
        _numbers = null;
    }

    private static void SetText(EditorDocument doc, string text)
    {
        var buffer = doc.Buffer;
        if (buffer.TextLength == text.Length && buffer.Text == text) return;
        buffer.Text = text;
        buffer.UndoStack.ClearAll();
        buffer.UndoStack.MarkAsOriginalFile();
    }

    private void Apply(string original, string modified, DiffModel model, UnifiedDiff? unified)
    {
        _originalText = original;
        _modifiedText = modified;
        _model = model;
        _unified = unified;
        if (_sideBySide && _leftDoc is not null && _rightDoc is not null && _left is not null && _right is not null)
        {
            var leftOffset = _left.Editor.VerticalOffset;
            var rightOffset = _right.Editor.VerticalOffset;
            SetText(_leftDoc, original);
            SetText(_rightDoc, modified);
            _leftRenderer.Kinds = model.OriginalKinds;
            _leftRenderer.Spans = model.OriginalSpans;
            _leftRenderer.Gaps = [.. model.Changes.Where(c => c.OriginalLength == 0).Select(c => c.OriginalStart)];
            _rightRenderer.Kinds = model.ModifiedKinds;
            _rightRenderer.Spans = model.ModifiedSpans;
            _rightRenderer.Gaps = [.. model.Changes.Where(c => c.ModifiedLength == 0).Select(c => c.ModifiedStart)];
            _syncing = true;
            try { ScrollTo(_left, leftOffset); ScrollTo(_right, rightOffset); }
            finally { _syncing = false; }
            _left.TextView.InvalidateLayer(KnownLayer.Background);
            _right.TextView.InvalidateLayer(KnownLayer.Background);
        }
        else if (!_sideBySide && _inlineDoc is not null && _inline is not null && unified is not null)
        {
            var offset = _inline.Editor.VerticalOffset;
            SetText(_inlineDoc, unified.Text);
            _inlineRenderer.Kinds = unified.Kinds;
            _inlineRenderer.Spans = unified.Spans;
            if (_numbers is not null) { _numbers.Diff = unified; _numbers.InvalidateMeasure(); _numbers.InvalidateVisual(); }
            ScrollTo(_inline, offset);
            _inline.TextView.InvalidateLayer(KnownLayer.Background);
        }
        var count = model.Changes.Count;
        _summary.Text = IsSingle ? "" : count == 0
            ? (original == modified ? "Files are identical" : "No changes (whitespace only)")
            : $"{count} change{(count == 1 ? "" : "s")}" + (model.HitTimeout ? " (diff took too long: shown coarsely)" : "");
        if (_pendingReveal is { } reveal)
        {
            _pendingReveal = null;
            RevealModifiedLine(reveal);
        }
    }

    private void ToggleInline()
    {
        _sideBySide = !_sideBySide;
        _unified = _sideBySide || _model is null ? null : UnifiedDiff.From(_model);
        BuildPanes();
        if (_model is not null) Apply(_originalText, _modifiedText, _model, _unified);
    }

    private void OnThemeChanged()
    {
        _brushes = DiffBrushes.From(_factory.Themes);
        foreach (var r in new[] { _leftRenderer, _rightRenderer, _inlineRenderer }) r.Brushes = _brushes;
        if (_numbers is not null) _numbers.Brushes = _brushes;
        foreach (var v in new[] { _left, _right, _inline }) v?.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // ------------------------------------------------------------------------------------- scrolling

    private void OnLeftScrolled(object? sender, EventArgs e) => Sync(_left, _right, fromOriginal: true);
    private void OnRightScrolled(object? sender, EventArgs e) => Sync(_right, _left, fromOriginal: false);

    private void Sync(CodeEditorControl? from, CodeEditorControl? to, bool fromOriginal)
    {
        if (_syncing || from is null || to is null || _model is not { } model || from.IsDisposed || to.IsDisposed) return;
        if (from.TextView.Document is null || to.TextView.Document is null) return;
        if (_pendingScroll.TryGetValue(from, out var pending))
        {
            var arrived = Math.Abs(from.TextView.ScrollOffset.Y - pending.Target) < 1;
            if (arrived || Environment.TickCount64 > pending.Until) _pendingScroll.Remove(from);
            if (arrived || Environment.TickCount64 <= pending.Until) return;
        }
        _syncing = true;
        try
        {
            var tv = from.TextView;
            var lineHeight = Math.Max(1, tv.DefaultLineHeight);
            var y = tv.ScrollOffset.Y;
            var top = TopLine(tv, y, lineHeight);
            var mapped = model.MapLine(top, fromOriginal);
            var target = Math.Clamp((int)Math.Floor(mapped), 0, Math.Max(0, to.Document.LineCount - 1));
            var frac = mapped - Math.Floor(mapped);
            var otherTop = to.TextView.GetVisualTopByDocumentLine(target + 1) + frac * Math.Max(1, to.TextView.DefaultLineHeight);
            otherTop = Math.Clamp(otherTop, 0, Math.Max(0, to.Editor.ExtentHeight - to.Editor.ViewportHeight));
            // the offset is applied at the next layout pass, and the pane reports its OLD offset meanwhile: remember where it is going, so
            // those reports (and the echo of this very scroll) are not taken for the user scrolling it and synced back
            _pendingScroll[to] = (otherTop, Environment.TickCount64 + 500);
            ScrollTo(to, otherTop, tv.ScrollOffset.X);
        }
        finally { _syncing = false; }
    }

    /// <summary>Scrolls a pane to an absolute offset (TextEditor.ScrollToVerticalOffset is a no-op for absolute positions: the text area's offset is set).</summary>
    private static void ScrollTo(CodeEditorControl view, double vertical, double? horizontal = null) =>
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Vector(horizontal ?? view.TextView.ScrollOffset.X, vertical);

    private static double TopLine(TextView tv, double y, double lineHeight)
    {
        if (tv.Document is null || tv.Document.LineCount == 0) return 0;
        var line = tv.GetDocumentLineByVisualTop(y);
        var lineTop = tv.GetVisualTopByDocumentLine(line.LineNumber);
        return line.LineNumber - 1 + Math.Clamp((y - lineTop) / lineHeight, 0, 0.999);
    }

    // ------------------------------------------------------------------------------------- navigation

    private int CurrentModifiedLine()
    {
        if (_right is not null) return _right.TextArea.Caret.Line - 1;
        if (_inline is not null && _unified is not null)
        {
            var l = Math.Clamp(_inline.TextArea.Caret.Line - 1, 0, _unified.ModifiedNumbers.Length - 1);
            for (var i = l; i < _unified.ModifiedNumbers.Length; i++) if (_unified.ModifiedNumbers[i] is { } n) return n - 1;
        }
        return -1;
    }

    /// <summary>Moves to the next (or previous) change, wrapping, and scrolls it into the middle of the view. Returns the change's index, or -1 when there is none.</summary>
    public int GoToChange(bool forward)
    {
        if (_model is not { } model || model.Changes.Count == 0) return -1;
        if (_sideBySide && _right is not null && _left is not null)
        {
            var onOriginal = _left.IsKeyboardFocusWithin;
            var view = onOriginal ? _left : _right;
            var index = model.NextChange(view.TextArea.Caret.Line - 1, forward, onModified: !onOriginal);
            var c = model.Changes[index];
            MoveCaret(_right, c.ModifiedStart);
            MoveCaret(_left, c.OriginalStart);
            view.Editor.ScrollToLine(Math.Min((onOriginal ? c.OriginalStart : c.ModifiedStart) + 1, Math.Max(1, view.Document.LineCount)));
            view.Controller.Reveal(new TextRange(new TextPosition(onOriginal ? c.OriginalStart : c.ModifiedStart, 0), new TextPosition(onOriginal ? c.OriginalStart : c.ModifiedStart, 0)), center: true);
            return index;
        }
        if (_inline is not null && _unified is { } u)
        {
            var line = _inline.TextArea.Caret.Line - 1;
            var index = forward ? Array.FindIndex(u.ChangeStarts, s => s > line) : Array.FindLastIndex(u.ChangeStarts, s => s < line);
            if (index < 0) index = forward ? 0 : u.ChangeStarts.Length - 1;
            MoveCaret(_inline, u.ChangeStarts[index]);
            _inline.Controller.Reveal(new TextRange(new TextPosition(u.ChangeStarts[index], 0), new TextPosition(u.ChangeStarts[index], 0)), center: true);
            return index;
        }
        return -1;
    }

    private static void MoveCaret(CodeEditorControl view, int line)
    {
        line = Math.Clamp(line, 0, Math.Max(0, view.Document.LineCount - 1));
        view.TextArea.Caret.Line = line + 1;
        view.TextArea.Caret.Column = 1;
    }

    private void RevealModifiedLine(int line)
    {
        if (_right is not null)
        {
            MoveCaret(_right, line);
            _right.Controller.Reveal(new TextRange(new TextPosition(line, 0), new TextPosition(line, 0)), center: true);
        }
        else if (_inline is not null && _unified is { } u)
        {
            var at = Array.FindIndex(u.ModifiedNumbers, n => n == line + 1);
            if (at >= 0) { MoveCaret(_inline, at); _inline.Controller.Reveal(new TextRange(new TextPosition(at, 0), new TextPosition(at, 0)), center: true); }
        }
    }

    /// <summary>Puts the keyboard in the modified (or inline) pane.</summary>
    public void FocusEditor() => (_right ?? _inline)?.Controller.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reloadCts?.Cancel();
        _debounce?.Dispose();
        _store.Dispose();
        ReleasePanes();
        Content = null;
    }
}
