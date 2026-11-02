using CsCode.Core.Debugging;
using CsCode.Dap.Protocol;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Stage 3 (C matrix): Apple's lldb-dap answers the first <c>threads</c> after a stop with the list it had while running, so a pthread that
/// started since (the one that stopped) is missing; the session asks again until the stopped thread is in the answer, a few times at most.
/// </summary>
public sealed class StaleThreadsTests
{
    [Fact]
    public async Task Threads_are_asked_again_while_the_stopped_thread_is_missing()
    {
        await using var h = await SessionHarness.StartAsync();
        var asked = 0;
        h.Adapter.On("threads", _ => Interlocked.Increment(ref asked) < 3
            ? new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "main" }] }
            : new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "main" }, new DapThread { Id = 2, Name = "worker" }] });
        await h.Adapter.SendStoppedAsync("breakpoint", threadId: 2, allThreads: true);
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Stopped, 5000, "stopped");

        var threads = await h.Session.GetThreadsAsync(Ct);

        Assert.Equal([1, 2], threads.Select(t => t.Id));
        Assert.Equal(3, Volatile.Read(ref asked));
    }

    [Fact]
    public async Task An_adapter_that_never_lists_the_thread_is_asked_a_bounded_number_of_times()
    {
        await using var h = await SessionHarness.StartAsync();
        var asked = 0;
        h.Adapter.On("threads", _ =>
        {
            Interlocked.Increment(ref asked);
            return new DapThreadsResponse { Threads = [new DapThread { Id = 1, Name = "main" }] };
        });
        await h.Adapter.SendStoppedAsync("breakpoint", threadId: 9, allThreads: true);
        await SessionHarness.Eventually(() => h.Session.State == DebugSessionState.Stopped, 5000, "stopped");

        var threads = await h.Session.GetThreadsAsync(Ct);

        Assert.Single(threads);
        Assert.Equal(1 + CsCode.Dap.Session.DebugSession.StaleThreadRetries, Volatile.Read(ref asked));
    }
}
