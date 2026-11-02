using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The debugpy evaluation matrix (docs/PYTHON.md §4) against the REAL adapter (debugpy 1.8.22 from tools/fetch-debugpy.sh) through the real debug
/// service: launch and clean exit, line / conditional / hit-count / log / exception breakpoints, steps, pause, call stacks (generators, async),
/// Locals of every shape with paging, set variable, the REPL with completions, threads, multiprocessing child sessions, attach by connect, the
/// bundled adapter, and a 1M-line output flood. Skips with the reason when the tools are missing.
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
public class RealDebugpyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static int L(string marker) => RealDebugpyRig.Line(marker) - 1; // the debug model's lines are 0-based
    private static void Note(string text) => TestMetrics.Report("[debugpy] " + text);

    private static void AssertAt(DebugStopLocation loc, string marker)
    {
        Assert.NotNull(loc.Uri);
        Assert.EndsWith("main.py", loc.Uri!.LocalPath, StringComparison.Ordinal);
        Assert.Equal(L(marker), loc.Line);
    }

    private static DebugVariable Var(IReadOnlyList<DebugVariable> vars, string name) =>
        vars.FirstOrDefault(v => v.Name == name) ?? throw new Xunit.Sdk.XunitException($"no variable '{name}' in: {string.Join(", ", vars.Select(v => v.Name))}");

    [Fact]
    public async Task Launch_stops_at_a_line_breakpoint_shows_every_kind_of_local_and_exits_cleanly()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.ProgramUri, L("print"));
        var session = await rig.StartAsync(rig.Launch());
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "print");
        Assert.Equal("breakpoint", rig.LastStop.Reason);
        Assert.Contains(session.Capabilities.ExceptionBreakpointFilters ?? [], f => f.Filter == "raised");
        Note("exception filters: " + string.Join(" | ", (session.Capabilities.ExceptionBreakpointFilters ?? []).Select(f => $"{f.Filter}:{f.Label} default={f.Default}")));

        var (frames, s) = await rig.TopFramesAsync();
        Assert.Equal("main", frames[0].Name);
        var locals = await RealDebugpyRig.LocalsAsync(s, frames[0].Id);
        Assert.Equal("10", Var(locals, "total").Value);
        Assert.StartsWith("{'a': 1", Var(locals, "data").Value, StringComparison.Ordinal);
        Assert.Equal("dict", Var(locals, "data").Type);
        Assert.Equal("list", Var(locals, "items").Type);
        Assert.Equal("set", Var(locals, "letters").Type);
        Assert.Equal("Point", Var(locals, "point").Type);
        Assert.Contains("Point(x=1, y=2)", Var(locals, "point").Value, StringComparison.Ordinal);
        Assert.Equal("Box", Var(locals, "box").Type);

        // a class instance expands to its attributes; a dict to its keys
        var boxMembers = await s.GetVariablesAsync(Var(locals, "box").VariablesReference, ct: Ct);
        Assert.Contains(boxMembers, v => v.Name == "name" && v.Value == "'box'");
        var dataMembers = await s.GetVariablesAsync(Var(locals, "data").VariablesReference, ct: Ct);
        Assert.Contains(dataMembers, v => v.Name == "'b'");

        // a large list: paged (debugpy answers start/count), the last page ends at 4999
        var big = Var(locals, "big");
        var all = await s.GetVariablesAsync(big.VariablesReference, ct: Ct);
        Note($"big list unpaged: {all.Count} children; first {string.Join(",", all.Take(3).Select(v => v.Name))}; last {string.Join(",", all.TakeLast(4).Select(v => v.Name + "=" + v.Value))}");
        // debugpy pages a big collection itself (it announces no indexedVariables): the first 100 items, then a "more" node, then len()
        Assert.Null(big.IndexedVariables);
        Assert.Equal("0099", all.Where(v => v.Name.All(char.IsAsciiDigit)).Last().Name);
        Assert.Contains(all, v => v.Name == "len()" && v.Value == "5000");
        var more = await s.GetVariablesAsync(Var(all, "more").VariablesReference, ct: Ct);
        Note($"big list 'more': {more.Count} children; first {more.FirstOrDefault()?.Name}, last {more.LastOrDefault()?.Name}");
        Assert.Equal("[100:1100]", more[0].Name);
        var last = await s.GetVariablesAsync(Var(more, "[4100:5000]").VariablesReference, ct: Ct);
        Assert.Contains(last, v => v.Value == "4999");

        rig.H.Breakpoints.Remove(bp.Id);
        await s.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Contains("done 2 3 2 1 box 5000", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_breakpoint_removed_while_stopped_does_not_stop_again()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var loop = rig.H.Breakpoints.Add(rig.ProgramUri, L("loop"));
        await rig.StartAsync(rig.Launch());
        await rig.WaitForStopAsync(1);
        var s = (DebugSession)rig.Service.StopLocation!.Session;
        rig.H.Breakpoints.Remove(loop.Id);
        await s.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Equal(1, rig.StopCount);
    }

    [Fact]
    public async Task Conditional_hit_count_and_log_breakpoints()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.ProgramUri, L("loop"), condition: "i == 3");
        rig.H.Breakpoints.Add(rig.ProgramUri, L("add"), hitCondition: "4");
        rig.H.Breakpoints.Add(rig.ProgramUri, L("print"), logMessage: "logged total={total}");
        await rig.StartAsync(rig.Launch());

        var first = await rig.WaitForStopAsync(1);
        var (frames, s) = await rig.TopFramesAsync();
        var hit = first.Line == L("add") ? "add" : "loop";
        Note($"first stop with condition i == 3 on @loop and hit count 4 on @add: {hit} (line {first.Line + 1})");
        // the 4th call of add() happens at i == 3 too, after the loop line's stop
        AssertAt(first, "loop");
        Assert.Equal("3", Var(await RealDebugpyRig.LocalsAsync(s, frames[0].Id), "i").Value);
        await s.ContinueAsync(ct: Ct);
        var second = await rig.WaitForStopAsync(2);
        AssertAt(second, "add");
        var (frames2, s2) = await rig.TopFramesAsync();
        Assert.Equal("3", Var(await RealDebugpyRig.LocalsAsync(s2, frames2[0].Id), "b").Value); // the 4th call: add(total, 3)
        await s2.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        Assert.Equal(2, rig.StopCount); // the logpoint never stops
        Assert.Contains("logged total=10", rig.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raised_and_uncaught_exception_filters()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        rig.H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("raised", "Raised Exceptions", true), new ExceptionFilterSetting("uncaught", "Uncaught Exceptions", true)]);
        await rig.StartAsync(rig.Launch("caught"));
        var caught = await rig.WaitForStopAsync(1);
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(caught, "caught");
        await caught.Session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();

        rig.H.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("raised", "Raised Exceptions", false), new ExceptionFilterSetting("uncaught", "Uncaught Exceptions", true)]);
        await rig.StartAsync(rig.Launch("raise"));
        var uncaught = await rig.WaitForStopAsync(2);
        Assert.Equal("exception", rig.LastStop.Reason);
        AssertAt(uncaught, "raise");
        Note($"uncaught stop text: {rig.LastStop.Text} / {rig.LastStop.Description}");
        await uncaught.Session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Steps_and_the_call_stacks_of_a_generator_and_a_coroutine()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var loop = rig.H.Breakpoints.Add(rig.ProgramUri, L("loop"));
        await rig.StartAsync(rig.Launch());
        await rig.WaitForStopAsync(1);
        var s = (DebugSession)rig.Service.StopLocation!.Session;
        rig.H.Breakpoints.Remove(loop.Id);

        await s.StepIntoAsync(ct: Ct);
        var into = await rig.WaitForStopAsync(2);
        AssertAt(into, "add");
        var (frames, _) = await rig.TopFramesAsync();
        Assert.Equal(["add", "main", "<module>"], frames.Take(3).Select(f => f.Name));
        await s.StepOutAsync(ct: Ct);
        var outOf = await rig.WaitForStopAsync(3);
        Assert.Equal(L("loop"), outOf.Line);
        await s.StepOverAsync(ct: Ct);
        await rig.WaitForStopAsync(4);
        Note($"step over from @loop landed on line {rig.Service.StopLocation!.Line + 1}");

        var gen = rig.H.Breakpoints.Add(rig.ProgramUri, L("gen"));
        await s.ContinueAsync(ct: Ct);
        var genStop = await rig.WaitForStopAsync(5);
        var (genFrames, _) = await rig.TopFramesAsync();
        Note($"stop 5: {rig.LastStop.Reason} line {genStop.Line + 1}; frames {string.Join(" < ", genFrames.Take(4).Select(f => $"{f.Name}:{f.Line + 1}"))}");
        Assert.Equal(["gen", "main"], genFrames.Take(2).Select(f => f.Name));
        rig.H.Breakpoints.Remove(gen.Id);

        rig.H.Breakpoints.Add(rig.ProgramUri, L("async"));
        await s.ContinueAsync(ct: Ct);
        var inAsync = await rig.WaitForStopAsync(6);
        AssertAt(inAsync, "async");
        var (asyncFrames, _) = await rig.TopFramesAsync(60);
        Note("coroutine call stack: " + string.Join(" < ", asyncFrames.Select(f => f.Name)));
        Assert.Equal("work", asyncFrames[0].Name);
        Assert.Contains(asyncFrames, f => f.Name == "main"); // through asyncio's event loop frames
        await s.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task Pause_set_variable_and_the_REPL_with_completions()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.ProgramUri, L("print"));
        await rig.StartAsync(rig.Launch("wait"));
        await rig.WaitForStopAsync(1);
        var (frames, s) = await rig.TopFramesAsync();
        rig.H.Breakpoints.Remove(bp.Id);

        // set variable: total is printed right after
        var scopes = await s.GetScopesAsync(frames[0].Id, Ct);
        var changed = await s.SetVariableAsync(scopes[0].VariablesReference, "total", "41 + 1", Ct);
        Assert.Equal("42", changed.Value);

        // REPL: an expression, a statement, completions
        var eval = await s.EvaluateAsync("len(items) + total", frames[0].Id, "repl", Ct);
        Assert.Equal("45", eval.Result);
        var completions = await s.CompletionsAsync(frames[0].Id, "ite", 4, Ct);
        Assert.Contains(completions, c => c.Label == "items");
        var watch = await s.EvaluateAsync("box.items", frames[0].Id, "watch", Ct);
        Assert.Equal("[1, 2, 3]", watch.Result);

        // pause while it sleeps in the wait loop
        await s.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => rig.OutputText.Contains("total 42", StringComparison.Ordinal), 15_000, "the changed total printed");
        await Task.Delay(500, Ct);
        await s.PauseAsync(ct: Ct);
        var paused = await rig.WaitForStopAsync(2);
        Assert.Equal("pause", rig.LastStop.Reason);
        var (pauseFrames, _) = await rig.TopFramesAsync();
        Note("paused in: " + string.Join(" < ", pauseFrames.Take(4).Select(f => $"{f.Name}:{f.Line + 1}")));
        Assert.Contains(pauseFrames, f => f.Name == "main");
        await s.StopAsync(Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Threads_are_listed_and_a_breakpoint_in_a_thread_stops_that_thread()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.ProgramUri, L("thread"));
        var session = await rig.StartAsync(rig.Launch("threads"));
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "thread");
        var threads = await session.GetThreadsAsync(Ct);
        Note("threads: " + string.Join(", ", threads.Select(t => $"{t.Id}:{t.Name}")));
        Assert.Contains(threads, t => t.Name.Contains("spinner", StringComparison.Ordinal));
        Assert.Contains(threads, t => t.Name.Contains("MainThread", StringComparison.Ordinal));
        Assert.Contains(threads, t => t.Id == loc.ThreadId && t.Name.Contains("spinner", StringComparison.Ordinal));
        await session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
    }

    [Fact]
    public async Task A_multiprocessing_child_becomes_a_child_session_that_hits_breakpoints()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        rig.H.Breakpoints.Add(rig.ProgramUri, L("child"));
        var parent = await rig.StartAsync(rig.Launch("mp", new Dictionary<string, object?> { ["subProcess"] = true }));
        var loc = await rig.WaitForStopAsync(1, 60_000);
        AssertAt(loc, "child");
        var child = (DebugSession)loc.Session;
        Assert.Same(parent, child.Parent);
        Assert.True(child.Configuration.IsAttach);
        Assert.True(child.Configuration.Properties.ContainsKey("subProcessId"));
        Note($"child session '{child.Name}' {child.Configuration.Request} subProcessId={child.Configuration.Properties["subProcessId"]}");
        await child.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync(60_000);
        Assert.Contains("child mp", rig.OutputText, StringComparison.Ordinal);
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task Attach_connects_to_a_program_started_with_debugpy_listen()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var port = FreePort();
        using var program = Process.Start(new ProcessStartInfo(rig.Python, ["-m", "debugpy", "--listen", $"127.0.0.1:{port}", "--wait-for-client", rig.ProgramPath])
        { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = rig.H.Dir.Path })!;
        try
        {
            rig.H.Breakpoints.Add(rig.ProgramUri, L("print"));
            var attach = RealDebugpyRig.Raw(new { type = "debugpy", request = "attach", name = "attach", connect = new { host = "localhost", port } }, rig.H.Dir.Uri);
            DebugSession? session = null;
            // debugpy --listen takes a moment to listen; a probe connection could be taken for the client, so the attach itself is retried
            for (var attempt = 0; session is null && attempt < 40; attempt++)
            {
                try { session = (DebugSession?)await rig.Service.StartAsync(attach, Ct); }
                catch (Exception) { session = null; }
                if (session is null) await Task.Delay(250, Ct);
            }
            Assert.NotNull(session);
            Assert.Null(session.AdapterProcessId); // the adapter debugpy --listen started; none of ours
            var loc = await rig.WaitForStopAsync(1);
            AssertAt(loc, "print");
            await session.ContinueAsync(ct: Ct);
            await program.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await rig.WaitForEndAsync();
        }
        finally
        {
            if (!program.HasExited) program.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// Attach by process id: debugpy injects itself into the running interpreter through the OS debugger (gdb on Linux, lldb on macOS), which the OS
    /// may refuse (Linux: ptrace_scope; macOS: developer tools access, hardened runtimes). The outcome is recorded; a refusal skips with its reason.
    /// </summary>
    [Fact]
    public async Task Attach_by_process_id_injects_into_a_running_program()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        using var program = Process.Start(new ProcessStartInfo(rig.Python, [rig.ProgramPath, "wait"]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = rig.H.Dir.Path })!;
        try
        {
            await Task.Delay(1000, Ct); // in the wait loop
            rig.H.Breakpoints.Add(rig.ProgramUri, L("wait"));
            var attach = RealDebugpyRig.Raw(new { type = "debugpy", request = "attach", name = "pid", processId = program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, rig.H.Dir.Uri);
            var session = (DebugSession?)await rig.Service.StartAsync(attach, Ct);
            if (session is null) { Note("attach by pid: not started: " + string.Join(" | ", rig.H.Window.Messages.Select(m => m.Message))); Assert.Skip("attach by process id was refused: " + string.Join(" | ", rig.H.Window.Messages.Select(m => m.Message))); return; }
            try
            {
                await rig.WaitForStopAsync(1, 45_000);
            }
            catch (TimeoutException ex)
            {
                Note("attach by pid: no stop: " + ex.Message + " | output: " + rig.OutputText);
                Assert.Skip("attach by process id did not reach the program (the OS refused the injection?): " + rig.OutputText);
                return;
            }
            AssertAt(rig.Service.StopLocation!, "wait");
            Note($"attach by pid {program.Id}: stopped at @wait");
            await session.StopAsync(Ct);
        }
        finally
        {
            if (!program.HasExited) program.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_bundled_adapter_debugs_with_an_interpreter_that_has_no_debugpy()
    {
        if (DebugpyToolchain.PythonWithoutDebugpy() is not { } python) { Assert.Skip("no interpreter without debugpy on this machine"); return; }
        if (!File.Exists(Path.Combine(DebugpyToolchain.BundledAdapter, "__main__.py"))) { Assert.Skip("the bundled debugpy is missing: run tools/fetch-debugpy.sh"); return; }
        await using var rig = await RealDebugpyRig.CreateAsync(python);
        var factory = new DebugpyAdapterFactory(rig.H.Platform);
        var descriptor = Assert.IsType<DebugAdapterExecutable>(await factory.CreateDescriptorAsync(rig.Launch(), Ct));
        Assert.Equal(python, descriptor.Command);
        Assert.Equal([DebugpyToolchain.BundledAdapter], descriptor.Args);

        rig.H.Breakpoints.Add(rig.ProgramUri, L("print"));
        var session = await rig.StartAsync(rig.Launch());
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "print");
        Note($"bundled adapter with {python}: stopped at line {loc.Line + 1}");
        await session.ContinueAsync(ct: Ct);
        await rig.WaitForEndAsync();
        await rig.AssertNothingLeftRunningAsync();
    }

    [Fact]
    public async Task A_million_lines_of_output_stay_bounded_and_the_session_ends()
    {
        await using var rig = await RealDebugpyRig.CreateAsync();
        var clock = Stopwatch.StartNew();
        var session = await rig.StartAsync(rig.Launch("flood"));
        await rig.WaitForEndAsync(180_000);
        Note($"1M-line flood: ended after {clock.Elapsed.TotalSeconds:F1} s; {rig.OutputText.Length:N0} chars delivered, {session.DroppedOutputChars:N0} dropped");
        Assert.Contains("flood done", rig.OutputText, StringComparison.Ordinal);
        Assert.True(rig.OutputText.Length < 64 * 1024 * 1024, "output is bounded");
        await rig.AssertNothingLeftRunningAsync();
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
