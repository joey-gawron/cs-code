using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// What CsCode compensates for CodeLLDB (<see cref="CodeLldbAdapterQuirks"/>), through the real session over a fake adapter that behaves as
/// CodeLLDB v1.12.3's sources say it does: its own "rust_panic" filter, stops at it as reason "breakpoint", hit counts with CodeLLDB's operators.
/// </summary>
public class CodeLldbQuirksTests
{
    private static readonly CodeLldbAdapterQuirks Quirks = CodeLldbAdapterQuirks.Instance;

    /// <summary>A harness whose debug type "fakelldb" has the CodeLLDB quirks and talks to fake adapters advertising CodeLLDB's Rust filter.</summary>
    private static ServiceHarness Harness(Action<FakeDebugAdapter, int>? configure = null)
    {
        ServiceHarness? harness = null;
        var h = new ServiceHarness(
            configureHost: host => host.Configure = (a, i) =>
            {
                // CodeLLDB 1.12.3 with sourceLanguages ["rust"]: only "Rust: on panic" (default on); the setExceptionBreakpoints response lists
                // one breakpoint per filter (LLDB breakpoint ids), and a stop at it is reason "breakpoint" with that id.
                a.Capabilities["exceptionBreakpointFilters"] = new JsonArray(
                    new JsonObject { ["filter"] = "rust_panic", ["label"] = "Rust: on panic", ["default"] = true, ["supportsCondition"] = true });
                a.Capabilities["supportsExceptionFilterOptions"] = true;
                a.On("setExceptionBreakpoints", r =>
                {
                    var args = r.Args<DapSetExceptionBreakpointsArguments>();
                    var count = args.Filters.Count + (args.FilterOptions?.Count ?? 0);
                    return new DapBreakpointsResponse { Breakpoints = [.. Enumerable.Range(0, count).Select(n => new DapBreakpoint { Id = 7 + n, Verified = true })] };
                });
                configure?.Invoke(a, i);
            },
            moreBuiltins: _ => [new BuiltinDebugAdapter("fakelldb", "fake CodeLLDB", ["rust"], new LazyPortFactory(() => harness!.Host.Port)) { Quirks = CodeLldbAdapterQuirks.Instance }]);
        harness = h;
        return h;
    }

