using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Service;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// End-to-end against the REAL Samsung netcoredbg through <c>IDebugService</c>: a tiny C# console project is written to a temp dir,
/// built with the SDK's <c>dotnet build</c>, then debugged. Requires <c>tools/fetch-netcoredbg.sh</c> (→ <c>.scratch/tools/netcoredbg/</c>)
/// and <c>dotnet</c> on PATH; each test SKIPS with an explicit reason when either is missing.
/// Run just these with <c>dotnet test --filter Category=RealAdapter</c>; exclude them with <c>--filter "Category!=RealAdapter"</c>.
/// </summary>
[Trait("Category", RealAdapter)]
public class RealNetCoreDbgTests
{
    private const string RealAdapter = RealAdapterSupport.Category;

    private sealed class Rig : IAsyncDisposable
    {
        public required ServiceHarness H { get; init; }
        public required RealAdapterSupport.SampleBuild Sample { get; init; }
        public List<DebugStoppedEventArgs> Stops { get; } = new();
        public List<DebugOutputEventArgs> Output { get; } = new();
        public List<DebugTerminatedEventArgs> Terminated { get; } = new();
        public Uri SourceUri => new(Sample.SourcePath);
        public DebugService Service => H.Service;

        public async ValueTask DisposeAsync() => await H.DisposeAsync();

        public static async Task<Rig> CreateAsync()
        {
            var exe = RealAdapterSupport.FindNetCoreDbg();
            Assert.SkipUnless(exe is not null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/) or set CSCODE_NETCOREDBG");
            Assert.SkipUnless(RealAdapterSupport.FindDotnet() is not null, "`dotnet` is not on PATH, so the sample project cannot be built");
            var sample = await RealAdapterSupport.BuildSampleAsync(Ct);
            var h = new ServiceHarness();
            h.Settings.Set(NetCoreDbgAdapterFactory.SettingPath, exe);
            return new Rig { H = h, Sample = sample };
        }

        public DebugConfiguration Config(string name = "real", IDictionary<string, object?>? extra = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["program"] = Sample.Dll, ["cwd"] = Sample.Directory, ["args"] = Array.Empty<string>(),
                ["stopAtEntry"] = false, ["justMyCode"] = true, ["console"] = "internalConsole",
            };
            if (extra is not null) foreach (var (k, v) in extra) d[k] = v;
            return ServiceHarness.Config(name, "coreclr", "launch", d, H.Dir.Uri);
        }

        public async Task<DebugSession> StartAsync(DebugConfiguration? config = null)
        {
            var session = (DebugSession)await H.StartAsync(config ?? Config());
            session.Stopped.Subscribe(e => { lock (Stops) Stops.Add(e); });
            session.Output.Subscribe(e => { lock (Output) Output.Add(e); });
            session.Terminated.Subscribe(e => { lock (Terminated) Terminated.Add(e); });
            return session;
        }

        public async Task<DebugStopLocation> WaitForStopAsync(int count, int timeoutMs = 30_000)
        {
            await SessionHarness.Eventually(() => { lock (Stops) return Stops.Count >= count; }, timeoutMs, $"stop #{count}");
            // The service derives the location from the stack asynchronously after the event.
            await SessionHarness.Eventually(() => Service.StopLocation is { Session.State: DebugSessionState.Stopped }, timeoutMs, "stop location");
            return Service.StopLocation!;
        }

