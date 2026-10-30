using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Input;
using CsCode.Panels.Terminal.Rendering;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using CsCode.Ui.Menus;
using SelectionMode = CsCode.Panels.Terminal.Input.SelectionMode;

namespace CsCode.Panels.Terminal.Views;

/// <summary>
/// The terminal renderer and input surface. It draws the session's newest immutable snapshot in <see cref="Render"/> and then asks for the
/// next one, so the worker builds at most one snapshot per rendered frame however fast the program writes; when the control is not rendered
/// (hidden tool window, hidden session window) nothing is asked for and nothing is posted. Keys become xterm sequences written straight to
/// the PTY's bounded queue (no hop through the worker); text and IME commits arrive as text input. Mouse: selection (drag, word, line,
/// Alt+drag block), xterm mouse reports when the program asks (Shift bypasses them), wheel scrolling, Ctrl+click on links.
/// </summary>
public sealed class TerminalControl : Control, IClipboardTarget, IDisposable
{
    public const double PaddingLeft = 4;
    public const double PaddingTop = 2;
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(600);

    private readonly TerminalPanelRuntime _runtime;
    private readonly TerminalPainter _painter = new();
    private readonly DisposableStore _subs = new();
    private DisposableStore? _sessionSubs;
    private TerminalSession? _session;
    private TerminalSnapshot? _snapshot;
    private TerminalSnapshot? _notified;
    private TerminalFont _font;
    private TerminalColors? _colors;
    private TerminalSelection? _selection;
    private TerminalLink? _hoverLink;
    private int _hoverRow = -1;
    private int _invalidatePosted;
    private bool _selecting;
    private bool _mouseButtonHeld;
    private int _mouseButton;
    private bool _suppressText;
    private bool _cursorOn = true;
    private DispatcherTimer? _blink;
    private string? _preedit;
    private string? _primaryText;
    private (int Columns, int Rows) _grid;
    private bool _disposed;

