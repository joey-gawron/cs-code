using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Diff;

/// <summary>
/// The 3-way merge editor (VS Code's merge editor): Incoming (theirs) and Current (ours) on top, read-only, each change against the base
/// highlighted; the Result below, which is the file's own open document (editable, with every editor feature). Regions changed on one side, or
/// alike on both, are merged already; each conflict has an "Accept Incoming" / "Accept Current" checkbox above it in the input panes (a code
/// lens; taking both puts them in the order they were taken) and the header's buttons act on the conflict the Result's caret is in. The header
/// counts the conflicts left and steps through them; Complete Merge saves the result and runs the request's completion (git: stage the file).
/// <para>
/// The merge is computed with <see cref="ThreeWayMerge"/> (on the editor worker, over <see cref="LineDiff"/>). When the file still has conflict
/// markers, the Result starts as the base with the merged changes applied (one undo step; closing the editor without touching it undoes it);
/// otherwise its text is kept and each conflict is found in it by a diff, its state read from its text. Each conflict's place in the Result is
/// held by two anchors, so edits elsewhere move it; an edit inside it (typing, undo, redo) is classified again: a side's text checks that side,
/// anything else counts as handled by hand. The three panes scroll together through the conflicts' boundaries.
/// </para>
/// </summary>
public sealed class MergeEditorControl : UserControl, IDisposable
{
    /// <summary>The input panes' lens command: arguments (editor id, conflict index, "incoming" | "current").</summary>
    public const string ToggleCommand = "cscode.mergeEditor.toggleSide";
    internal const string Scheme = "cscode-merge";

    private static int s_counter;

    private readonly EditorViewFactory _factory;
    private readonly EditorDocument _result;
    private readonly int _id = Interlocked.Increment(ref s_counter);
    private readonly DisposableStore _store = new();
    private readonly List<IDisposable> _paneStore = new();
    private readonly Grid _body;
    private readonly TextBlock _count;
    private readonly Button _previous, _next, _acceptIncoming, _acceptCurrent, _acceptBoth, _complete;
    private readonly string _languageId;
    private readonly string _name;
    private readonly LensProvider _lenses;
    private readonly Emitter<object?> _completed = new();
    private readonly PaneRenderer _incomingRenderer, _currentRenderer;
    private readonly ResultRenderer _resultRenderer;
    private readonly Dictionary<CodeEditorControl, (double Target, long Until)> _pendingScroll = new();
    private ThreeWayMerge? _merge;
    private MergeResolution? _resolution;
    private EditorDocument? _incomingDoc, _currentDoc;
    private CodeEditorControl? _incomingPane, _currentPane, _resultPane;
    private TextAnchor[] _starts = [], _ends = [];
    private int _focused = -1;
    private int? _resetVersion;
    private bool _syncing, _disposed;

    internal MergeEditorControl(EditorViewFactory factory, MergeEditorRequest request, EditorDocument result)
    {
        EditorStyles.Ensure();
        _factory = factory;
        _result = result;
        Request = request;
        _name = Path.GetFileName(request.Result.IsFile ? request.Result.LocalPath : request.Result.AbsolutePath);
        _languageId = result.LanguageId;
        _lenses = new LensProvider(this);
        _resultRenderer = new ResultRenderer(this);
        _incomingRenderer = new PaneRenderer(this, MergeSides.Incoming);
        _currentRenderer = new PaneRenderer(this, MergeSides.Current);
        Brushes = MergeBrushes.From(factory);

        var p = EditorPalette.From(factory.Themes);
        var title = new TextBlock { Text = request.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontWeight = FontWeight.SemiBold };
        _count = new TextBlock { Text = "Computing the merge…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Foreground = p.DescriptionForeground };
        _previous = ToolButton("↑", "Previous Conflict", () => GoToConflict(forward: false));
        _next = ToolButton("↓", "Next Conflict", () => GoToConflict(forward: true));
        _acceptIncoming = ToolButton("Accept Incoming", "Take the incoming side of the conflict at the caret", () => SetFocused(MergeSides.Incoming));
        _acceptCurrent = ToolButton("Accept Current", "Take the current side of the conflict at the caret", () => SetFocused(MergeSides.Current));
        _acceptBoth = ToolButton("Accept Both", "Take both sides of the conflict at the caret, current first", () => SetFocused(MergeSides.Both));
        _complete = ToolButton("Complete Merge", "Save the result and mark the file resolved", () => _ = CompleteAsync());
        _complete.Classes.Add("accent");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _count, _previous, _next, _acceptIncoming, _acceptCurrent, _acceptBoth, _complete } };
        var header = new DockPanel { Height = 28, Background = p.WidgetBackground };
        DockPanel.SetDock(tools, Avalonia.Controls.Dock.Right);
        header.Children.Add(tools);
        header.Children.Add(title);