        public int StopCount { get { lock (Stops) return Stops.Count; } }
        public string OutputText { get { lock (Output) return string.Concat(Output.Select(o => o.Output)); } }
    }

    private static int Zero(string marker) => RealAdapterSupport.LineOf(marker) - 1;

    private static void AssertAt(DebugStopLocation loc, string marker)
    {
        Assert.Equal("Program.cs", Path.GetFileName(loc.Uri?.LocalPath));
        Assert.Equal(Zero(marker), loc.Line);
    }

    // ------------------------------------------------------------------ the main flow

    [Fact]
    public async Task Breakpoint_locals_evaluate_stepping_and_a_clean_exit_end_to_end()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Toggle(rig.SourceUri, Zero("call"));
        var session = await rig.StartAsync();
        var adapterPid = session.AdapterProcessId;
        Assert.NotNull(adapterPid);

        // Stopped(breakpoint) at the right file/line, top frame is Main
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("breakpoint", rig.Stops[0].Reason);
        AssertAt(loc, "call");
        var frames = await session.GetStackTraceAsync(loc.ThreadId, 0, 10, Ct);
        Assert.Contains("Main", frames[0].Name);
        var debuggeePid = session.DebuggeeProcessId;

        // locals contain x with the right value (41 + 0+1+2+3+4 = 51) and s
        var scopes = await session.GetScopesAsync(frames[0].Id, Ct);
        Assert.NotEmpty(scopes);
        var locals = await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        var x = Assert.Single(locals, v => v.Name == "x");
        Assert.Equal("51", x.Value);
        Assert.Contains("hello", Assert.Single(locals, v => v.Name == "s").Value);

        // evaluate
        Assert.Equal("52", (await session.EvaluateAsync("x + 1", frames[0].Id, "repl", Ct)).Result);
        Assert.Equal("51", (await session.EvaluateAsync("x", frames[0].Id, "watch", Ct)).Result);

        // step into Outer, over, out
        await session.StepIntoAsync(ct: Ct);
        var inOuter = await rig.WaitForStopAsync(2);
        var outerFrames = await session.GetStackTraceAsync(inOuter.ThreadId, 0, 10, Ct);
        Assert.Contains("Outer", outerFrames[0].Name);
        AssertAt(inOuter, "outer");
        await session.StepOverAsync(ct: Ct);
        var next = await rig.WaitForStopAsync(3);
        Assert.Equal(Zero("outer") + 1, next.Line); // `return inner * 2;`
        await session.StepOutAsync(ct: Ct);
        var back = await rig.WaitForStopAsync(4);
        Assert.Contains("Main", (await session.GetStackTraceAsync(back.ThreadId, 0, 10, Ct))[0].Name);

        // continue → runs to exit with the program's exit code
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => { lock (rig.Terminated) return rig.Terminated.Count == 1; }, 30_000, "Terminated");
        Assert.Equal(7, rig.Terminated[0].ExitCode);
        Assert.Contains("loop 4", rig.OutputText);
        Assert.Contains("done hello", rig.OutputText);
        await SessionHarness.Eventually(() => rig.Service.Sessions.Count == 0);
        Assert.Equal(DebugState.Inactive, rig.Service.State);

        // nothing of ours is left running
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(adapterPid!.Value), 10_000, "netcoredbg must exit");
        if (debuggeePid is { } pid) await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive((int)pid), 10_000, "debuggee must exit");
    }

    [Fact]
    public async Task Stopping_a_session_stopped_at_a_breakpoint_kills_adapter_and_debuggee()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Toggle(rig.SourceUri, Zero("loop"));
        var session = await rig.StartAsync();
        var adapterPid = session.AdapterProcessId!.Value;
        await rig.WaitForStopAsync(1);
        var debuggeePid = session.DebuggeeProcessId;
        await session.StopAsync(Ct);
        await SessionHarness.Eventually(() => rig.Service.Sessions.Count == 0);
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(adapterPid), 10_000, "netcoredbg must exit");
        if (debuggeePid is { } pid) await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive((int)pid), 10_000, "debuggee must exit");
    }

    // ------------------------------------------------------------------ breakpoint kinds

    [Fact]
    public async Task Conditional_breakpoint_stops_only_when_the_condition_holds()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Add(rig.SourceUri, Zero("loop"), condition: "i == 3");
        var session = await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "loop");
        var frames = await session.GetStackTraceAsync(loc.ThreadId, 0, 5, Ct);
        var scopes = await session.GetScopesAsync(frames[0].Id, Ct);
        var locals = await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal("3", Assert.Single(locals, v => v.Name == "i").Value);
        Assert.Equal("44", Assert.Single(locals, v => v.Name == "x").Value); // 41+0+1+2 before `x += i` of the 4th pass
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => rig.Service.Sessions.Count == 0, 30_000, "runs to the end: the condition never holds again");
        Assert.Equal(1, rig.StopCount);
    }

    [Fact]
    public async Task Hit_count_breakpoint_stops_on_the_requested_hit()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Add(rig.SourceUri, Zero("loop"), hitCondition: "3");
        var session = await rig.StartAsync();
        var loc = await rig.WaitForStopAsync(1);
        AssertAt(loc, "loop");
        var frames = await session.GetStackTraceAsync(loc.ThreadId, 0, 5, Ct);
        var scopes = await session.GetScopesAsync(frames[0].Id, Ct);
        var locals = await session.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal("2", Assert.Single(locals, v => v.Name == "i").Value); // third pass through the loop body
        await session.StopAsync(Ct);
    }

    [Fact]
    public async Task Logpoint_prints_to_the_debug_console_without_stopping()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Add(rig.SourceUri, Zero("print"), logMessage: "logged i={i}");
        var session = await rig.StartAsync();
        Assert.SkipUnless(session.Capabilities.SupportsLogPoints, "this netcoredbg build does not advertise logpoints");
        await SessionHarness.Eventually(() => { lock (rig.Terminated) return rig.Terminated.Count == 1; }, 30_000, "program runs to the end");
        Assert.Equal(0, rig.StopCount);
        Assert.Contains("logged i=0", rig.OutputText);
        Assert.Contains("logged i=4", rig.OutputText);
    }

    [Fact]
    public async Task Breakpoint_verdicts_are_reported_back_by_the_adapter()
    {
        await using var rig = await Rig.CreateAsync();
        var bp = rig.H.Breakpoints.Add(rig.SourceUri, Zero("call"));
        var session = await rig.StartAsync();
        await SessionHarness.Eventually(() => rig.H.Breakpoints.GetStatus(bp.Id) is not null, 20_000, "adapter verdict");
        Assert.NotNull(rig.H.Breakpoints.GetStatus(bp.Id));
        await session.StopAsync(Ct);
    }

    [Fact]
    public async Task Exception_breakpoint_stops_on_the_thrown_exception_and_reports_it()
    {
        await using var rig = await Rig.CreateAsync();
        var session = await rig.StartAsync();
        Assert.SkipUnless(session.Capabilities.ExceptionBreakpointFilters is { Count: > 0 }, "this netcoredbg build advertises no exception filters");
        // Enable the "all exceptions" filter after launch (breakpoints are mirrored live): the program may already have thrown, so
        // use a fresh session with the filter on from the start.
        await session.StopAsync(Ct);
        var filters = session.Capabilities.ExceptionBreakpointFilters!;
        rig.H.Breakpoints.SetExceptionFilters([.. filters.Select(f => new ExceptionFilterSetting(f.Filter, f.Label, f.Filter == "all"))]);
        var second = await rig.StartAsync(rig.Config("exc"));
        var loc = await rig.WaitForStopAsync(1);
        Assert.Equal("exception", rig.Stops[0].Reason);
        await SessionHarness.Eventually(() => second.LastException is not null, 10_000, "exceptionInfo");
        Assert.Contains("InvalidOperationException", second.LastException!.TypeName ?? second.LastException.ExceptionId);
        Assert.NotNull(loc);
        await second.StopAsync(Ct);
    }

    // ------------------------------------------------------------------ leaks

    [Fact]
    public async Task Ten_real_sessions_leave_no_processes_threads_or_retained_sessions()
    {
        await using var rig = await Rig.CreateAsync();
        rig.H.Breakpoints.Toggle(rig.SourceUri, Zero("loop"));
        // warm up (lane thread, JIT, thread-pool) so the baseline is meaningful
        var warm = (DebugSession)await rig.H.StartAsync(rig.Config("warm"));
        await warm.StopAsync(Ct);
        await SessionHarness.Eventually(() => rig.Service.Sessions.Count == 0);

        var baselineThreads = Process.GetCurrentProcess().Threads.Count;
        var pids = new List<int>();
        var weak = new List<WeakReference<IDebugSession>>();
        for (var i = 0; i < 10; i++)
        {
            var s = await rig.H.StartAsync(rig.Config("s" + i));
            var ds = (DebugSession)s;
            if (ds.AdapterProcessId is { } a) pids.Add(a);
            weak.Add(new WeakReference<IDebugSession>(s));
            await SessionHarness.Eventually(() => ds.State == DebugSessionState.Stopped, 30_000, "stop in the loop");
            await ds.StopAsync(Ct);
            await SessionHarness.Eventually(() => rig.Service.Sessions.Count == 0);
        }
        Assert.Equal(10, pids.Count);
        foreach (var pid in pids) await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 10_000, $"netcoredbg pid {pid}");
        Assert.Equal(0, rig.H.Breakpoints.StatusCount);

        for (var i = 0; i < 10 && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(50, Ct);
        }
        Assert.DoesNotContain(weak, w => w.TryGetTarget(out _));
        await Task.Delay(500, Ct);
        Assert.InRange(Process.GetCurrentProcess().Threads.Count, 0, baselineThreads + 6);
    }
}

