using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// What the C++ debugging matrix (docs/C-CPP.md) found and CsCode compensates, over fake adapters behaving as the real ones were observed to
/// (<see cref="RealCppDebuggingTests"/>): CodeLLDB's C++ exception filters stop as plain breakpoints, and lldb-dap's first <c>threads</c>
/// answer after a stop on a new thread leaves that thread out.
/// </summary>
public class CppDebuggingQuirksTests
{
    private sealed class LazyPortFactory(Func<int> port) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterServer("127.0.0.1", port()));
    }

    /// <summary>A harness whose type "fakelldb" has the CodeLLDB quirks; the fake advertises CodeLLDB's C++ filters (sourceLanguages ["cpp"]).</summary>
    private static ServiceHarness CodeLldbHarness(Action<FakeDebugAdapter>? configure = null)
    {
        ServiceHarness? harness = null;
        var h = new ServiceHarness(
            configureHost: host => host.Configure = (a, _) =>
            {
                a.Capabilities["exceptionBreakpointFilters"] = new JsonArray(
                    new JsonObject { ["filter"] = "cpp_throw", ["label"] = "C++: on throw", ["default"] = true },
                    new JsonObject { ["filter"] = "cpp_catch", ["label"] = "C++: on catch", ["default"] = false });
                // one LLDB breakpoint per enabled filter, in the order sent: 7 for the first, 8 for the second
                a.On("setExceptionBreakpoints", r => new DapBreakpointsResponse
                {
                    Breakpoints = [.. Enumerable.Range(0, r.Args<DapSetExceptionBreakpointsArguments>().Filters.Count).Select(n => new DapBreakpoint { Id = 7 + n, Verified = true })],
                });
                configure?.Invoke(a);
            },
            moreBuiltins: _ => [new BuiltinDebugAdapter("fakelldb", "fake CodeLLDB", ["cpp"], new LazyPortFactory(() => harness!.Host.Port)) { Quirks = CodeLldbAdapterQuirks.Instance }]);
        harness = h;
        return h;
    }

    [Fact]
    public async Task A_stop_at_codelldbs_cpp_throw_or_catch_filter_is_reported_as_that_exception()
    {
        await using var h = CodeLldbHarness();
        h.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("cpp_throw", "C++: on throw", true), new ExceptionFilterSetting("cpp_catch", "C++: on catch", true)]);
        var session = (DebugSession)await h.StartAsync(ServiceHarness.Config("cpp", "fakelldb", folder: h.Dir.Uri));
        await h.AdapterFor(0).WaitForRequestAsync("setExceptionBreakpoints");
        Assert.Equal(["cpp_throw", "cpp_catch"], h.AdapterFor(0).RequestsOf("setExceptionBreakpoints")[^1].Args<DapSetExceptionBreakpointsArguments>().Filters);
        await session.GetThreadsAsync(Ct); // the setExceptionBreakpoints answer has been handled

        var stops = new List<DebugStoppedEventArgs>();
        session.Stopped.Subscribe(e => { lock (stops) stops.Add(e); });
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [7]);
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count == 1; });
        Assert.Equal(("exception", "Paused on exception: C++ throw", "A C++ exception was thrown."), (stops[0].Reason, stops[0].Description, stops[0].Text));
        await SessionHarness.Eventually(() => session.LastException is { ExceptionId: "cpp_throw" });

        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running);
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [8]);
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count == 2; });
        Assert.Equal(("exception", "Paused on exception: C++ catch"), (stops[1].Reason, stops[1].Description));

        // a user's breakpoint stays a breakpoint
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running);
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count == 3; });
        Assert.Equal("breakpoint", stops[2].Reason);
    }

    [Fact]
    public async Task Threads_asked_for_again_when_the_first_answer_after_a_stop_leaves_out_the_stopped_thread()
    {
        var answers = 0;
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.On("threads", _ =>
            Interlocked.Increment(ref answers) <= 1
                ? new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "Thread 1" }] }
                : new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "Thread 1" }, new DapThread { Id = 2, Name = "Thread 2" }] }));
        var session = (DebugSession)await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", threadId: 2);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        Volatile.Write(ref answers, 0);
        var threads = await session.GetThreadsAsync(Ct);
        Assert.Equal([(1, "Thread 1"), (2, "Thread 2")], threads.Select(t => (t.Id, t.Name)));
        Assert.Equal(2, Volatile.Read(ref answers));
    }

    [Fact]
    public async Task A_stopped_thread_the_adapter_never_lists_is_listed_anyway()
    {
        await using var h = new ServiceHarness();
        var session = (DebugSession)await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", threadId: 5); // the fake lists thread 1 only
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var threads = await session.GetThreadsAsync(Ct);
        Assert.Equal([(1, "main"), (5, "Thread 5")], threads.Select(t => (t.Id, t.Name)));
    }
}
