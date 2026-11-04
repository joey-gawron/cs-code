using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The test glyphs (Test Explorer, <see cref="ITestGutterProvider"/>): a narrow margin right of the breakpoint margin with, on each line that
/// declares a test the explorer knows, its last result — a green run triangle (not run), a green check (passed), a red cross (failed), a grey
/// slash (skipped), a yellow ring (queued / running). A click runs the test (<c>testing.defaultGutterClickAction</c>: <c>run</c>, <c>debug</c>
/// or <c>contextMenu</c>); right-click offers Run Test, Debug Test and Reveal in Test Explorer; the tooltip says the result and its message.
/// The margin takes no width while the document has no test. Asked 300 ms after the text changes and whenever the provider says results
/// changed, off the UI thread, latest-wins; each glyph is anchored to its line so it follows edits until the next answer. Setting
/// <c>testing.gutterEnabled</c> = false removes them.
/// </summary>
internal sealed class TestGutterFeature : IThemeAware, ISettingsAware, IDisposable
{
    internal const double MarginWidth = 16;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    internal sealed record Placed(TextAnchor Anchor, TestGutterItem Item);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly TestMargin _margin;
    private readonly Dictionary<ITestGutterProvider, IDisposable> _providerSubs = [];
    private CancellationTokenSource? _debounce, _request;
    private Placed[] _items = [];
    private bool _disposed;

