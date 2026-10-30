using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Exceptions;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// The Debug panels over the REAL DAP module and the REAL CodeLLDB (v1.12.3, <c>lldb</c>) debugging the same Rust program as
/// <see cref="RealGdbPanelsTests"/>: what the Call Stack, Locals, Watch, Breakpoints, Exception and Debug Console windows show, recorded as
/// evidence for the CodeLLDB column of docs/DEBUGGING-RUST-TS.md (<c>CSCODE_DAP_TEST_METRICS=&lt;file&gt;</c>). Skips with a reason when
/// CodeLLDB (tools/fetch-codelldb.sh or CSCODE_CODELLDB), cargo or rustc is missing.
/// </summary>
[Trait("Category", "RealAdapter")]
public class RealCodeLldbPanelsTests
{
    private static void Note(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path)
            lock (typeof(RealCodeLldbPanelsTests)) File.AppendAllText(path, "[panels/codelldb] " + text + "\n");
    }

    private static string Rows(CallStackViewModel cs) => string.Join(" | ", cs.Rows.Select(r => $"{r.Kind}:{r.Text}@{r.Location}{(r.IsExternal ? " (ext)" : "")}{(r.IsCurrent ? " <=" : "")}"));
    private static string Rows(VariablesTreeViewModel tree) => string.Join(" | ", tree.Rows.Select(r => $"{new string('.', Math.Max(0, r.Depth))}{r.Name}={r.DisplayValue}{(r.HasChildren ? "+" : "")}{(r.IsError ? "!" : "")}"));
    private static string Here(CallStackViewModel cs) => cs.Rows.FirstOrDefault(r => r.IsCurrent)?.Location ?? "";

    /// <summary><see cref="PanelRig.Eventually"/>, saying what the panels showed when it timed out.</summary>
    private static async Task Within(Func<bool> condition, int timeoutMs, Func<string> what)
    {
        try { await PanelRig.Eventually(condition, timeoutMs); }
        catch (TimeoutException) { throw new TimeoutException("Condition not reached in time: " + what()); }
    }

    [AvaloniaFact(Timeout = 240_000)]
    public async Task The_panels_show_a_rust_session_through_codelldb()
    {
        await using var app = await RealGdbPanelsTests.App.CreateAsync(CodeLldbAdapterFactory.PrimaryType);
        var debug = app.Debug;
        app.PanicStops(false);
        debug.Breakpoints.Add(app.MainUri, RustSample.MainLine("call"));
        debug.AddWatch("p.x + p.y");

        var breakpoints = app.View<BreakpointsViewModel>("debug.breakpoints");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        var locals = app.View<FrameVariablesViewModel>("debug.locals").Tree;
        var watch = app.View<WatchViewModel>("debug.watch").Tree;
        var console = app.View<DebugConsoleViewModel>("debug.console");
        try
        {
            _ = await debug.StartAsync(app.Config()) ?? throw new InvalidOperationException("no session: " + string.Join(" | ", app.Window.StatusMessages));
            await Within(() => Here(callStack) == $"main.rs : {RustSample.MainLine("call") + 1}", 60_000, () => "stopped at @call; call stack: " + Rows(callStack));
            Note("call stack: " + Rows(callStack));
            Note("breakpoints window: " + string.Join(" | ", breakpoints.Rows.Select(r => $"{r.Kind} {r.Label} {r.Verification}")));
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "big") && locals.Rows.Any(r => r.Name == "wrapper"), 20_000, "locals of main");
            Note("locals in main: " + Rows(locals));
            Assert.Equal("51", locals.Rows.First(r => r.Name == "x" && r.Depth <= 0).DisplayValue);

            // a vector of 250 through the pager: CodeLLDB ignores paging (it answers with every child), so the session pages for it; before,
            // "… more" brought the first page again and the window stopped at [99]
            var big = locals.Rows.First(r => r.Name == "big");
            await locals.ToggleAsync(big);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == big), 20_000, "first page of big");
            for (var page = 0; page < 5 && locals.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More); page++)
                await locals.LoadMoreAsync(locals.Rows.First(r => r.Parent == big && r.Kind == VariableRowKind.More));
            var elements = locals.Rows.Where(r => r.Parent == big && r.Kind == VariableRowKind.Variable).ToList();
            Note($"big after paging: {elements.Count} rows, last {elements[^1].Name}={elements[^1].DisplayValue}");
            Assert.Equal(Enumerable.Range(0, 250).Select(i => $"[{i}]").Append("[raw]"), elements.Select(r => r.Name));
            Assert.Equal("498", elements[^2].DisplayValue);
            await locals.ToggleAsync(big);
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "p.x + p.y" && r.DisplayValue == "7"), 20_000, "watch");

            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text == "loop 4"), 20_000, "program output");
            Note("console before the REPL: " + string.Join(" | ", console.Lines.Select(l => $"{l.Kind}:{l.Text}")));
            console.Input = "x * 2";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text == "102"), 20_000, "REPL expression");
            console.Input = "-exec bt";
            await console.SubmitAsync();
            // CodeLLDB answers a command with an empty result and prints its output as console output: the backtrace follows the input line
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Console && l.Text.Contains("frame #0", StringComparison.Ordinal)), 20_000, "the backtrace");
            Note("REPL: " + string.Join(" | ", console.Lines.Where(l => l.Kind is ConsoleLineKind.Input or ConsoleLineKind.Result or ConsoleLineKind.ResultError).Select(l => $"{l.Kind}:{l.Text}")));
            Assert.Contains(console.Lines, l => l.Text.Contains("sample`sample::main at main.rs", StringComparison.Ordinal));

            // completions of an expression (LLDB's frame variable completion), accepted as the console accepts them
            console.Input = "wrapper.po";
            await console.RequestCompletionsAsync(console.Input.Length);
            Note("completions of 'wrapper.po': " + string.Join(", ", console.Completions.Select(c => $"{c.Label}@{c.Start}+{c.Length}")));
            console.AcceptCompletion(Assert.Single(console.Completions));
            Assert.Equal("wrapper.point", console.Input);
            console.Input = "";

            debug.Breakpoints.RemoveAll();
            await Task.Delay(300);
            await debug.StopLocation!.Session.ContinueAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 60_000, "session ended");
            Note("console at the end: " + string.Join(" | ", console.Lines.TakeLast(12).Select(l => $"{l.Kind}:{l.Text}")));
            await PanelRig.Eventually(() => locals.Rows.Count == 0 && callStack.Rows.Count == 0, 10_000, "panels cleared");
        }
        finally
        {
            foreach (var t in new IDisposable[] { breakpoints, callStack, console }) t.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_rust_panic_shows_in_the_exception_window_at_the_users_frame()
    {
        await using var app = await RealGdbPanelsTests.App.CreateAsync(CodeLldbAdapterFactory.PrimaryType); // "Rust: on panic" on by default
        var debug = app.Debug;
        var exception = app.View<ExceptionHelperViewModel>("debug.exception");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        try
        {
            await debug.StartAsync(app.Config("panic"));
            await PanelRig.Eventually(() => exception.HasException, 60_000, "exception shown");
            Note($"exception window: title '{exception.Title}', type '{exception.Details!.TypeName}', message '{exception.Details.Message}', stack: {exception.Details.StackTrace}");
            Assert.Equal("boom at the bottom", exception.Details.Message);
            await Within(() => Here(callStack) == $"util.rs : {RustSample.UtilLine("panic") + 1}", 20_000, () => "the arrow on the user's frame; call stack: " + Rows(callStack));
            Note("call stack at the panic: " + Rows(callStack));
            await debug.StopAllAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 30_000, "session ended");
        }
        finally
        {
            callStack.Dispose();
            exception.Dispose();
        }
    }

    /// <summary>
    /// Break All: CodeLLDB reports the stop of a pause as the exception "signal SIGSTOP" (LLDB halts the process with SIGSTOP). The session
    /// reports it as a pause, so the Exception window stays closed, and the arrow is on the user's frame (libc's sleep and std are external).
    /// </summary>
    [AvaloniaFact(Timeout = 180_000)]
    public async Task Break_all_shows_the_users_frame_and_no_exception()
    {
        await using var app = await RealGdbPanelsTests.App.CreateAsync(CodeLldbAdapterFactory.PrimaryType);
        var debug = app.Debug;
        var exception = app.View<ExceptionHelperViewModel>("debug.exception");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        try
        {
            var session = await debug.StartAsync(app.Config("serve")) ?? throw new InvalidOperationException("no session: " + string.Join(" | ", app.Window.StatusMessages));
            await PanelRig.Eventually(() => session.State == DebugSessionState.Running, 60_000, "running");
            await Task.Delay(500);
            await session.PauseAsync();
            await Within(() => Here(callStack) == $"main.rs : {RustSample.MainLine("tick") + 1}" || Here(callStack) == $"main.rs : {RustSample.MainLine("tick") + 2}", 30_000,
                () => "the arrow in serve(); call stack: " + Rows(callStack));
            Note($"break all: stop reason '{debug.StopLocation?.Reason}'; call stack: {Rows(callStack)}");
            Assert.Equal("pause", debug.StopLocation!.Reason);
            await Task.Delay(500); // an exceptionInfo answer would have arrived by now
            Assert.False(exception.HasException, $"exception window: {exception.Title} {exception.Details?.Message}");
            await debug.StopAllAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 30_000, "session ended");
        }
        finally
        {
            callStack.Dispose();
            exception.Dispose();
        }
    }
}
