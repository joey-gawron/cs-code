using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The TypeScript/JavaScript evaluation matrix (docs/DEBUGGING-RUST-TS.md) against the REAL js-debug v1.140.0 through the real
/// <see cref="CsCode.Dap.Service.DebugService"/>: a two-file TypeScript sample compiled by the real tsc (source maps), the adapter server
/// started by the built-in factory, the program as a child session. Skips with an explicit reason when node, tsc or js-debug is missing
/// (tools/fetch-js-debug.sh, CSCODE_JSDEBUG).
/// </summary>
[Trait("Category", JsDebugToolchain.Category)]
[Collection(SerialCollection.Name)]
public class RealJsDebugTests
{
    private static int InMain(string marker) => JsDebugSample.MainLine(marker);
    private static int Util(string marker) => JsDebugSample.UtilLine(marker);
    private static void Note(string text) => TestMetrics.Report("[js-debug] " + text);

    private static void AssertAt(DebugStopLocation loc, string file, int line)
    {
        Assert.Equal(file, Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(line, loc.Line);
    }

    // ------------------------------------------------------------------ launch, breakpoints in .ts, clean exit

    [Fact]
    public async Task Launch_stops_at_a_ts_breakpoint_in_the_child_session_and_exits_cleanly()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var parent = await rig.StartAsync();
        Assert.Null(parent.Parent);
        Assert.NotNull(parent.AdapterProcessId);
        Assert.NotNull(parent.AdapterEndpoint);

        var loc = await rig.WaitForStopAsync(1);
        var child = (DebugSession)loc.Session;
        Assert.Same(parent, child.Parent);
        Assert.Equal(parent.AdapterEndpoint, child.AdapterEndpoint); // the child is a second connection to the same server
        Assert.Null(child.AdapterProcessId);
        Assert.Equal("breakpoint", rig.LastStop.Reason);
        AssertAt(loc, "main.ts", InMain("loop"));

        var status = rig.H.Breakpoints.GetStatus(bp.Id);
        Note($"verdict: {status}");
        Assert.True(status is { Verified: true });

        var (frames, session) = await rig.TopFramesAsync();
        Note("frames: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{f.Source?.Path}:{f.Line} hint={f.PresentationHint}/{f.Source?.PresentationHint}")));
        Assert.Equal("main", frames[0].Name);
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Note("locals: " + string.Join(" | ", locals.Select(v => $"{v.Name}={v.Value} ({v.Type})")));
        Assert.Equal("41", Assert.Single(locals, v => v.Name == "x").Value);

        await session.ContinueAsync(ct: Ct);
        await rig.WaitForStopAsync(2); // the loop breakpoint again (i = 1)
        rig.H.Breakpoints.RemoveAll();
        await Task.Delay(300, Ct); // let the removal reach the adapter (debounced)
        await rig.Service.StopLocation!.Session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Note("output: " + rig.OutputText.Replace("\n", "\\n"));
        Assert.Contains("done hello 52 5 2 3 250 outer HELLO!51", rig.OutputText);
        Assert.Equal(DebugState.Inactive, rig.Service.State);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_breakpoint_on_a_line_without_code_moves_to_the_next_statement_through_the_source_map()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("comment"));
        var parent = await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        Note($"comment-line breakpoint: requested line {InMain("comment") + 1}, stopped at {loc.Line + 1}, verdict {rig.H.Breakpoints.GetStatus(bp.Id)}");
        AssertAt(loc, "main.ts", InMain("comment") + 1);
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true, ActualLine: not null }, 10_000, "moved verdict");
        Assert.Equal(InMain("comment") + 1, rig.H.Breakpoints.GetStatus(bp.Id)!.ActualLine);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task A_breakpoint_in_a_second_ts_file_hits_with_source_mapped_and_async_frames()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.UtilUri, Util("add"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "util.ts", Util("add"));
        var (frames, _) = await rig.TopFramesAsync(50);
        Note("async stack: " + string.Join(" | ", frames.Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1} hint={f.PresentationHint}/{f.Source?.PresentationHint} ref={f.Source?.SourceReference}")));
        Assert.Equal("add", frames[0].Name);
        Assert.Equal(("work", "main.ts", InMain("async")), (frames[1].Name, Path.GetFileName(frames[1].Source?.Path), frames[1].Line));
        // The async part of the stack: a label frame for the await, then main() at the awaiting call.
        var label = Assert.Single(frames, f => f.PresentationHint == "label");
        var mainFrame = frames.First(f => f.Name == "main");
        Assert.True(frames.ToList().IndexOf(label) < frames.ToList().IndexOf(mainFrame), "the await label precedes main()");
        Assert.Equal(InMain("call"), mainFrame.Line);
        // Node internals are skipped (skipFiles) and marked so the UI can fold them: they carry a sourceReference, no file path.
        var internals = frames.Where(f => f.Source?.Path?.StartsWith("<node_internals>", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(internals);
        Assert.All(internals, f => Assert.Equal("deemphasize", f.Source!.PresentationHint));
        Assert.All(internals, f => Assert.NotNull(f.Source!.SourceReference));
        Assert.True(rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true });
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ breakpoint kinds

    /// <summary>
    /// Integrator finding: stopped at a breakpoint, adding another one in the same file turned the first into "Unbound breakpoint" in the
    /// Breakpoints window, although the program still stopped there. Both sessions (the orchestrating parent and the program's child) get the
    /// file's breakpoints again; what each one answers is recorded.
    /// </summary>
    [Fact]
    public async Task Adding_a_breakpoint_while_stopped_keeps_the_other_breakpoints_of_the_file_verified()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var reports = new List<string>();
        rig.Service.SessionStarted.Subscribe(s =>
        {
            if (s is DebugSession ds)
                ds.BreakpointStatusChanged.Subscribe(st => { lock (reports) reports.Add($"{(ds.Parent is null ? "parent" : "child")}: {st.BreakpointId} verified={st.Verified} line={st.ActualLine} '{st.Message}'"); });
        });
        var loop = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(loop.Id) is { Verified: true }, 10_000, "the loop breakpoint is verified");
        lock (reports) reports.Add("--- adding a breakpoint while stopped");

        var done = rig.H.Breakpoints.Add(rig.MainUri, InMain("done"));
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(done.Id) is { Verified: true }, 10_000, "the new breakpoint is verified");
        await Task.Delay(1_000, Ct); // every session answered (and any breakpoint event arrived)
        lock (reports) Note("statuses per session: " + string.Join(" | ", reports));
        Assert.True(rig.H.Breakpoints.GetStatus(loop.Id) is { Verified: true }, $"the loop breakpoint stays verified: {rig.H.Breakpoints.GetStatus(loop.Id)}");

        await rig.Service.StopLocation!.Session.ContinueAsync(ct: Ct);
        var again = await rig.WaitForStopAsync(2);
        AssertAt(again, "main.ts", InMain("loop")); // and it still stops there
        Assert.True(rig.H.Breakpoints.GetStatus(loop.Id) is { Verified: true });
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Conditional_breakpoint_stops_only_when_the_condition_holds()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), condition: "i === 3");
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "main.ts", InMain("loop"));
        var (frames, session) = await rig.TopFramesAsync();
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Assert.Equal("3", Assert.Single(locals, v => v.Name == "i").Value);
        Assert.Equal("44", Assert.Single(locals, v => v.Name == "x").Value); // 41+0+1+2 before `x += i` of the 4th pass
        await session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Equal(1, rig.StopCount);
    }

    [Fact]
    public async Task Hit_count_breakpoint_stops_on_the_requested_hit()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"), hitCondition: "3");
        var parent = await rig.StartAsync();
        Assert.True(parent.Capabilities.SupportsHitConditionalBreakpoints);
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Assert.Equal("2", Assert.Single(locals, v => v.Name == "i").Value); // third pass through the loop body
        await session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Equal(1, rig.StopCount);
        Assert.Null(rig.H.Breakpoints.GetStatus(bp.Id)); // verdicts belong to (ended) sessions
    }

    [Fact]
    public async Task Logpoint_prints_to_the_debug_console_without_stopping()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("print"), logMessage: "logged i={i} x={x}");
        var parent = await rig.StartAsync();
        Assert.True(parent.Capabilities.SupportsLogPoints);
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
        var logged = rig.OutputSnapshot.Where(o => o.Output.Contains("logged i=", StringComparison.Ordinal)).ToList();
        Note("logpoint output: " + string.Join(" | ", logged.Select(o => $"[{o.Category}] {o.Output.TrimEnd()} @{Path.GetFileName(o.Source?.Path)}:{o.Line + 1}")));
        Assert.Contains("logged i=0 x=41", rig.OutputText);
        Assert.Contains("logged i=4 x=51", rig.OutputText);
    }

    [Fact]
    public async Task Function_breakpoints_are_reported_unsupported_by_js_debug()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var fn = rig.H.Breakpoints.AddFunction("add");
        var verdicts = new List<DebugBreakpointStatus>();
        rig.H.Breakpoints.Changed.Subscribe(_ => { if (rig.H.Breakpoints.GetStatus(fn.Id) is { } s) lock (verdicts) verdicts.Add(s); });
        var parent = await rig.StartAsync();
        Assert.False(parent.Capabilities.SupportsFunctionBreakpoints); // js-debug v1.140.0 advertises supportsFunctionBreakpoints: false
        await SessionHarness.Eventually(() => { lock (verdicts) return verdicts.Count > 0; }, 10_000, "an unsupported verdict for the function breakpoint");
        lock (verdicts)
        {
            Assert.False(verdicts[0].Verified);
            Assert.Equal("This debug adapter does not support function breakpoints.", verdicts[0].Message);
        }
        await rig.WaitForEndAsync();
        Assert.Equal(0, rig.StopCount);
    }

    [Fact]
    public async Task Caught_exception_filter_stops_at_the_throw_with_exception_info()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var parent = await rig.StartAsync(rig.Config("probe-filters"));
        var filters = parent.Capabilities.ExceptionBreakpointFilters!;
        Note("exception filters: " + string.Join(" | ", filters.Select(f => $"{f.Filter}:{f.Label} default={f.Default} condition={f.SupportsCondition}")));
        await rig.WaitForEndAsync();
        rig.H.Breakpoints.SetExceptionFilters([.. filters.Select(f => new ExceptionFilterSetting(f.Filter, f.Label, f.Filter == "all"))]);

        await rig.StartAsync(rig.Config("caught"));
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(loc, "main.ts", InMain("throw"));
        var child = (DebugSession)loc.Session;
        await SessionHarness.Eventually(() => child.LastException is not null, 10_000, "exceptionInfo");
        Note($"exceptionInfo: id={child.LastException!.ExceptionId} desc={child.LastException.Description} mode={child.LastException.BreakMode} type={child.LastException.TypeName} msg={child.LastException.Message}");
        Assert.Contains("boom", child.LastException.ExceptionId); // js-debug puts "Error: boom" in exceptionId; description and details are empty
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Uncaught_exception_filter_stops_at_an_uncaught_throw()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("all", "Caught Exceptions", false), new ExceptionFilterSetting("uncaught", "Uncaught Exceptions", true)]);
        await rig.StartAsync(rig.Config("uncaught", new Dictionary<string, object?> { ["args"] = new[] { "uncaught" } }));
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(loc, "main.ts", InMain("uncaught"));
        Note($"uncaught stop: {rig.LastStop.Description} / {rig.LastStop.Text}");
        await loc.Session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Equal(1, rig.StopCount); // the caught throw in fails() did not stop
        Assert.Contains("uncaught boom", rig.OutputText);
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ execution control

    [Fact]
    public async Task Step_into_over_and_out_across_ts_files_and_awaits()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "main.ts", InMain("call"));
        var session = (DebugSession)loc.Session;
        var trail = new List<string>();
        string Where(DebugStopLocation l) => $"{Path.GetFileName(l.Uri?.LocalPath)}:{l.Line + 1}";

        await session.StepIntoAsync(ct: Ct);                       // into work(): its first statement, `await delay(1)`
        var s1 = await rig.WaitForStopAsync(2); trail.Add("into " + Where(s1));
        AssertAt(s1, "main.ts", InMain("async") - 1);
        await session.StepOverAsync(ct: Ct);                       // over the await: resumes after the timer, on the next statement
        var s2 = await rig.WaitForStopAsync(3); trail.Add("over " + Where(s2));
        AssertAt(s2, "main.ts", InMain("async"));
        await session.StepIntoAsync(ct: Ct);                       // into add() in util.ts
        var s3 = await rig.WaitForStopAsync(4); trail.Add("into " + Where(s3));
        AssertAt(s3, "util.ts", Util("add"));
        await session.StepOverAsync(ct: Ct);
        var s4 = await rig.WaitForStopAsync(5); trail.Add("over " + Where(s4));
        AssertAt(s4, "util.ts", Util("ret"));
        await session.StepOutAsync(ct: Ct);                        // back in work()
        var s5 = await rig.WaitForStopAsync(6); trail.Add("out " + Where(s5));
        Note("steps: " + string.Join(" → ", trail));
        Assert.Equal("main.ts", Path.GetFileName(s5.Uri?.LocalPath));
        Assert.InRange(s5.Line, InMain("async"), InMain("async") + 1);
        Assert.All(rig.Stops.Skip(1), s => Assert.Equal("step", s.Reason));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Pause_stops_a_running_program()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        await rig.StartAsync(rig.Config("serve", new Dictionary<string, object?> { ["args"] = new[] { "serve" } }));
        var child = await rig.ChildOfAsync(rig.Started[0]);
        await SessionHarness.Eventually(() => child.State == DebugSessionState.Running, 20_000, "child running");
        await Task.Delay(300, Ct);
        await child.PauseAsync(ct: Ct);
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("pause", rig.LastStop.Reason);
        var (frames, _) = await rig.TopFramesAsync();
        Note($"paused at {Path.GetFileName(loc.Uri?.LocalPath)}:{loc.Line + 1}; frames: " + string.Join(" | ", frames.Take(6).Select(f => $"{f.Name}@{Path.GetFileName(f.Source?.Path)}:{f.Line + 1}")));
        Assert.NotEmpty(frames);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Stop_while_stopped_at_a_breakpoint_ends_every_session_and_process_quickly()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var sw = Stopwatch.StartNew();
        await rig.Service.StopAllAsync(Ct); // Shift+F5
        await rig.WaitForEndAsync();
        sw.Stop();
        Note($"stop (Shift+F5) at a breakpoint: every session ended after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"stopping took {sw.ElapsedMilliseconds} ms");
        await rig.AssertNothingLeftRunningAsync();
        Assert.DoesNotContain("done hello", rig.OutputText); // the program did not run to its end
    }

    [Fact]
    public async Task Stopping_only_the_child_session_ends_the_program_and_the_parent()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        var sw = Stopwatch.StartNew();
        await loc.Session.StopAsync(Ct); // the Call Stack / Threads "stop" on the focused (child) session
        await rig.WaitForEndAsync();
        Note($"stopping the child alone ended both sessions after {sw.ElapsedMilliseconds} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Restart_runs_the_program_again_in_a_new_child_session()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("start"));
        var parent = await rig.StartAsync();
        var first = await rig.WaitForStopAsync(1);
        var firstChild = first.Session;
        var sw = Stopwatch.StartNew();
        await rig.H.Commands.ExecuteAsync(CsCode.Core.Platform.KnownCommands.DebugRestart); // the Restart command (Ctrl+Shift+F5)
        var second = await rig.WaitForStopAsync(2, 40_000);
        Note($"restart: stopped again after {sw.ElapsedMilliseconds} ms in session '{second.Session.Name}' (first child '{firstChild.Name}' state {firstChild.State}); sessions now: {string.Join(", ", rig.Service.Sessions.Select(s => $"{s.Name}/{s.State}"))}");
        AssertAt(second, "main.ts", InMain("start"));
        Assert.NotSame(firstChild, second.Session);
        await SessionHarness.Eventually(() => firstChild.State == DebugSessionState.Terminated, 10_000, "the first run's child ended");
        Assert.Equal(2, rig.Service.Sessions.Count); // one parent, one (new) child
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ data

    [Fact]
    public async Task Variables_of_objects_classes_maps_sets_getters_and_paged_arrays()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var locals = await rig.LocalsAsync(frames[0].Id, session);
        Note("locals at @call: " + string.Join(" | ", locals.Select(v => $"{v.Name}={v.Value} ({v.Type}) ref={v.VariablesReference} named={v.NamedVariables} indexed={v.IndexedVariables}")));
        DebugVariable V(string name) => Assert.Single(locals, v => v.Name == name);
        Assert.Equal("51", V("x").Value);
        Assert.Equal("'hello'", V("s").Value);

        // a class instance with a getter
        var p = V("p");
        Assert.StartsWith("Point", p.Value);
        var pChildren = await session.GetVariablesAsync(p.VariablesReference, ct: Ct);
        Note("p: " + string.Join(" | ", pChildren.Select(v => $"{v.Name}={v.Value} ({v.Type}) ref={v.VariablesReference}")));
        Assert.Equal("3", Assert.Single(pChildren, v => v.Name == "x").Value);
        var getter = Assert.Single(pChildren, v => v.Name == "length");
        var getterValue = await session.GetVariablesAsync(getter.VariablesReference, ct: Ct); // js-debug: lazy getter, expanding evaluates it
        Note("p.length (lazy) expands to: " + string.Join(" | ", getterValue.Select(v => $"{v.Name}={v.Value}")));
        Assert.Contains(getterValue, v => v.Value == "5");

        // Map and Set
        var m = V("m");
        Note($"m = {m.Value}; set = {V("set").Value}; nested = {V("nested").Value}");
        Assert.Contains("Map(2)", m.Value);
        var entries = await session.GetVariablesAsync(m.VariablesReference, ct: Ct);
        Assert.Contains(entries, v => v.Value.Contains("\"a\" => 1", StringComparison.Ordinal));
        Assert.Contains("Set(3)", V("set").Value);

        // an array of 250: paged through indexed children
        var big = V("big");
        Assert.Equal(250, big.IndexedVariables);
        var page = await session.GetVariablesAsync(big.VariablesReference, "indexed", 100, 100, Ct);
        Assert.Equal(100, page.Count);
        Assert.Equal(("100", "200"), (page[0].Name, page[0].Value));
        var unfiltered = await session.GetVariablesAsync(big.VariablesReference, 100, 100, Ct); // what a client without the filter asks
        Note($"big[100..] without filter: {unfiltered.Count} items, first {unfiltered.FirstOrDefault()?.Name}={unfiltered.FirstOrDefault()?.Value}");

        // nested object
        var nested = await session.GetVariablesAsync(V("nested").VariablesReference, ct: Ct);
        var inner = Assert.Single(nested, v => v.Name == "inner");
        Assert.Contains("depth: 2", inner.Value);
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Set_variable_evaluate_and_completions_in_the_stopped_frame()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var top = frames[0].Id;
        Assert.Equal("52", (await session.EvaluateAsync("x + 1", top, "repl", Ct)).Result);
        Assert.Equal("5", (await session.EvaluateAsync("p.length", top, "watch", Ct)).Result);
        Assert.Equal("'a,b'", (await session.EvaluateAsync("[...m.keys()].join()", top, "repl", Ct)).Result);
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await session.EvaluateAsync("nope.nope", top, "watch", Ct));
        Note("evaluate error: " + error.Message);

        var scopes = await session.GetScopesAsync(top, Ct);
        var local = scopes.First(s => s.PresentationHint == "locals" || s.Name.StartsWith("Local", StringComparison.Ordinal));
        Assert.True(session.Capabilities.SupportsSetVariable);
        var set = await session.SetVariableAsync(local.VariablesReference, "x", "100", Ct);
        Assert.Equal("100", set.Value);
        Assert.Equal("101", (await session.EvaluateAsync("x + 1", top, "repl", Ct)).Result);

        Assert.True(session.Capabilities.SupportsCompletionsRequest);
        var completions = await session.CompletionsAsync(top, "s.toUpp", 7, Ct);
        Note("completions for 's.toUpp': " + string.Join(", ", completions.Take(10).Select(c => $"{c.Label}({c.Type}) start={c.Start} len={c.Length}")));
        Assert.Contains(completions, c => c.Label == "toUpperCase");
        var globals = await session.CompletionsAsync(top, "Ma", 2, Ct);
        Assert.Contains(globals, c => c.Label == "Math");
        Assert.Contains(globals, c => c.Label == "Map");
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Set_variable_after_expanding_objects_and_running_a_getter_reaches_the_program()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("call"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var (frames, session) = await rig.TopFramesAsync();
        var top = frames[0].Id;
        var scopes = await session.GetScopesAsync(top, Ct);
        var local = scopes.First(s => s.PresentationHint == "locals");
        var vars = await session.GetVariablesAsync(local.VariablesReference, ct: Ct);
        var steps = new List<string>();
        async Task<string> X() => (await session.EvaluateAsync("x", top, "repl", Ct)).Result;
        var p = vars.Single(v => v.Name == "p");
        var pChildren = await session.GetVariablesAsync(p.VariablesReference, ct: Ct);
        steps.Add("after expanding p: x=" + await X());
        var getter = pChildren.Single(v => v.Name == "length");
        await session.GetVariablesAsync(getter.VariablesReference, ct: Ct); // runs the getter
        steps.Add("after the getter: x=" + await X());
        var big = vars.Single(v => v.Name == "big");
        await session.GetVariablesAsync(big.VariablesReference, "indexed", 100, 100, Ct);
        steps.Add("after paging big: x=" + await X());
        await session.CompletionsAsync(top, "s.toUpp", 7, Ct);
        steps.Add("after completions: x=" + await X());
        try { await session.EvaluateAsync("nope.nope", top, "repl", Ct); } catch (Exception) { }
        steps.Add("after a failing evaluation: x=" + await X());
        var set = await session.SetVariableAsync(local.VariablesReference, "x", "100", Ct);
        steps.Add($"setVariable answered {set.Value}: x=" + await X());
        Note("set variable after other requests: " + string.Join(" → ", steps));
        Assert.Equal("100", await X());
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Program_stdout_and_stderr_reach_the_debug_console_with_their_categories()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        await rig.StartAsync();
        await rig.WaitForEndAsync();
        var output = rig.OutputSnapshot;
        Note("output events: " + string.Join(" | ", output.Where(o => o.Category is "stdout" or "stderr" or "console").Select(o => $"[{o.Category}] {o.Output.TrimEnd()} @{Path.GetFileName(o.Source?.Path)}:{(o.Line is { } l ? l + 1 : 0)}")));
        Assert.Equal(["loop 0\n", "loop 1\n", "loop 2\n", "loop 3\n", "loop 4\n"], output.Where(o => o.Category == "stdout" && o.Output.StartsWith("loop", StringComparison.Ordinal)).Select(o => o.Output));
        var err = Assert.Single(output, o => o.Output == "err 52\n");
        Assert.Equal("stderr", err.Category);
        Assert.Contains(output, o => o.Category == "stdout" && o.Output.StartsWith("done hello", StringComparison.Ordinal));
    }

    /// <summary>
    /// js-debug's <c>outputCapture</c> semantics (documented adapter behaviour, as in VS Code): the default "console" shows console.* calls only,
    /// so text written with process.stdout.write never reaches the Debug Console; "std" captures the streams (and node's own "Debugger
    /// attached." on stderr); <c>console: "integratedTerminal"</c> makes js-debug ask us to run the program (runInTerminal), and the session pipes
    /// its streams to the Debug Console (there is no terminal UI).
    /// </summary>
    [Fact]
    public async Task Raw_stdout_writes_reach_the_console_with_outputCapture_std_or_a_terminal_launch_but_not_by_default()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        async Task<List<string>> RunAsync(string name, Dictionary<string, object?> extra)
        {
            lock (rig.Output) rig.Output.Clear();
            extra["args"] = new[] { "flood", "3" };
            await rig.StartAsync(rig.Config(name, extra));
            await rig.WaitForEndAsync();
            await rig.AssertNothingLeftRunningAsync();
            return [.. rig.OutputSnapshot.Where(o => o.Category is "stdout" or "stderr").Select(o => $"[{o.Category}]{o.Output.TrimEnd()}")];
        }
        var byDefault = await RunAsync("default-capture", new());
        var withStd = await RunAsync("std-capture", new() { ["outputCapture"] = "std" });
        var inTerminal = await RunAsync("terminal", new() { ["console"] = "integratedTerminal" });
        Note($"process.stdout.write + console.log — outputCapture console (default): {string.Join(" | ", byDefault)}; outputCapture std: {string.Join(" | ", withStd).Replace("\n", "\\n", StringComparison.Ordinal)}; console integratedTerminal: {string.Join(" | ", inTerminal).Replace("\n", "\\n", StringComparison.Ordinal)}");
        Assert.DoesNotContain(byDefault, o => o.Contains("flood line 2", StringComparison.Ordinal));
        Assert.Contains(byDefault, o => o.Contains("flood done", StringComparison.Ordinal));
        Assert.Contains(withStd, o => o.Contains("flood line 2", StringComparison.Ordinal));
        Assert.Contains(inTerminal, o => o.Contains("flood line 2", StringComparison.Ordinal));
        // There is no terminal: a terminal launch runs in the console with the streams captured, so a console.log line shows ONCE (it used to
        // come twice: through the runInTerminal pipe and from js-debug's own console capture).
        Assert.Equal(1, string.Concat(inTerminal).Split("flood done").Length - 1);
    }

    // ------------------------------------------------------------------ processes the program starts, stop while running, shutdown

    private static string WriteProgram(RealJsDebugRig rig, string name, string source)
    {
        var path = rig.Sample.Dir.Combine(name);
        File.WriteAllText(path, source);
        return path;
    }

    /// <summary>Spawns `sleep 600` detached into its own session (Node's detached + unref: how `open`/xdg-open start a browser), prints its pid.</summary>
    private const string DetachProgram = """
        const { spawn } = require("child_process");
        const child = spawn("sleep", ["600"], { detached: true, stdio: "ignore" });
        child.unref();
        console.log("detached " + child.pid);
        if (process.argv[2] === "stay") setInterval(() => {}, 1000);

        """;

    private static async Task<int> DetachedPidAsync(RealJsDebugRig rig)
    {
        var pid = 0;
        await SessionHarness.Eventually(() =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(rig.OutputText, @"detached (\d+)");
            if (m.Success) pid = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return pid > 0;
        }, 30_000, "the program reported the pid it detached");
        return pid;
    }

    private static void KillTestProcess(int pid)
    {
        if (pid <= 0) return;
        try { using var p = Process.GetProcessById(pid); p.Kill(); } // the sleep this test's program started
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [Fact]
    public async Task A_process_the_program_detached_on_purpose_outlives_a_session_that_ended_normally()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var program = WriteProgram(rig, "detach.js", DetachProgram);
        var detached = 0;
        try
        {
            await rig.StartAsync(rig.Config("detach", program: program));
            detached = await DetachedPidAsync(rig);
            await rig.WaitForEndAsync();
            await SessionHarness.Eventually(() => rig.ServerPids.All(p => !ProcessTable.IsAlive(p)), 10_000, "the js-debug server is gone");
            await Task.Delay(1_500, Ct);
            Note($"detached process {detached} ({ProcessTable.CommandLine(detached)}) after the session ended: alive={ProcessTable.IsAlive(detached)}");
            Assert.True(ProcessTable.IsAlive(detached), "a process the program detached on purpose (a browser it opened) must not be killed with the session");
        }
        finally { KillTestProcess(detached); }
    }

    [Fact]
    public async Task An_adapter_crash_reaps_the_orphaned_debuggee_but_not_a_process_it_detached_on_purpose()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var program = WriteProgram(rig, "detach.js", DetachProgram);
        var detached = 0;
        try
        {
            var parent = await rig.StartAsync(rig.Config("detach-stay", new Dictionary<string, object?> { ["args"] = new[] { "stay" } }, program));
            detached = await DetachedPidAsync(rig);
            var server = parent.AdapterProcessId!.Value;
            var marker = parent.AdapterProcessMarker!;
            var debuggees = ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Where(p => p != server && p != detached).ToList();
            Note($"before the crash: server {server} sid {CsCode.Dap.Transport.AdapterProcessReaper.SessionOf(server)}; debuggees " +
                string.Join(", ", debuggees.Select(p => $"{p} sid {CsCode.Dap.Transport.AdapterProcessReaper.SessionOf(p)} '{ProcessTable.CommandLine(p)}'")) +
                $"; detached {detached} sid {CsCode.Dap.Transport.AdapterProcessReaper.SessionOf(detached)}");
            Assert.NotEmpty(debuggees);
            using (var p = Process.GetProcessById(server)) p.Kill(); // the adapter alone dies
            await rig.WaitForEndAsync(20_000);
            foreach (var pid in debuggees) await SessionHarness.Eventually(() => !ProcessTable.IsAlive(pid), 10_000, $"orphaned debuggee {pid} reaped");
            Assert.True(ProcessTable.IsAlive(detached), "the process the program detached into its own session is left running");
        }
        finally { KillTestProcess(detached); }
    }

    [Fact]
    public async Task Stop_while_the_program_runs_ends_every_session_and_process_quickly()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        await rig.StartAsync(rig.Config("serve", new Dictionary<string, object?> { ["args"] = new[] { "serve" } }));
        await SessionHarness.Eventually(() => { lock (rig.Started) return rig.Started.OfType<DebugSession>().Any(s => s.Parent is not null && s.State == DebugSessionState.Running); },
            30_000, "the program runs in the child session");
        await Task.Delay(300, Ct); // a few timer ticks
        var sw = Stopwatch.StartNew();
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        Note($"stop while running: every session ended after {sw.ElapsedMilliseconds} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// Since a normal close no longer reaps processes that left the server's tree: a program that starts another Node.js process (js-debug
    /// auto-attaches it as a grandchild session, with its watchdog processes) still leaves nothing behind when debugging is stopped.
    /// </summary>
    [Fact]
    public async Task Stop_ends_a_program_that_started_another_node_process_with_every_session_and_process()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var program = WriteProgram(rig, "parent.js", """
            const { spawn } = require("child_process");
            const child = spawn(process.execPath, ["-e", "setInterval(() => console.log('child tick'), 200)"], { stdio: "inherit" });
            console.log("spawned " + child.pid);
            setInterval(() => {}, 1000);

            """);
        await rig.StartAsync(rig.Config("parent", program: program));
        await SessionHarness.Eventually(() => rig.OutputText.Contains("child tick", StringComparison.Ordinal), 30_000, "the second node process runs");
        await Task.Delay(300, Ct);
        string sessions;
        lock (rig.Started) sessions = string.Join(", ", rig.Started.Select(s => $"{s.Name}{(s.Parent is { } p ? " <- " + p.Name : "")}"));
        var marked = rig.Markers.SelectMany(m => ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", m)).Select(p => $"{p} '{ProcessTable.CommandLine(p)}'").ToList();
        var sw = Stopwatch.StartNew();
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        Note($"a program with a second node process: sessions {sessions}; processes carrying the marker before Stop: {string.Join(" | ", marked)}; everything ended after {sw.ElapsedMilliseconds} ms");
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Disposing_the_debug_service_synchronously_while_the_lane_is_busy_leaves_no_js_debug_process()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var release = new int[1];
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.H.Lane.Post(() =>
        {
            blocked.TrySetResult();
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            while (Volatile.Read(ref release[0]) == 0 && Stopwatch.GetTimestamp() < deadline) Thread.Sleep(5);
        });
        await blocked.Task;
        try
        {
            var sw = Stopwatch.StartNew();
            rig.Service.Dispose(); // what a synchronous container dispose does; the async shutdown cannot run on the busy lane
            await rig.AssertNothingLeftRunningAsync(3_000);
            Note($"synchronous dispose with the lane busy: js-debug and the program gone after {sw.ElapsedMilliseconds} ms");
        }
        finally { Volatile.Write(ref release[0], 1); }
    }

    // ------------------------------------------------------------------ attach

    [Fact]
    public async Task Attach_to_a_node_process_started_with_inspect_and_detach_leaves_it_running()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var psi = new ProcessStartInfo(JsDebugToolchain.FindNode()!) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = rig.Sample.Dir.Path };
        foreach (var a in new[] { "--inspect=127.0.0.1:0", rig.Sample.MainJs, "serve" }) psi.ArgumentList.Add(a);
        using var target = Process.Start(psi)!;
        try
        {
            var port = 0;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (port == 0 && DateTime.UtcNow < deadline && await target.StandardError.ReadLineAsync(Ct) is { } line)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"ws://127\.0\.0\.1:(\d+)/");
                if (m.Success) port = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            Assert.True(port > 0, "node printed its inspector port");
            _ = target.StandardError.ReadToEndAsync(Ct);
            _ = target.StandardOutput.ReadToEndAsync(Ct);

            rig.H.Breakpoints.Add(rig.MainUri, InMain("tick"));
            var config = ServiceHarness.Config("attach", "pwa-node", "attach", new Dictionary<string, object?>
            {
                ["port"] = port, ["address"] = "127.0.0.1", ["outFiles"] = new[] { rig.Sample.OutDir + "/**/*.js" }, ["sourceMaps"] = true,
                ["skipFiles"] = new[] { "<node_internals>/**" }, ["cwd"] = rig.Sample.Dir.Path,
            }, rig.Sample.Dir.Uri);
            var parent = await rig.StartAsync(config);
            var loc = await rig.WaitForStopAsync(1);
            AssertAt(loc, "main.ts", InMain("tick"));
            Note($"attach: parent '{parent.Name}' attach={parent.IsAttach}; stopped in '{loc.Session.Name}' (attach={((DebugSession)loc.Session).IsAttach})");
            var (frames, session) = await rig.TopFramesAsync();
            var locals = await rig.LocalsAsync(frames[0].Id, session);
            Assert.Contains(locals, v => v.Name == "ticks");

            await rig.Service.StopAllAsync(Ct); // detach
            await rig.WaitForEndAsync();
            await rig.AssertNothingLeftRunningAsync();
            await Task.Delay(500, Ct);
            Assert.False(target.HasExited, "detaching must leave the attached program running");
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync(Ct);
        }
    }

    [Fact]
    public async Task An_adapter_crash_ends_both_sessions_and_reaps_the_orphaned_debuggee()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        var parent = await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var server = parent.AdapterProcessId!.Value;
        var marker = parent.AdapterProcessMarker!;
        var debuggees = ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", marker).Where(p => p != server).ToList();
        Note($"before the crash: server {server}, other processes carrying its marker: {string.Join(", ", debuggees.Select(p => $"{p} {ProcessTable.CommandLine(p)}"))}");
        Assert.NotEmpty(debuggees);
        using (var p = Process.GetProcessById(server)) p.Kill(); // the adapter alone dies (SIGKILL): its debuggee is orphaned
        await rig.WaitForEndAsync(20_000);
        await rig.AssertNothingLeftRunningAsync();
        Note("after the crash: terminations " + string.Join(" | ", rig.Terminated.Select(t => $"{t.Session.Name}: {t.Reason}")));
    }

    [Fact]
    public async Task Shutting_the_debug_service_down_while_stopped_ends_every_process()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        await rig.StartAsync();
        await rig.WaitForStopAsync(1);
        var sw = Stopwatch.StartNew();
        await rig.Service.DisposeAsync(); // app shutdown
        Note($"debug service disposed while stopped in {sw.ElapsedMilliseconds} ms");
        // The app may exit right after its shutdown: the server's socket directory must already be gone, not deleted a moment later.
        string[] sockets;
        lock (rig.SocketDirectories) sockets = [.. rig.SocketDirectories];
        Assert.NotEmpty(sockets);
        Assert.All(sockets, dir => Assert.False(Directory.Exists(dir), $"{dir} is deleted when DisposeAsync returns"));
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ the templates

    [Fact]
    public async Task The_typescript_template_builds_with_tsc_through_its_preLaunchTask_and_stops_in_the_ts_source()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        using var fresh = JsDebugSample.Write("jsdebug-template-"); // sources only: the preLaunchTask must compile them
        var folder = fresh.Dir.Uri;
        var launchJson = await rig.H.Configurations.CreateLaunchJsonAsync("pwa-node", folder, Ct);
        Note("generated launch.json: " + File.ReadAllText(launchJson.LocalPath).Replace("\n", " ", StringComparison.Ordinal));
        Note("generated tasks.json: " + File.ReadAllText(fresh.Dir.Combine(".cscode", "tasks.json")).Replace("\n", " ", StringComparison.Ordinal));
        var config = (await rig.H.Configurations.GetLaunchConfigurationsAsync(folder, Ct)).Single(c => c.Name == "TypeScript: Launch (tsc build)");
        Assert.False(File.Exists(fresh.MainJs));
        rig.H.Breakpoints.Add(new Uri(fresh.MainTs), InMain("loop"));

        await rig.StartAsync(config);
        Assert.True(File.Exists(fresh.MainJs + ".map"), "the preLaunchTask ran tsc");
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(fresh.MainTs, loc.Uri?.LocalPath);
        Assert.Equal(InMain("loop"), loc.Line);
        Note("Tasks channel: " + rig.H.Output.Text(CsCode.Dap.Tasks.TaskService.ChannelName).Replace("\n", " | ", StringComparison.Ordinal));
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>
    /// A package whose tsc outDir is OUTSIDE the opened folder (a monorepo package with "outDir": "../build"): the generated template must point
    /// outFiles there and let js-debug read source maps there, or no .ts breakpoint binds.
    /// </summary>
    [Fact]
    public async Task The_typescript_template_of_a_package_whose_outDir_is_outside_the_folder_stops_in_the_ts_source()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        using var repo = new TempDirectory("jsdebug-monorepo-");
        repo.WriteFile("pkg/src/main.ts", JsDebugSample.MainSource);
        repo.WriteFile("pkg/src/util.ts", JsDebugSample.UtilSource);
        repo.WriteFile("pkg/tsconfig.json", JsDebugSample.TsConfig.Replace("\"outDir\": \"out\"", "\"outDir\": \"../build\"", StringComparison.Ordinal));
        var folder = new Uri(repo.Combine("pkg") + "/");
        var launchJson = await rig.H.Configurations.CreateLaunchJsonAsync("pwa-node", folder, Ct);
        Note("generated launch.json (outDir ../build): " + File.ReadAllText(launchJson.LocalPath).Replace("\n", " ", StringComparison.Ordinal));
        var config = (await rig.H.Configurations.GetLaunchConfigurationsAsync(folder, Ct)).Single(c => c.Name == "TypeScript: Launch (tsc build)");
        var mainTs = repo.Combine("pkg", "src", "main.ts");
        var bp = rig.H.Breakpoints.Add(new Uri(mainTs), InMain("loop"));

        await rig.StartAsync(config);
        Assert.True(File.Exists(repo.Combine("build", "main.js.map")), "the preLaunchTask compiled into ../build");
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal(mainTs, loc.Uri?.LocalPath);
        Assert.Equal(InMain("loop"), loc.Line);
        Assert.True(rig.H.Breakpoints.GetStatus(bp.Id) is { Verified: true });
        await rig.Service.StopAllAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    // ------------------------------------------------------------------ leaks

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")] // skipped elsewhere
    public async Task Js_debug_listens_on_a_private_unix_socket_only_this_user_can_reach_and_opens_no_tcp_port()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads the server's sockets from Linux /proc");
        await using var rig = await RealJsDebugRig.CreateAsync();
        // A CsCode killed during a session left its socket directory (and the socket file) behind: the next start removes it.
        var root = Path.TrimEndingDirectorySeparator(CsCode.Dap.Transport.PrivateSocket.DefaultRoot());
        var stale = Path.Combine(root, $"{CsCode.Dap.Adapters.JsDebugAdapterFactory.SocketPrefix}-{SocketTable.DeadPid()}-1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(stale, CsCode.Dap.Transport.PrivateSocket.SocketFileName), "");
        try
        {
            rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
            var parent = await rig.StartAsync();
            var loc = await rig.WaitForStopAsync(1);
            var child = (DebugSession)loc.Session;
            Assert.False(Directory.Exists(stale), "the stale socket directory of a killed CsCode is removed at the next start");

            var path = Assert.IsType<DebugAdapterNamedPipe>(parent.AdapterEndpoint).Path;
            Assert.Equal(parent.AdapterEndpoint, child.AdapterEndpoint); // the program's session connects to the same socket
            var dir = parent.AdapterSocketDirectory!;
            Assert.Equal(Path.Combine(dir, CsCode.Dap.Transport.PrivateSocket.SocketFileName), path);
            Assert.Equal(root, Path.GetDirectoryName(dir)); // $XDG_RUNTIME_DIR when set, else the temp directory
            Assert.Equal((SocketTable.MyUid(), "700"), SocketTable.OwnerAndMode(dir));
            var server = parent.AdapterProcessId!.Value;
            var unix = SocketTable.UnixSocketPaths(server);
            var tcp = SocketTable.TcpListeners(server);
            Note($"js-debug server pid {server}: listens on {path} (dir mode {SocketTable.OwnerAndMode(dir).Mode}, owner uid {SocketTable.OwnerAndMode(dir).Uid}); TCP listeners: [{string.Join(", ", tcp)}]; unix sockets: [{string.Join(", ", unix)}]");
            Assert.Contains(path, unix);
            Assert.Empty(tcp);
            foreach (var program in ProcessTable.WithEnvironment("CSCODE_DAP_ADAPTER", parent.AdapterProcessMarker!).Where(p => p != server))
                Note($"debuggee pid {program} ({ProcessTable.CommandLine(program)}): TCP listeners [{string.Join(", ", SocketTable.TcpListeners(program))}]");

            await rig.Service.StopAllAsync(Ct);
            await rig.WaitForEndAsync();
            // the session list empties first; the transport's close (server killed, then the directory deleted) completes right after
            await SessionHarness.Eventually(() => !Directory.Exists(dir), 10_000, "the socket directory goes with the session");
            await rig.AssertNothingLeftRunningAsync();
        }
        finally
        {
            if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
        }
    }

    [Fact]
    public async Task Ten_real_sessions_leave_no_processes_threads_or_retained_sessions()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.MainUri, InMain("loop"));
        // warm up (lane thread, JIT, thread pool, the TCP stack) so the baseline is meaningful
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
        Assert.Equal(20, weak.Count); // a parent and a child per run
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
        Note($"10-session soak: {weak.Count} sessions collected, {rig.ServerPids.Count} js-debug servers all gone, threads {baselineThreads} -> {threads}");
        Assert.InRange(threads, 0, baselineThreads + 6);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference<IDebugSession>>> RunSessionsAsync(RealJsDebugRig rig, int count)
    {
        var weak = new List<WeakReference<IDebugSession>>();
        for (var i = 0; i < count; i++)
        {
            await rig.StartAsync(rig.Config("soak" + i));
            await rig.WaitForStopAsync(i + 1);
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

    // ------------------------------------------------------------------ flood

    [Fact]
    public async Task A_million_line_flood_is_bounded_and_the_debug_lane_stays_responsive()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var lane = rig.H.Lane;
        var latencies = new List<double>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var probe = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                await lane.RunAsync(() => Task.FromResult(0));
                lock (latencies) latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                try { await Task.Delay(5, cts.Token); } catch (OperationCanceledException) { }
            }
        }, CancellationToken.None);

        var before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        await rig.StartAsync(rig.Config("flood", new Dictionary<string, object?> { ["args"] = new[] { "flood", "1000000" }, ["outputCapture"] = "std" }));
        await rig.WaitForEndAsync(120_000);
        sw.Stop();
        cts.Cancel();
        await probe;
        double[] sorted;
        lock (latencies) sorted = [.. latencies.Order()];
        var p99 = sorted[(int)(sorted.Length * 0.99)];
        var output = rig.OutputSnapshot;
        var lines = output.Sum(o => o.Output.Count(c => c == '\n'));
        var dropped = rig.Started.OfType<DebugSession>().Sum(s => s.DroppedOutputChars);
        var history = rig.Service.GetOutputHistory().Sum(e => e.Output.Count(c => c == '\n'));
        Note($"1M-line flood through js-debug (outputCapture std): {sw.ElapsedMilliseconds} ms, {output.Count} output events, {lines:N0} lines delivered, {dropped:N0} chars dropped, history {history:N0} lines, lane round trip p99 {p99:F1} ms max {sorted[^1]:F1} ms ({sorted.Length} probes), heap delta {(GC.GetTotalMemory(true) - before) / 1024} KB");
        Assert.True(p99 < 250, $"lane round trip p99 {p99:F1} ms during the flood");
        Assert.Contains("flood done", rig.OutputText);
        Assert.InRange(history, 1, 12_000);
        Assert.True(GC.GetTotalMemory(true) - before < 64L * 1024 * 1024, "heap stays bounded");
        await rig.AssertNothingLeftRunningAsync();
    }

    /// <summary>One <c>console.log</c> per line: with js-debug's default capture every line is its own DAP output event (1,000,000 events).</summary>
    internal const string LogsProgram = """
        const n = Number(process.argv[2] || "1000000");
        for (let i = 0; i < n; i++) console.log("log line " + i);
        console.log("logs done");

        """;

    [Fact]
    public async Task A_million_console_log_lines_each_its_own_event_stay_bounded_and_keep_the_debug_lane_responsive()
    {
        await using var rig = await RealJsDebugRig.CreateAsync();
        var program = WriteProgram(rig, "logs.js", LogsProgram);
        var lane = rig.H.Lane;
        var latencies = new List<double>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var probe = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var t0 = Stopwatch.GetTimestamp();
                await lane.RunAsync(() => Task.FromResult(0));
                lock (latencies) latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                try { await Task.Delay(5, cts.Token); } catch (OperationCanceledException) { }
            }
        }, CancellationToken.None);
        var events = 0;
        using var counting = rig.Service.Output.Subscribe(_ => Interlocked.Increment(ref events));

        var before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        await rig.StartAsync(rig.Config("logs", new Dictionary<string, object?> { ["args"] = new[] { "1000000" } }, program)); // default outputCapture
        await rig.WaitForEndAsync(240_000);
        sw.Stop();
        cts.Cancel();
        await probe;
        double[] sorted;
        lock (latencies) sorted = [.. latencies.Order()];
        var p99 = sorted[(int)(sorted.Length * 0.99)];
        var history = rig.Service.GetOutputHistory().Sum(e => e.Output.Count(c => c == '\n'));
        int recorded;
        lock (rig.Output)
        {
            recorded = rig.Output.Count;
            rig.Output.Clear(); // the rig's own record of every event (a million of them) is test memory, not the product's
        }
        var heap = GC.GetTotalMemory(true) - before;
        long dropped;
        lock (rig.Started) dropped = rig.Started.OfType<DebugSession>().Sum(s => s.DroppedOutputChars);
        Assert.Equal(Volatile.Read(ref events), recorded);
        Note($"1M console.log lines through js-debug (one event each): {sw.ElapsedMilliseconds} ms, {Volatile.Read(ref events):N0} output events reached the service, {dropped:N0} chars dropped, history {history:N0} lines, lane round trip p99 {p99:F1} ms max {sorted[^1]:F1} ms ({sorted.Length} probes), heap delta {heap / 1024} KB");
        Assert.True(Volatile.Read(ref events) > 0);
        Assert.True(p99 < 250, $"lane round trip p99 {p99:F1} ms during the flood");
        Assert.Contains(rig.Service.GetOutputHistory(), e => e.Output == "logs done\n");
        Assert.InRange(history, 1, DebugServiceHistory.MaxLines);
        Assert.True(heap < 64L * 1024 * 1024, "heap stays bounded");
        await rig.AssertNothingLeftRunningAsync();
    }
}