        _body = new Grid();
        _body.Children.Add(new TextBlock { Text = "Computing the merge…", Margin = new Thickness(12), Foreground = p.DescriptionForeground });
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_body);
        Content = root;
        Background = p.Background;
        Avalonia.Automation.AutomationProperties.SetName(this, request.Title);
        UpdateHeader();

        factory.MergeEditors[_id] = this;
        _store.Add(Disposable.Create(() => factory.MergeEditors.TryRemove(_id, out _)));
        _store.Add(factory.Services.Features.Register<ICodeLensProvider>(new DocumentSelector([new DocumentFilter(Scheme: Scheme)]), _lenses, "cscode.mergeEditor"));
        _store.Add(Disposable.Create(_lenses.Dispose));
        _store.Add(factory.Themes.Changed.Subscribe(_ => factory.Services.Ui.Post(OnThemeChanged)));
        Ready = LoadAsync();
    }

    public MergeEditorRequest Request { get; }

    /// <summary>Completes when the merge has been computed and the panes built (tests).</summary>
    public Task Ready { get; private set; }

    /// <summary>Fires (UI thread) after Complete Merge saved the result and ran the request's completion: the host closes the tab.</summary>
    public IEventSource<object?> Completed => _completed;

    internal int Id => _id;
    internal ThreeWayMerge? Merge => _merge;
    internal MergeResolution? Resolution => _resolution;
    internal CodeEditorControl? IncomingPane => _incomingPane;
    internal CodeEditorControl? CurrentPane => _currentPane;
    internal CodeEditorControl? ResultPane => _resultPane;
    internal int FocusedConflict => _focused;
    internal string CountText => _count.Text ?? "";
    internal MergeBrushes Brushes { get; private set; }

    private static Button ToolButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1), Margin = new Thickness(1, 2), MinHeight = 0, FontSize = 12, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------------------------- computing

    /// <summary>What the worker computes from the request and one snapshot of the result: the merge and where its conflicts are in the result.</summary>
    private sealed record Prepared(ThreeWayMerge Merge, string Initial, bool Reset, int[] Starts, int[] Ends, (MergeSides Sides, bool IncomingFirst)?[] States);

    private async Task LoadAsync()
    {
        for (var attempt = 0; attempt < 5 && !_disposed; attempt++)
        {
            var snapshot = _result.Snapshot();
            var request = Request;
            Prepared prepared;
            try { prepared = await _factory.Worker.RunAsync(WorkPriority.Interactive, ct => Prepare(request, snapshot, ct)).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            if (_disposed) return;
            if (_result.Version != snapshot.Version) continue; // edited meanwhile: compute again
            Apply(prepared);
            return;
        }
    }

    private static Prepared Prepare(MergeEditorRequest request, DocumentSnapshot snapshot, CancellationToken ct)
    {
        var merge = ThreeWayMerge.Compute(request.Base, request.Current, request.Incoming, ct);
        var initial = new MergeResolution(merge).ResultText();
        var text = snapshot.GetText();
        // the file as git left it (markers): start from the base with the merged changes, as VS Code does; else keep what the user has
        var reset = MergeConflictParser.Parse(text, ct).Count > 0;
        var composed = ThreeWayMerge.SplitLinesKeepingEnds(initial);
        var lines = reset ? composed : ThreeWayMerge.SplitLinesKeepingEnds(text);
        // each conflict's lines in the composed result (unresolved conflicts hold their base lines)
        var spans = new (int Start, int End)[merge.Conflicts.Count];
        var at = 0;
        foreach (var r in merge.Regions)
        {
            var n = r.Kind switch
            {
                MergeRegionKind.Current or MergeRegionKind.Both => r.Current.Length,
                MergeRegionKind.Incoming => r.Incoming.Length,
                _ => r.Base.Length,
            };
            if (r.Kind == MergeRegionKind.Conflict) spans[r.ConflictIndex] = (at, at + n);
            at += n;
        }
        // composed line → result line (-1: changed in the result); identity after a reset
        var map = new int[composed.Length];
        if (reset) for (var k = 0; k < map.Length; k++) map[k] = k;
        else
        {
            Array.Fill(map, -1);
            var diff = LineDiff.Compute(composed, lines, new LineDiffOptions(MaxComputationTime: TimeSpan.FromSeconds(2)), ct);
            int o = 0, m = 0;
            foreach (var c in diff.Changes)
            {
                for (; o < c.OriginalStart; o++, m++) map[o] = m;
                o = c.OriginalEnd;
                m = c.ModifiedEnd;
            }
            for (; o < composed.Length; o++, m++) map[o] = m;
        }
        var lineStarts = new int[lines.Length + 1];
        for (var k = 0; k < lines.Length; k++) lineStarts[k + 1] = lineStarts[k] + lines[k].Length;
        var starts = new int[spans.Length];
        var ends = new int[spans.Length];
        var states = new (MergeSides, bool)?[spans.Length];
        var resolution = new MergeResolution(merge);
        var previousEnd = 0;
        for (var k = 0; k < spans.Length; k++)
        {
            // the conflict runs from after the last unchanged line before it to the first unchanged line after it
            var before = spans[k].Start - 1;
            while (before >= 0 && map[before] < 0) before--;
            var after = spans[k].End;
            while (after < map.Length && map[after] < 0) after++;
            var first = Math.Max(previousEnd, before >= 0 ? map[before] + 1 : 0);
            var last = Math.Max(first, after < map.Length ? map[after] : lines.Length);
            previousEnd = last;
            starts[k] = lineStarts[first];
            ends[k] = lineStarts[last];
            states[k] = reset ? (MergeSides.None, false) : resolution.Classify(k, string.Concat(lines[first..last]));
        }
        return new Prepared(merge, initial, reset, starts, ends, states);
    }

    private void Apply(Prepared p)
    {
        _merge = p.Merge;
        _resolution = new MergeResolution(p.Merge);
        for (var k = 0; k < p.States.Length; k++)
        {
            if (p.States[k] is { } s) _resolution.Set(k, s.Sides, s.IncomingFirst);
            else _resolution.MarkManual(k);
        }
        if (p.Reset)
        {
            _result.ApplyEdits([new TextEdit(new TextRange(TextPosition.Zero, _result.PositionAt(_result.Length)), p.Initial)]);
            _resetVersion = _result.Version;
        }
        var buffer = _result.Buffer;
        _starts = new TextAnchor[p.Starts.Length];
        _ends = new TextAnchor[p.Ends.Length];
        for (var k = 0; k < _starts.Length; k++)
        {
            // an insertion at the start goes into the conflict (the start stays before it); one at the end too (the end moves after it)
            _starts[k] = buffer.CreateAnchor(Math.Min(p.Starts[k], buffer.TextLength));
            _starts[k].MovementType = AnchorMovementType.BeforeInsertion;
            _starts[k].SurviveDeletion = true;
            _ends[k] = buffer.CreateAnchor(Math.Min(p.Ends[k], buffer.TextLength));
            _ends[k].MovementType = AnchorMovementType.AfterInsertion;
            _ends[k].SurviveDeletion = true;
        }
        BuildPanes();
        _store.Add(_result.Changed.Subscribe(OnResultChanged));
        OnStateChanged();
        if (_merge.Conflicts.Count > 0) GoTo(Enumerable.Range(0, _merge.Conflicts.Count).FirstOrDefault(k => !_resolution.IsHandled(k)));
    }

    // ------------------------------------------------------------------------------------- panes

    private EditorDocument Snapshot(string side, string text) =>
        _factory.CreateSnapshotDocument(new Uri($"{Scheme}:/{_id}/{side}/{Uri.EscapeDataString(_name)}"), text, _languageId);

    private static Border Label(string text, string? detail, EditorPalette p) => new()
    {
        Background = p.WidgetBackground,
        Padding = new Thickness(8, 3),
        Child = new TextBlock
        {
            Inlines = [new Avalonia.Controls.Documents.Run(text) { FontWeight = FontWeight.SemiBold }, new Avalonia.Controls.Documents.Run(detail is null ? "" : "  " + detail) { Foreground = p.DescriptionForeground }],
        },
    };

    private void BuildPanes()
    {
        var p = EditorPalette.From(_factory.Themes);
        _incomingDoc = Snapshot("incoming", Request.Incoming);
        _currentDoc = Snapshot("current", Request.Current);
        _incomingPane = new CodeEditorControl(_factory, _incomingDoc) { AllowMinimap = false };
        _currentPane = new CodeEditorControl(_factory, _currentDoc) { AllowMinimap = false };
        _resultPane = new CodeEditorControl(_factory, _result) { AllowMinimap = false };
        _body.Children.Clear();
        _body.RowDefinitions = new RowDefinitions("Auto,*,4,Auto,*");
        _body.ColumnDefinitions = new ColumnDefinitions("*,4,*");
        void Add(Control c, int row, int column, int span = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, span);
            _body.Children.Add(c);
        }
        Add(Label("Incoming", Request.IncomingLabel, p), 0, 0);
        Add(Label("Current", Request.CurrentLabel, p), 0, 2);
        Add(_incomingPane, 1, 0);
        Add(new GridSplitter { Width = 4, Background = p.WidgetBorder, ResizeDirection = GridResizeDirection.Columns }, 1, 1);
        Add(_currentPane, 1, 2);
        Add(new GridSplitter { Height = 4, Background = p.WidgetBorder, ResizeDirection = GridResizeDirection.Rows }, 2, 0, 3);
        Add(Label("Result", _name, p), 3, 0, 3);
        Add(_resultPane, 4, 0, 3);
        Attach(_incomingPane, _incomingRenderer);
        Attach(_currentPane, _currentRenderer);
        Attach(_resultPane, _resultRenderer);
        foreach (var pane in new[] { _incomingPane, _currentPane, _resultPane })
        {
            var v = pane;
            void OnScrolled(object? s, EventArgs e) => SyncFrom(v);
            v.TextView.ScrollOffsetChanged += OnScrolled;
            _paneStore.Add(Disposable.Create(() => v.TextView.ScrollOffsetChanged -= OnScrolled));
        }
        _paneStore.Add(_resultPane.Controller.CaretChanged.Subscribe(_ => OnResultCaretMoved()));
    }

    private void Attach(CodeEditorControl view, IBackgroundRenderer renderer)
    {
        view.TextView.BackgroundRenderers.Add(renderer);
        _paneStore.Add(Disposable.Create(() => view.TextView.BackgroundRenderers.Remove(renderer)));
    }

    private void OnThemeChanged()
    {
        Brushes = MergeBrushes.From(_factory);
        foreach (var v in new[] { _incomingPane, _currentPane, _resultPane }) v?.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // ------------------------------------------------------------------------------------- state

    /// <summary>The result lines [start, end) of conflict <paramref name="k"/> now.</summary>
    internal (int Start, int End) ResultLines(int k)
    {
        var doc = _result.Buffer;
        var s = _starts[k].IsDeleted ? 0 : _starts[k].Offset;
        var e = Math.Max(s, _ends[k].IsDeleted ? s : _ends[k].Offset);
        var start = doc.GetLineByOffset(s).LineNumber - 1;
        var endLine = doc.GetLineByOffset(e);
        var end = e == endLine.Offset ? endLine.LineNumber - 1 : endLine.LineNumber; // a conflict ends at the start of the line after it
        return (start, Math.Max(start, end));
    }

    /// <summary>Conflict <paramref name="k"/>'s text in the result now.</summary>
    internal string ResultTextOf(int k)
    {
        var s = _starts[k].Offset;
        return _result.Buffer.GetText(s, Math.Max(0, _ends[k].Offset - s));
    }

    /// <summary>Takes <paramref name="sides"/> for conflict <paramref name="k"/>: its text in the result is replaced in ONE undo step.</summary>
    internal void Set(int k, MergeSides sides, bool incomingFirst = false)
    {
        if (_resolution is null || k < 0 || k >= _starts.Length || _result.IsReadOnly) return;
        _resolution.Set(k, sides, incomingFirst);
        Replace(k);
    }

    /// <summary>An input pane's checkbox: takes or drops one side (taking both puts them in the order they were taken).</summary>
    internal void Toggle(int k, MergeSides side)
    {
        if (_resolution is null || k < 0 || k >= _starts.Length || _result.IsReadOnly) return;
        _resolution.Toggle(k, side);
        Replace(k);
        GoTo(k, reveal: false);
    }

    private void SetFocused(MergeSides sides)
    {
        if (_focused >= 0) Set(_focused, sides);
    }

    private void Replace(int k)
    {
        var start = _starts[k].Offset;
        var end = Math.Max(start, _ends[k].Offset);
        var text = _resolution!.TextOf(k);
        if (_result.Buffer.GetText(start, end - start) != text)
            _result.ApplyEdits([new TextEdit(new TextRange(_result.PositionAt(start), _result.PositionAt(end)), text)]);
        OnStateChanged();
    }

    /// <summary>An edit of the result (typing, undo, a toggle): the conflicts it touched are read again from their text.</summary>
    private void OnResultChanged(TextDocumentChangedEventArgs e)
    {
        if (_resolution is null) return;
        var changed = false;
        for (var k = 0; k < _starts.Length; k++)
        {
            var s = _starts[k].Offset;
            var end = _ends[k].Offset;
            var touched = e.Changes.Any(c => c.RangeOffset <= end && c.RangeOffset + c.Text.Length >= s);
            if (!touched) continue;
            var state = _resolution.Classify(k, ResultTextOf(k));
            var was = (_resolution.Taken(k), _resolution.IncomingFirst(k), _resolution.IsHandled(k));
            if (state is { } st) _resolution.Set(k, st.Sides, st.IncomingFirst);
            else _resolution.MarkManual(k);
            changed |= was != (_resolution.Taken(k), _resolution.IncomingFirst(k), _resolution.IsHandled(k));
        }
        if (changed) OnStateChanged();
        else _resultPane?.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void OnStateChanged()
    {
        if (_merge is null || _resolution is null) return;
        _incomingRenderer.Update(_merge, _resolution);
        _currentRenderer.Update(_merge, _resolution);
        foreach (var v in new[] { _incomingPane, _currentPane, _resultPane }) v?.TextView.InvalidateLayer(KnownLayer.Background);
        _lenses.Rebuild();
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (_merge is null || _resolution is null)
        {
            foreach (var b in new[] { _previous, _next, _acceptIncoming, _acceptCurrent, _acceptBoth, _complete }) b.IsEnabled = false;
            return;
        }
        var left = _resolution.UnhandledCount;
        _count.Text = _merge.Conflicts.Count == 0 ? "No conflicts" : left switch { 0 => "No Remaining Conflicts", 1 => "1 Conflict Remaining", _ => $"{left} Conflicts Remaining" };
        _previous.IsEnabled = _next.IsEnabled = _merge.Conflicts.Count > 0;
        _acceptIncoming.IsEnabled = _acceptCurrent.IsEnabled = _acceptBoth.IsEnabled = _focused >= 0 && !_result.IsReadOnly;
        _complete.IsEnabled = !_result.IsReadOnly;
    }

    private void OnResultCaretMoved()
    {
        if (_resultPane is null || _merge is null) return;
        var line = _resultPane.Controller.Caret.Line;
        var found = -1;
        for (var k = 0; k < _starts.Length; k++)
        {
            var (s, e) = ResultLines(k);
            if (line >= s && line < Math.Max(e, s + 1)) { found = k; break; }
        }
        if (found >= 0 && found != _focused) { _focused = found; UpdateHeader(); }
    }

    // ------------------------------------------------------------------------------------- navigation

    /// <summary>Moves to the next (previous) conflict after (before) the Result's caret, wrapping; -1 when there is none.</summary>
    public int GoToConflict(bool forward)
    {
        if (_merge is null || _resultPane is null || _starts.Length == 0) return -1;
        var line = _resultPane.Controller.Caret.Line;
        var n = _starts.Length;
        int target;
        if (forward) target = Enumerable.Range(0, n).FirstOrDefault(k => ResultLines(k).Start > line, -1) is var f and >= 0 ? f : 0;
        else target = Enumerable.Range(0, n).LastOrDefault(k => ResultLines(k).Start < line, -1) is var b and >= 0 ? b : n - 1;
        GoTo(target);
        return target;
    }

    private void GoTo(int k, bool reveal = true)
    {
        if (_merge is null || _resultPane is null || k < 0 || k >= _starts.Length) return;
        _focused = k;
        UpdateHeader();
        if (!reveal) return;
        var region = _merge.Conflicts[k];
        var (start, _) = ResultLines(k);
        _syncing = true;
        try
        {
            _resultPane.Controller.SetSelection(TextRange.At(start, 0), reveal: false);
            _resultPane.Controller.Reveal(TextRange.At(start, 0), center: true);
            Reveal(_incomingPane, region.Incoming.Start);
            Reveal(_currentPane, region.Current.Start);
        }
        finally { _syncing = false; }
    }

    private static void Reveal(CodeEditorControl? pane, int line)
    {
        if (pane is null) return;
        line = Math.Clamp(line, 0, Math.Max(0, pane.Document.LineCount - 1));
        pane.Controller.SetSelection(TextRange.At(line, 0), reveal: false);
        pane.Controller.Reveal(TextRange.At(line, 0), center: true);
    }

    // ------------------------------------------------------------------------------------- scrolling together

    /// <summary>
    /// (incoming, current, result) line triples the panes line up on: the starts and ends of the conflicts (from the anchors, so they follow
    /// edits of the result), plus the texts' starts and ends; lines in between are interpolated.
    /// </summary>
    internal List<(double Incoming, double Current, double Result)> SyncPoints()
    {
        var points = new List<(double, double, double)> { (0, 0, 0) };
        if (_merge is null) return points;
        for (var k = 0; k < _starts.Length; k++)
        {
            var r = _merge.Conflicts[k];
            var (s, e) = ResultLines(k);
            points.Add((r.Incoming.Start, r.Current.Start, s));
            points.Add((r.Incoming.End, r.Current.End, e));
        }
        points.Add((_merge.IncomingLines.Count, _merge.CurrentLines.Count, Math.Max(0, _result.LineCount - 1)));
        return points;
    }

    /// <summary>Maps line <paramref name="line"/> of pane <paramref name="from"/> (0 incoming, 1 current, 2 result) to pane <paramref name="to"/>.</summary>
    internal double MapLine(double line, int from, int to)
    {
        static double C(in (double I, double C, double R) p, int which) => which switch { 0 => p.I, 1 => p.C, _ => p.R };
        var points = SyncPoints();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var a0 = C(a, from);
            var b0 = C(b, from);
            if (line > b0 && i + 2 < points.Count) continue;
            if (b0 <= a0) return C(a, to);
            var t = Math.Clamp((line - a0) / (b0 - a0), 0, 1);
            return C(a, to) + t * (C(b, to) - C(a, to));
        }
        return C(points[^1], to);
    }

    private void SyncFrom(CodeEditorControl from)
    {
        if (_syncing || _merge is null || from.IsDisposed || from.TextView.Document is null) return;
        if (_pendingScroll.TryGetValue(from, out var pending))
        {
            var arrived = Math.Abs(from.TextView.ScrollOffset.Y - pending.Target) < 1;
            if (arrived || Environment.TickCount64 > pending.Until) _pendingScroll.Remove(from);
            if (arrived || Environment.TickCount64 <= pending.Until) return;
        }
        var index = ReferenceEquals(from, _incomingPane) ? 0 : ReferenceEquals(from, _currentPane) ? 1 : 2;
        var tv = from.TextView;
        var top = TopLine(tv);
        _syncing = true;
        try
        {
            for (var other = 0; other < 3; other++)
            {
                if (other == index) continue;
                var to = other switch { 0 => _incomingPane, 1 => _currentPane, _ => _resultPane };
                if (to is null || to.IsDisposed || to.TextView.Document is null) continue;
                var mapped = MapLine(top, index, other);
                var target = Math.Clamp((int)Math.Floor(mapped), 0, Math.Max(0, to.Document.LineCount - 1));
                var y = to.TextView.GetVisualTopByDocumentLine(target + 1) + (mapped - Math.Floor(mapped)) * Math.Max(1, to.TextView.DefaultLineHeight);
                y = Math.Clamp(y, 0, Math.Max(0, to.Editor.ExtentHeight - to.Editor.ViewportHeight));
                _pendingScroll[to] = (y, Environment.TickCount64 + 500);
                ((Avalonia.Controls.Primitives.IScrollable)to.TextArea).Offset = new Vector(tv.ScrollOffset.X, y);
            }
        }
        finally { _syncing = false; }
    }

    private static double TopLine(TextView tv)
    {
        if (tv.Document is null || tv.Document.LineCount == 0) return 0;
        var y = tv.ScrollOffset.Y;
        var line = tv.GetDocumentLineByVisualTop(y);
        var lineTop = tv.GetVisualTopByDocumentLine(line.LineNumber);
        return line.LineNumber - 1 + Math.Clamp((y - lineTop) / Math.Max(1, tv.DefaultLineHeight), 0, 0.999);
    }

    // ------------------------------------------------------------------------------------- completing

    /// <summary>
    /// Complete Merge: with conflicts left unhandled it asks first; then the result is saved and the request's completion runs (git: the file is
    /// staged, which marks it resolved). True when it completed.
    /// </summary>
    public async Task<bool> CompleteAsync()
    {
        if (_resolution is null || _disposed) return false;
        var window = _factory.Services.Window;
        if (_resolution.UnhandledCount > 0 && window is not null)
        {
            var answer = await window.ShowMessageAsync(MessageSeverity.Warning, $"Do you want to complete the merge of {_name}? The file contains unhandled conflicts.",
                [new MessageAction("Complete with Conflicts"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Complete with Conflicts") return false;
        }
        try
        {
            await _result.SaveAsync().ConfigureAwait(true);
            if (Request.Complete is { } complete) await complete(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (window is not null) _ = window.ShowMessageAsync(MessageSeverity.Error, $"Could not complete the merge of {_name}: {ex.Message}").AsTask();
            return false;
        }
        _resetVersion = null; // completed: closing keeps the result
        _completed.Fire(null);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // opened and closed without touching it: the file gets its conflict markers back (the reset is undone, the document is clean again)
        if (_resetVersion is { } v && _result.Version == v && _result.CanUndo && !_result.IsReadOnly) _result.Undo();
        _store.Dispose();
        for (var i = _paneStore.Count - 1; i >= 0; i--) _paneStore[i].Dispose();
        _paneStore.Clear();
        foreach (var v2 in new[] { _incomingPane, _currentPane, _resultPane }) v2?.Dispose();
        foreach (var d in new[] { _incomingDoc, _currentDoc }) if (d is not null) _factory.ReleaseSnapshotDocument(d);
        _incomingPane = _currentPane = _resultPane = null;
        _incomingDoc = _currentDoc = null;
        _starts = _ends = [];
        _completed.Clear();
        Content = null;
    }

    // ------------------------------------------------------------------------------------- lenses

    /// <summary>
    /// The checkbox above each conflict in the input panes ("☐ Accept Incoming" / "☑ Accept Incoming"): code lenses, so the editor's own code
    /// lens draws them and runs <see cref="ToggleCommand"/> on a click. Built on the UI thread whenever the state changes; the provider only
    /// hands them out.
    /// </summary>
    private sealed class LensProvider(MergeEditorControl owner) : ICodeLensProvider, IDisposable
    {
        private readonly Emitter<Uri?> _changed = new();
        private IReadOnlyList<CodeLens> _incoming = [], _current = [];

        public IEventSource<Uri?> CodeLensesChanged => _changed;

        public void Rebuild()
        {
            if (owner._merge is not { } merge || owner._resolution is not { } resolution) return;
            _incoming = Build(merge, resolution, MergeSides.Incoming, owner._incomingDoc?.LineCount ?? 1);
            _current = Build(merge, resolution, MergeSides.Current, owner._currentDoc?.LineCount ?? 1);
            _changed.Fire(null);
        }

        private IReadOnlyList<CodeLens> Build(ThreeWayMerge merge, MergeResolution resolution, MergeSides side, int lineCount)
        {
            var list = new List<CodeLens>(merge.Conflicts.Count);
            var id = System.Text.Json.JsonSerializer.SerializeToElement(owner._id);
            var which = System.Text.Json.JsonSerializer.SerializeToElement(side == MergeSides.Incoming ? "incoming" : "current");
            for (var k = 0; k < merge.Conflicts.Count; k++)
            {
                var span = side == MergeSides.Incoming ? merge.Conflicts[k].Incoming : merge.Conflicts[k].Current;
                var line = Math.Clamp(span.Start, 0, Math.Max(0, lineCount - 1));
                var taken = (resolution.Taken(k) & side) != 0;
                var title = (taken ? "☑ " : "☐ ") + (side == MergeSides.Incoming ? "Accept Incoming" : "Accept Current");
                list.Add(new CodeLens(TextRange.At(line, 0), new Command(title, ToggleCommand, [id, System.Text.Json.JsonSerializer.SerializeToElement(k), which])));
            }
            return list;
        }

        public ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<CodeLens>?>(ReferenceEquals(document, owner._incomingDoc) ? _incoming : ReferenceEquals(document, owner._currentDoc) ? _current : null);

        public void Dispose() => _changed.Clear();
    }

    // ------------------------------------------------------------------------------------- drawing

    internal sealed class MergeBrushes
    {
        public required IBrush Change, CurrentConflict, IncomingConflict, CurrentTaken, IncomingTaken, Unhandled, HandledByHand;
        public required IPen Gap, UnhandledBorder;

        public static MergeBrushes From(EditorViewFactory factory)
        {
            var themes = factory.Themes;
            IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
            IPen P(string key) => new ImmutablePen(new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key))), 1);
            return new MergeBrushes
            {
                Change = B("diffEditor.insertedLineBackground"),
                CurrentConflict = B("merge.currentContentBackground"),
                IncomingConflict = B("merge.incomingContentBackground"),
                CurrentTaken = B("merge.currentHeaderBackground"),
                IncomingTaken = B("merge.incomingHeaderBackground"),
                Unhandled = B("merge.commonContentBackground"),
                HandledByHand = B("diffEditor.insertedLineBackground"),
                Gap = P("editorGutter.addedBackground"),
                UnhandledBorder = P("editorWarning.foreground"),
            };
        }
    }

    private enum LineKind : byte { None, Change, Conflict, Taken }

    /// <summary>An input pane: lines this side changed against the base, conflicts (stronger once taken), and a thin line where a change removed lines.</summary>
    private sealed class PaneRenderer(MergeEditorControl owner, MergeSides side) : IBackgroundRenderer
    {
        private LineKind[] _kinds = [];
        private HashSet<int> _gaps = [];

        public KnownLayer Layer => KnownLayer.Background;

        public void Update(ThreeWayMerge merge, MergeResolution resolution)
        {
            var lines = side == MergeSides.Incoming ? merge.IncomingLines.Count : merge.CurrentLines.Count;
            var kinds = new LineKind[lines];
            var gaps = new HashSet<int>();
            foreach (var r in merge.Regions)
            {
                var span = side == MergeSides.Incoming ? r.Incoming : r.Current;
                var kind = r.Kind switch
                {
                    MergeRegionKind.Conflict => (resolution.Taken(r.ConflictIndex) & side) != 0 ? LineKind.Taken : LineKind.Conflict,
                    MergeRegionKind.Both => LineKind.Change,
                    MergeRegionKind.Current when side == MergeSides.Current => LineKind.Change,
                    MergeRegionKind.Incoming when side == MergeSides.Incoming => LineKind.Change,
                    _ => LineKind.None,
                };
                if (kind == LineKind.None) continue;
                if (span.Length == 0) gaps.Add(span.Start);
                for (var k = span.Start; k < span.End; k++) kinds[k] = kind;
            }
            _kinds = kinds;
            _gaps = gaps;
        }

        public void Draw(TextView textView, DrawingContext context)
        {
            if (textView.VisualLines.Count == 0 || !textView.VisualLinesValid) return;
            var brushes = owner.Brushes;
            var width = textView.Bounds.Width + textView.ScrollOffset.X;
            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine.LineNumber - 1;
                var zone = LineZones.HeightOf(vl);
                var top = vl.VisualTop - textView.ScrollOffset.Y + zone;
                var kind = line < _kinds.Length ? _kinds[line] : LineKind.None;
                IBrush? brush = kind switch
                {
                    LineKind.Change => brushes.Change,
                    LineKind.Conflict => side == MergeSides.Incoming ? brushes.IncomingConflict : brushes.CurrentConflict,
                    LineKind.Taken => side == MergeSides.Incoming ? brushes.IncomingTaken : brushes.CurrentTaken,
                    _ => null,
                };
                if (brush is not null) context.FillRectangle(brush, new Rect(0, top, width, Math.Max(0, vl.Height - zone)));
                if (_gaps.Contains(line)) context.DrawLine(brushes.Gap, new Point(0, top + 0.5), new Point(width, top + 0.5));
            }
        }
    }

    /// <summary>The result pane: each conflict's lines, by state (unhandled: tinted and outlined; a side taken: that side's colour; edited by hand: a change).</summary>
    private sealed class ResultRenderer(MergeEditorControl owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (owner._resolution is not { } resolution || textView.VisualLines.Count == 0 || !textView.VisualLinesValid || textView.Document is null) return;
            var b = owner.Brushes;
            var width = textView.Bounds.Width + textView.ScrollOffset.X;
            var first = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            var last = textView.VisualLines[^1].LastDocumentLine.LineNumber - 1;
            for (var k = 0; k < owner._starts.Length; k++)
            {
                var (s, e) = owner.ResultLines(k);
                if (e < first || s > last + 1) continue;
                var taken = resolution.Taken(k);
                var brush = !resolution.IsHandled(k) ? b.Unhandled
                    : taken == MergeSides.Current ? b.CurrentConflict
                    : taken == MergeSides.Incoming ? b.IncomingConflict
                    : b.HandledByHand;
                double? top = null, bottom = null;
                foreach (var vl in textView.VisualLines)
                {
                    var line = vl.FirstDocumentLine.LineNumber - 1;
                    var zone = LineZones.HeightOf(vl);
                    var y = vl.VisualTop - textView.ScrollOffset.Y + zone;
                    if (line == s) top ??= y;
                    if (line < s || line >= e) continue;
                    context.FillRectangle(brush, new Rect(0, y, width, Math.Max(0, vl.Height - zone)));
                    bottom = y + vl.Height - zone;
                }
                if (resolution.IsHandled(k) || top is not { } t) continue;
                // unhandled: outlined (an empty one is a line where its text would go)
                if (bottom is { } bt) context.DrawRectangle(b.UnhandledBorder, new Rect(0.5, t + 0.5, Math.Max(0, width - 1), Math.Max(0, bt - t - 1)));
                else context.DrawLine(b.UnhandledBorder, new Point(0, t + 0.5), new Point(width, t + 0.5));
            }
        }
    }
}
