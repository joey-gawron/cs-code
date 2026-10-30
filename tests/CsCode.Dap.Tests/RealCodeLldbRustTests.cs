using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The Rust evaluation matrix (docs/DEBUGGING-RUST-TS.md, "Rust through CodeLLDB") against the REAL CodeLLDB v1.12.3 through the real
/// <see cref="CsCode.Dap.Service.DebugService"/>, row for row beside <see cref="RealGdbRustTests"/>: the same cargo sample, CodeLLDB's adapter
/// started by the built-in <see cref="CodeLldbAdapterFactory"/> on stdio. Skips with an explicit reason when CodeLLDB (tools/fetch-codelldb.sh or
/// CSCODE_CODELLDB), cargo or rustc is missing. Assertions are what the matrix decides on (stops, lines, values a user reads, processes); how
/// CodeLLDB renders everything else is recorded as evidence (<c>CSCODE_DAP_TEST_METRICS=&lt;file&gt;</c>), as the GDB rows were.
/// </summary>
[Trait("Category", CodeLldbToolchain.Category)]
[Collection(SerialCollection.Name)]
public class RealCodeLldbRustTests
{
    private const string Lldb = CodeLldbAdapterFactory.PrimaryType;
    private static int InMain(string marker) => RustSample.MainLine(marker);
    private static int Util(string marker) => RustSample.UtilLine(marker);
    private static void Note(string text) => TestMetrics.Report("[codelldb] " + text);

    private static Task<RealGdbRig> RigAsync(RustSample? sample = null) => RealGdbRig.CreateAsync(sample, type: Lldb);

