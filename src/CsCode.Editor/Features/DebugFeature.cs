using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The debugging surface of one view, entirely through Core (<see cref="IDebugService"/> / <see cref="IBreakpointManager"/>) and absent — no margin,
/// no handlers — when no debugger is registered:
/// the breakpoint glyph margin (click toggles; right-click: condition / hit count / logpoint / enable / remove; red = verified, hollow grey = disabled or
/// unverified, tooltips from the adapter's message; a ghost dot follows the pointer), the current-execution-line arrow + highlight (auto-revealed when the
/// debugger stops in this file), and breakpoints that follow edits (<see cref="IBreakpointManager.ApplyLineShift"/> is called from document changes).
/// Hover-evaluate lives in <see cref="HoverFeature"/>.
/// </summary>
internal sealed class DebugFeature : IThemeAware, ISettingsAware, IDisposable
{
    internal const double MarginWidth = 18;

    private readonly CodeEditorControl _v;
    private readonly IDebugService _debug;
    private readonly DisposableStore _d = new();
    private readonly BreakpointMargin _margin;
    private Dictionary<int, SourceBreakpoint> _byLine = new();
    private bool _disposed;

    public DebugFeature(CodeEditorControl view, IDebugService debug)
    {
        _v = view;
        _debug = debug;
        _margin = new BreakpointMargin(this);
        view.TextArea.LeftMargins.Insert(0, _margin);
        Refresh();

        _d.Add(debug.Breakpoints.Changed.Subscribe(e =>
        {
            if (e.AffectedUris.Contains(view.Document.Uri) || e.AffectedUris.Count == 0) view.Post(Refresh);
        }));
        _d.Add(debug.StopLocationChanged.Subscribe(loc => view.Post(() => ShowStop(loc))));
        _d.Add(debug.StateChanged.Subscribe(_ => view.Post(() => ShowStop(debug.StopLocation))));
        _d.Add(view.Document.Changed.Subscribe(OnDocumentChanged));
        _d.Add(view.Controller.StopLineChanged.Subscribe(_ => _margin.InvalidateVisual()));
        _d.Add(Disposable.Create(() => view.TextArea.LeftMargins.Remove(_margin)));
        ShowStop(debug.StopLocation);
    }

    internal IDebugService Debug => _debug;
    internal CodeEditorControl View => _v;
    /// <summary>The breakpoint glyph margin: the code-action lightbulb never goes left of its right edge (<see cref="CodeActionFeature"/>).</summary>
    internal Control Margin => _margin;
    internal bool IsFileDocument => _v.Document.Uri.IsFile;

    public void OnThemeChanged() => _margin.InvalidateVisual();

    /// <summary>The line-number margin is (re)inserted by the editor when line numbers are toggled: keep the glyph margin leftmost, as in Visual Studio.</summary>
    public void OnSettingsChanged()
    {
        var margins = _v.TextArea.LeftMargins;
        if (margins.IndexOf(_margin) is var i and > 0)
        {
            margins.RemoveAt(i);
            margins.Insert(0, _margin);
        }
    }

    // ---- state ----

    internal SourceBreakpoint? BreakpointAt(int line) => _byLine.GetValueOrDefault(line);
    internal IReadOnlyCollection<SourceBreakpoint> Breakpoints => _byLine.Values;

    private void Refresh()
    {
        if (_disposed) return;
        _byLine = IsFileDocument ? _debug.Breakpoints.GetForUri(_v.Document.Uri).GroupBy(b => b.Line).ToDictionary(g => g.Key, g => g.First()) : new();
        _margin.InvalidateVisual();
    }

    private void ShowStop(DebugStopLocation? location)
    {
        if (_disposed) return;
        var here = location is { Uri: { } uri } && FeatureSupport.SameFile(uri, _v.Document.Uri) && _debug.State == DebugState.Stopped;
        _v.Controller.SetDebugStopLine(here ? location!.Line : null);
    }

    // ---- breakpoints follow edits ----

    private void OnDocumentChanged(TextDocumentChangedEventArgs e)
    {
        if (_disposed || !IsFileDocument || _byLine.Count == 0) return;
        foreach (var change in e.Changes)
        {
            var shift = BreakpointShift.Of(change);
            if (shift is null) continue;
            var (vanishedFrom, vanishedTo, fromLine, delta) = shift.Value;
            if (vanishedTo >= vanishedFrom)
                foreach (var bp in _debug.Breakpoints.GetForUri(_v.Document.Uri).Where(b => b.Line >= vanishedFrom && b.Line <= vanishedTo).ToList())
                    _debug.Breakpoints.Remove(bp.Id);
            if (delta != 0) _debug.Breakpoints.ApplyLineShift(_v.Document.Uri, fromLine, delta);
        }
        Refresh();
    }