    public TestGutterFeature(CodeEditorControl view)
    {
        _v = view;
        _margin = new TestMargin(this);
        view.TextArea.LeftMargins.Insert(0, _margin);
        _d.Add(Disposable.Create(() => view.TextArea.LeftMargins.Remove(_margin)));
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule()));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(_ => _v.Post(Schedule)));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t => { if (t == typeof(ITestGutterProvider)) _v.Post(Schedule); }));
        _d.Add(view.Services.Config.Changed.Subscribe(e => { if (e.Affects("testing")) _v.Post(Schedule); }));
        _d.Add(Disposable.Create(() => { foreach (var s in _providerSubs.Values) s.Dispose(); _providerSubs.Clear(); }));
        Schedule();
    }

    /// <summary>The glyphs placed now: (0-based line, item), in line order.</summary>
    internal IReadOnlyList<(int Line, TestGutterItem Item)> Placements =>
        _items.Where(p => !p.Anchor.IsDeleted).Select(p => (p.Anchor.Line - 1, p.Item)).OrderBy(x => x.Item1).ToList();

    internal Control Margin => _margin;

    private bool Enabled => _v.Services.Config.Get<bool?>("testing.gutterEnabled") ?? true; // registered by the Test Explorer module, which may be absent

    public void OnThemeChanged() => _margin.InvalidateVisual();

    /// <summary>Keep the margin right of the breakpoint margin when the editor re-inserts its margins (line numbers toggled).</summary>
    public void OnSettingsChanged()
    {
        var margins = _v.TextArea.LeftMargins;
        var want = margins.Count > 0 && !ReferenceEquals(margins[0], _margin) && Avalonia.Automation.AutomationProperties.GetName(margins[0]) == "Breakpoint margin" ? 1 : 0;
        if (margins.IndexOf(_margin) is var i && i != want && i >= 0)
        {
            margins.RemoveAt(i);
            margins.Insert(Math.Min(want, margins.Count), _margin);
        }
    }

    private void Schedule()
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(Delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() => { if (!cts.IsCancellationRequested) _ = RefreshAsync(); });
        }, TaskScheduler.Default);
    }

    /// <summary>Asks the providers now (tests call this instead of waiting for the debounce).</summary>
    internal async Task RefreshAsync()
    {
        if (_disposed) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<ITestGutterProvider>(doc);
        SyncSubscriptions(providers);
        if (providers.Count == 0 || !Enabled || !doc.Uri.IsFile)
        {
            Set([]);
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var all = new List<TestGutterItem>();
        foreach (var call in providers.Select(p => FeatureSupport.CallAsync(_v, c => p.ProvideTestsAsync(doc, c), cts.Token, TimeSpan.FromSeconds(3))).ToList())
            if (await call.ConfigureAwait(true) is { } list) all.AddRange(list);
        if (_disposed || cts.IsCancellationRequested || doc.Version != version) return; // the text moved on: a newer request is scheduled
        var placed = new List<Placed>();
        foreach (var item in all.GroupBy(i => i.Line).Select(g => g.First()))
        {
            if (item.Line < 0 || item.Line >= doc.LineCount) continue;
            var anchor = doc.Buffer.CreateAnchor(doc.Buffer.GetLineByNumber(item.Line + 1).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            placed.Add(new Placed(anchor, item));
        }
        Set([.. placed]);
    }

    private void SyncSubscriptions(IReadOnlyList<ITestGutterProvider> providers)
    {
        foreach (var gone in _providerSubs.Keys.Where(p => !providers.Contains(p)).ToList())
        {
            _providerSubs[gone].Dispose();
            _providerSubs.Remove(gone);
        }
        foreach (var p in providers)
            if (!_providerSubs.ContainsKey(p))
                _providerSubs[p] = p.StatesChanged.Subscribe(uri => { if (uri is null || FeatureSupport.SameFile(uri, _v.Document.Uri)) _v.Post(Schedule); });
    }

    private void Set(Placed[] items)
    {
        var hadAny = _items.Length > 0;
        _items = items;
        if (hadAny != items.Length > 0) _margin.InvalidateMeasure();
        _margin.InvalidateVisual();
    }

    internal TestGutterItem? ItemAt(int line)
    {
        foreach (var p in _items)
            if (!p.Anchor.IsDeleted && p.Anchor.Line - 1 == line) return p.Item;
        return null;
    }

    private ITestGutterProvider? Provider => _v.Services.Features.GetProviders<ITestGutterProvider>(_v.Document).FirstOrDefault();

    // ---- gestures (also called by tests) ----

    internal void Click(int line, Control anchor)
    {
        if (ItemAt(line) is not { } item || Provider is not { } provider) return;
        switch ((_v.Services.Config.Get<string>("testing.defaultGutterClickAction") ?? "run"))
        {
            case "debug": _ = provider.RunAsync(item.TestId, debug: true).AsTask(); break;
            case "contextMenu": BuildContextMenu(line)?.Open(anchor); break;
            default: _ = provider.RunAsync(item.TestId, debug: false).AsTask(); break;
        }
    }

    internal ContextMenu? BuildContextMenu(int line)
    {
        if (ItemAt(line) is not { } item || Provider is not { } provider) return null;
        var menu = new ContextMenu();
        MenuItem Item(string header, Func<ValueTask> run)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => _ = run().AsTask();
            return m;
        }
        menu.Items.Add(Item("Run Test", () => provider.RunAsync(item.TestId, debug: false)));
        menu.Items.Add(Item("Debug Test", () => provider.RunAsync(item.TestId, debug: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reveal in Test Explorer", () => provider.RevealAsync(item.TestId)));
        return menu;
    }

    internal string? Describe(int line)
    {
        if (ItemAt(line) is not { } item) return null;
        var state = item.State switch
        {
            TestGutterState.Passed => "passed",
            TestGutterState.Failed => "failed",
            TestGutterState.Skipped => "skipped",
            TestGutterState.Running => "running",
            TestGutterState.Queued => "queued",
            _ => "not run",
        };
        var click = (_v.Services.Config.Get<string>("testing.defaultGutterClickAction") ?? "run") switch { "debug" => "Click to debug", "contextMenu" => "Click for test actions", _ => "Click to run" };
        return $"{item.Label}: {state}" + (item.Message is { Length: > 0 } m ? "\n" + (m.Length > 400 ? m[..400] + "…" : m) : "") + $"\n{click}, right-click for more";
    }

    // ---- the margin ----

    private sealed class TestMargin : AbstractMargin
    {
        private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#73C991"));
        private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F14C4C"));
        private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#848484"));
        private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#CCA700"));
        private readonly TestGutterFeature _owner;
        private int _hoverLine = -1;

        public TestMargin(TestGutterFeature owner)
        {
            _owner = owner;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetShowDelay(this, 300);
            Avalonia.Automation.AutomationProperties.SetName(this, "Test margin");
        }

        protected override Size MeasureOverride(Size availableSize) => new(_owner._items.Length > 0 ? MarginWidth : 0, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            var textView = TextView;
            if (textView is null || !textView.VisualLinesValid || _owner._items.Length == 0) return;
            context.FillRectangle(_owner._v.Palette.Gutter, new Rect(0, 0, Bounds.Width, Bounds.Height));
            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine.LineNumber - 1;
                if (_owner.ItemAt(line) is not { } item) continue;
                // level with the line number: the text of the first row, below a code lens, whatever stretches the row
                var c = new Point(MarginWidth / 2, vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextMiddle) - textView.VerticalOffset);
                var r = Math.Min(MarginWidth, textView.DefaultLineHeight) / 2 - 2.5;
                Draw(context, item.State, c, r, line == _hoverLine);
            }
        }

        internal static void Draw(DrawingContext g, TestGutterState state, Point c, double r, bool hover)
        {
            switch (state)
            {
                case TestGutterState.Passed:
                    g.DrawEllipse(Green, null, c, r, r);
                    g.DrawGeometry(null, new Pen(Brushes.White, 1.4), Polyline(new Point(c.X - r * 0.5, c.Y), new Point(c.X - r * 0.12, c.Y + r * 0.4), new Point(c.X + r * 0.5, c.Y - r * 0.4)));
                    break;
                case TestGutterState.Failed:
                    g.DrawEllipse(Red, null, c, r, r);
                    var k = r * 0.42;
                    g.DrawLine(new Pen(Brushes.White, 1.4), new Point(c.X - k, c.Y - k), new Point(c.X + k, c.Y + k));
                    g.DrawLine(new Pen(Brushes.White, 1.4), new Point(c.X + k, c.Y - k), new Point(c.X - k, c.Y + k));
                    break;
                case TestGutterState.Skipped:
                    g.DrawEllipse(null, new Pen(Grey, 1.3), c, r - 0.5, r - 0.5);
                    g.DrawLine(new Pen(Grey, 1.3), new Point(c.X - r * 0.6, c.Y + r * 0.6), new Point(c.X + r * 0.6, c.Y - r * 0.6));
                    break;
                case TestGutterState.Running or TestGutterState.Queued:
                    g.DrawEllipse(null, new Pen(Yellow, 1.6, dashStyle: state == TestGutterState.Running ? new DashStyle([2, 1.5], 0) : null), c, r - 0.5, r - 0.5);
                    break;
                default:
                    // Visual Studio / VS Code's run glyph: a green triangle (filled while hovered)
                    var tri = new StreamGeometry();
                    using (var ctx = tri.Open())
                    {
                        ctx.BeginFigure(new Point(c.X - r * 0.6, c.Y - r), true);
                        ctx.LineTo(new Point(c.X + r * 0.9, c.Y));
                        ctx.LineTo(new Point(c.X - r * 0.6, c.Y + r));
                        ctx.EndFigure(true);
                    }
                    g.DrawGeometry(hover ? Green : null, new Pen(Green, 1.3), tri);
                    break;
            }
        }

        private static StreamGeometry Polyline(params Point[] points)
        {
            var geo = new StreamGeometry();
            using var ctx = geo.Open();
            ctx.BeginFigure(points[0], false);
            foreach (var p in points.Skip(1)) ctx.LineTo(p);
            ctx.EndFigure(false);
            return geo;
        }

        private int LineAt(PointerEventArgs e)
        {
            var textView = TextView;
            if (textView is null) return -1;
            var vl = textView.GetVisualLineFromVisualTop(e.GetPosition(this).Y + textView.VerticalOffset);
            return vl is null ? -1 : vl.FirstDocumentLine.LineNumber - 1;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var line = LineAt(e);
            if (line == _hoverLine) return;
            _hoverLine = line;
            ToolTip.SetTip(this, line >= 0 ? _owner.Describe(line) : null);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverLine = -1;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var line = LineAt(e);
            if (line < 0 || _owner.ItemAt(line) is null) return;
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed) _owner.Click(line, this);
            else if (point.Properties.IsRightButtonPressed) _owner.BuildContextMenu(line)?.Open(this);
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _request?.Cancel();
        _request?.Dispose();
        _d.Dispose();
        _items = [];
    }
}