    private static void AssertAt(DebugStopLocation loc, string file, int line)
    {
        Assert.Equal(file, Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(line, loc.Line);
    }

    private static async Task ContinueAsync(RealGdbRig rig) => await rig.Service.StopLocation!.Session.ContinueAsync(ct: Ct);

    private static async Task<string> ValueOfAsync(RealGdbRig rig, string expression)
    {
        var loc = rig.Service.StopLocation!;
        return (await loc.Session.EvaluateAsync(expression, loc.FrameId, "watch", Ct)).Result;
    }

    private static string Frames(IReadOnlyList<DebugStackFrame> frames) =>
        string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path) ?? f.Source?.Name}:{f.Line + 1} ref={f.Source?.SourceReference} {f.Source?.PresentationHint}{f.PresentationHint}"));

    /// <summary>
    /// The program's pid. CodeLLDB sends no DAP <c>process</c> event (its sources have none; it prints "Launched process &lt;pid&gt;" instead),
    /// so <see cref="DebugSession.DebuggeeProcessId"/> stays empty: the program is found under the adapter (codelldb → lldb-server → program).
    /// </summary>
    private static int DebuggeeOf(DebugSession session, RealGdbRig rig)
    {
        Note($"DebuggeeProcessId from a process event: {session.DebuggeeProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)"}");
        if (session.DebuggeeProcessId is { } pid) return (int)pid;
        var program = ProcessTable.Descendants(session.AdapterProcessId!.Value).First(p => ProcessTable.CommandLine(p).Contains(rig.Sample.Binary, StringComparison.Ordinal));
        lock (rig.DebuggeePids) if (!rig.DebuggeePids.Contains(program)) rig.DebuggeePids.Add(program);
        return program;
    }

    // ------------------------------------------------------------------ launch, breakpoints, clean exit

    [Fact]
    public async Task Launch_stops_at_a_breakpoint_and_exits_cleanly_with_codelldb_below_normal_priority()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false); // the sample catches a panic on its way to the end
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var session = await rig.StartAsync();
        Note($"capabilities: exception filters {string.Join(", ", session.Capabilities.ExceptionBreakpointFilters?.Select(f => $"{f.Filter}='{f.Label}' default={f.Default}") ?? [])}");
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("breakpoint", rig.LastStop.Reason);
        AssertAt(loc, "main.rs", InMain("loop"));
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true }, 10_000, "verified breakpoint");
        Note($"verdict: {rig.H.Breakpoints.GetStatus(bp.Id)}");

        var adapter = session.AdapterProcessId!.Value;
        var debuggee = DebuggeeOf(session, rig);
        Note($"codelldb pid {adapter} nice={RealGdbRig.NiceOf(adapter)} ({ProcessTable.CommandLine(adapter)}); debuggee pid {debuggee} nice={RealGdbRig.NiceOf(debuggee)}; tree {string.Join(", ", ProcessTable.Descendants(adapter).Select(p => $"{p}:{ProcessTable.CommandLine(p)}"))}");
        Assert.Equal(10, RealGdbRig.NiceOf(adapter));
        Assert.Equal(10, RealGdbRig.NiceOf(debuggee)); // launched through lldb-server, which inherits the lower priority
        Assert.Contains(debuggee, ProcessTable.Descendants(adapter));
        Assert.Contains(debuggee, ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", session.AdapterProcessMarker!));

        var (frames, s) = await rig.TopFramesAsync();
        Note("frames: " + Frames(frames));
        Assert.Contains("main", frames[0].Name, StringComparison.Ordinal);
        var locals = await rig.LocalsAsync(frames[0].Id, s);
        Note("locals: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} ({v.Type})")));
        Assert.Equal("41", locals["x"].Value);
        Assert.Equal("0", locals["i"].Value);

        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Note("output: " + rig.OutputText.Replace("\n", "\\n"));
        Note("console: " + rig.OutputOf("console").Replace("\n", "\\n"));
        Assert.Contains("done hello world 3 2 Some(5) None Ok(12)", rig.OutputText, StringComparison.Ordinal);
        Assert.Equal(0, rig.Terminated.Single().ExitCode);
        Assert.Equal(DebugState.Inactive, rig.Service.State);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_breakpoint_on_a_line_without_code_moves_to_the_next_statement()
    {
        await using var rig = await RigAsync();
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("comment"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note($"comment-line breakpoint: requested line {InMain("comment") + 1}, stopped at {loc.Line + 1}, verdict {rig.H.Breakpoints.GetStatus(bp.Id)}");
        AssertAt(loc, "main.rs", InMain("comment") + 1);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Conditional_hit_count_and_log_breakpoints()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        // @loop is one statement (one location); a println! line may have two (GDB stops twice per pass there): the condition goes on it, and
        // every stop it makes is counted rather than assumed.
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), hitCondition: "2"); // sent as "=2": the second hit only
        rig.H.Breakpoints.Add(rig.MainUri, InMain("print"), condition: "i == 3");
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"), logMessage: "logged x={x}");
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note($"hit count '2': stop at {loc.Line + 1}, i={await ValueOfAsync(rig, "i")}");
        AssertAt(loc, "main.rs", InMain("loop"));
        Assert.Equal("1", await ValueOfAsync(rig, "i")); // the second hit only
        await ContinueAsync(rig);
        loc = await rig.WaitForStopAsync(2);
        AssertAt(loc, "main.rs", InMain("print"));
        Assert.Equal("3", await ValueOfAsync(rig, "i"));
        var printStops = 1;
        while (true)
        {
            await ContinueAsync(rig);
            await SessionHarness.Eventually(() => rig.StopCount > printStops + 1 || rig.Service.Sessions.Count == 0, 60_000, "the next stop or the end");
            if (rig.Service.Sessions.Count == 0) break;
            loc = await rig.WaitForStopAsync(printStops + 2);
            AssertAt(loc, "main.rs", InMain("print"));
            Assert.Equal("3", await ValueOfAsync(rig, "i")); // only i == 3, however many locations the line has
            printStops++;
        }
        await rig.WaitForEndAsync();
        Note($"stops: {rig.StopCount} ({printStops} at the println! line); console: {rig.OutputOf("console").Replace("\n", "\\n")}");
        Assert.InRange(printStops, 1, 2);
        Assert.Contains("logged x=51", rig.OutputText, StringComparison.Ordinal); // and no stop at the logpoint
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Function_breakpoints_stop_on_a_rust_path()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        var fn = rig.H.Breakpoints.AddFunction("sample::util::add");
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note($"function breakpoint: stop at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}, verdict {rig.H.Breakpoints.GetStatus(fn.Id)}");
        Assert.Equal("util.rs", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.InRange(loc.Line, Util("add") - 1, Util("add"));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ panics

    [Fact]
    public async Task Rust_panics_stop_as_exceptions_with_the_panic_message_and_an_uncaught_one_ends_the_program()
    {
        await using var rig = await RigAsync(); // CodeLLDB's "Rust: on panic" is on by default
        var session = await rig.StartAsync(rig.Config(args: ["panic"]));
        Assert.Contains(session.Capabilities.ExceptionBreakpointFilters!, f => f.Filter == CodeLldbAdapterQuirks.RustPanicFilter);

        var loc = await rig.WaitForStopAsync(1); // the panic the sample catches
        var (frames, s) = await rig.TopFramesAsync();
        Note($"panic stop: reason={rig.LastStop.Reason} description='{rig.LastStop.Description}' text='{rig.LastStop.Text}' at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}");
        Note("panic frames: " + Frames(frames));
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(loc, "util.rs", Util("panic")); // the user's frame, not std's rust_panic
        var body = await s.CustomRequestAsync("exceptionInfo", JsonSerializer.SerializeToElement(new { threadId = loc.ThreadId }), Ct);
        Note("exceptionInfo: " + body?.GetRawText());
        Assert.Equal("boom at the bottom", body!.Value.GetProperty("description").GetString());

        await ContinueAsync(rig);
        loc = await rig.WaitForStopAsync(2); // the uncaught one
        AssertAt(loc, "util.rs", Util("panic"));
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Note($"uncaught panic: exit code {rig.Terminated.Single().ExitCode}");
        Assert.Equal(101, rig.Terminated.Single().ExitCode);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Start_without_debugging_and_panics_off_run_the_program_to_its_end()
    {
        await using var rig = await RigAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync(rig.Config() with { NoDebug = true });
        await rig.WaitForEndAsync();
        Note($"noDebug: {rig.StopCount} stops, exit code {rig.Terminated.Single().ExitCode}");
        Assert.Equal(0, rig.StopCount);
        Assert.Equal(0, rig.Terminated.Single().ExitCode);

        lock (rig.Terminated) rig.Terminated.Clear();
        lock (rig.Started) rig.Started.Clear();
        rig.H.Breakpoints.RemoveAll();
        rig.PanicStops(false);
        await rig.StartAsync(rig.Config(args: ["panic"]));
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
        Assert.Equal(101, rig.Terminated.Single().ExitCode);
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ execution control

    [Fact]
    public async Task Step_into_over_and_out_across_files()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "main.rs", InMain("call"));

        await loc.Session.StepIntoAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(2);
        Assert.Equal("step", rig.LastStop.Reason);
        AssertAt(loc, "util.rs", Util("add"));
        var (frames, _) = await rig.TopFramesAsync();
        Note("in add: " + Frames(frames));

        await loc.Session.StepOverAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(3);
        Note($"step over from util.rs:{Util("add") + 1} -> util.rs:{loc.Line + 1}");
        Assert.InRange(loc.Line, Util("ret"), Util("ret") + 1);

        await loc.Session.StepOutAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(4);
        AssertAt(loc, "main.rs", InMain("call"));
        var session = (DebugSession)loc.Session;
        var scopes = await session.GetScopesAsync(loc.FrameId, Ct);
        Note("scopes after step out: " + string.Join(" | ", scopes.Select(sc => $"{sc.Name} hint={sc.PresentationHint}")));
        foreach (var scope in scopes)
            Note($"scope {scope.Name}: " + string.Join(", ", (await session.GetVariablesAsync(scope.VariablesReference, ct: Ct)).Take(8).Select(v => $"{v.Name}={v.Value}")));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Pause_stop_while_running_and_restart()
    {
        await using var rig = await RigAsync();
        var session = await rig.StartAsync(rig.Config(args: ["serve"]));
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running, 30_000, "running");
        await Task.Delay(500, Ct);
        await session.PauseAsync(ct: Ct);
        var loc = await rig.WaitForStopAsync(1);
        var (frames, _) = await rig.TopFramesAsync();
        Note($"pause: reason {rig.LastStop.Reason}; stop location {loc.Uri}:{loc.Line + 1}; frames: {Frames(frames)}");
        Assert.Equal("pause", rig.LastStop.Reason);
        Assert.Equal("main.rs", Path.GetFileName(loc.Uri?.LocalPath)); // libc's sleep (disassembly) and std are de-emphasised: serve() is shown
        Assert.InRange(loc.Line, InMain("tick"), InMain("tick") + 1);

        Note($"restart supported: {session.Capabilities.SupportsRestartRequest}");
        var firstAdapter = session.AdapterProcessId;
        var started = Stopwatch.GetTimestamp();
        await session.RestartAsync(Ct);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running, 30_000, "running again");
        Note($"restart: running again after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms, adapter {firstAdapter} -> {session.AdapterProcessId}");
        Assert.Empty(rig.Terminated);

        started = Stopwatch.GetTimestamp();
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        Note($"stop while running: every session gone after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task An_adapter_crash_ends_the_session_and_leaves_no_debuggee()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var session = await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var adapter = session.AdapterProcessId!.Value;
        var debuggee = DebuggeeOf(session, rig);
        using (var p = Process.GetProcessById(adapter)) p.Kill();
        await rig.WaitForEndAsync();
        Note($"adapter crash: terminated with '{rig.Terminated.Single().Reason}'; debuggee {debuggee} alive afterwards: {ProcessTable.IsAlive(debuggee)}");
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ call stack, values

    [Fact]
    public async Task Generic_functions_and_closures_appear_in_the_call_stack()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("apply"));
        rig.H.Breakpoints.Add(rig.UtilUri, Util("generic"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        var (frames, _) = await rig.TopFramesAsync();
        Note("generic caller: " + Frames(frames));
        Assert.Contains("apply", frames[0].Name, StringComparison.Ordinal);

        await loc.Session.StepIntoAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(2);
        (frames, _) = await rig.TopFramesAsync();
        Note("closure: " + Frames(frames));
        AssertAt(loc, "main.rs", InMain("closure"));

        await ContinueAsync(rig);
        loc = await rig.WaitForStopAsync(3);
        (frames, _) = await rig.TopFramesAsync();
        Note("generic function: " + Frames(frames));
        Assert.Contains("describe", frames[0].Name, StringComparison.Ordinal);
        AssertAt(loc, "util.rs", Util("generic"));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Locals_show_rust_values_with_the_toolchains_lldb_formatters()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("done"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Note("values: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} [{v.Type}] ref={v.VariablesReference} named={v.NamedVariables} indexed={v.IndexedVariables}")));
        foreach (var name in new[] { "v", "map", "some", "ok", "err", "shape", "p", "boxed", "shared", "pair", "wrapper" })
        {
            if (!locals.TryGetValue(name, out var variable) || variable.VariablesReference <= 0) continue;
            var children = await session.GetVariablesAsync(variable.VariablesReference, ct: Ct);
            Note($"{name} children: " + string.Join(", ", children.Take(12).Select(c => $"{c.Name}={c.Value}")));
        }
        // what a user reads, whatever the exact rendering (the enums', structs' and collections' renderings are recorded above for the matrix)
        Assert.Contains("hello", locals["s"].Value, StringComparison.Ordinal);
        Assert.Contains("world", locals["text"].Value, StringComparison.Ordinal);
        Assert.Equal("51", locals["x"].Value);
        var big = locals["big"];
        var page = await session.GetVariablesAsync(big.VariablesReference, "indexed", 240, 10, Ct);
        Note($"big: {big.Value} indexed={big.IndexedVariables}; [240..250): {string.Join(", ", page.Select(c => $"{c.Name}={c.Value}"))}");
        Assert.Contains(page, c => c.Value == "498");
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Watch_set_variable_and_the_debug_console_in_evaluate_mode()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        var started = await rig.StartAsync();
        Note($"launch arguments: _adapterSettings={(started.Configuration.Properties.TryGetValue("_adapterSettings", out var settings) ? settings.GetRawText() : "(none)")}");
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        Assert.Equal("102", (await session.EvaluateAsync("x * 2", loc.FrameId, "watch", Ct)).Result);
        Assert.Equal("7", (await session.EvaluateAsync("p.x + p.y", loc.FrameId, "watch", Ct)).Result);
        var watchError = await Assert.ThrowsAsync<DapRequestException>(async () => await session.EvaluateAsync("nope", loc.FrameId, "watch", Ct));
        Note("watch error: " + watchError.Message);

        async Task<string> Repl(string text) => (await session.EvaluateAsync(text, loc.FrameId, "repl", Ct)).Result;
        Assert.Equal("102", await Repl("x * 2")); // an expression: the console is in evaluate mode
        var bt = await Repl("-exec bt");
        Note("-exec bt: " + bt.Replace("\n", "\\n"));
        Assert.Contains("main", bt, StringComparison.Ordinal);
        Note("`frame variable x: " + (await Repl("`frame variable x")).Replace("\n", "\\n"));
        var completions = await session.CompletionsAsync(loc.FrameId, "p.", 2, Ct);
        Note("completions of 'p.': " + string.Join(", ", completions.Select(c => $"{c.Label}=>{c.Text}@{c.Start}+{c.Length}")));

        var scope = (await session.GetScopesAsync(loc.FrameId, Ct)).First();
        var set = await session.SetVariableAsync(scope.VariablesReference, "x", "100", Ct);
        Note($"set x: {set.Value}");
        Assert.Equal("100", (await session.EvaluateAsync("x", loc.FrameId, "watch", Ct)).Result);
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Contains("err 101", rig.OutputText, StringComparison.Ordinal);
        Note("categories: " + string.Join(", ", rig.OutputSnapshot.Select(o => o.Category).Distinct()) + $"; stderr: '{rig.OutputOf("stderr").Replace("\n", "\\n")}'");
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ threads, attach, cargo

    [Fact]
    public async Task Threads_of_std_thread_are_listed_and_stop_in_the_worker()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("worker"));
        await rig.StartAsync(rig.Config(args: ["threads"]));
        var loc = await rig.WaitForStopAsync(1);
        var threads = await ((DebugSession)loc.Session).GetThreadsAsync(Ct);
        var (frames, _) = await rig.TopFramesAsync();
        Note("threads: " + string.Join(", ", threads.Select(t => $"{t.Id}={t.Name}")) + "; worker frames: " + Frames(frames));
        Assert.InRange(threads.Count, 2, 3);
        Assert.Contains("worker", frames[0].Name, StringComparison.Ordinal);
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Contains("threads done 96", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Attach_to_a_running_rust_program_by_pid_and_detach_leaves_it_running()
    {
        Assert.SkipWhen(CodeLldbToolchain.SkipReason() is not null, CodeLldbToolchain.SkipReason() ?? "");
        var scope = File.Exists("/proc/sys/kernel/yama/ptrace_scope") ? File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim() : "0";
        Assert.SkipWhen(scope != "0", $"kernel.yama.ptrace_scope is {scope}: LLDB may not attach to a process it did not start");
        await using var rig = await RigAsync();
        using var program = Process.Start(new ProcessStartInfo(rig.Sample.Binary, "serve") { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(300, Ct);
            rig.H.Breakpoints.Add(rig.MainUri, InMain("tick"));
            var config = RealGdbRig.Raw(new { type = Lldb, request = "attach", name = "attach", program = rig.Sample.Binary, pid = program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, rig.Sample.Dir.Uri);
            var session = await rig.StartAsync(config);
            Assert.True(session.IsAttach);
            var loc = await rig.WaitForStopAsync(1);
            Note($"attach: first stop reason '{rig.LastStop.Reason}' at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}");
            Assert.Equal("main.rs", Path.GetFileName(loc.Uri?.LocalPath));
            await rig.Service.StopAllAsync(Ct);
            await rig.WaitForEndAsync();
            await SessionHarness.Eventually(() => session.AdapterProcessId is not { } adapter || !ProcessTable.IsAlive(adapter), 10_000, "codelldb exits");
            Assert.False(program.HasExited, "detaching left the program running");
        }
        finally
        {
            try { program.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public async Task The_cargo_key_builds_the_test_binary_and_a_breakpoint_in_a_unit_test_hits()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.UtilUri, Util("test"));
        var config = RealGdbRig.Raw(new
        {
            type = Lldb, request = "launch", name = "tests",
            cargo = new { args = new[] { "test", "--no-run", "--offline" }, filter = new { kind = "bin" }, env = new { CARGO_TARGET_DIR = rig.Sample.Dir.Combine("target") } },
            args = Array.Empty<string>(), cwd = rig.Sample.Dir.Path,
        }, rig.Sample.Dir.Uri);
        var session = await rig.StartAsync(config);
        Assert.Equal(rig.Sample.TestBinary, session.Configuration.GetString("program"));
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "util.rs", Util("test"));
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Contains("test result: ok. 1 passed", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task The_codelldb_template_builds_through_its_preLaunchTask_and_stops_in_main()
    {
        Assert.SkipWhen(CodeLldbToolchain.SkipReason() is not null, CodeLldbToolchain.SkipReason() ?? "");
        using var fresh = RustSample.Write("rustlldb-template-");
        await using var rig = await RigAsync(fresh);
        rig.PanicStops(false);
        var launchJson = await rig.H.Configurations.CreateLaunchJsonAsync(Lldb, fresh.Dir.Uri, Ct);
        Note("generated launch.json: " + File.ReadAllText(launchJson.LocalPath).Replace("\n", " ", StringComparison.Ordinal));
        var configs = await rig.H.Configurations.GetLaunchConfigurationsAsync(fresh.Dir.Uri, Ct);
        rig.H.Breakpoints.Add(new Uri(fresh.MainRs), InMain("loop"));
        await rig.StartAsync(configs[0]);
        Assert.True(File.Exists(fresh.Binary), "the preLaunchTask ran cargo build");
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(fresh.MainRs, loc.Uri?.LocalPath);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ flood, soak

    [Fact]
    public async Task A_million_line_flood_ends_with_its_last_line_and_bounded_memory()
    {
        await using var rig = await RigAsync();
        rig.OutputLimit = 50;
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var started = Stopwatch.GetTimestamp();
        var session = await rig.StartAsync(rig.Config(args: ["flood", "1000000"]));
        await rig.WaitForEndAsync(600_000);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        GC.Collect();
        var heapAfter = GC.GetTotalMemory(true);
        var history = rig.Service.GetOutputHistory();
        Note($"1M-line flood: {seconds:0.0} s, {rig.OutputEvents} output batches delivered, {session.DroppedOutputChars:N0} chars dropped by design, heap {heapBefore / 1024 / 1024.0:0.0} -> {heapAfter / 1024 / 1024.0:0.0} MB, last output '{rig.OutputOf("stdout")[^Math.Min(40, rig.OutputOf("stdout").Length)..].Replace("\n", "\\n")}'");
        Assert.Contains(history, h => h.Output.Contains("flood done", StringComparison.Ordinal));
        Assert.InRange(heapAfter - heapBefore, long.MinValue, 64L * 1024 * 1024);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Ten_real_sessions_leave_no_processes_or_retained_sessions()
    {
        await using var rig = await RigAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var weak = await RunSessionsAsync(rig, 10);
        Assert.Equal(10, weak.Count);
        await rig.AssertNothingLeftRunningAsync();
        for (var i = 0; i < 10 && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, Ct);
        }
        Assert.DoesNotContain(weak, w => w.TryGetTarget(out _));
        Note($"10-session soak: {weak.Count} sessions collected, {rig.AdapterPids.Count} codelldb processes all gone");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference<IDebugSession>>> RunSessionsAsync(RealGdbRig rig, int count)
    {
        var weak = new List<WeakReference<IDebugSession>>();
        for (var i = 0; i < count; i++)
        {
            await rig.StartAsync(rig.Config("soak" + i));
            await rig.WaitForStopAsync(i + 1);
            var (frames, session) = await rig.TopFramesAsync();
            Assert.Equal("41", (await rig.LocalsAsync(frames[0].Id, session))["x"].Value);
            await rig.Service.StopAllAsync(Ct);
            await rig.WaitForEndAsync();
        }
        lock (rig.Started)
        {
            weak.AddRange(rig.Started.Select(s => new WeakReference<IDebugSession>(s)));
            rig.Started.Clear();
        }
        lock (rig.Stops) rig.Stops.Clear();
        lock (rig.Terminated) rig.Terminated.Clear();
        lock (rig.Output) rig.Output.Clear();
        return weak;
    }
}