    // ---- gestures (also callable by tests and by the context menu) ----

    internal void Toggle(int line)
    {
        if (!IsFileDocument || line < 0 || line >= _v.Document.LineCount) return;
        _debug.Breakpoints.Toggle(_v.Document.Uri, line);
    }

    internal async Task EditConditionAsync(int line)
    {
        var existing = BreakpointAt(line);
        if (existing?.Condition is null && DebugCapabilityGate.ConditionUnsupported(_debug) is { } why) { await TellAsync(why).ConfigureAwait(true); return; }
        var text = await Ask("Edit Condition", "Expression must evaluate to true for the breakpoint to be hit", existing?.Condition).ConfigureAwait(true);
        if (text is null) return;
        var condition = string.IsNullOrWhiteSpace(text) ? null : text;
        if (existing is not null) _debug.Breakpoints.Update(existing with { Condition = condition });
        else if (condition is not null) _debug.Breakpoints.Add(_v.Document.Uri, line, condition: condition);
    }

    internal async Task EditHitCountAsync(int line)
    {
        var existing = BreakpointAt(line);
        if (existing?.HitCondition is null && DebugCapabilityGate.HitCountUnsupported(_debug) is { } why) { await TellAsync(why).ConfigureAwait(true); return; }
        var text = await Ask("Edit Hit Count", "Break when the hit count satisfies this expression, for example '> 5' or '% 2 == 0'", existing?.HitCondition).ConfigureAwait(true);
        if (text is null) return;
        var hit = string.IsNullOrWhiteSpace(text) ? null : text;
        if (existing is not null) _debug.Breakpoints.Update(existing with { HitCondition = hit });
        else if (hit is not null) _debug.Breakpoints.Add(_v.Document.Uri, line, hitCondition: hit);
    }

    internal async Task EditLogMessageAsync(int line)
    {
        var existing = BreakpointAt(line);
        if (existing?.LogMessage is null && DebugCapabilityGate.LogPointUnsupported(_debug) is { } why) { await TellAsync(why).ConfigureAwait(true); return; }
        var text = await Ask("Edit Log Message", "Message to log when the breakpoint is hit; {expression} is interpolated. The program does not stop.", existing?.LogMessage).ConfigureAwait(true);
        if (text is null) return;
        var message = string.IsNullOrWhiteSpace(text) ? null : text;
        if (existing is not null) _debug.Breakpoints.Update(existing with { LogMessage = message });
        else if (message is not null) _debug.Breakpoints.Add(_v.Document.Uri, line, logMessage: message);
    }

    internal void SetEnabled(int line, bool enabled)
    {
        if (BreakpointAt(line) is { } bp) _debug.Breakpoints.Update(bp with { Enabled = enabled });
    }

    internal void Remove(int line)
    {
        if (BreakpointAt(line) is { } bp) _debug.Breakpoints.Remove(bp.Id);
    }

    private async ValueTask<string?> Ask(string title, string prompt, string? value)
    {
        var window = _v.Services.Window;
        return window is null ? null : await window.ShowInputBoxAsync(new InputBoxOptions(Title: title, Prompt: prompt, Value: value)).ConfigureAwait(true);
    }

    private async ValueTask TellAsync(string message)
    {
        if (_v.Services.Window is { } window) await window.ShowMessageAsync(MessageSeverity.Info, message).ConfigureAwait(true);
    }

    /// <summary>Tooltip for the glyph of a breakpoint: what it does and what the adapter said about it.</summary>
    internal string? Describe(int line)
    {
        if (BreakpointAt(line) is not { } bp) return null;
        var parts = new List<string>();
        parts.Add(bp.LogMessage is not null ? "Logpoint" : bp.Condition is not null ? "Conditional breakpoint" : "Breakpoint");
        if (bp.Condition is not null) parts.Add($"Condition: {bp.Condition}");
        if (bp.HitCondition is not null) parts.Add($"Hit count: {bp.HitCondition}");
        if (bp.LogMessage is not null) parts.Add($"Log message: {bp.LogMessage}");
        if (!bp.Enabled) parts.Add("Disabled");
        else if (_debug.Breakpoints.GetStatus(bp.Id) is { } status)
        {
            if (!status.Verified) parts.Add(status.Message is { Length: > 0 } m ? $"Unverified: {m}" : "Unverified breakpoint");
            else if (status.ActualLine is { } actual && actual != bp.Line) parts.Add($"Moved to line {actual + 1} by the debugger");
            else if (status.Message is { Length: > 0 } m2) parts.Add(m2);
        }
        return string.Join("\n", parts);
    }