    private sealed class LazyPortFactory(Func<int> port) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterServer("127.0.0.1", port()));
    }

    private static DebugConfiguration Config(ServiceHarness h) => ServiceHarness.Config("lldb", "fakelldb", folder: h.Dir.Uri);

    // ------------------------------------------------------------------ Rust panics

    [Fact]
    public async Task A_stop_at_codelldbs_own_panic_filter_is_reported_as_the_panic_with_its_message()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        Assert.Equal(["rust_panic"], session.Capabilities.ExceptionBreakpointFilters!.Select(f => f.Filter)); // the adapter's own, nothing added
        Assert.Contains(h.Breakpoints.ExceptionFilters, f => f is { Filter: "rust_panic", Enabled: true });
        await h.AdapterFor(0).WaitForRequestAsync("setExceptionBreakpoints");
        Assert.Equal(["rust_panic"], h.AdapterFor(0).RequestsOf("setExceptionBreakpoints")[^1].Args<DapSetExceptionBreakpointsArguments>().Filters);
        Assert.Equal(0, h.AdapterFor(0).CountOf("setFunctionBreakpoints") == 0 ? 0 : h.AdapterFor(0).RequestsOf("setFunctionBreakpoints")[^1].Args<DapSetFunctionBreakpointsArguments>().Breakpoints.Count);

        var stops = new List<DebugStoppedEventArgs>();
        session.Stopped.Subscribe(e => { lock (stops) stops.Add(e); });
        await h.AdapterFor(0).SendOutputAsync("\nthread 'main' (4242) panicked at src/util.rs:15:9:\nboom at the bottom\nnote: run with `RUST_BACKTRACE=1` environment variable to display a backtrace\n", "stdout");
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [7]);
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count == 1; });
        var stop = stops[0];
        Assert.Equal(("exception", "Paused on exception: Rust panics", "boom at the bottom"), (stop.Reason, stop.Description, stop.Text));
        var body = await session.CustomRequestAsync("exceptionInfo", JsonSerializer.SerializeToElement(new { threadId = 1 }), Ct);
        Assert.Equal("panic", body!.Value.GetProperty("exceptionId").GetString());
        Assert.Equal("thread 'main' panicked at src/util.rs:15:9", body.Value.GetProperty("details").GetProperty("stackTrace").GetString());
        Assert.Equal(0, h.AdapterFor(0).CountOf("exceptionInfo")); // CodeLLDB would answer "Breakpoint": the session describes the panic itself
        await SessionHarness.Eventually(() => session.LastException is { Description: "boom at the bottom" });

        // any other breakpoint stays a breakpoint
        await session.ContinueAsync(ct: Ct);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Running);
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count == 2; });
        Assert.Equal(("breakpoint", null), (stops[1].Reason, stops[1].Text));
    }

    [Fact]
    public async Task With_the_filter_off_codelldb_gets_no_filter_and_a_stop_with_an_old_id_stays_a_breakpoint()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setExceptionBreakpoints");
        h.Breakpoints.SetExceptionFilters([.. h.Breakpoints.ExceptionFilters.Select(f => f.Filter == "rust_panic" ? f with { Enabled = false } : f)]);
        await h.AdapterFor(0).WaitForRequestAsync("setExceptionBreakpoints", 2);
        Assert.Empty(h.AdapterFor(0).RequestsOf("setExceptionBreakpoints")[^1].Args<DapSetExceptionBreakpointsArguments>().Filters);

        DebugStoppedEventArgs? stop = null;
        session.Stopped.Subscribe(e => stop = e);
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [7]); // an id the adapter no longer has for the filter
        await SessionHarness.Eventually(() => stop is not null);
        Assert.Equal("breakpoint", stop!.Reason);
    }

    [Fact]
    public async Task A_filter_with_a_condition_is_sent_as_a_filter_option_and_its_stop_is_still_the_panic()
    {
        await using var h = Harness();
        h.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("rust_panic", "Rust: on panic", true, "n > 1")]);
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setExceptionBreakpoints");
        var sent = h.AdapterFor(0).RequestsOf("setExceptionBreakpoints")[^1].Args<DapSetExceptionBreakpointsArguments>();
        Assert.Empty(sent.Filters);
        Assert.Equal(("rust_panic", "n > 1"), (Assert.Single(sent.FilterOptions!).FilterId, sent.FilterOptions![0].Condition));

        DebugStoppedEventArgs? stop = null;
        session.Stopped.Subscribe(e => stop = e);
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [7]);
        await SessionHarness.Eventually(() => stop is not null);
        Assert.Equal("exception", stop!.Reason);
    }

    [Fact]
    public void The_panic_message_is_read_as_for_gdb() =>
        Assert.Equal("first line\nsecond line",
            Quirks.DescribeSyntheticStop(Quirks.BreakpointExceptionFilters[0], "loop 4\nthread 'main' panicked at src/main.rs:2:5:\nfirst line\nsecond line\n")!.Description);

    // ------------------------------------------------------------------ hit counts, Debug Console, frames

    [Theory]
    [InlineData("3", "=3")]
    [InlineData(" 12 ", "=12")]
    [InlineData("007", "=7")]
    [InlineData(">5", ">5")]
    [InlineData(">= 5", ">= 5")]
    [InlineData("%2", "%2")]
    [InlineData("<4", "<4")]
    [InlineData("==6", "==6")]
    public void A_bare_hit_count_means_that_hit_only_and_codelldbs_operators_pass_unchanged(string typed, string sent)
    {
        var m = Quirks.MapHitCondition(typed);
        Assert.Equal((sent, null), (m.Wire, m.Unsupported));
    }

    [Fact]
    public async Task A_bare_hit_count_reaches_codelldb_as_an_exact_count()
    {
        await using var h = Harness();
        var main = h.Dir.WriteFile("src/main.rs", "fn main() {}\n");
        h.Breakpoints.Add(new Uri(main), 0, hitCondition: "3");
        await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setBreakpoints");
        var bp = Assert.Single(h.AdapterFor(0).RequestsOf("setBreakpoints")[^1].Args<DapSetBreakpointsArguments>().Breakpoints!);
        Assert.Equal("=3", bp.HitCondition);
    }

    [Theory]
    [InlineData("x * 2", "x * 2")]
    [InlineData("-exec bt", "/cmd bt")]
    [InlineData("  -exec   frame variable ", "/cmd frame variable")]
    [InlineData("`bt", "`bt")]
    [InlineData("/cmd bt", "/cmd bt")]
    public void Debug_console_input_goes_to_codelldb_as_typed_except_vs_codes_exec_prefix(string input, string expression)
    {
        var r = Quirks.MapReplInput(input);
        Assert.Equal((expression, "repl", null), (r.Expression, r.Context, r.Fallback));
    }

    [Fact]
    public async Task An_exec_command_in_the_debug_console_reaches_codelldb_as_a_cmd_command()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var frames = await session.GetStackTraceAsync(1, 0, 20, Ct);
        await session.EvaluateAsync("-exec bt", frames[0].Id, "repl", Ct);
        var evaluate = h.AdapterFor(0).RequestsOf("evaluate")[^1].Arguments;
        Assert.Equal(("/cmd bt", "repl"), (evaluate.GetProperty("expression").GetString(), evaluate.GetProperty("context").GetString()));
    }

    [Theory]
    [InlineData("/rustc/48a229ceaefd4985c50990b14116b6d856af0985/library/std/src/panicking.rs", 0, true)]
    [InlineData("/home/u/.rustup/toolchains/stable/lib/rustlib/src/rust/library/core/src/panicking.rs", 0, true)]
    [InlineData(null, 3, true)]                       // CodeLLDB's disassembly of code without sources (libc…)
    [InlineData("/work/src/main.rs", 0, false)]
    public void Library_and_disassembly_frames_are_de_emphasised(string? path, int sourceReference, bool deemphasized)
    {
        var frame = new DebugStackFrame(1, "f", new DebugSource(path is null ? "libc.so.6" : Path.GetFileName(path), path, sourceReference > 0 ? sourceReference : null), 10, 0);
        var mapped = Quirks.MapFrame(frame);
        Assert.Equal(deemphasized, mapped.Source!.PresentationHint == "deemphasize");
    }
}