/// <summary>Verifies the pieces the real-adapter tests rely on WITHOUT launching netcoredbg (SDK build only).</summary>
[Trait("Category", "RealTools")]
public class RealAdapterSampleTests
{
    [Fact]
    public async Task The_sample_project_builds_with_the_sdk_and_produces_a_portable_pdb()
    {
        Assert.SkipUnless(RealAdapterSupport.FindDotnet() is not null, "`dotnet` is not on PATH");
        var sample = await RealAdapterSupport.BuildSampleAsync(Ct);
        Assert.True(File.Exists(sample.Dll), sample.Dll);
        Assert.True(File.Exists(Path.ChangeExtension(sample.Dll, ".pdb")));
        Assert.Equal(Path.Combine(sample.Directory, "Program.cs"), sample.SourcePath);
    }

    [Fact]
    public void Source_markers_resolve_to_the_expected_lines_in_order()
    {
        var lines = new[] { "add", "outer", "start", "loop", "print", "call", "throw", "done" }.Select(RealAdapterSupport.LineOf).ToArray();
        Assert.Equal(lines.Order(), lines);
        var source = RealAdapterSupport.ProgramSource.Split('\n');
        Assert.Contains("x += i;", source[RealAdapterSupport.LineOf("loop") - 1]);
        Assert.Contains("Outer(x)", source[RealAdapterSupport.LineOf("call") - 1]);
    }

    [Fact]
    public void The_adapter_is_found_where_the_fetch_script_puts_it_or_skipped_with_a_reason()
    {
        var found = RealAdapterSupport.FindNetCoreDbg();
        // Informational: tests that need it skip with an explicit message when it is absent.
        Assert.True(found is null || File.Exists(found));
    }
}