    internal void ShowContextMenu(int line, Control anchor) => BuildContextMenu(line).Open(anchor);

    /// <summary>
    /// The glyph margin's menu. Conditions, hit counts and logpoints are offered only when the active debug adapter supports them (netcoredbg
    /// has conditions only, js-debug all three; known from the running session, else from the last session of the selected configuration's
    /// type, else assumed): an unsupported entry is disabled and says why. One the breakpoint already has can still be edited (cleared).
    /// </summary>
    internal ContextMenu BuildContextMenu(int line)
    {
        var existing = BreakpointAt(line);
        var menu = new ContextMenu();
        MenuItem Item(string header, Action run, string? unsupported = null)
        {
            var m = new MenuItem { Header = unsupported is null ? header : header + " (not supported)", IsEnabled = unsupported is null };
            if (unsupported is not null)
            {
                ToolTip.SetTip(m, unsupported);
                ToolTip.SetShowOnDisabled(m, true);
            }
            m.Click += (_, _) => run();
            return m;
        }
        var noCondition = DebugCapabilityGate.ConditionUnsupported(_debug);
        var noHitCount = DebugCapabilityGate.HitCountUnsupported(_debug);
        var noLogPoint = DebugCapabilityGate.LogPointUnsupported(_debug);
        if (existing is null)
        {
            menu.Items.Add(Item("Add Breakpoint", () => Toggle(line)));
            menu.Items.Add(Item("Add Conditional Breakpoint…", () => _ = EditConditionAsync(line), noCondition));
            menu.Items.Add(Item("Add Logpoint…", () => _ = EditLogMessageAsync(line), noLogPoint));
        }
        else
        {
            menu.Items.Add(Item("Remove Breakpoint", () => Remove(line)));
            menu.Items.Add(Item(existing.Enabled ? "Disable Breakpoint" : "Enable Breakpoint", () => SetEnabled(line, !existing.Enabled)));
            menu.Items.Add(Item("Edit Condition…", () => _ = EditConditionAsync(line), existing.Condition is null ? noCondition : null));
            menu.Items.Add(Item("Edit Hit Count…", () => _ = EditHitCountAsync(line), existing.HitCondition is null ? noHitCount : null));
            menu.Items.Add(Item("Edit Log Message…", () => _ = EditLogMessageAsync(line), existing.LogMessage is null ? noLogPoint : null));
        }
        return menu;
    }

    // ---- the glyph margin ----

    private sealed class BreakpointMargin : AbstractMargin
    {
        private readonly DebugFeature _owner;
        private int _hoverLine = -1;

        public BreakpointMargin(DebugFeature owner)
        {
            _owner = owner;
            Width = MarginWidth;
            Cursor = new Cursor(StandardCursorType.Arrow);
            ToolTip.SetShowDelay(this, 200);
            Avalonia.Automation.AutomationProperties.SetName(this, "Breakpoint margin");
        }

        protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

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
            if (textView is null || !textView.VisualLinesValid) return;
            var palette = _owner._v.Palette;
            context.FillRectangle(palette.Gutter, new Rect(0, 0, Bounds.Width, Bounds.Height));
            var stop = _owner._v.Controller.StopLine;
            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine.LineNumber - 1;
                var zone = LineZones.HeightOf(vl); // a code lens above the line: the glyph goes by the text
                var top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) + zone - textView.VerticalOffset;
                var height = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineBottom) - vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - zone;
                var center = new Point(MarginWidth / 2, top + height / 2);
                var radius = Math.Min(MarginWidth, height) / 2 - 3;
                var bp = _owner.BreakpointAt(line);
                if (bp is not null) DrawBreakpoint(context, palette, bp, center, radius);
                else if (line == _hoverLine && _owner.IsFileDocument) context.DrawEllipse(new SolidColorBrush(((ISolidColorBrush)palette.BreakpointFill).Color, 0.35), null, center, radius, radius);
                if (stop == line) DrawArrow(context, palette, center, radius + 2);
            }
        }

        private void DrawBreakpoint(DrawingContext context, EditorPalette palette, SourceBreakpoint bp, Point c, double r)
        {
            var status = _owner._debug.Breakpoints.GetStatus(bp.Id);
            var inactive = !bp.Enabled || status is { Verified: false };
            var fill = palette.BreakpointFill;
            var grey = palette.BreakpointDisabled;
            if (bp.LogMessage is not null)
            {
                var d = r + 1;
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(c.X, c.Y - d), true);
                    g.LineTo(new Point(c.X + d, c.Y));
                    g.LineTo(new Point(c.X, c.Y + d));
                    g.LineTo(new Point(c.X - d, c.Y));
                    g.EndFigure(true);
                }
                context.DrawGeometry(inactive ? null : fill, new Pen(inactive ? grey : fill, 1.5), diamond);
                return;
            }
            if (inactive) context.DrawEllipse(null, new Pen(grey, 1.5), c, r - 0.5, r - 0.5);
            else context.DrawEllipse(fill, null, c, r, r);
            var white = Brushes.White;
            if (bp.Condition is not null)
            {
                context.FillRectangle(inactive ? grey : white, new Rect(c.X - r / 2, c.Y - r / 3 - 0.5, r, 1.2));
                context.FillRectangle(inactive ? grey : white, new Rect(c.X - r / 2, c.Y + r / 3 - 0.5, r, 1.2));
            }
            else if (bp.HitCondition is not null)
            {
                context.FillRectangle(inactive ? grey : white, new Rect(c.X - r / 2, c.Y - r / 2, 1.2, r));
                context.FillRectangle(inactive ? grey : white, new Rect(c.X + r / 2 - 1.2, c.Y - r / 2, 1.2, r));
                context.FillRectangle(inactive ? grey : white, new Rect(c.X - r / 2, c.Y - 0.6, r, 1.2));
            }
        }

        private static void DrawArrow(DrawingContext context, EditorPalette palette, Point c, double size)
        {
            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                var half = size / 2;
                g.BeginFigure(new Point(c.X - size / 2 - 1, c.Y - half / 2), true);
                g.LineTo(new Point(c.X, c.Y - half / 2));
                g.LineTo(new Point(c.X, c.Y - half));
                g.LineTo(new Point(c.X + size / 2 + 1, c.Y));
                g.LineTo(new Point(c.X, c.Y + half));
                g.LineTo(new Point(c.X, c.Y + half / 2));
                g.LineTo(new Point(c.X - size / 2 - 1, c.Y + half / 2));
                g.EndFigure(true);
            }
            context.DrawGeometry(palette.StackFrameArrow, new Pen(Brushes.Black, 0.5), arrow);
        }

        private int LineAt(PointerEventArgs e)
        {
            var textView = TextView;
            if (textView is null) return -1;
            var y = e.GetPosition(this).Y + textView.VerticalOffset;
            var vl = textView.GetVisualLineFromVisualTop(y);
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
            if (line < 0 || !_owner.IsFileDocument) return;
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed) _owner.Toggle(line);
            else if (point.Properties.IsRightButtonPressed) _owner.ShowContextMenu(line, this);
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _d.Dispose();
        _v.Controller.SetDebugStopLine(null);
    }
}

/// <summary>How one text change moves breakpoint lines (pure; unit-tested).</summary>
internal static class BreakpointShift
{
    /// <summary>
    /// Lines [VanishedFrom, VanishedTo] ceased to exist (their breakpoints must go); every breakpoint at a line &gt;= FromLine moves by Delta.
    /// Null when the change does not alter the line structure.
    /// </summary>
    public static (int VanishedFrom, int VanishedTo, int FromLine, int Delta)? Of(TextContentChange change)
    {
        var start = change.Range.Start;
        var end = change.Range.End;
        var removed = Math.Max(0, end.Line - start.Line);
        var inserted = CountBreaks(change.Text);
        if (removed == 0 && inserted == 0) return null;

        var vanishedFrom = 1;
        var vanishedTo = 0; // empty
        var fromLine = start.Line + 1;
        var delta = 0;
        if (removed > 0)
        {
            // A deletion joins the tail of its end line onto its start line. When something of the start line survives (start column > 0, or the
            // replacement text begins with code) it keeps its breakpoint and the end line's go; when nothing does, the surviving code is the end
            // line's, so ITS breakpoint moves up and the start line's goes.
            var keepsStart = start.Character > 0 || (change.Text.Length > 0 && change.Text[0] is not ('\n' or '\r'));
            if (keepsStart)
            {
                vanishedFrom = start.Line + 1;
                vanishedTo = end.Line;
                fromLine = end.Line + 1;
            }
            else
            {
                vanishedFrom = start.Line;
                vanishedTo = end.Line - 1;
                fromLine = end.Line;
            }
            delta = -removed;
        }
        if (inserted > 0)
        {
            if (removed == 0)
            {
                // the tail of start.Line moves down by `inserted` lines; when the insertion is whole lines at column 0 the line itself moves too
                var wholeLines = start.Character == 0 && change.Text.Length > 0 && change.Text[^1] is '\n' or '\r';
                fromLine = wholeLines ? start.Line : start.Line + 1;
            }
            delta += inserted;
        }
        return (vanishedFrom, vanishedTo, fromLine, delta);
    }

    private static int CountBreaks(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') n++;
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) n++;
        }
        return n;
    }
}
