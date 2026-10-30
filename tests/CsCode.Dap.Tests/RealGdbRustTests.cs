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
/// The Rust evaluation matrix (docs/DEBUGGING-RUST-TS.md, "Rust through GDB") against the REAL GDB through the real
/// <see cref="CsCode.Dap.Service.DebugService"/>: the cargo sample built by the real cargo (offline), GDB's native DAP started by the built-in
/// <see cref="GdbAdapterFactory"/>. Skips with an explicit reason when gdb (14+), cargo or rustc is missing.
/// </summary>
[Trait("Category", GdbToolchain.Category)]
[Collection(SerialCollection.Name)]
public class RealGdbRustTests
{
    private static int InMain(string marker) => RustSample.MainLine(marker);
    private static int Util(string marker) => RustSample.UtilLine(marker);
    private static void Note(string text) => TestMetrics.Report("[gdb] " + text);

    private static void AssertAt(DebugStopLocation loc, string file, int line)
    {
        Assert.Equal(file, Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(line, loc.Line);
    }

    private static async Task ContinueAsync(RealGdbRig rig) => await rig.Service.StopLocation!.Session.ContinueAsync(ct: Ct);

    private static async Task<int> ValueOfAsync(RealGdbRig rig, string expression)
    {
        var loc = rig.Service.StopLocation!;
        var result = await loc.Session.EvaluateAsync(expression, loc.FrameId, "watch", Ct);
        return int.Parse(result.Result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ launch, breakpoints, clean exit

    [Fact]
    public async Task Launch_stops_at_a_breakpoint_and_exits_cleanly_with_gdb_below_normal_priority()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        Note("gdb: " + GdbToolchain.Version());
        rig.PanicStops(false); // the sample catches a panic on its way to the end
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var session = await rig.StartAsync();
        Assert.NotNull(session.AdapterProcessId);
        Assert.NotNull(session.AdapterProcessMarker);

        var loc = await rig.WaitForStopAsync(1);
        Note($"start to the first stop: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms (load {File.ReadAllText("/proc/loadavg").Split(' ')[0]})");
        Assert.Equal("breakpoint", rig.LastStop.Reason);
        AssertAt(loc, "main.rs", InMain("loop"));
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true }, 10_000, "verified breakpoint");
        Note($"verdict: {rig.H.Breakpoints.GetStatus(bp.Id)}");

        var gdbPid = session.AdapterProcessId!.Value;
        var debuggee = (int)session.DebuggeeProcessId!.Value;
        Note($"gdb pid {gdbPid} nice={RealGdbRig.NiceOf(gdbPid)} ({ProcessTable.CommandLine(gdbPid)}); debuggee pid {debuggee} nice={RealGdbRig.NiceOf(debuggee)} ({ProcessTable.CommandLine(debuggee)})");
        Assert.Equal(10, RealGdbRig.NiceOf(gdbPid));
        Assert.Equal(10, RealGdbRig.NiceOf(debuggee)); // the program GDB launched inherits the lower priority
        Assert.Contains(debuggee, ProcessTable.Descendants(gdbPid));
        Assert.Contains(debuggee, ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", session.AdapterProcessMarker!));

        var (frames, s) = await rig.TopFramesAsync();
        Note("frames: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1} col={f.Column} hint={f.Source?.PresentationHint}")));
        Assert.Equal("sample::main", frames[0].Name);
        var locals = await rig.LocalsAsync(frames[0].Id, s);
        Note("locals: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} ({v.Type})")));
        Assert.Equal("41", locals["x"].Value);
        Assert.Equal("0", locals["i"].Value);

        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct); // let the removal reach the adapter (debounced)
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Note("output: " + rig.OutputText.Replace("\n", "\\n"));
        Assert.Contains("done hello world 3 2 Some(5) None Ok(12) Err(\"invalid digit found in string\")", rig.OutputText);
        Assert.Contains("more two 2 7 Some(\"w\")", rig.OutputText);
        // GDB's own messages ("Breakpoint 1 (…) pending.") arrive on the program's pipe as stdout: shown as debugger messages instead.
        Note("console: " + rig.OutputOf("console").Replace("\n", "\\n"));
        Assert.DoesNotContain("pending", rig.OutputOf("stdout"), StringComparison.Ordinal);
        Assert.Contains("pending", rig.OutputOf("console"), StringComparison.Ordinal);
        Assert.Equal(0, rig.Terminated.Single().ExitCode);
        Assert.Equal(DebugState.Inactive, rig.Service.State);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_breakpoint_on_a_line_without_code_moves_to_the_next_statement()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("comment"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note($"comment-line breakpoint: requested line {InMain("comment") + 1}, stopped at {loc.Line + 1}, verdict {rig.H.Breakpoints.GetStatus(bp.Id)}");
        AssertAt(loc, "main.rs", InMain("comment") + 1);
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true, ActualLine: not null }, 10_000, "moved verdict");
        Assert.Equal(InMain("comment") + 1, rig.H.Breakpoints.GetStatus(bp.Id)!.ActualLine);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ breakpoint kinds

    [Fact]
    public async Task A_conditional_breakpoint_stops_only_when_the_condition_holds()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), condition: "i == 3");
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        Assert.Equal(3, await ValueOfAsync(rig, "i"));
        Assert.Equal(44, await ValueOfAsync(rig, "x"));
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Equal(1, rig.StopCount);
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// GDB takes a hit condition as an IGNORE count, which it consumes once: "3" sent as is would skip three hits and stop at the fourth, then on
    /// every hit after it. The quirk sends N−1 so the first stop is the Nth hit; the "every hit after" part is GDB's (documented).
    /// </summary>
    [Fact]
    public async Task A_hit_count_breakpoint_first_stops_on_the_requested_hit_and_gdb_keeps_stopping_after_it()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), hitCondition: "3");
        var modulo = rig.H.Breakpoints.Add(rig.MainUri, InMain("print"), hitCondition: "%2");
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        Assert.Equal(2, await ValueOfAsync(rig, "i")); // the third hit
        Note($"'%2' verdict: {rig.H.Breakpoints.GetStatus(modulo.Id)}");
        Assert.True(rig.H.Breakpoints.GetStatus(modulo.Id) is { Verified: false, Message: { } why } && why.Contains("GDB cannot break on the hit count '%2'", StringComparison.Ordinal));
        await ContinueAsync(rig);
        await rig.WaitForStopAsync(2);
        Assert.Equal(3, await ValueOfAsync(rig, "i")); // GDB: every hit after the ignored ones stops
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Equal(2, rig.StopCount);
        Assert.True(bp is not null);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_logpoint_prints_each_message_on_its_own_line_without_stopping()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), logMessage: "logged i={i} x={x}");
        await rig.StartAsync();
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
        var logged = rig.OutputSnapshot.Where(o => o.Output.Contains("logged", StringComparison.Ordinal)).ToList();
        Note("logpoint output: " + string.Join(" | ", logged.Select(o => $"{o.Category}:{o.Output.Replace("\n", "\\n")}")));
        Assert.All(logged, o => Assert.Equal("console", o.Category));
        var lines = string.Concat(logged.Select(o => o.Output)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["logged i=0 x=41", "logged i=1 x=41", "logged i=2 x=42", "logged i=3 x=44", "logged i=4 x=47"], lines);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_breakpoint_added_while_stopped_is_verified_and_hit()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        var loop = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var call = rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(call.Id) is { Verified: true }, 10_000, "the new breakpoint is verified");
        Assert.True(rig.H.Breakpoints.GetStatus(loop.Id) is { Verified: true });
        rig.H.Breakpoints.Remove(loop.Id);
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        AssertAt(await rig.WaitForStopAsync(2), "main.rs", InMain("call"));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// rustc gives a <c>println!</c> line two statement entries (formatting, then the print) and GDB 18 puts a breakpoint location on each, so a
    /// breakpoint there stops twice per pass, at two addresses of the same line. Adapter (compiler + GDB) behaviour, recorded.
    /// </summary>
    [Fact]
    public async Task A_breakpoint_on_a_println_line_stops_twice_per_pass()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("print"));
        await rig.StartAsync();
        var first = await rig.WaitForStopAsync(1);
        var i1 = await ValueOfAsync(rig, "i");
        await ContinueAsync(rig);
        var second = await rig.WaitForStopAsync(2);
        var i2 = await ValueOfAsync(rig, "i");
        Note($"println! line: stop 1 at line {first.Line + 1} with i={i1}, stop 2 at line {second.Line + 1} with i={i2}");
        Assert.Equal((InMain("print"), InMain("print"), 0, 0), (first.Line, second.Line, i1, i2));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// A function breakpoint is "pending" while GDB has no symbols (the program loads at launch, after configurationDone); its verdict arrives
    /// later by event. GDB resolves Rust paths (<c>sample::util::add</c>), not a bare <c>add</c>, through DAP.
    /// </summary>
    [Fact]
    public async Task Function_breakpoints_stop_on_a_rust_path_and_are_verified_when_the_program_loads()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        var path = rig.H.Breakpoints.AddFunction("sample::util::add");
        var bare = rig.H.Breakpoints.AddFunction("add");
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "util.rs", Util("add"));
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(path.Id) is { Verified: true }, 10_000, "the function breakpoint is verified by event");
        Note($"function breakpoints: sample::util::add -> {rig.H.Breakpoints.GetStatus(path.Id)}; add -> {rig.H.Breakpoints.GetStatus(bare.Id)}");
        Assert.Null(rig.H.Breakpoints.GetStatus(path.Id)!.ActualLine); // a function breakpoint has no moved line
        Assert.True(rig.H.Breakpoints.GetStatus(bare.Id) is { Verified: false }); // GDB 18 over DAP: a bare name stays pending
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ panics

    [Fact]
    public async Task Rust_panics_stop_as_exceptions_with_the_panic_message_and_an_uncaught_one_ends_the_program()
    {
        await using var rig = await RealGdbRig.CreateAsync(); // "Rust panics" is on by default
        var session = await rig.StartAsync(rig.Config(args: ["panic"]));
        Assert.Contains(session.Capabilities.ExceptionBreakpointFilters!, f => f is { Filter: GdbAdapterQuirks.RustPanicFilter, Label: "Rust panics", Default: true });
        Assert.Contains(rig.H.Breakpoints.ExceptionFilters, f => f is { Filter: GdbAdapterQuirks.RustPanicFilter, Enabled: true });

        // 1. the panic the sample catches (catch_unwind)
        var loc = await rig.WaitForStopAsync(1);
        Note($"panic stop: reason={rig.LastStop.Reason} description='{rig.LastStop.Description}' text='{rig.LastStop.Text}' at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}");
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(loc, "util.rs", Util("panic")); // the user's frame, not std's rust_panic
        var (frames, s) = await rig.TopFramesAsync();
        Note("panic frames: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1} {f.Source?.PresentationHint}")));
        Assert.Equal("std::panicking::rust_panic", frames[0].Name);
        Assert.Equal("deemphasize", frames[0].Source?.PresentationHint);
        Assert.Contains(frames, f => f.Name == "sample::util::explode" && f.Source?.PresentationHint is null);
        var body = await s.CustomRequestAsync("exceptionInfo", JsonSerializer.SerializeToElement(new { threadId = loc.ThreadId }), Ct);
        Note("exceptionInfo: " + body?.GetRawText());
        Assert.Equal("panic", body!.Value.GetProperty("exceptionId").GetString());
        Assert.Equal("boom at the bottom", body.Value.GetProperty("description").GetString());
        Assert.StartsWith("thread 'main'", body.Value.GetProperty("details").GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.Contains("panicked at src/util.rs:", body.Value.GetProperty("details").GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        await SessionHarness.Eventually(() => s.LastException is { ExceptionId: "panic" }, 5_000, "LastException");

        // 2. the uncaught one (mode "panic")
        await ContinueAsync(rig);
        loc = await rig.WaitForStopAsync(2);
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(loc, "util.rs", Util("panic"));
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Note($"uncaught panic: exit code {rig.Terminated.Single().ExitCode}");
        Assert.Equal(101, rig.Terminated.Single().ExitCode);
        Assert.Contains("boom at the bottom", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>Start Without Debugging (Ctrl+F5): GDB ignores <c>noDebug</c>, so the session sends it no breakpoints and no panic filter.</summary>
    [Fact]
    public async Task Start_without_debugging_runs_the_program_to_its_end_past_breakpoints_and_panics()
    {
        await using var rig = await RealGdbRig.CreateAsync(); // "Rust panics" on, a breakpoint in the loop
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync(rig.Config() with { NoDebug = true });
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
        Assert.Contains("done hello world", rig.OutputText, StringComparison.Ordinal);
        Assert.Equal(0, rig.Terminated.Single().ExitCode);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task With_rust_panics_off_a_panicking_program_just_ends_with_exit_code_101()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        await rig.StartAsync(rig.Config(args: ["panic"]));
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
        Assert.Equal(101, rig.Terminated.Single().ExitCode);
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ execution control

    [Fact]
    public async Task Step_into_over_and_out_across_files_and_the_return_value_after_step_out()
    {
        await using var rig = await RealGdbRig.CreateAsync();
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
        Assert.Equal(["sample::util::add", "sample::main"], frames.Select(f => f.Name).Take(2));

        await loc.Session.StepOverAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(3);
        Note($"step over from util.rs:{Util("add") + 1} -> util.rs:{loc.Line + 1}");
        Assert.Equal("util.rs", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.InRange(loc.Line, Util("ret"), Util("ret") + 1); // rustc gives the tail expression `sum` no line of its own: GDB lands on `}`

        await loc.Session.StepOutAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(4);
        AssertAt(loc, "main.rs", InMain("call"));
        var session = (DebugSession)loc.Session;
        var scopes = await session.GetScopesAsync(loc.FrameId, Ct);
        Note("scopes after step out: " + string.Join(" | ", scopes.Select(sc => $"{sc.Name} hint={sc.PresentationHint}")));
        var ret = Assert.Single(scopes, sc => sc.PresentationHint == "returnValue");
        var value = Assert.Single(await session.GetVariablesAsync(ret.VariablesReference, ct: Ct));
        Note($"return value: {value.Name} = {value.Value} ({value.Type})");
        Assert.Equal("52", value.Value);

        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Pause_stops_a_running_program_in_the_users_code_and_library_frames_are_deemphasized()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        var session = await rig.StartAsync(rig.Config(args: ["serve"]));
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running, 30_000, "running");
        await Task.Delay(500, Ct);
        await session.PauseAsync(ct: Ct);
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("pause", rig.LastStop.Reason);
        var (frames, _) = await rig.TopFramesAsync();
        Note("pause frames: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{f.Source?.Path}:{f.Line + 1} ref={f.Source?.SourceReference} {f.Source?.PresentationHint}")));
        Note($"pause stop location: {loc.Uri?.LocalPath}:{loc.Line + 1}");
        // GDB stops inside libc's sleep (no source on disk: a sourceReference it cannot serve) under std's sleep: both de-emphasised, and the
        // stop shows serve(), the user's code.
        Assert.Equal("deemphasize", frames[0].Source?.PresentationHint);
        Assert.Equal("main.rs", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.InRange(loc.Line, InMain("tick"), InMain("tick") + 1);
        var serve = frames.First(f => f.Name == "sample::serve");
        Assert.Null(serve.Source?.PresentationHint);
        Assert.Equal(serve.Id, loc.FrameId);

        var started = Stopwatch.GetTimestamp();
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        Note($"stop while paused: every session gone after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Stop_while_the_program_runs_ends_gdb_and_the_program()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        var session = await rig.StartAsync(rig.Config(args: ["serve"]));
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running && session.DebuggeeProcessId is not null, 30_000, "running");
        lock (rig.DebuggeePids) rig.DebuggeePids.Add((int)session.DebuggeeProcessId!.Value);
        await Task.Delay(300, Ct);
        var started = Stopwatch.GetTimestamp();
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        Note($"stop while running: every session gone after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>GDB has no <c>restart</c> request: the session stops GDB and the program and runs the whole handshake again (a new GDB).</summary>
    [Fact]
    public async Task Restart_runs_the_program_again_under_a_new_gdb()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var session = await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        Assert.False(session.Capabilities.SupportsRestartRequest);
        var firstGdb = session.AdapterProcessId!.Value;
        var started = Stopwatch.GetTimestamp();
        await session.RestartAsync(Ct);
        var loc = await rig.WaitForStopAsync(2);
        Note($"restart: stopped again after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms, gdb {firstGdb} -> {session.AdapterProcessId}");
        AssertAt(loc, "main.rs", InMain("loop"));
        Assert.Same(session, loc.Session);
        Assert.NotEqual(firstGdb, session.AdapterProcessId);
        await SessionHarness.Eventually(() => !ProcessTable.IsAlive(firstGdb), 10_000, "the first gdb exits");
        Assert.Empty(rig.Terminated);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>GDB killed outright while the program is stopped: the session ends, and the program GDB launched does not survive it.</summary>
    [Fact]
    public async Task An_adapter_crash_ends_the_session_and_leaves_no_debuggee()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var session = await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var gdb = session.AdapterProcessId!.Value;
        var debuggee = (int)session.DebuggeeProcessId!.Value;
        using (var p = Process.GetProcessById(gdb)) p.Kill();
        await rig.WaitForEndAsync();
        Note($"adapter crash: terminated with '{rig.Terminated.Single().Reason}'; debuggee {debuggee} alive afterwards: {ProcessTable.IsAlive(debuggee)}");
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ call stack, values

    [Fact]
    public async Task Generic_functions_and_closures_appear_in_the_call_stack()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("apply"));
        rig.H.Breakpoints.Add(rig.UtilUri, Util("generic"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        var (frames, _) = await rig.TopFramesAsync();
        Note("generic caller: " + string.Join(" | ", frames.Select(f => f.Name)));
        Assert.StartsWith("sample::apply<sample::main::{closure_env#", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal("sample::main", frames[1].Name);

        await loc.Session.StepIntoAsync(ct: Ct);
        loc = await rig.WaitForStopAsync(2);
        (frames, _) = await rig.TopFramesAsync();
        Note("closure: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1}")));
        Assert.StartsWith("sample::main::{closure#", frames[0].Name, StringComparison.Ordinal);
        AssertAt(loc, "main.rs", InMain("closure"));

        await ContinueAsync(rig);
        loc = await rig.WaitForStopAsync(3);
        (frames, _) = await rig.TopFramesAsync();
        Note("generic function: " + string.Join(" | ", frames.Select(f => f.Name)));
        Assert.Equal("sample::util::describe<i32>", frames[0].Name);
        AssertAt(loc, "util.rs", Util("generic"));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Locals_show_rust_values_with_the_pretty_printers_and_summaries()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("done"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Note("values: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} [{v.Type}] ref={v.VariablesReference} named={v.NamedVariables} indexed={v.IndexedVariables}")));
        string Value(string name) => locals[name].Value;
        Assert.Equal("\"hello\"", Value("s"));
        Assert.Equal("\"world\"", Value("text"));
        Assert.Equal("Vec(size=3)", Value("v"));
        Assert.Equal("HashMap(size=2)", Value("map"));
        Assert.Equal("Some(5)", Value("some"));
        Assert.Equal("None", Value("none"));
        Assert.Equal("Ok(12)", Value("ok"));
        Assert.Equal("Err(\"invalid digit found in string\")", Value("err"));
        Assert.Equal("Circle{radius: 1.5}", Value("shape"));
        Assert.Equal("Rect(3, 4)", Value("rect"));
        Assert.Equal("Empty", Value("empty"));
        Assert.Equal("Point {x: 3, y: 4}", Value("p"));
        Assert.Matches("^0x[0-9a-f]+ Point \\{x: 5, y: 6\\}$", Value("boxed"));
        Assert.Equal("Rc(strong=2, weak=0)", Value("shared"));
        Assert.Equal("(1, \"two\")", Value("pair"));
        Assert.Equal("Wrapper {point: Point {x: 7, y: 8}, tag: Some(\"w\")}", Value("wrapper"));
        Assert.Equal("Vec(size=250)", Value("big"));
        Assert.Equal("alloc::string::String", locals["s"].Type);

        async Task<Dictionary<string, string>> ChildrenAsync(string name, string? filter = null, int? start = null, int? count = null) =>
            (await session.GetVariablesAsync(locals[name].VariablesReference, filter, start, count, Ct)).ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal(new Dictionary<string, string> { ["[0]"] = "1", ["[1]"] = "2", ["[2]"] = "3" }, await ChildrenAsync("v"));
        var map = await ChildrenAsync("map");
        Note("map children: " + string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal(4, map.Count);
        Assert.Contains("\"a\"", map.Values);
        var some = await ChildrenAsync("some");
        Note("some children: " + string.Join(", ", some.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal("Some", Assert.Single(some).Key);
        var boxed = await ChildrenAsync("boxed");
        Assert.Equal("Point {x: 5, y: 6}", boxed["value"]);
        Assert.Equal(new Dictionary<string, string> { ["value"] = "\"shared\"", ["strong"] = "2", ["weak"] = "0" }, await ChildrenAsync("shared"));
        Assert.Equal(250, locals["big"].IndexedVariables);
        var page = await ChildrenAsync("big", "indexed", 240, 20);
        Note($"big[240..]: {string.Join(", ", page.Select(kv => $"{kv.Key}={kv.Value}"))}");
        Assert.Equal(10, page.Count); // GDB truncates a page that runs past the end
        Assert.Equal("498", page["[249]"]);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Watch_expressions_set_variable_and_the_program_continues_with_the_new_value()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        Assert.Equal("102", (await session.EvaluateAsync("x * 2", loc.FrameId, "watch", Ct)).Result);
        Assert.Equal("7", (await session.EvaluateAsync("p.x + p.y", loc.FrameId, "watch", Ct)).Result);
        var watchError = await Assert.ThrowsAsync<DapRequestException>(async () => await session.EvaluateAsync("nope", loc.FrameId, "watch", Ct));
        Note("watch error: " + watchError.Message);
        Assert.Equal("No symbol 'nope' in current context", watchError.Message);
        var (rust, _) = await RealGdbRig.EvaluateRustExpressionsAsync(session, loc.FrameId);
        Note("rust expressions: " + rust);

        var scope = (await session.GetScopesAsync(loc.FrameId, Ct)).First(s => s.PresentationHint == "locals");
        // GDB finds a variable to set among the children it listed (the Locals window lists them first): "no variable named 'x'" otherwise.
        Assert.Contains(await session.GetVariablesAsync(scope.VariablesReference, ct: Ct), v => v.Name == "x");
        var set = await session.SetVariableAsync(scope.VariablesReference, "x", "100", Ct);
        Assert.Equal("100", set.Value);
        Assert.Equal("100", (await session.EvaluateAsync("x", loc.FrameId, "watch", Ct)).Result);
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Contains("err 101", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// GDB runs the Debug Console's input (context <c>repl</c>) as a GDB COMMAND. The quirk evaluates expressions, runs <c>-exec</c> input as a
    /// command, retries "p x"-like input as one, and completes expressions through <c>print &lt;expression&gt;</c>.
    /// </summary>
    [Fact]
    public async Task The_debug_console_evaluates_expressions_runs_gdb_commands_and_completes()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        async Task<string> Repl(string text) => (await session.EvaluateAsync(text, loc.FrameId, "repl", Ct)).Result;

        Assert.Equal("102", await Repl("x * 2"));
        var vec = await session.EvaluateAsync("v", loc.FrameId, "repl", Ct);
        Assert.Equal("Vec(size=3)", vec.Result);
        Assert.True(vec.VariablesReference > 0, "an expression's value is expandable");
        Assert.Equal("Point {x: 3, y: 4}", await Repl("p"));
        var locals = await Repl("-exec info locals");
        Note("-exec info locals: " + locals.Replace("\n", "\\n"));
        Assert.Contains("x = 51", locals, StringComparison.Ordinal);
        Assert.Matches(@"^\$\d+ = 51$", await Repl("p x")); // not an expression: run as the GDB command it is
        Assert.Contains("sample::main", await Repl("bt"), StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<DapRequestException>(async () => await Repl("nope + 1"));
        Note("repl error: " + error.Message);
        Assert.Equal("No symbol 'nope' in current context", error.Message);

        var completions = await session.CompletionsAsync(loc.FrameId, "p.", 2, Ct);
        Note("completions of 'p.': " + string.Join(", ", completions.Select(c => $"{c.Label}=>{c.Text}@{c.Start}+{c.Length}")));
        Assert.Contains(completions, c => c is { Label: "p.x", Text: "p.x", Start: 0, Length: 2 });
        Assert.Contains(completions, c => c is { Text: "p.y" });
        var commands = await session.CompletionsAsync(loc.FrameId, "-exec info lo", 13, Ct);
        Note("completions of '-exec info lo': " + string.Join(", ", commands.Select(c => $"{c.Label}=>{c.Text}")));
        Assert.Contains(commands, c => c.Text == "-exec info locals");
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>GDB sends the program's stdout AND stderr (one pipe) as <c>stdout</c>: an adapter limitation, recorded.</summary>
    [Fact]
    public async Task Program_output_reaches_the_console_and_stderr_arrives_as_stdout()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        await rig.StartAsync();
        await rig.WaitForEndAsync();
        var stdout = rig.OutputOf("stdout");
        Note("stdout: " + stdout.Replace("\n", "\\n"));
        Note("categories: " + string.Join(", ", rig.OutputSnapshot.Select(o => o.Category).Distinct()));
        Assert.Contains("loop 0\nloop 1\nloop 2\nloop 3\nloop 4\n", stdout, StringComparison.Ordinal);
        Assert.Contains("err 52\n", stdout, StringComparison.Ordinal); // eprintln!: GDB merges stderr into the same pipe
        Assert.Empty(rig.OutputOf("stderr"));
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ threads, attach

    [Fact]
    public async Task Threads_of_std_thread_are_listed_by_name_and_stop_in_the_worker()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("worker"));
        await rig.StartAsync(rig.Config(args: ["threads"]));
        var loc = await rig.WaitForStopAsync(1);
        var session = (DebugSession)loc.Session;
        var threads = await session.GetThreadsAsync(Ct);
        Note("threads: " + string.Join(", ", threads.Select(t => $"{t.Id}={t.Name}")));
        // The stopped worker has its name; the other one may not have set it yet when the first stop comes (it is still starting).
        Assert.StartsWith("worker-", threads.Single(t => t.Id == loc.ThreadId).Name, StringComparison.Ordinal);
        Assert.Equal(RustSample.PackageName, threads[0].Name); // the main thread carries the process name
        Assert.InRange(threads.Count, 2, 3);
        var (frames, _) = await rig.TopFramesAsync();
        Note("worker frames: " + string.Join(" | ", frames.Select(f => $"{f.Name} {f.Source?.PresentationHint}")));
        Assert.Equal("sample::worker", frames[0].Name);
        Assert.Contains(frames, f => f.Source?.PresentationHint == "deemphasize"); // std's thread start-up
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
        Assert.SkipWhen(GdbToolchain.SkipReason() is not null, GdbToolchain.SkipReason() ?? "");
        var scope = File.Exists("/proc/sys/kernel/yama/ptrace_scope") ? File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim() : "0";
        Assert.SkipWhen(scope != "0", $"kernel.yama.ptrace_scope is {scope}: GDB may not attach to a process it did not start");
        await using var rig = await RealGdbRig.CreateAsync();
        using var program = Process.Start(new ProcessStartInfo(rig.Sample.Binary, "serve") { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(300, Ct);
            rig.H.Breakpoints.Add(rig.MainUri, InMain("tick"));
            var config = RealGdbRig.Raw(new { type = "gdb", request = "attach", name = "attach", program = rig.Sample.Binary, pid = program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, rig.Sample.Dir.Uri);
            var session = await rig.StartAsync(config);
            Assert.True(session.IsAttach);
            var loc = await rig.WaitForStopAsync(1);
            Note($"attach: first stop reason '{rig.LastStop.Reason}' at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}");
            Assert.Equal("attach", rig.LastStop.Reason); // GDB stops the process it attached to
            if (rig.H.Breakpoints.SourceBreakpoints.Count > 0)
            {
                await ContinueAsync(rig);
                loc = await rig.WaitForStopAsync(2);
                AssertAt(loc, "main.rs", InMain("tick"));
                Assert.True(await ValueOfAsync(rig, "ticks") > 0);
            }
            await rig.Service.StopAllAsync(Ct);
            await rig.WaitForEndAsync();
            await SessionHarness.Eventually(() => session.AdapterProcessId is not { } gdb || !ProcessTable.IsAlive(gdb), 10_000, "gdb exits");
            Assert.False(program.HasExited, "detaching left the program running");
            await rig.AssertNothingLeftRunningAsync();
        }
        finally
        {
            try { program.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // ------------------------------------------------------------------ cargo: test binary, template, environment

    /// <summary>The "cargo" key (CodeLLDB's): cargo test --no-run builds the unit tests, and the executable it reports is debugged.</summary>
    [Fact]
    public async Task The_cargo_key_builds_the_test_binary_and_a_breakpoint_in_a_unit_test_hits()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.UtilUri, Util("test"));
        var config = RealGdbRig.Raw(new
        {
            type = "gdb", request = "launch", name = "tests",
            cargo = new { args = new[] { "test", "--no-run", "--offline" }, filter = new { kind = "bin" }, env = new { CARGO_TARGET_DIR = rig.Sample.Dir.Combine("target") } },
            args = Array.Empty<string>(), cwd = rig.Sample.Dir.Path,
        }, rig.Sample.Dir.Uri);
        var session = await rig.StartAsync(config);
        var program = session.Configuration.GetString("program");
        Note($"cargo key -> program {program}; Tasks: {rig.H.Output.Text(CsCode.Dap.Tasks.TaskService.ChannelName).Replace("\n", " | ")}");
        Assert.Equal(rig.Sample.TestBinary, program);
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "util.rs", Util("test"));
        var (frames, _) = await rig.TopFramesAsync();
        Assert.Equal("sample::util::tests::adds_numbers", frames[0].Name);
        var threads = await session.GetThreadsAsync(Ct);
        Note("test threads: " + string.Join(", ", threads.Select(t => t.Name)));
        // The test harness names a test's thread after it, but Linux keeps 15 characters of a thread name (TASK_COMM_LEN), which GDB reports.
        Assert.Contains(threads, t => t.Name == "util::tests::ad");
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct);
        await ContinueAsync(rig);
        await rig.WaitForEndAsync();
        Assert.Contains("test result: ok. 1 passed", rig.OutputText, StringComparison.Ordinal);
        Assert.Equal(0, rig.Terminated.Single().ExitCode);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task The_cargo_template_builds_through_its_preLaunchTask_and_stops_in_main()
    {
        Assert.SkipWhen(GdbToolchain.SkipReason() is not null, GdbToolchain.SkipReason() ?? "");
        using var fresh = RustSample.Write("rustgdb-template-"); // sources only: the preLaunchTask must build them
        await using var rig = await RealGdbRig.CreateAsync(fresh);
        rig.PanicStops(false);
        var folder = fresh.Dir.Uri;
        var launchJson = await rig.H.Configurations.CreateLaunchJsonAsync("gdb", folder, Ct);
        Note("generated launch.json: " + File.ReadAllText(launchJson.LocalPath).Replace("\n", " ", StringComparison.Ordinal));
        Note("generated tasks.json: " + File.ReadAllText(fresh.Dir.Combine(".cscode", "tasks.json")).Replace("\n", " ", StringComparison.Ordinal));
        var configs = await rig.H.Configurations.GetLaunchConfigurationsAsync(folder, Ct);
        Assert.Equal(["Rust: Debug sample (cargo build)", "Rust: Debug unit tests of sample (cargo test)", "Rust: Attach to sample"], configs.Select(c => c.Name));
        Assert.False(File.Exists(fresh.Binary));
        rig.H.Breakpoints.Add(new Uri(fresh.MainRs), InMain("loop"));

        await rig.StartAsync(configs[0]);
        Assert.True(File.Exists(fresh.Binary), "the preLaunchTask ran cargo build");
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(fresh.MainRs, loc.Uri?.LocalPath);
        Assert.Equal(InMain("loop"), loc.Line);
        Note("Tasks channel: " + rig.H.Output.Text(CsCode.Dap.Tasks.TaskService.ChannelName).Replace("\n", " | ", StringComparison.Ordinal));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();

        // the unit-test template: the cargo key, no preLaunchTask
        rig.H.Breakpoints.RemoveAll();
        rig.H.Breakpoints.Add(new Uri(fresh.UtilRs), Util("test"));
        await rig.StartAsync(configs[1]);
        loc = await rig.WaitForStopAsync(2);
        Assert.Equal(fresh.UtilRs, loc.Uri?.LocalPath);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>GDB's own <c>env</c> REPLACES the program's environment (PATH, HOME… gone); the provider turns it into additions.</summary>
    [Fact]
    public async Task Env_adds_to_the_programs_environment_instead_of_replacing_it()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("start"));
        var session = await rig.StartAsync(rig.Config(extra: new Dictionary<string, object?> { ["env"] = new Dictionary<string, string?> { ["CSCODE_GDB_TEST"] = "yes it is", ["LANG"] = null } }));
        await rig.WaitForStopAsync(1);
        var environ = File.ReadAllText($"/proc/{session.DebuggeeProcessId}/environ").Split('\0');
        Note("debuggee environment: " + string.Join(" ", environ.Where(e => e.StartsWith("CSCODE", StringComparison.Ordinal) || e.StartsWith("PATH=", StringComparison.Ordinal) || e.StartsWith("LANG=", StringComparison.Ordinal))));
        Assert.Contains("CSCODE_GDB_TEST=yes it is", environ);
        Assert.Contains(environ, e => e.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Contains($"CSCODE_DAP_ADAPTER={session.AdapterProcessMarker}", environ);
        Assert.DoesNotContain(environ, e => e.StartsWith("LANG=", StringComparison.Ordinal));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ flood, soak

    /// <summary>
    /// A program printing 1,000,000 lines: GDB reads them on a thread of its own and sends one DAP event per line; the last ones arrive AFTER
    /// <c>terminated</c>, so the session waits for the output to go quiet before it ends (else "flood done" was lost).
    /// </summary>
    [Fact]
    public async Task A_million_line_flood_ends_with_its_last_line_and_bounded_memory()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.OutputLimit = 50; // the newest batches only: the heap measured is the debugger's
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var started = Stopwatch.GetTimestamp();
        var session = await rig.StartAsync(rig.Config(args: ["flood", "1000000"]));
        await rig.WaitForEndAsync(600_000);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        GC.Collect();
        var heapAfter = GC.GetTotalMemory(true);
        var history = rig.Service.GetOutputHistory();
        Note($"1M-line flood: {seconds:0.0} s, {rig.OutputEvents} output batches delivered, {session.DroppedOutputChars:N0} chars dropped by design, history {history.Sum(h => h.Output.Count(c => c == '\n'))} lines, heap {heapBefore / 1024 / 1024.0:0.0} -> {heapAfter / 1024 / 1024.0:0.0} MB");
        Assert.EndsWith("flood done\n", rig.OutputOf("stdout"), StringComparison.Ordinal);
        Assert.Contains(history, h => h.Output.Contains("flood done", StringComparison.Ordinal));
        Assert.InRange(heapAfter - heapBefore, long.MinValue, 64L * 1024 * 1024);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Ten_real_sessions_leave_no_processes_threads_or_retained_sessions()
    {
        await using var rig = await RealGdbRig.CreateAsync();
        rig.PanicStops(false);
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        // warm up (lane thread, JIT, thread pool) so the baseline is meaningful
        await rig.StartAsync(rig.Config("warm"));
        await rig.WaitForStopAsync(1);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
        lock (rig.Started) rig.Started.Clear();
        lock (rig.Stops) rig.Stops.Clear();
        lock (rig.Terminated) rig.Terminated.Clear();
        for (var i = 0; i < 5; i++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); await Task.Delay(50, Ct); }
        var baselineThreads = Process.GetCurrentProcess().Threads.Count;

        var weak = await RunSessionsAsync(rig, 10);
        Assert.Equal(10, weak.Count);
        await rig.AssertNothingLeftRunningAsync();
        Assert.Equal(0, rig.H.Breakpoints.StatusCount);
        for (var i = 0; i < 10 && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, Ct);
        }
        Assert.DoesNotContain(weak, w => w.TryGetTarget(out _));
        await Task.Delay(1_000, Ct); // idle pool threads retire
        var threads = Process.GetCurrentProcess().Threads.Count;
        Note($"10-session soak: {weak.Count} sessions collected, {rig.AdapterPids.Count} gdb processes all gone, threads {baselineThreads} -> {threads}");
        Assert.InRange(threads, 0, baselineThreads + 6);
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
