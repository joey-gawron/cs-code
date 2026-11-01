using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>
/// The Markdown preview of one source document: a themed, virtualised rendering (<see cref="MarkdownBlockPanel"/>) that follows the document
/// while it is edited. A change waits <see cref="MarkdownPreviewSettings.UpdateDelay"/> ms (latest wins), the parse and the code colouring run
/// on the editor worker from a snapshot, and the UI thread only swaps the blocks that changed. When the document is not open in an editor (its
/// last tab closed), the file is read through <see cref="IFileSystem"/> instead and the preview picks the document up again when it reopens.
/// <para>
/// Scrolling: <see cref="ScrollToLine"/> shows a source line at the top (the editor drives it), and <see cref="Scrolled"/> reports the source line
/// at the top when the user scrolls the preview (not when <see cref="ScrollToLine"/> did): the shell couples the two. Links and double-clicks are
/// reported (<see cref="LinkClicked"/>, <see cref="DoubleClickedLine"/>); anchors inside the document are followed here.
/// </para>
/// Disposing it drops every control, subscription and cached image. UI thread.
/// </summary>
public sealed class MarkdownPreviewView : UserControl, IDisposable
{
    private readonly MarkdownPreviewFactory _owner;
    private readonly DisposableStore _subs = new();
    private IDisposable? _docSub;
    private readonly ScrollViewer _scroller;
    private readonly MarkdownBlockPanel _panel;
    private readonly Border _content;
    private readonly TextBlock _notice;
    private readonly MarkdownImageLoader _images;
    private readonly Emitter<PreviewDocument> _rendered = new();
    private readonly Emitter<double> _scrolled = new();
    private readonly Emitter<string> _linkClicked = new();
    private readonly Emitter<double> _doubleClicked = new();
    private readonly DispatcherTimer _debounce;
    private ITextDocument? _document;
    private PreviewDocument _current = PreviewDocument.Empty;
    private MarkdownPreviewStyle _style;
    private MarkdownRenderContext _context;
    private MarkdownPreviewOptions _options;
    private CancellationTokenSource? _parseCts;
    private double? _pendingLine;
    private double _expectedOffset = double.NaN;
    private int _requested;
    private bool _colorsStale;
    private bool _disposed;

