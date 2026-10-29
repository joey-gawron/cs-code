using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Protocol;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Regression tests (fake adapters over real loopback TCP) for the defects the js-debug evaluation matrix found in the DAP module
/// (docs/DEBUGGING-RUST-TS.md). Each one fails without its fix.
/// </summary>
public class JsDebugWaveFixTests
{

    [Fact]
    public async Task A_child_session_connects_to_its_parents_adapter_server_instead_of_asking_the_factory_again()
    {
        await using var h = new ServiceHarness();
        var parent = (CsCode.Dap.Session.DebugSession)await h.StartAsync();
        Assert.Equal(1, h.FakeFactory.Calls);
        Assert.Equal(("127.0.0.1", h.Host.Port), parent.AdapterEndpoint);
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new JsonObject { ["name"] = "child", ["__pendingTargetId"] = "t1" },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        var child = (CsCode.Dap.Session.DebugSession)h.Service.Sessions[1];
        Assert.Same(parent, child.Parent);
        Assert.Equal(parent.AdapterEndpoint, child.AdapterEndpoint);
        // js-debug's pending target only exists on the server the parent started: the factory (which would start ANOTHER server) is not asked.
        Assert.Equal(1, h.FakeFactory.Calls);
        Assert.Equal("t1", h.AdapterFor(1).RequestsOf("launch")[0].Arguments.GetProperty("__pendingTargetId").GetString());
    }

    [Fact]
    public async Task Child_sessions_end_when_their_parent_ends()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync();
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new JsonObject { ["name"] = "child" },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        var child = h.Service.Sessions[1];
        await h.AdapterFor(0).SendTerminatedAsync(); // the parent ends (its adapter server would take the children's connections with it)
        await SessionHarness.Eventually(() => child.State == DebugSessionState.Terminated, 10_000, "the child ended with its parent");
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
        Assert.True(h.AdapterFor(1).CountOf("disconnect") + h.AdapterFor(1).CountOf("terminate") > 0, "the child was asked to end");
    }

    [Fact]
    public async Task The_restart_command_restarts_the_top_session_when_a_child_has_the_focus()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Capabilities["supportsRestartRequest"] = true);
        var parent = await h.StartAsync();
        await h.AdapterFor(0).RequestFromAdapterAsync("startDebugging", new DapStartDebuggingArguments
        {
            Request = "launch", Configuration = new JsonObject { ["name"] = "child" },
        });
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 2);
        Assert.NotSame(parent, h.Service.ActiveSession); // the new child took the focus, as it does when it stops
        await h.Commands.ExecuteAsync(KnownCommands.DebugRestart);
        await h.AdapterFor(0).WaitForRequestAsync("restart");
        Assert.Equal(0, h.AdapterFor(1).CountOf("restart")); // js-debug closes a child's connection on `restart`
    }

    [Fact]
    public async Task Function_breakpoints_an_adapter_does_not_support_are_reported_unverified_with_the_reason()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Capabilities["supportsFunctionBreakpoints"] = false);
        var fn = h.Breakpoints.AddFunction("add");
        await h.StartAsync();
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(fn.Id) is not null, 5_000, "a verdict for the function breakpoint");
        var status = h.Breakpoints.GetStatus(fn.Id)!;
        Assert.False(status.Verified);
        Assert.Equal("This debug adapter does not support function breakpoints.", status.Message);
        Assert.Equal(0, h.AdapterFor(0).CountOf("setFunctionBreakpoints"));
        // the verdict's own change notification does not loop back into another report
        var requests = h.AdapterFor(0).Received.Count;
        await Task.Delay(300, Ct);
        Assert.Equal(requests, h.AdapterFor(0).Received.Count);
    }

    [Fact]
    public async Task After_a_flood_batch_the_console_history_still_holds_the_newest_5000_lines()
    {
        await using var h = new ServiceHarness();
        await h.StartAsync();
        var first = string.Concat(Enumerable.Range(0, 4_000).Select(i => $"a{i}\n"));
        var second = string.Concat(Enumerable.Range(0, 3_000).Select(i => $"b{i}\n"));
        await h.AdapterFor(0).SendOutputAsync(first, "stdout");
        await SessionHarness.Eventually(() => h.Service.GetOutputHistory().Any(e => e.Output.Contains("a3999\n", StringComparison.Ordinal)), 5_000, "first batch in the history");
        await h.AdapterFor(0).SendOutputAsync(second, "stdout");
        await SessionHarness.Eventually(() => h.Service.GetOutputHistory().Any(e => e.Output.Contains("b2999\n", StringComparison.Ordinal)), 5_000, "second batch in the history");
        var text = string.Concat(h.Service.GetOutputHistory().Select(e => e.Output));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(DebugServiceHistory.MaxLines, lines.Length);
        Assert.Equal("a2000", lines[0]); // the oldest 2,000 lines of the first batch were cut, the rest of it kept
        Assert.Equal("b2999", lines[^1]);
    }
}

internal static class DebugServiceHistory
{
    public const int MaxLines = CsCode.Dap.Service.DebugService.MaxHistoryLines;
}
