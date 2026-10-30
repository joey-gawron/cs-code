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
        // The fake answers in order: once a request sent after it has its answer, the session has also handled the setExceptionBreakpoints
        // response (its continuation was queued on the lane first). A stop the adapter sends before that response is read cannot be told apart.
        await session.GetThreadsAsync(Ct);

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

    /// <summary>
    /// CodeLLDB runs a <c>noDebug</c> launch without the debugger, but answers every breakpoint request then with "Not supported in noDebug
    /// mode.": sending them would show each of the user's breakpoints as failed while the program runs.
    /// </summary>
    [Fact]
    public async Task Start_without_debugging_sends_codelldb_no_breakpoints_so_none_is_shown_as_failed()
    {
        await using var h = Harness((a, _) =>
        {
            foreach (var command in new[] { "setBreakpoints", "setFunctionBreakpoints" })
                a.On(command, _ => throw new InvalidOperationException("Not supported in noDebug mode."));
        });
        var bp = h.Breakpoints.Add(new Uri(h.Dir.Combine("main.rs")), 3);
        h.Breakpoints.AddFunction("sample::util::add");
        await h.StartAsync(Config(h) with { NoDebug = true });
        await h.AdapterFor(0).WaitForRequestAsync("configurationDone");
        Assert.True(h.AdapterFor(0).RequestsOf("launch")[0].Arguments.GetProperty("noDebug").GetBoolean());
        Assert.Equal(0, h.AdapterFor(0).CountOf("setBreakpoints") + h.AdapterFor(0).CountOf("setFunctionBreakpoints") + h.AdapterFor(0).CountOf("setExceptionBreakpoints"));
        h.Breakpoints.Add(new Uri(h.Dir.Combine("main.rs")), 9); // nor one added while it runs
        await Task.Delay(300, Ct);
        Assert.Equal(0, h.AdapterFor(0).CountOf("setBreakpoints"));
        Assert.Null(h.Breakpoints.GetStatus(bp.Id)); // no "failed" verdict
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

    /// <summary>
    /// What reaches CodeLLDB for a completion, and where its answer goes. Its answers, as observed: "`fram" → frame (start 0, length 4);
    /// "/cmd frame variable p." → p.x and p.y (start 15, length 2): the start counts inside the command with its prefix stripped.
    /// </summary>
    [Theory]
    [InlineData("p.", 2, "/cmd frame variable p.", 2, 0)]
    [InlineData("x + p.", 6, "/cmd frame variable x + p.", 2, 4)]
    [InlineData("`fram", 5, "`fram", 4, 1)]
    [InlineData("/cmd fram", 9, "/cmd fram", 4, 5)]
    [InlineData("-exec fram", 10, "/cmd fram", 4, 6)]
    [InlineData("  -exec   fram", 14, "/cmd fram", 4, 10)]
    [InlineData("wrapper.po + 1", 10, "/cmd frame variable wrapper.po", 10, 0)] // only what is before the caret
    public void Completions_go_to_codelldb_as_lldb_commands_and_come_back_at_the_caret(string text, int column, string sent, int overlap, int start)
    {
        var mapping = Quirks.MapCompletions(text, column)!;
        Assert.Equal((sent, sent.Length), (mapping.Text, mapping.Column));
        var answer = mapping.Map(new DebugCompletionItem("completed", "completed", null, 0, overlap))!;
        Assert.Equal((start, overlap), (answer.Start, answer.Length));
    }

    [Fact]
    public async Task A_completion_reaches_codelldb_as_frame_variable_and_replaces_what_was_typed()
    {
        await using var h = Harness((a, _) => a.On("completions", r => new DapCompletionsResponse
        {
            // CodeLLDB v1.12.3's answer to "/cmd frame variable p." (start: 0-based inside "frame variable p.")
            Targets = r.Args<DapCompletionsArguments>().Text == "/cmd frame variable p."
                ? [new DapCompletionItem { Label = "p.x", Text = "p.x", Start = 15, Length = 2 }, new DapCompletionItem { Label = "p.y", Text = "p.y", Start = 15, Length = 2 }]
                : [],
        }));
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var frames = await session.GetStackTraceAsync(1, 0, 20, Ct);
        var items = await session.CompletionsAsync(frames[0].Id, "p.", 2, Ct);
        var sent = h.AdapterFor(0).RequestsOf("completions")[^1].Args<DapCompletionsArguments>();
        Assert.Equal(("/cmd frame variable p.", 23), (sent.Text, sent.Column)); // the caret after the text, 1-based
        Assert.Equal([("p.x", 0, 2), ("p.y", 0, 2)], items.Select(i => (i.Text!, i.Start!.Value, i.Length!.Value)));
    }

    // ------------------------------------------------------------------ paging, pause (observed with the real CodeLLDB v1.12.3)

    /// <summary>
    /// CodeLLDB answers <c>variables</c> with every child whatever <c>start</c>/<c>count</c>/<c>filter</c> say (seen on the wire: [240, 250) of a
    /// 250-element Vec came back as all 250 and its [raw] row). Before, the session kept the first <c>count</c> of them, so every page was the
    /// first one and the Locals window could never show a Vec's elements past the first hundred.
    /// </summary>
    [Fact]
    public async Task Codelldb_ignores_paging_so_the_session_asks_once_for_every_child_and_cuts_the_pages_itself()
    {
        static List<DapVariable> Elements() =>
            [.. Enumerable.Range(0, 250).Select(i => new DapVariable { Name = $"[{i}]", Value = (2 * i).ToString(System.Globalization.CultureInfo.InvariantCulture), Type = "unsigned int" }),
             new DapVariable { Name = "[raw]", Value = "alloc::vec::Vec<unsigned int, alloc::alloc::Global>", VariablesReference = 31 }];
        await using var h = Harness((a, _) =>
        {
            a.On("scopes", _ => new DapScopesResponse { Scopes = [new DapScope { Name = "Local", VariablesReference = 1 }] });
            a.On("variables", r => r.Args<DapVariablesArguments>().VariablesReference switch
            {
                1 => new DapVariablesResponse { Variables = [new DapVariable { Name = "big", Value = "size=250", VariablesReference = 30 }] },
                30 => new DapVariablesResponse { Variables = Elements() }, // CodeLLDB: start, count and filter are not read
                _ => new DapVariablesResponse(),
            });
        });
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", 1);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);

        async Task<int> LoadBigAsync()
        {
            var frames = await session.GetStackTraceAsync(1, 0, 20, Ct);
            var scope = Assert.Single(await session.GetScopesAsync(frames[0].Id, Ct));
            return Assert.Single(await session.GetVariablesAsync(scope.VariablesReference, ct: Ct)).VariablesReference;
        }
        var big = await LoadBigAsync();
        static string Names(IReadOnlyList<DebugVariable> page) => page.Count == 0 ? "" : $"{page[0].Name}..{page[^1].Name} ({page.Count})";
        var tail = await session.GetVariablesAsync(big, "indexed", 240, 10, Ct);
        Assert.Equal("[240]..[249] (10)", Names(tail));
        Assert.Equal("498", tail[^1].Value);
        Assert.Equal("[0]..[99] (100)", Names(await session.GetVariablesAsync(big, 0, 100, Ct)));      // the panels' first page
        Assert.Equal("[100]..[199] (100)", Names(await session.GetVariablesAsync(big, 100, 100, Ct))); // "… more"
        Assert.Equal("[200]..[raw] (51)", Names(await session.GetVariablesAsync(big, 200, 100, Ct))); // a short page: the end
        Assert.Empty(await session.GetVariablesAsync(big, 300, 100, Ct));
        Assert.Equal(251, (await session.GetVariablesAsync(big, ct: Ct)).Count);
        var asked = h.AdapterFor(0).RequestsOf("variables").Select(r => r.Args<DapVariablesArguments>()).Where(a => a.VariablesReference == 30).ToList();
        var once = Assert.Single(asked); // one request per stop for all the pages
        Assert.Equal((null, null, null), (once.Start, once.Count, once.Filter));
        Assert.True((await session.GetVariablesAsync(31, ct: Ct)).Count == 0); // the [raw] row's handle was registered with the children

        // a new stop: the children are asked for again (references die when the program moves)
        await session.ContinueAsync(ct: Ct);
        await h.AdapterFor(0).SendStoppedAsync("step", 1);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        big = await LoadBigAsync();
        Assert.Equal("[100]..[199] (100)", Names(await session.GetVariablesAsync(big, 100, 100, Ct)));
        Assert.Equal(2, h.AdapterFor(0).RequestsOf("variables").Count(r => r.Args<DapVariablesArguments>().VariablesReference == 30));
    }

    [Fact]
    public void A_page_of_a_complete_child_list_is_cut_by_start_and_count()
    {
        static DebugVariable V(int i) => new($"[{i}]", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        IReadOnlyList<DebugVariable> all = [.. Enumerable.Range(0, 5).Select(V)];
        Assert.Same(all, DebugSession.Page(all, null, null));
        Assert.Same(all, DebugSession.Page(all, 0, 10));
        Assert.Equal(["[1]", "[2]"], DebugSession.Page(all, 1, 2).Select(v => v.Name));
        Assert.Equal(["[3]", "[4]"], DebugSession.Page(all, 3, null).Select(v => v.Name));
        Assert.Empty(DebugSession.Page(all, 9, 2));
        Assert.Equal(["[0]"], DebugSession.Page(all, -4, 1).Select(v => v.Name));
    }

    /// <summary>
    /// CodeLLDB pauses by stopping the process (LLDB sends SIGSTOP) and reports every signal stop as reason "exception" with the signal as the
    /// description — so Break All showed as the exception "signal SIGSTOP" (seen on the wire: <c>{"reason":"exception","description":"signal
    /// SIGSTOP"}</c> after our pause). Only that signal and only while our pause is outstanding is the pause.
    /// </summary>
    [Fact]
    public async Task A_pause_codelldb_reports_as_the_exception_signal_sigstop_is_a_pause_and_real_signals_stay_exceptions()
    {
        var answer = "signal SIGSTOP";
        await using var h = Harness((a, _) =>
            a.AfterResponse("pause", () => a.SendEventAsync("stopped", new DapStoppedEvent { Reason = "exception", Description = answer, ThreadId = 1, AllThreadsStopped = true })));
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("configurationDone");
        var stops = new List<DebugStoppedEventArgs>();
        session.Stopped.Subscribe(e => { lock (stops) stops.Add(e); });
        int Stops() { lock (stops) return stops.Count; }

        await session.PauseAsync(ct: Ct);
        await SessionHarness.Eventually(() => Stops() == 1);
        Assert.Equal(("pause", null, null), (stops[0].Reason, stops[0].Description, stops[0].Text));
        await session.GetThreadsAsync(Ct); // a lane round trip: an exceptionInfo request would have been sent by now
        Assert.Equal(0, h.AdapterFor(0).CountOf("exceptionInfo"));
        Assert.Null(session.LastException);

        // the same signal without our pause (kill -STOP from a terminal) is what the adapter says it is
        await session.ContinueAsync(ct: Ct);
        await h.AdapterFor(0).SendEventAsync("stopped", new DapStoppedEvent { Reason = "exception", Description = "signal SIGSTOP", ThreadId = 1, AllThreadsStopped = true });
        await SessionHarness.Eventually(() => Stops() == 2);
        Assert.Equal(("exception", "signal SIGSTOP"), (stops[1].Reason, stops[1].Description));
        await h.AdapterFor(0).WaitForRequestAsync("exceptionInfo");

        // a real signal that wins the race with our pause stays an exception
        await session.ContinueAsync(ct: Ct);
        answer = "signal SIGSEGV: address not mapped to object";
        await session.PauseAsync(ct: Ct);
        await SessionHarness.Eventually(() => Stops() == 3);
        Assert.Equal(("exception", answer), (stops[2].Reason, stops[2].Description));
    }

    [Theory]
    [InlineData("exception", "signal SIGSTOP", true)]
    [InlineData("exception", "signal SIGSEGV", false)]
    [InlineData("exception", null, false)]
    [InlineData("breakpoint", "signal SIGSTOP", false)]
    public void Only_the_sigstop_exception_answers_a_pause(string reason, string? description, bool pause) =>
        Assert.Equal(pause, Quirks.IsPauseStop(reason, description));

    [Fact]
    public void Other_adapters_keep_their_pause_stops_and_their_paging() =>
        Assert.Equal((false, false), (DebugAdapterQuirks.None.IsPauseStop("exception", "signal SIGSTOP"), DebugAdapterQuirks.None.IgnoresVariablePaging));

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
