using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Debugger follow-ups fixed in the TypeScript/js-debug wave (docs/DEBUGGING-RUST-TS.md): the glyph margin's menu offers hit counts and
/// logpoints only when the active adapter supports them (netcoredbg has neither, js-debug both), and the editor reveals the stop line only
/// when it changes, however often the same location is announced.
/// </summary>
public class DebugGateAndRevealTests
{
    /// <summary>A stopped session with the given capabilities (the shared fake's are fixed).</summary>
    private sealed class CapableSession(DebugCapabilities capabilities, string type) : IDebugSession
    {
        private readonly FakeDebugSession _inner = new();
        public string Id => "capable";
        public string Name => type;
        public DebugConfiguration Configuration { get; } = new(type, "launch", type, new Dictionary<string, JsonElement>());
        public DebugSessionState State => DebugSessionState.Stopped;
        public IDebugSession? Parent => null;
        public DebugCapabilities Capabilities => capabilities;
        public int? FocusedThreadId => 1;
        public IEventSource<DebugStoppedEventArgs> Stopped => _inner.Stopped;
        public IEventSource<DebugContinuedEventArgs> Continued => _inner.Continued;
        public IEventSource<DebugOutputEventArgs> Output => _inner.Output;
        public IEventSource<DebugThreadEventArgs> ThreadChanged => _inner.ThreadChanged;
        public IEventSource<DebugSessionState> StateChanged => _inner.StateChanged;
        public IEventSource<DebugTerminatedEventArgs> Terminated => _inner.Terminated;
        public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => _inner.BreakpointStatusChanged;
        public IEventSource<IDebugSession> Invalidated => _inner.Invalidated;
        public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => _inner.GetThreadsAsync(ct);
        public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => _inner.GetStackTraceAsync(threadId, startFrame, levels, ct);
        public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => _inner.GetScopesAsync(frameId, ct);
        public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => _inner.GetVariablesAsync(variablesReference, start, count, ct);
        public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) => _inner.EvaluateAsync(expression, frameId, context, ct);
        public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => _inner.SetVariableAsync(variablesReference, name, value, ct);
        public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => _inner.CompletionsAsync(frameId, text, column, ct);
        public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RestartAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) => ValueTask.FromResult<JsonElement?>(null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (FakeDebugService Debug, ViewHost Host) DebugHost()
    {
        var debug = new FakeDebugService();
        return (debug, ViewHost.Create(s => s.AddSingleton<IDebugService>(debug)));
    }

    private static string[] Headers(ContextMenu menu) => [.. menu.Items.OfType<MenuItem>().Select(m => (string)m.Header!)];
    private static MenuItem Entry(ContextMenu menu, string prefix) => menu.Items.OfType<MenuItem>().Single(m => ((string)m.Header!).StartsWith(prefix, StringComparison.Ordinal));

    [AvaloniaFact]
    public async Task Hit_counts_and_logpoints_are_offered_only_when_the_active_adapter_supports_them()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n", "a.cs");
        var feature = view.Feature<DebugFeature>()!;
        debug.Manager.Add(doc.Uri, 1);
        await ViewHost.WaitFor(() => feature.BreakpointAt(1) is not null, "breakpoint shown");

        // no session yet: nothing is known, everything is offered (the session reports an unsupported kind itself)
        Assert.All(feature.BuildContextMenu(1).Items.OfType<MenuItem>(), m => Assert.True(m.IsEnabled));

        // netcoredbg-like: conditions only
        debug.ActiveSession = new CapableSession(new DebugCapabilities(SupportsConditionalBreakpoints: true), "coreclr");
        var menu = feature.BuildContextMenu(1);
        Assert.Equal(["Remove Breakpoint", "Disable Breakpoint", "Edit Condition…", "Edit Hit Count… (not supported)", "Edit Log Message… (not supported)"], Headers(menu));
        Assert.False(Entry(menu, "Edit Hit Count").IsEnabled);
        Assert.Equal("The 'coreclr' debug adapter does not support hit-count breakpoints.", ToolTip.GetTip(Entry(menu, "Edit Hit Count")));
        Assert.True(Entry(menu, "Edit Condition").IsEnabled);
        var empty = feature.BuildContextMenu(2);
        Assert.False(Entry(empty, "Add Logpoint").IsEnabled);
        Assert.Equal("The 'coreclr' debug adapter does not support logpoints.", ToolTip.GetTip(Entry(empty, "Add Logpoint")));

        // the edit commands themselves say why instead of asking for a value that would never work
        host.Window.InputResponses.Enqueue("5");
        await feature.EditHitCountAsync(1);
        await feature.EditLogMessageAsync(2);
        Assert.Equal("5", Assert.Single(host.Window.InputResponses)); // no input box consumed it
        host.Window.InputResponses.Clear();
        Assert.Contains(host.Window.Messages, m => m.Message == "The 'coreclr' debug adapter does not support hit-count breakpoints.");
        Assert.Contains(host.Window.Messages, m => m.Message == "The 'coreclr' debug adapter does not support logpoints.");
        Assert.Null(debug.Manager.GetForUri(doc.Uri).Single().HitCondition);

        // js-debug-like: both
        debug.ActiveSession = new CapableSession(new DebugCapabilities(SupportsConditionalBreakpoints: true, SupportsHitConditionalBreakpoints: true, SupportsLogPoints: true), "pwa-node");
        Assert.All(feature.BuildContextMenu(1).Items.OfType<MenuItem>(), m => Assert.True(m.IsEnabled));
        host.Window.InputResponses.Enqueue("> 3");
        await feature.EditHitCountAsync(1);
        Assert.Equal("> 3", debug.Manager.GetForUri(doc.Uri).Single().HitCondition);
    }

    [AvaloniaFact]
    public async Task The_stop_line_is_revealed_when_it_changes_not_every_time_the_same_line_is_announced()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync(string.Join('\n', Enumerable.Range(0, 300).Select(i => "line " + i)), "a.cs", height: 300);
        debug.Stop(doc.Uri, 200);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => view.TextView.VerticalOffset > 100, "revealed: scrolled to the stop line");

        // the user scrolls back to the top to read other code while stopped
        view.Controller.Reveal(TextRange.At(0, 0), center: false);
        await ViewHost.WaitFor(() => view.TextView.VerticalOffset == 0, "scrolled to the top");

        // the same stop announced again (a state change, a second notification of the same location): the view stays where the user put it
        ((Emitter<DebugState>)debug.StateChanged).Fire(DebugState.Stopped);
        debug.Stop(doc.Uri, 200);
        view.Controller.SetDebugStopLine(200);
        for (var i = 0; i < 5; i++) { ViewHost.Pump(); await Task.Delay(20); }
        Assert.Equal(0, view.TextView.VerticalOffset);
        Assert.Equal(200, view.Controller.StopLine);

        // a new stop after a resume is revealed again
        debug.Resume();
        ViewHost.Pump();
        Assert.Null(view.Controller.StopLine);
        debug.Stop(doc.Uri, 200);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => view.TextView.VerticalOffset > 100, "a new stop is revealed");
    }

    /// <summary>
    /// The code-action lightbulb never covers the breakpoint margin, so a click there always sets a breakpoint (integrator finding on a real
    /// display: it was drawn over the caret line's breakpoint glyph; then from the desktop: even without a breakpoint it sat in the glyph column,
    /// so the first click on the caret line opened the code actions). It goes into the text: the line's indentation, a blank or indented
    /// neighbouring line, else the folding column right against the text.
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_never_covers_the_breakpoint_margin()
    {
        var (debug, host, view) = await BulbHost("function f() {\n        return total;\n}\nconst x = 1;\nconst y = 2;\n\nconst z = 3;\n", "a.ts");
        using var _ = host;
        var bulb = view.Feature<CodeActionFeature>()!;

        // an indented line: in its indentation
        var indented = await BulbOn(view, 1, 10);
        Assert.True(indented.Right <= OverlayPopup.AnchorFor(view, new TextPosition(1, 8)).Top.X + 0.5, $"inside the indentation: {Layout(view)}");
        Assert.InRange(indented.Top, LineTop(view, 1) - 1, LineTop(view, 1) + 4);

        // a breakpoint there changes nothing: the bulb was never in its way
        debug.Manager.Add(view.Document.Uri, 1);
        await ViewHost.WaitFor(() => view.Feature<DebugFeature>()!.BreakpointAt(1) is not null, "breakpoint shown");
        Assert.Equal(indented, bulb.BulbBounds);

        // an unindented line under a blank one: on the blank line, where it covers no text
        var underBlank = await BulbOn(view, 6, 2);
        Assert.InRange(underBlank.Top, LineTop(view, 5) - 1, LineTop(view, 5) + 4);
        Assert.True(underBlank.Left >= TextViewRect(view).Left - 0.5, $"in the text column: {Layout(view)}");

        // an unindented line between unindented ones: in the folding column, right against the text
        var squeezed = await BulbOn(view, 3, 2);
        Assert.InRange(squeezed.Top, LineTop(view, 3) - 1, LineTop(view, 3) + 4);
        Assert.True(squeezed.Right <= TextViewRect(view).Left + 0.5, $"left of the text: {Layout(view)}");
    }

    /// <summary>
    /// With word wrap off and the caret at the end of a long line the text is scrolled to the left, column 0 and the indentation with it: the
    /// bulb is placed against the text view's edge, where the text shows, so it neither lands over the gutter nor leaves the editor (review: it
    /// was placed from column 0's position, which had scrolled over the glyph column of the caret line and past the editor's left edge).
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_stays_out_of_the_gutter_when_the_text_is_scrolled_sideways()
    {
        var call = "        return " + string.Join(" + ", Enumerable.Range(0, 80).Select(i => "value" + i)) + ";";
        var (_, host, view) = await BulbHost($"function f() {{\n    const a = 1;\n{call}\n    const b = 2;\n}}\n", "a.ts");
        using var _ = host;
        var end = OverlayPopup.AnchorFor(view, new TextPosition(2, call.Length)).Top.X - TextViewRect(view).Left;

        var b = await BulbOn(view, 2, call.Length, new Vector(end - TextViewRect(view).Width / 2, 0)); // the line's end in view, as End leaves it
        Assert.True(view.TextView.ScrollOffset.X > 200, $"scrolled sideways: {Layout(view)}");
        // the indentation of the caret line and of its neighbours is scrolled away: the folding column, by the caret line
        Assert.True(b.Right <= TextViewRect(view).Left + 0.5, $"left of the text: {Layout(view)}");
        AssertOnRow(view, b, 2, "scrolled sideways");
    }

    /// <summary>
    /// A line hidden in a collapsed fold is no place for the bulb: its position is its fold header's row, whose text the bulb covered (review:
    /// Python's <c>for x in xs:</c> collapsed, the caret on <c>print(total)</c> under it). Opening the fold moves the caret line down a row with
    /// the caret and the scroll offset unchanged: the bulb follows instead of staying over the text that is now where it was.
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_is_never_put_on_a_line_hidden_in_a_collapsed_fold()
    {
        var (_, host, view) = await BulbHost("for x in xs:\n    total += x\nprint(total)\n", "a.py");
        using var _ = host;
        var bulb = view.Feature<CodeActionFeature>()!;
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.Ranges.Contains((0, 1)), "the loop's fold");
        folding.FoldLines(EditorFoldAction.Close, 0, 0);
        PumpLayout();
        Assert.Equal([0], folding.FoldedStartLines);

        var folded = await BulbOn(view, 2, 3);
        var header = TextRect(view, 0);
        Assert.False(folded.Intersects(header), $"over the fold header's text {header}: {Layout(view)}");

        folding.UnfoldAll();
        PumpLayout();
        Assert.True(bulb.BulbVisible, $"still shown: {Layout(view)}");
        var unfolded = AssertBulbClearOfTheGutter(view, "unfolded");
        // the loop body is on screen again, right above the caret line: in its indentation
        AssertOnRow(view, unfolded, 1, "unfolded");
        Assert.True(unfolded.Right <= OverlayPopup.AnchorFor(view, new TextPosition(1, 4)).Top.X + 0.5, $"inside the indentation: {Layout(view)}");
    }

    /// <summary>
    /// The caret line is the first row in view: the blank line above it, where the bulb would go, is scrolled out, so the bulb goes on the blank
    /// line below (review: it went above the view, clipped, and still counted as shown).
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_is_never_put_on_a_row_outside_the_view()
    {
        // blank lines between the statements: line 101 is "const v50 = 50;", lines 100 and 102 are blank
        var (_, host, view) = await BulbHost(string.Concat(Enumerable.Range(0, 100).Select(i => $"\nconst v{i} = {i};\n")), "a.ts");
        using var _ = host;

        var b = await BulbOn(view, 101, 2, new Vector(0, view.TextView.GetVisualTopByDocumentLine(102)));
        Assert.InRange(LineTop(view, 101), TextViewRect(view).Top - 0.5, TextViewRect(view).Top + 0.5);
        Assert.True(b.Top >= TextViewRect(view).Top - 0.5 && b.Bottom <= TextViewRect(view).Bottom + 0.5, $"inside the view: {Layout(view)}");
        AssertOnRow(view, b, 102, "first row");
    }

    /// <summary>
    /// Down past the last row scrolls the caret line's bottom onto the view's, and that row is shorter than the 18 px bulb, which reaches below
    /// it: the bulb shows, kept inside the view, and hides only while the row is partly scrolled out (review: it was hidden on the last row,
    /// where the keyboard always leaves the caret line, because it reached 1.7 px below the view).
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_shows_on_the_last_row_the_keyboard_scrolls_to()
    {
        var (_, host, view) = await BulbHost(string.Concat(Enumerable.Range(0, 200).Select(i => $"const v{i} = {i};\n")), "a.ts");
        using var _ = host;
        var bulb = view.Feature<CodeActionFeature>()!;
        var window = (Window)TopLevel.GetTopLevel(view)!;
        view.Controller.SetSelection(TextRange.At(0, 2));
        PumpLayout();

        for (var i = 0; i < 200 && view.TextView.ScrollOffset.Y <= 0; i++)
        {
            ViewTestHelpers.Key(window, Avalonia.Input.Key.Down);
            PumpLayout();
        }
        var line = view.Controller.Caret.Line;
        var (top, bottom) = OverlayPopup.AnchorFor(view, new TextPosition(line, 0));
        Assert.InRange(bottom.Y, TextViewRect(view).Bottom - 0.5, TextViewRect(view).Bottom + 0.5);
        Assert.True(bottom.Y - top.Y < 18, $"a row shorter than the bulb ({top.Y}..{bottom.Y}): {Layout(view)}");

        await ViewHost.WaitFor(() => bulb.BulbVisible, $"lightbulb for line {line}, the last row");
        PumpLayout();
        var b = AssertBulbClearOfTheGutter(view, "last row");
        Assert.True(b.Top >= TextViewRect(view).Top - 0.5 && b.Bottom <= TextViewRect(view).Bottom + 0.5, $"inside the view: {Layout(view)}");
        AssertOnRow(view, b, line, "last row");

        // half the row scrolled out below: hidden; back in: shown where it was
        var offset = view.TextView.ScrollOffset;
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = offset - new Vector(0, (bottom.Y - top.Y) / 2);
        PumpLayout();
        Assert.False(bulb.BulbVisible, $"the caret line is partly out of view: {Layout(view)}");
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = offset;
        PumpLayout();
        Assert.True(bulb.BulbVisible, $"the caret line is back in view: {Layout(view)}");
        Assert.Equal(b, bulb.BulbBounds);
    }

    /// <summary>
    /// Turning line numbers off or on, or a new font size, lays the text out again with the caret and the scroll offset unchanged: the bulb in
    /// the folding column follows the text and the caret line's row (review: it stayed where it was, over the text once the numbers went, over
    /// the numbers once they came back).
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_follows_the_text_when_line_numbers_or_the_font_size_change()
    {
        var (_, host, view) = await BulbHost("const x = 1;\nconst y = 2;\nconst z = 3;\n", "a.ts");
        using var _ = host;
        var bulb = view.Feature<CodeActionFeature>()!;
        var before = await BulbOn(view, 1, 2);
        Assert.True(before.Right <= TextViewRect(view).Left + 0.5, $"left of the text: {Layout(view)}");

        host.Config.Set("editor.lineNumbers", "off");
        PumpLayout();
        Assert.Empty(view.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>());
        Assert.True(bulb.BulbVisible, $"still shown: {Layout(view)}");
        var off = AssertBulbClearOfTheGutter(view, "line numbers off");
        var glyphRight = InOverlay(view, view.Feature<DebugFeature>()!.Margin).Right;
        Assert.True(Math.Abs(off.Left - Math.Max(TextViewRect(view).Left - off.Width, glyphRight)) < 0.5, $"against the text, or the breakpoint margin: {Layout(view)}");
        AssertOnRow(view, off, 1, "line numbers off");

        host.Config.Set("editor.lineNumbers", "on");
        PumpLayout();
        var on = AssertBulbClearOfTheGutter(view, "line numbers on again");
        Assert.True(Math.Abs(on.Left - before.Left) < 0.5, $"back where it was ({before}): {Layout(view)}");

        host.Config.Set("editor.fontSize", 28);
        PumpLayout();
        var larger = AssertBulbClearOfTheGutter(view, "larger font");
        Assert.True(larger.Right <= TextViewRect(view).Left + 0.5, $"left of the text: {Layout(view)}");
        AssertOnRow(view, larger, 1, "larger font");
    }

    /// <summary>
    /// With line numbers off and a small font the folding column is narrower than the 18 px bulb: the bulb stops at the breakpoint margin's
    /// right edge instead of reaching into the glyph column (review: "right against the text" alone put it over the glyph column's last pixels).
    /// </summary>
    [AvaloniaFact]
    public async Task The_lightbulb_stops_at_the_breakpoint_margin_when_the_folding_column_is_narrower_than_the_bulb()
    {
        var (_, host, view) = await BulbHost("const x = 1;\nconst y = 2;\nconst z = 3;\n", "a.ts", new() { ["editor.lineNumbers"] = "off", ["editor.fontSize"] = 8 });
        using var _ = host;
        var glyphRight = InOverlay(view, view.Feature<DebugFeature>()!.Margin).Right;
        Assert.True(TextViewRect(view).Left - glyphRight < 18, $"a folding column narrower than the bulb: {Layout(view)}");

        var b = await BulbOn(view, 1, 2);
        Assert.True(Math.Abs(b.Left - glyphRight) < 0.5, $"against the breakpoint margin: {Layout(view)}");
        AssertOnRow(view, b, 1, "small font");
    }

    // ---- the lightbulb's surroundings ----

    /// <summary>A view with the breakpoint glyph margin and a code-action provider that always has an action: the bulb shows wherever the caret settles.</summary>
    private static async Task<(FakeDebugService Debug, ViewHost Host, CodeEditorControl View)> BulbHost(string text, string fileName, Dictionary<string, object>? settings = null)
    {
        var (debug, host) = DebugHost();
        foreach (var (key, value) in settings ?? []) host.Config.Set(key, value);
        var (_, view, _) = await host.OpenAsync(text, fileName);
        host.Factory.Services.Features.Register<CsCode.Core.Languages.ICodeActionProvider>(DocumentSelector.All,
            new FakeCodeActionProvider((_, _, _, _) => Task.FromResult<IReadOnlyList<CsCode.Core.Languages.CodeAction>?>(
                [new CsCode.Core.Languages.CodeAction("Fix it", "quickfix")])));
        return (debug, host, view);
    }

    /// <summary>
    /// Puts the caret at (<paramref name="line"/>, <paramref name="column"/>), revealed or with the view scrolled to <paramref name="scrollTo"/>
    /// instead, and returns where the bulb shows, checked to be clear of the gutter.
    /// </summary>
    private static async Task<Rect> BulbOn(CodeEditorControl view, int line, int column, Vector? scrollTo = null)
    {
        var bulb = view.Feature<CodeActionFeature>()!;
        view.Controller.SetSelection(TextRange.At(line, column), reveal: scrollTo is null);
        if (scrollTo is { } offset) ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = offset; // (TextEditor.ScrollToVerticalOffset ignores absolute positions)
        await ViewHost.WaitFor(() => bulb.BulbVisible, $"lightbulb for line {line}");
        PumpLayout();
        return AssertBulbClearOfTheGutter(view, $"line {line}");
    }

    /// <summary>The bulb is right of the breakpoint margin and of the line numbers; returns where it is.</summary>
    private static Rect AssertBulbClearOfTheGutter(CodeEditorControl view, string when)
    {
        var b = view.Feature<CodeActionFeature>()!.BulbBounds;
        Assert.True(b.Left >= InOverlay(view, view.Feature<DebugFeature>()!.Margin).Right - 0.5, $"{when}: over the breakpoint margin: {Layout(view)}");
        if (view.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().FirstOrDefault() is { } numbers)
            Assert.True(b.Left >= InOverlay(view, numbers).Right - 0.5, $"{when}: over the line numbers: {Layout(view)}");
        return b;
    }

    /// <summary>The bulb's middle is on <paramref name="line"/>'s row.</summary>
    private static void AssertOnRow(CodeEditorControl view, Rect bulb, int line, string when)
    {
        var (top, bottom) = OverlayPopup.AnchorFor(view, new TextPosition(line, 0));
        Assert.True(bulb.Center.Y >= top.Y && bulb.Center.Y <= bottom.Y, $"{when}: not on line {line}'s row ({top.Y}..{bottom.Y}): {Layout(view)}");
    }

    /// <summary>A layout pass and what it posts (the bulb is placed after the text view's layout).</summary>
    private static void PumpLayout()
    {
        for (var i = 0; i < 5; i++) ViewHost.Pump();
    }

    private static Rect InOverlay(CodeEditorControl view, Control c) => new(c.TranslatePoint(default, view.Overlay)!.Value, c.Bounds.Size);

    /// <summary>The text view in overlay coordinates: where text shows, however far it is scrolled.</summary>
    private static Rect TextViewRect(CodeEditorControl view) => InOverlay(view, view.TextView);

    private static double LineTop(CodeEditorControl view, int line) => OverlayPopup.AnchorFor(view, new TextPosition(line, 0)).Top.Y;

    /// <summary>The text of <paramref name="line"/> on screen, from column 0 to its end.</summary>
    private static Rect TextRect(CodeEditorControl view, int line) =>
        new(OverlayPopup.AnchorFor(view, new TextPosition(line, 0)).Top, OverlayPopup.AnchorFor(view, new TextPosition(line, view.Document.GetLineText(line).Length)).Bottom);

    private static string Layout(CodeEditorControl view)
    {
        var bulb = view.Feature<CodeActionFeature>()!;
        var margins = view.TextArea.LeftMargins.OfType<Control>().Select(m => $"{m.GetType().Name} {InOverlay(view, m)}");
        return $"bulb {bulb.BulbBounds} (shown: {bulb.BulbVisible}); margins {string.Join(", ", margins)}; text view {TextViewRect(view)} scrolled {view.TextView.ScrollOffset}";
    }
}
