using System.Text.Json;
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
}