    public TerminalControl(TerminalPanelRuntime runtime)
    {
        _runtime = runtime;
        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _font = runtime.CreateFont();
        _painter.SetFont(_font);
        AutomationProperties.SetName(this, "Terminal");
        _subs.Add(runtime.AppearanceChanged.Subscribe(_ => Dispatcher.UIThread.Post(OnAppearanceChanged)));
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _ime ??= new ImeClient(this), handledEventsToo: false);
    }

    /// <summary>The terminal shown (null: none).</summary>
    public TerminalSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            _sessionSubs?.Dispose();
            _sessionSubs = null;
            _session = value;
            _snapshot = null;
            _selection = null;
            _hoverLink = null;
            if (value is not null)
            {
                _sessionSubs = new DisposableStore();
                _sessionSubs.Add(value.FrameReady.Subscribe(_ => ScheduleInvalidate()));
                _sessionSubs.Add(value.TitleChanged.Subscribe(s => Dispatcher.UIThread.Post(() => AutomationProperties.SetName(this, $"Terminal {s.Id}: {s.Title}"))));
                AutomationProperties.SetName(this, $"Terminal {value.Id}: {value.Title}");
                ResizeSession();
                value.RequestFrame();
            }
            InvalidateVisual();
        }
    }

    public TerminalSnapshot? Snapshot => _snapshot;
    public TerminalSelection? Selection => _selection;
    internal TerminalPanelRuntime Runtime => _runtime;
    internal TerminalFont Font => _font;
    internal TerminalPainter Painter => _painter;

    /// <summary>Fires on the UI thread after a frame was drawn (the view syncs its scrollbar).</summary>
    public event Action<TerminalSnapshot>? FrameDrawn;

    // ------------------------------------------------------------------ frames

    private void ScheduleInvalidate()
    {
        // FrameReady fires at most once per requested frame; the flag keeps it at one queued callback even if a request raced.
        if (Interlocked.Exchange(ref _invalidatePosted, 1) == 0)
            _runtime.Ui.Post(() =>
            {
                Volatile.Write(ref _invalidatePosted, 0);
                if (!_disposed) InvalidateVisual();
            });
    }

    /// <summary>How many times the control was rendered (tests: a hidden window renders nothing).</summary>
    internal int RenderCount { get; private set; }

    /// <summary>Time spent in <see cref="Render"/> (total and longest), for the budget tests.</summary>
    internal TimeSpan RenderTime { get; private set; }
    internal TimeSpan LongestRender { get; private set; }

    public override void Render(DrawingContext context)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderCore(context); }
        finally
        {
            var spent = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            RenderTime += spent;
            if (spent > LongestRender) LongestRender = spent;
        }
    }

    private void RenderCore(DrawingContext context)
    {
        RenderCount++;
        var session = _session;
        if (session?.LatestSnapshot is { } latest) _snapshot = latest;
        var bounds = new Size(Bounds.Width, Bounds.Height);
        _colors ??= _runtime.Colors();
        if (_snapshot is { } snap)
        {
            using (context.PushTransform(Matrix.CreateTranslation(PaddingLeft, PaddingTop)))
            {
                var linkOverlay = _hoverLink is { } l && _hoverRow >= 0 ? (_hoverRow, l.StartColumn, l.EndColumn) : ((int, int, int)?)null;
                _painter.Paint(context, snap, _colors, new TerminalPainter.Overlay(SelectionColumns, linkOverlay, _cursorOn, IsKeyboardFocusWithin, _runtime.CursorStyle),
                    new Size(Math.Max(0, bounds.Width), Math.Max(0, bounds.Height)));
                if (_preedit is { Length: > 0 } preedit && snap.CursorY >= 0) DrawPreedit(context, snap, preedit);
            }
            if (!ReferenceEquals(snap, _notified))
            {
                // other visuals (the scrollbar) must not change during the render pass: tell the view right after it
                _notified = snap;
                Dispatcher.UIThread.Post(() => { if (!_disposed) FrameDrawn?.Invoke(snap); });
            }
        }
        else context.FillRectangle(_painter.Brush(_colors.Background), new Rect(bounds));
        session?.RequestFrame();
    }

    private (int Start, int End)? SelectionColumns(long line, int columns) => _selection?.ColumnsOn(line, columns);

    private void DrawPreedit(DrawingContext context, TerminalSnapshot snap, string text)
    {
        var x = snap.CursorX * _font.CellWidth;
        var y = snap.CursorY * _font.CellHeight;
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(_font.Family), _font.Size,
            _painter.Brush(_colors!.Foreground));
        context.FillRectangle(_painter.Brush(_colors.Background), new Rect(x, y, formatted.Width, _font.CellHeight));
        context.DrawText(formatted, new Point(x, y + _font.Baseline - formatted.Baseline));
        context.FillRectangle(_painter.Brush(_colors.Foreground), new Rect(x, y + _font.UnderlineY, formatted.Width, 1));
    }

    private void OnAppearanceChanged()
    {
        if (_disposed) return;
        _colors = null;
        var font = _runtime.CreateFont();
        if (font.Key != _font.Key)
        {
            _font = font;
            _painter.SetFont(font);
            ResizeSession();
        }
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ size

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 * _font.CellWidth : availableSize.Width, double.IsInfinity(availableSize.Height) ? 24 * _font.CellHeight : availableSize.Height);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeSession();
    }

    /// <summary>The grid that fits the control.</summary>
    public (int Columns, int Rows) GridSize =>
        (Math.Max(2, (int)((Bounds.Width - PaddingLeft) / _font.CellWidth)), Math.Max(1, (int)((Bounds.Height - PaddingTop) / _font.CellHeight)));

    private void ResizeSession()
    {
        if (_session is not { } s || Bounds.Width < 1 || Bounds.Height < 1) return;
        var size = GridSize;
        if (size == _grid && s.Id == _lastSizedId) return;
        _grid = size;
        _lastSizedId = s.Id;
        s.Resize(size.Columns, size.Rows);
    }

    private int _lastSizedId; // an id, not the session: a killed terminal must not stay reachable through its view

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// Encodes and sends a key the terminal owns (called by the window-level precedence handler before the shell's keybindings, and by the
    /// control's own key handler). Returns true when bytes were sent.
    /// </summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        _suppressText = false;
        if (_session is not { } s) return false;
        var bytes = KeyEncoder.Encode(e.Key, e.KeyModifiers, e.KeySymbol, s.Modes);
        if (bytes is null) return false;
        var interrupt = bytes is [0x03] or [0x1c] or [0x1a];
        s.Input(bytes, urgent: interrupt);
        // a handled Ctrl/Alt combination must not also arrive as text (some platforms send both)
        _suppressText = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
        ClearSelectionOnInput();
        ResetBlink();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            OpenContextMenu(keyboard: true);
            e.Handled = true;
            return;
        }
        if (HandleKey(e)) e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        if (_suppressText)
        {
            _suppressText = false;
            e.Handled = true;
            return;
        }
        _preedit = null;
        _session?.Input(KeyEncoder.Text(e.Text));
        ClearSelectionOnInput();
        ResetBlink();
        e.Handled = true;
    }

    private void ClearSelectionOnInput()
    {
        if (_selection is null) return;
        _selection = null;
        InvalidateVisual();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _runtime.SetTerminalFocus(true, this);
        if (_session is { } s && s.Modes.FocusEvents) s.Input(KeyEncoder.Focus(true));
        StartBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _runtime.SetTerminalFocus(false, this);
        if (_session is { } s && s.Modes.FocusEvents) s.Input(KeyEncoder.Focus(false));
        StopBlink();
        InvalidateVisual();
    }

    // Blinking runs only while the terminal has keyboard focus in an active window: an unfocused or hidden window does no periodic work.
    private void StartBlink()
    {
        _cursorOn = true;
        if (!_runtime.CursorBlinking(_snapshot) || _blink is not null) return;
        _blink = new DispatcherTimer(BlinkInterval, DispatcherPriority.Background, (_, _) =>
        {
            if (!IsKeyboardFocusWithin || TopLevel.GetTopLevel(this) is Window { IsActive: false }) { StopBlink(); InvalidateVisual(); return; }
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        });
        _blink.Start();
    }

    private void StopBlink()
    {
        _blink?.Stop();
        _blink = null;
        _cursorOn = true;
    }

    private void ResetBlink()
    {
        if (_blink is null) return;
        _cursorOn = true;
        _blink.Stop();
        _blink.Start();
    }

    // ------------------------------------------------------------------ mouse

    private (int Column, int Row) CellAt(Point p)
    {
        var snap = _snapshot;
        var cols = snap?.Columns ?? _grid.Columns;
        var rows = snap?.RowCount ?? _grid.Rows;
        var c = (int)Math.Floor((p.X - PaddingLeft) / _font.CellWidth);
        var r = (int)Math.Floor((p.Y - PaddingTop) / _font.CellHeight);
        return (Math.Clamp(c, 0, Math.Max(0, cols - 1)), Math.Clamp(r, 0, Math.Max(0, rows - 1)));
    }

    private bool MouseReporting(KeyModifiers modifiers) =>
        _session is { } s && s.Modes.Mouse != MouseTracking.None && (modifiers & KeyModifiers.Shift) == 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        var (col, row) = CellAt(point.Position);
        var mods = e.KeyModifiers;
        if (_session is not { } s || _snapshot is not { } snap) return;

        if (props.IsRightButtonPressed && !MouseReporting(mods))
        {
            e.Handled = true;
            OpenContextMenu(keyboard: false);
            return;
        }
        if (MouseReporting(mods))
        {
            var button = props.IsLeftButtonPressed ? 0 : props.IsMiddleButtonPressed ? 1 : 2;
            _mouseButton = button;
            _mouseButtonHeld = true;
            if (MouseEncoder.Encode(s.Modes, MouseAction.Press, button, col, row, false, (mods & KeyModifiers.Alt) != 0, (mods & KeyModifiers.Control) != 0, true) is { } report)
                s.Input(report);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsMiddleButtonPressed)
        {
            e.Handled = true;
            _ = _runtime.PastePrimaryAsync(this);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if ((mods & KeyModifiers.Control) != 0 && LinkAt(col, row) is { } link)
        {
            e.Handled = true;
            _ = _runtime.OpenLinkAsync(link, s);
            return;
        }
        var line = snap.ViewportTop + row;
        var at = new GridPoint(line, col);
        _selection = e.ClickCount switch
        {
            2 => WordSelection(snap, row, col),
            >= 3 => LineSelection(line),
            _ => new TerminalSelection(at, at, (mods & KeyModifiers.Alt) != 0 ? SelectionMode.Block : SelectionMode.Character),
        };
        _selecting = true;
        _selectionMoved = e.ClickCount >= 2;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    private bool _selectionMoved;

    private TerminalSelection WordSelection(TerminalSnapshot snap, int row, int col)
    {
        var (start, end) = TerminalSelection.WordAt(snap.Rows[row], col, snap.Columns, _runtime.WordSeparators);
        var line = snap.ViewportTop + row;
        return new TerminalSelection(new GridPoint(line, start), new GridPoint(line, end), SelectionMode.Word);
    }

    private TerminalSelection LineSelection(long line)
    {
        var snap = _snapshot!;
        TerminalRow? RowAt(long l) => l >= snap.ViewportTop && l < snap.ViewportTop + snap.RowCount ? snap.Rows[l - snap.ViewportTop] : null;
        var (first, last) = TerminalSelection.LogicalLine(RowAt, line, snap.ViewportTop, snap.ViewportTop + snap.RowCount - 1);
        return new TerminalSelection(new GridPoint(first, 0), new GridPoint(last, snap.Columns - 1), SelectionMode.Line);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var (col, row) = CellAt(p);
        var mods = e.KeyModifiers;
        if (_session is { } s && MouseReporting(mods) && !_selecting)
        {
            if (MouseEncoder.Encode(s.Modes, MouseAction.Move, _mouseButton, col, row, false, (mods & KeyModifiers.Alt) != 0, (mods & KeyModifiers.Control) != 0, _mouseButtonHeld) is { } report)
                s.Input(report);
            return;
        }
        if (_selecting && _selection is { } sel && _snapshot is { } snap)
        {
            var line = snap.ViewportTop + row;
            _selection = sel.Mode switch
            {
                SelectionMode.Word => ExtendWord(sel, snap, row, col),
                SelectionMode.Line => sel with { Active = line >= sel.Anchor.Line ? new GridPoint(line, snap.Columns - 1) : new GridPoint(line, 0) },
                _ => sel with { Active = new GridPoint(line, col) },
            };
            _selectionMoved = true;
            InvalidateVisual();
            return;
        }
        UpdateHoverLink(col, row, (mods & KeyModifiers.Control) != 0);
    }

    private TerminalSelection ExtendWord(TerminalSelection sel, TerminalSnapshot snap, int row, int col)
    {
        var (start, end) = TerminalSelection.WordAt(snap.Rows[row], col, snap.Columns, _runtime.WordSeparators);
        var line = snap.ViewportTop + row;
        return new GridPoint(line, col) >= sel.Anchor ? sel with { Active = new GridPoint(line, end) } : sel with { Active = new GridPoint(line, start) };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (col, row) = CellAt(e.GetPosition(this));
        if (_session is { } s && _mouseButtonHeld)
        {
            _mouseButtonHeld = false;
            if (MouseEncoder.Encode(s.Modes, MouseAction.Release, _mouseButton, col, row, false, (e.KeyModifiers & KeyModifiers.Alt) != 0, (e.KeyModifiers & KeyModifiers.Control) != 0, false) is { } report)
                s.Input(report);
            e.Pointer.Capture(null);
            return;
        }
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
        if (!_selectionMoved && _selection is { Mode: SelectionMode.Character or SelectionMode.Block } sel && sel.Anchor == sel.Active)
        {
            _selection = null; // a plain click selects nothing
            InvalidateVisual();
            return;
        }
        _ = SelectionFinishedAsync();
    }

    /// <summary>Copy on selection and the PRIMARY selection (Linux): the text is read on the worker once, so the provider only returns a string.</summary>
    private async Task SelectionFinishedAsync()
    {
        if (await GetSelectionTextAsync() is not { Length: > 0 } text) return;
        _primaryText = text;
        _runtime.SetPrimarySelection(() => Volatile.Read(ref _primaryText) ?? "");
        if (_runtime.CopyOnSelection) await _runtime.CopyAsync(text);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is not { } s || _snapshot is not { } snap) return;
        var notches = (int)Math.Round(e.Delta.Y);
        if (notches == 0) notches = Math.Sign(e.Delta.Y);
        if (notches == 0) return;
        e.Handled = true;
        var (col, row) = CellAt(e.GetPosition(this));
        if (MouseReporting(e.KeyModifiers))
        {
            for (var i = 0; i < Math.Abs(notches); i++)
                if (MouseEncoder.Encode(s.Modes, notches > 0 ? MouseAction.WheelUp : MouseAction.WheelDown, 0, col, row, false, false, (e.KeyModifiers & KeyModifiers.Control) != 0, false) is { } report)
                    s.Input(report);
            return;
        }
        if (s.Modes.AlternateScreen && s.Modes.AlternateScroll)
        {
            // xterm.js: on the alternate screen (less, vim) the wheel sends cursor keys
            var key = notches > 0 ? (s.Modes.ApplicationCursorKeys ? "\eOA" : "\e[A") : (s.Modes.ApplicationCursorKeys ? "\eOB" : "\e[B");
            s.Input(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(key, Math.Abs(notches) * 3))));
            return;
        }
        ScrollLines(-notches * 3);
        _ = snap;
    }

    // ------------------------------------------------------------------ scrolling

    // The viewport belongs to the emulator (the worker): the UI only asks. Each lambda captures the session it was asked for.
    public void ScrollLines(int delta) { if (_session is { } s) s.Post(() => s.Emulator.ScrollViewportBy(delta)); }
    public void ScrollPages(int delta) => ScrollLines(delta * Math.Max(1, (_snapshot?.RowCount ?? _grid.Rows) - 1));
    public void ScrollToTop() { if (_session is { } s) s.Post(() => s.Emulator.ScrollViewportTo(long.MinValue)); }
    public void ScrollToBottom() { if (_session is { } s) s.Post(s.Emulator.ScrollViewportToBottom); }
    public void ScrollTo(long absoluteTop) { if (_session is { } s) s.Post(() => s.Emulator.ScrollViewportTo(absoluteTop)); }
    /// <summary>Shell integration: the previous (-1) or next (+1) prompt at the top of the view.</summary>
    public void ScrollToCommand(int direction) { if (_session is { } s) s.Post(() => s.Emulator.ScrollToCommand(direction)); }

    // ------------------------------------------------------------------ links

    private TerminalLink? LinkAt(int col, int row) =>
        _snapshot is { } snap && row < snap.Rows.Length ? LinkDetector.At(snap.Rows[row], col, snap.Columns) : null;

    private void UpdateHoverLink(int col, int row, bool ctrl)
    {
        var link = ctrl ? LinkAt(col, row) : null;
        if (link == _hoverLink && (link is null || row == _hoverRow)) return;
        _hoverLink = link;
        _hoverRow = link is null ? -1 : row;
        Cursor = new Cursor(link is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
        ToolTip.SetTip(this, link is null ? null : (link.Kind == LinkKind.File ? "Open file in editor" : "Follow link") + " (ctrl + click)");
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ selection, clipboard

    /// <summary>The selected text (read on the session's worker, which owns the scrollback), or null.</summary>
    public async Task<string?> GetSelectionTextAsync()
    {
        if (_selection is not { } sel || _session is not { } s) return null;
        return await s.InvokeAsync(e => sel.GetText(e.GetRow, e.Columns)).ConfigureAwait(true);
    }

    public void SelectAll()
    {
        if (_snapshot is not { } snap) return;
        _selection = new TerminalSelection(new GridPoint(snap.ScrollbackTop, 0), new GridPoint(snap.ScreenTop + snap.RowCount - 1, snap.Columns - 1), SelectionMode.Character);
        InvalidateVisual();
        _ = SelectionFinishedAsync();
    }

    public void ClearSelection()
    {
        _selection = null;
        InvalidateVisual();
    }

    /// <summary>Sets the selection (tests, Find).</summary>
    internal void SetSelection(TerminalSelection? selection)
    {
        _selection = selection;
        InvalidateVisual();
    }

    public bool CanCopy => _selection is not null;
    public bool CanPaste => _session is { HasExited: false };

    public async Task CopyAsync()
    {
        if (await GetSelectionTextAsync() is { Length: > 0 } text) await _runtime.CopyAsync(text);
    }

    public Task PasteAsync() => _runtime.PasteAsync(this);

    /// <summary>Sends pasted text (bracketed when the program asked for it).</summary>
    internal void SendPaste(string text)
    {
        if (_session is not { } s) return;
        s.Input(KeyEncoder.Paste(text, s.Modes.BracketedPaste));
        ClearSelectionOnInput();
    }

    private void OpenContextMenu(bool keyboard)
    {
        var anchor = keyboard && _snapshot is { CursorY: >= 0 } snap
            ? new Rect(PaddingLeft + snap.CursorX * _font.CellWidth, PaddingTop + snap.CursorY * _font.CellHeight, _font.CellWidth, _font.CellHeight)
            : (Rect?)null;
        ContextMenus.Open(_runtime.ContextMenuEntries(this), this, anchor, "Terminal menu");
    }

    // ------------------------------------------------------------------ IME

    private ImeClient? _ime;

    /// <summary>Composition happens at the terminal's cursor: the IME's candidate window is placed there and the preedit text drawn there.</summary>
    private sealed class ImeClient(TerminalControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override TextSelection Selection { get => default; set { } }

        public override Rect CursorRectangle
        {
            get
            {
                var f = owner._font;
                return owner._snapshot is { CursorY: >= 0 } s
                    ? new Rect(PaddingLeft + s.CursorX * f.CellWidth, PaddingTop + s.CursorY * f.CellHeight, f.CellWidth, f.CellHeight)
                    : new Rect(PaddingLeft, PaddingTop, f.CellWidth, f.CellHeight);
            }
        }

        public override void SetPreeditText(string? preeditText)
        {
            owner._preedit = preeditText;
            owner.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ lifetime

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Avalonia's focus machinery keeps the last focused element until focus moves elsewhere: a hidden terminal must not stay reachable
        // (with its view and painter) through it.
        ReleaseFocus(e.RootVisual as TopLevel);
        _runtime.SetTerminalFocus(false, this);
        StopBlink();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Gives keyboard focus up (to the window, else nobody) when it is in this control: the shell's rule for views that go away.</summary>
    internal void ReleaseFocus(TopLevel? top = null)
    {
        top ??= TopLevel.GetTopLevel(this);
        if (top?.FocusManager is not { } focus || !ReferenceEquals(focus.GetFocusedElement(), this)) return;
        if (!top.Focus()) focus.Focus(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopBlink();
        _sessionSubs?.Dispose();
        _subs.Dispose();
        _painter.Dispose();
        _session = null;
        _runtime.SetTerminalFocus(false, this);
    }
}