    internal MarkdownPreviewView(MarkdownPreviewFactory owner, Uri source)
    {
        _owner = owner;
        Source = source;
        _style = ReadStyle();
        _options = ReadOptions();
        _context = Context(PreviewDocument.Empty);
        _images = new MarkdownImageLoader(owner.Services.FileSystem, owner.Services.Ui, () => Source, () => owner.Services.Config.Get(MarkdownPreviewSettings.AllowRemoteImages, false), () => _style);
        _images.Loaded += () => _panel?.InvalidateMeasure();

        _panel = new MarkdownBlockPanel();
        _panel.AnchorShift += OnAnchorShift;
        _notice = new TextBlock { IsVisible = false, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        var stack = new DockPanel();
        DockPanel.SetDock(_notice, Dock.Top);
        stack.Children.Add(_notice);
        stack.Children.Add(_panel);
        _content = new Border { Padding = new Thickness(26, 16, 26, 26), Child = stack, MaxWidth = double.PositiveInfinity };
        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _content,
            Focusable = true,
        };
        _scroller.ScrollChanged += OnScrollChanged;
        _scroller.LayoutUpdated += OnLayoutUpdated;
        _scroller.DoubleTapped += OnDoubleTapped;
        Content = _scroller;
        Focusable = false;
        Avalonia.Automation.AutomationProperties.SetName(this, "Markdown Preview: " + Title);
        ApplyStyle();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, owner.Services.Config.Get(MarkdownPreviewSettings.UpdateDelay, MarkdownPreviewSettings.DefaultUpdateDelay))) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Request();
        };

        var services = owner.Services;
        _subs.Add(owner.Themes.Changed.Subscribe(_ => Post(OnThemeChanged)));
        _subs.Add(services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects("markdown.preview") || e.Affects("cscode.markdown.preview") || e.Affects(SettingKeys.EditorFontFamily)) Post(OnSettingsChanged);
        }));
        _subs.Add(services.Documents.Opened.Subscribe(e =>
        {
            if (e.Document.Uri == Source) Post(() => Bind(e.Document));
        }));
        _subs.Add(services.Documents.Closed.Subscribe(e =>
        {
            if (e.Document.Uri == Source) Post(() => { if (ReferenceEquals(_document, e.Document)) Bind(null); });
        }));
        Bind(services.Documents.Get(source));
    }

    public Uri Source { get; }

    /// <summary>"Preview README.md": the tab title VS Code uses.</summary>
    public string Title => "Preview " + EditorTabTitle(Source);

    /// <summary>The document as last rendered.</summary>
    public PreviewDocument Document => _current;

    /// <summary>Raised on the UI thread after each render (the document passed is <see cref="Document"/>).</summary>
    public IEventSource<PreviewDocument> Rendered => _rendered;

    /// <summary>The user scrolled the preview: the 0-based (fractional) source line now at its top.</summary>
    public IEventSource<double> Scrolled => _scrolled;

    /// <summary>A link was clicked that does not point inside this document (its destination as written).</summary>
    public IEventSource<string> LinkClicked => _linkClicked;

    /// <summary>The preview was double-clicked: the source line under the pointer.</summary>
    public IEventSource<double> DoubleClickedLine => _doubleClicked;

    public ScrollViewer Scroller => _scroller;
    public MarkdownBlockPanel Blocks => _panel;
    public MarkdownPreviewStyle Style => _style;
    internal MarkdownImageLoader Images => _images;

    /// <summary>The 0-based (fractional) source line at the top of the preview.</summary>
    public double TopLine => _panel.LineAtOffset(Math.Max(0, _scroller.Offset.Y - PanelTop));

    private double PanelTop => _content.Padding.Top + (_notice.IsVisible ? _notice.Bounds.Height + _notice.Margin.Bottom : 0);

    /// <summary>Scrolls so that source line <paramref name="line"/> (0-based, fractional) is at the top. Before the first render, it is applied by it.</summary>
    public void ScrollToLine(double line)
    {
        if (_disposed) return;
        if (_current.Blocks.Count == 0)
        {
            _pendingLine = line;
            return;
        }
        Pin(Math.Max(0, line));
    }

    /// <summary>Scrolls to the heading with id <paramref name="anchor"/> ("getting-started", with or without '#'). False when there is none.</summary>
    public bool RevealAnchor(string anchor)
    {
        anchor = Uri.UnescapeDataString(anchor.TrimStart('#'));
        if (!_current.Anchors.TryGetValue(anchor, out var index) && !_current.Anchors.TryGetValue(anchor.ToLowerInvariant(), out index)) return false;
        Pin(_current.Blocks[index].StartLine);
        return true;
    }

    // A scroll to a line far away lands on estimated heights; the blocks built there then have their real sizes, which moves the line. So the
    // line stays pinned to the top for the next few layout passes (until the user scrolls), re-applied whenever the layout moved it.
    private double? _pinLine;
    private int _pinPasses;

    private void Pin(double line)
    {
        _pinLine = line;
        _pinPasses = 16;
        ApplyPin();
    }

    private void ApplyPin()
    {
        if (_pinLine is not { } line) return;
        var y = line <= 0 ? 0 : _panel.OffsetOfLine(line) + PanelTop;
        if (Math.Abs(_scroller.Offset.Y - y) > 0.75) SetOffset(y);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pinLine is null || _disposed) return;
        if (--_pinPasses <= 0)
        {
            _pinLine = null;
            return;
        }
        ApplyPin();
    }

    private void SetOffset(double y)
    {
        _expectedOffset = Math.Max(0, y);
        _scroller.Offset = new Vector(_scroller.Offset.X, _expectedOffset);
    }

    private void OnAnchorShift(double shift)
    {
        if (_pinLine is not null) return; // the pin keeps its line in place itself
        // The blocks above the top changed height after they were built: keep what the user looks at in place (posted: this runs in layout).
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _pinLine is not null) return;
            SetOffset(_scroller.Offset.Y + shift);
        }, DispatcherPriority.Render);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0) return;
        var y = _scroller.Offset.Y;
        if (!double.IsNaN(_expectedOffset))
        {
            // Our own scroll (SetOffset), possibly clamped to the end of the content.
            var max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
            if (Math.Abs(y - _expectedOffset) < 1.5 || (_expectedOffset > max && Math.Abs(y - max) < 1.5))
            {
                _expectedOffset = double.NaN;
                return;
            }
            _expectedOffset = double.NaN;
        }
        _pinLine = null; // the user scrolled: stop holding the last requested line
        _scrolled.Fire(TopLine);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var y = e.GetPosition(_content).Y - _content.Padding.Top - (_notice.IsVisible ? _notice.Bounds.Height + _notice.Margin.Bottom : 0);
        _doubleClicked.Fire(Math.Floor(_panel.LineAtOffset(Math.Max(0, y))));
    }

    // ---------------------------------------------------------------- the source

    private void Bind(ITextDocument? document)
    {
        if (_disposed || ReferenceEquals(document, _document) && document is not null) return;
        _docSub?.Dispose();
        _document = document;
        _docSub = document?.Changed.Subscribe(_ => Post(Schedule));
        Request();
    }

    private void Schedule()
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Re-renders now (tests, and commands that refresh the preview).</summary>
    public void Refresh() => Request();

    private void Request()
    {
        if (_disposed) return;
        _parseCts?.Cancel();
        _parseCts?.Dispose();
        var cts = _parseCts = new CancellationTokenSource();
        var request = ++_requested;
        // Stays stale until a full render has been applied: a later request that cancels the theme's or the settings' render rebuilds everything too.
        var reuse = !_colorsStale;
        _ = RenderAsync(request, reuse, cts.Token);
    }

    private async Task RenderAsync(int request, bool reuse, CancellationToken ct)
    {
        try
        {
            var services = _owner.Services;
            var options = _options;
            var previous = reuse ? _current : null;
            var colorize = _owner.CreateColorizer();
            PreviewDocument parsed;
            if (_document is { } doc)
            {
                // A live editor document: the worker reads the immutable snapshot; another kind of document is read here (UI thread) once.
                Func<(string Text, int Version)> read = doc is EditorDocument editor
                    ? () => { var snap = editor.Snapshot(); return (snap.GetText(), snap.Version); }
                    : CaptureText(doc);
                parsed = await _owner.Worker.RunAsync(WorkPriority.Background, token =>
                {
                    var (text, version) = read();
                    return MarkdownPreviewParser.Parse(text, version, options, colorize, previous, token);
                }, ct, key: this).ConfigureAwait(true);
            }
            else
            {
                byte[] bytes;
                try { bytes = await services.FileSystem.ReadFileAsync(Source, ct).ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OperationCanceledException) { bytes = System.Text.Encoding.UTF8.GetBytes("*The file could not be read:* " + ex.Message); }
                parsed = await _owner.Worker.RunAsync(WorkPriority.Background, token =>
                    MarkdownPreviewParser.Parse(TextFileCodec.Decode(bytes, TextFileCodec.Detect(bytes)), 0, options, colorize, previous, token), ct, key: this).ConfigureAwait(true);
            }
            if (_disposed || request != _requested || ct.IsCancellationRequested) return;
            Apply(parsed, reuse);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change
        }
    }

    private static Func<(string, int)> CaptureText(ITextDocument doc)
    {
        var text = doc.GetText();
        var version = doc.Version;
        return () => (text, version);
    }

    private void Apply(PreviewDocument parsed, bool reuse)
    {
        if (!reuse) _colorsStale = false;
        _current = parsed;
        _context = Context(parsed);
        _notice.IsVisible = parsed.Truncated;
        if (parsed.Truncated) _notice.Text = $"The file is too long to preview whole: the first {MarkdownPreviewParser.MaxLength:N0} characters are shown.";
        _panel.SetBlocks(parsed.Blocks, b => MarkdownBlockRenderer.Render(b.Block, _context), reuse);
        if (_pendingLine is { } line)
        {
            _pendingLine = null;
            // after this layout pass, when the blocks near the top have their real heights
            Dispatcher.UIThread.Post(() => ScrollToLine(line), DispatcherPriority.Loaded);
        }
        _rendered.Fire(parsed);
    }

    private MarkdownRenderContext Context(PreviewDocument doc) => new(_style, doc.CodeSpans)
    {
        OnLink = OnLink,
        CreateImage = (url, alt) => _images.Create(url, alt),
    };

    private void OnLink(string url)
    {
        if (url.StartsWith('#'))
        {
            RevealAnchor(url);
            return;
        }
        _linkClicked.Fire(url);
    }

    // ---------------------------------------------------------------- theme and settings

    private void OnThemeChanged()
    {
        if (_disposed) return;
        _style = ReadStyle();
        ApplyStyle();
        _colorsStale = true; // code colours depend on the theme: re-colour, and rebuild every block with the new brushes
        Request();
    }

    private void OnSettingsChanged()
    {
        if (_disposed) return;
        _style = ReadStyle();
        _options = ReadOptions();
        _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _owner.Services.Config.Get(MarkdownPreviewSettings.UpdateDelay, MarkdownPreviewSettings.DefaultUpdateDelay)));
        ApplyStyle();
        _colorsStale = true;
        Request();
    }

    private void ApplyStyle()
    {
        Background = _style.Background;
        _scroller.Background = _style.Background;
        _notice.Foreground = _style.Muted;
        _notice.FontFamily = _style.Body;
        _notice.FontSize = _style.FontSize * 0.9;
        _panel.SetMetrics(_style.BlockSpacing, _style.FontSize * _style.LineHeight);
    }

    private MarkdownPreviewStyle ReadStyle()
    {
        var config = _owner.Services.Config;
        var bodySetting = config.Get(MarkdownPreviewSettings.FontFamily, "");
        var body = new FontFamily(FontFamilies.Normalize(string.IsNullOrWhiteSpace(bodySetting) ? DefaultBodyFont : bodySetting));
        var mono = new FontFamily(FontFamilies.Normalize(config.Get(SettingKeys.EditorFontFamily, FontFamilies.DefaultCode)));
        return MarkdownPreviewStyle.From(_owner.Themes, body, mono,
            config.Get(MarkdownPreviewSettings.FontSize, MarkdownPreviewSettings.DefaultFontSize),
            config.Get(MarkdownPreviewSettings.LineHeight, MarkdownPreviewSettings.DefaultLineHeight));
    }

    /// <summary>The user interface font (the one the shell uses), which VS Code's preview also uses for its text.</summary>
    public static string DefaultBodyFont => OperatingSystem.IsMacOS() ? FontFamilies.MacUi : "Segoe UI, Inter, Noto Sans, sans-serif";

    private MarkdownPreviewOptions ReadOptions()
    {
        var config = _owner.Services.Config;
        return new MarkdownPreviewOptions(config.Get(MarkdownPreviewSettings.Breaks, false), config.Get(MarkdownPreviewSettings.Linkify, true));
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        if (_owner.Services.Ui.CheckAccess()) action();
        else _owner.Services.Ui.Post(() => { if (!_disposed) action(); });
    }

    private static string EditorTabTitle(Uri uri)
    {
        var path = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
        return Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path;
    }

    // ---------------------------------------------------------------- clipboard

    /// <summary>The text selected in the focused text block of the preview, if any.</summary>
    public string? SelectedText => FocusedText()?.SelectedText is { Length: > 0 } t ? t : null;

    public bool CanCopy => SelectedText is not null;

    /// <summary>Copies the selection of the focused text block (Edit > Copy, Ctrl+C / Cmd+C through the shell's clipboard routing).</summary>
    public void Copy() => FocusedText()?.Copy();

    private SelectableTextBlock? FocusedText() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is SelectableTextBlock tb && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, tb) ? tb : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _parseCts?.Cancel();
        _parseCts?.Dispose();
        _parseCts = null;
        _subs.Dispose();
        _docSub?.Dispose();
        _docSub = null;
        _document = null;
        _scroller.ScrollChanged -= OnScrollChanged;
        _scroller.LayoutUpdated -= OnLayoutUpdated;
        _scroller.DoubleTapped -= OnDoubleTapped;
        _panel.AnchorShift -= OnAnchorShift;
        _panel.Clear();
        _images.Dispose();
        _current = PreviewDocument.Empty;
        _context = new MarkdownRenderContext(_style, _current.CodeSpans);
        _rendered.Clear();
        _scrolled.Clear();
        _linkClicked.Clear();
        _doubleClicked.Clear();
        Content = null;
    }
}
