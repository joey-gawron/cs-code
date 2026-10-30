using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Regression tests for what the GDB/Rust evaluation (docs/DEBUGGING-RUST-TS.md, "Rust through GDB") found, against scripted fake adapters that
/// behave like GDB 18's native DAP (over real loopback TCP) and the quirk mappings themselves. Each fails without its fix.
/// </summary>
public class GdbQuirksTests
{
    private static readonly GdbAdapterQuirks Quirks = GdbAdapterQuirks.Instance;

    /// <summary>A harness whose debug type "fakegdb" has the GDB quirks and talks to the fake adapters.</summary>
    private static ServiceHarness Harness(Action<FakeDebugAdapter, int>? configure = null)
    {
        ServiceHarness? harness = null;
        var h = new ServiceHarness(
            configureHost: host => host.Configure = (a, i) =>
            {
                // GDB 18: no exceptionInfo, Ada/C++ filters only, no restart
                a.Capabilities.Remove("supportsExceptionInfoRequest");
                a.Capabilities["exceptionBreakpointFilters"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["filter"] = "throw", ["label"] = "C++ exceptions, when thrown", ["supportsCondition"] = true });
                configure?.Invoke(a, i);
            },
            moreBuiltins: _ => [new BuiltinDebugAdapter("fakegdb", "fake GDB", ["rust"], new LazyPortFactory(() => harness!.Host.Port)) { Quirks = GdbAdapterQuirks.Instance }]);
        harness = h;
        return h;
    }

    private sealed class LazyPortFactory(Func<int> port) : IDebugAdapterFactory
    {
        public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct) =>
            ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterServer("127.0.0.1", port()));
    }

    private static DebugConfiguration Config(ServiceHarness h) => ServiceHarness.Config("gdb", "fakegdb", folder: h.Dir.Uri);

    // ------------------------------------------------------------------ Debug Console

    [Theory]
    [InlineData("x * 2", "x * 2", "watch", true)]
    [InlineData("  p.x + 1 ", "p.x + 1", "watch", false)]
    [InlineData("-exec info locals", "info locals", "repl", false)]
    [InlineData("`bt", "bt", "repl", false)]
    [InlineData("p x", "p x", "watch", true)]
    [InlineData("info locals", "info locals", "watch", true)]
    [InlineData("bt", "bt", "watch", true)]
    [InlineData("print(x)", "print(x)", "watch", false)]
    public void Debug_console_input_is_an_expression_unless_it_is_a_gdb_command(string input, string expression, string context, bool commandFallback)
    {
        var r = Quirks.MapReplInput(input);
        Assert.Equal((expression, context), (r.Expression, r.Context));
        Assert.Equal(commandFallback, r.Fallback is { Context: "repl" });
    }

    [Theory]
    [InlineData("p x", "A syntax error in expression, near `x'.", true)]
    [InlineData("info locals", "A syntax error in expression, near `locals'.", true)]
    [InlineData("p s", "Syntax error near 's'", true)] // GDB 18's Rust parser words it so
    [InlineData("bt", "No symbol 'bt' in current context", true)]
    [InlineData("x + 1", "No symbol 'x' in current context", false)] // x is also GDB's examine-memory command: keep the expression's error
    [InlineData("p + 1", "No symbol 'p' in current context", false)]
    public void Only_input_that_is_no_expression_is_run_as_a_gdb_command(string input, string expressionError, bool retried) =>
        Assert.Equal(retried, Quirks.MapReplInput(input).FallbackWhen!.Invoke(expressionError));

    [Fact]
    public async Task The_console_evaluates_an_expression_and_retries_a_gdb_command_as_one()
    {
        await using var h = Harness((a, _) => a.On("evaluate", r =>
        {
            var args = r.Args<DapEvaluateArguments>();
            // GDB: "repl" runs a command; "watch" parses an expression
            if (args.Context == "repl") return args.Expression == "p x" ? new DapEvaluateResponse { Result = "$1 = 51\n" } : throw new FakeAdapterError("Undefined command");
            return args.Expression == "x * 2" ? new DapEvaluateResponse { Result = "102", Type = "int" } : throw new FakeAdapterError("A syntax error in expression, near `x'.");
        }));
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var frame = (await session.GetStackTraceAsync(1, ct: Ct))[0].Id;
        Assert.Equal("102", (await session.EvaluateAsync("x * 2", frame, "repl", Ct)).Result);
        Assert.Equal("$1 = 51", (await session.EvaluateAsync("p x", frame, "repl", Ct)).Result); // the command's line break trimmed
        var sent = h.AdapterFor(0).RequestsOf("evaluate").Select(r => r.Args<DapEvaluateArguments>()).Select(a => $"{a.Context}:{a.Expression}").ToList();
        Assert.Equal(["watch:x * 2", "watch:p x", "repl:p x"], sent);
        var error = await Assert.ThrowsAsync<DapRequestException>(async () => await session.EvaluateAsync("nope", frame, "repl", Ct));
        Assert.Equal("A syntax error in expression, near `x'.", error.Message); // an expression's own error, no retry as a command
    }

    [Fact]
    public async Task Console_completions_complete_an_expression_through_print_and_map_gdbs_whole_lines_back()
    {
        await using var h = Harness((a, _) => a.On("completions", r =>
        {
            var args = r.Args<DapCompletionsArguments>();
            // GDB: completions of a COMMAND line, labels are the whole completed line, no start
            return args.Text switch
            {
                "print p." => new DapCompletionsResponse { Targets = [new() { Label = "print p.", Length = 8 }, new() { Label = "print p.x", Length = 9 }, new() { Label = "print p.y", Length = 9 }] },
                "info lo" => new DapCompletionsResponse { Targets = [new() { Label = "info locals", Length = 11 }] },
                _ => new DapCompletionsResponse(),
            };
        }));
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync();
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var frame = (await session.GetStackTraceAsync(1, ct: Ct))[0].Id;

        var items = await session.CompletionsAsync(frame, "p.", 2, Ct);
        Assert.Contains(items, i => i is { Label: "p.x", Text: "p.x", Start: 0, Length: 2 });
        Assert.Contains(items, i => i is { Label: "p.y", Text: "p.y" });
        var sent = h.AdapterFor(0).RequestsOf("completions")[0].Args<DapCompletionsArguments>();
        Assert.Equal(("print p.", 9), (sent.Text, sent.Column)); // 1-based column after "print p."

        var commands = await session.CompletionsAsync(frame, "-exec info lo", 13, Ct);
        Assert.Equal(("locals", "-exec info locals", 0, 13), (commands[0].Label, commands[0].Text, commands[0].Start, commands[0].Length));
    }

    // ------------------------------------------------------------------ breakpoints

    [Theory]
    [InlineData("3", "2")]
    [InlineData("=3", "2")]
    [InlineData("== 3", "2")]
    [InlineData(">3", "3")]
    [InlineData(">= 3", "2")]
    [InlineData("1", "0")]
    [InlineData("0", "0")]
    public void Hit_counts_become_gdb_ignore_counts(string typed, string sent) => Assert.Equal(sent, Quirks.MapHitCondition(typed).Wire);

    [Theory]
    [InlineData("%2")]
    [InlineData("<5")]
    [InlineData("x > 3")]
    public void Hit_counts_gdb_cannot_express_are_refused(string typed)
    {
        var m = Quirks.MapHitCondition(typed);
        Assert.Null(m.Wire);
        Assert.Contains($"GDB cannot break on the hit count '{typed}'", m.Unsupported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hit_counts_reach_gdb_as_ignore_counts_and_inexpressible_ones_are_not_sent()
    {
        await using var h = Harness();
        var file = new Uri(h.Dir.Combine("main.rs"));
        h.Breakpoints.Add(file, 3, hitCondition: "3");
        var modulo = h.Breakpoints.Add(file, 7, hitCondition: "%2");
        await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setBreakpoints");
        var sent = h.AdapterFor(0).RequestsOf("setBreakpoints")[0].Args<DapSetBreakpointsArguments>().Breakpoints!;
        Assert.Equal("2", Assert.Single(sent).HitCondition);
        Assert.True(h.Breakpoints.GetStatus(modulo.Id) is { Verified: false, Message: { } why } && why.StartsWith("GDB cannot break", StringComparison.Ordinal));
    }

    /// <summary>GDB answers a function breakpoint set before the program is loaded "pending" and verifies it by a later event.</summary>
    [Fact]
    public async Task A_function_breakpoint_verified_later_by_a_breakpoint_event_shows_verified()
    {
        var ids = new List<int>();
        await using var h = Harness((a, _) => a.On("setFunctionBreakpoints", r =>
        {
            var args = r.Args<DapSetFunctionBreakpointsArguments>();
            return new DapBreakpointsResponse { Breakpoints = [.. args.Breakpoints.Select(_ => { var id = 50 + ids.Count; ids.Add(id); return new DapBreakpoint { Id = id, Verified = false, Reason = "pending" }; })] };
        }));
        var fn = h.Breakpoints.AddFunction("sample::util::add");
        await h.StartAsync(Config(h));
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(fn.Id) is { Verified: false }, 5_000, "pending verdict");
        await h.AdapterFor(0).SendEventAsync("breakpoint", new DapBreakpointEvent
        {
            Reason = "changed",
            Breakpoint = new DapBreakpoint { Id = ids[0], Verified = true, Line = 4, Source = new DapSource { Path = "/src/util.rs" } },
        });
        await SessionHarness.Eventually(() => h.Breakpoints.GetStatus(fn.Id) is { Verified: true }, 5_000, "verified by the event");
        Assert.Null(h.Breakpoints.GetStatus(fn.Id)!.ActualLine); // the function's line is not a "moved" line
    }

    /// <summary>GDB ignores <c>noDebug</c>: Start Without Debugging (Ctrl+F5) would stop at breakpoints and panics, so none are sent.</summary>
    [Fact]
    public async Task Start_without_debugging_sends_gdb_no_breakpoints_and_no_panic_filter()
    {
        await using var h = Harness();
        h.Breakpoints.Add(new Uri(h.Dir.Combine("main.rs")), 3);
        h.Breakpoints.AddFunction("sample::util::add");
        await h.StartAsync(Config(h) with { NoDebug = true });
        await h.AdapterFor(0).WaitForRequestAsync("configurationDone");
        Assert.True(h.AdapterFor(0).RequestsOf("launch")[0].Arguments.GetProperty("noDebug").GetBoolean());
        Assert.Equal(0, h.AdapterFor(0).CountOf("setBreakpoints") + h.AdapterFor(0).CountOf("setFunctionBreakpoints") + h.AdapterFor(0).CountOf("setExceptionBreakpoints"));
        h.Breakpoints.Add(new Uri(h.Dir.Combine("main.rs")), 9); // a breakpoint added while it runs is not sent either
        await Task.Delay(300, Ct);
        Assert.Equal(0, h.AdapterFor(0).CountOf("setBreakpoints"));
    }

    [Fact]
    public async Task A_debugged_launch_still_sends_its_breakpoints()
    {
        await using var h = Harness();
        h.Breakpoints.Add(new Uri(h.Dir.Combine("main.rs")), 3);
        await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setBreakpoints");
    }

    // ------------------------------------------------------------------ Rust panics

    [Fact]
    public async Task Rust_panics_are_offered_on_by_default_sent_as_a_function_breakpoint_and_reported_as_an_exception()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        Assert.Equal(["rust_panic", "throw"], session.Capabilities.ExceptionBreakpointFilters!.Select(f => f.Filter));
        Assert.True(session.Capabilities.SupportsExceptionInfo);
        Assert.Contains(h.Breakpoints.ExceptionFilters, f => f is { Filter: "rust_panic", Label: "Rust panics", Enabled: true });
        await h.AdapterFor(0).WaitForRequestAsync("setFunctionBreakpoints");
        var fns = h.AdapterFor(0).RequestsOf("setFunctionBreakpoints")[0].Args<DapSetFunctionBreakpointsArguments>().Breakpoints;
        Assert.Equal(GdbAdapterQuirks.RustPanicFunction, Assert.Single(fns).Name);
        var exceptionFilters = h.AdapterFor(0).RequestsOf("setExceptionBreakpoints").Select(r => r.Args<DapSetExceptionBreakpointsArguments>()).Last();
        Assert.DoesNotContain("rust_panic", exceptionFilters.Filters); // GDB does not know it
        var adapterId = 1001; // the fake numbers breakpoints from 1001

        DebugStoppedEventArgs? stop = null;
        session.Stopped.Subscribe(e => stop = e);
        await h.AdapterFor(0).SendOutputAsync("\nthread 'main' (4242) panicked at src/util.rs:15:9:\n", "stdout");
        await h.AdapterFor(0).SendOutputAsync("boom at the bottom\n", "stdout");
        await h.AdapterFor(0).SendOutputAsync("note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace\n", "stdout");
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [adapterId]);
        await SessionHarness.Eventually(() => stop is not null);
        Assert.Equal(("exception", "Paused on exception: Rust panics", "boom at the bottom"), (stop!.Reason, stop.Description, stop.Text));
        var body = await session.CustomRequestAsync("exceptionInfo", JsonSerializer.SerializeToElement(new { threadId = 1 }), Ct);
        Assert.Equal("panic", body!.Value.GetProperty("exceptionId").GetString());
        Assert.Equal("boom at the bottom", body.Value.GetProperty("details").GetProperty("message").GetString());
        Assert.Equal("thread 'main' panicked at src/util.rs:15:9", body.Value.GetProperty("details").GetProperty("stackTrace").GetString());
        Assert.Equal(0, h.AdapterFor(0).CountOf("exceptionInfo")); // answered by the session, not sent to GDB
        await SessionHarness.Eventually(() => session.LastException is { Description: "boom at the bottom" });

        // turning the filter off removes the breakpoint from GDB
        h.Breakpoints.SetExceptionFilters([.. h.Breakpoints.ExceptionFilters.Select(f => f.Filter == "rust_panic" ? f with { Enabled = false } : f)]);
        await h.AdapterFor(0).WaitForRequestAsync("setFunctionBreakpoints", 2);
        Assert.Empty(h.AdapterFor(0).RequestsOf("setFunctionBreakpoints")[1].Args<DapSetFunctionBreakpointsArguments>().Breakpoints);
    }

    /// <summary>
    /// Restart while stopped on a panic: the new run's panic (its message arriving just after the stop) is described by its own message, not
    /// the previous run's, which stayed in the output the description is read from (end-pass debug-11).
    /// </summary>
    [Fact]
    public async Task After_a_restart_a_panic_is_described_by_its_own_message_not_the_previous_runs()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setFunctionBreakpoints");
        await h.AdapterFor(0).SendOutputAsync("thread 'main' panicked at src/a.rs:1:1:\nboom A\n", "stdout");
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => session.LastException is { Description: "boom A" });
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);

        await session.RestartAsync(Ct); // GDB has no `restart`: a new connection, the whole handshake again
        Assert.Null(session.LastException);
        Assert.Null(h.Service.StopLocation); // the old run's stop is over
        await h.WaitForAdapters(2);
        await h.AdapterFor(1).WaitForRequestAsync("setFunctionBreakpoints");
        DebugStoppedEventArgs? stop = null;
        session.Stopped.Subscribe(e => stop = e);
        await h.AdapterFor(1).SendStoppedAsync("breakpoint", hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => stop is not null);
        await Task.Delay(100, Ct);
        await h.AdapterFor(1).SendOutputAsync("thread 'main' panicked at src/b.rs:2:2:\nboom B\n", "stdout");
        await SessionHarness.Eventually(() => session.LastException is not null);
        Assert.Equal(("boom B", "thread 'main' panicked at src/b.rs:2:2"), (session.LastException!.Description, session.LastException.StackTrace));
        Assert.NotEqual("boom A", stop!.Text);
    }

    /// <summary>The panic message can arrive just AFTER the stop (GDB reads the program's output on another thread): exceptionInfo waits for it.</summary>
    [Fact]
    public async Task A_panic_message_that_arrives_after_the_stop_still_describes_it()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        await h.AdapterFor(0).WaitForRequestAsync("setFunctionBreakpoints");
        await h.AdapterFor(0).SendStoppedAsync("breakpoint", hitBreakpointIds: [1001]);
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Stopped);
        var info = session.CustomRequestAsync("exceptionInfo", null, Ct).AsTask();
        await Task.Delay(100, Ct);
        await h.AdapterFor(0).SendOutputAsync("thread 'worker' panicked at src/main.rs:3:5:\nlate message\n", "stdout");
        var body = await info;
        Assert.Equal("late message", body!.Value.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("thread 'main' (4242) panicked at src/util.rs:15:9:\nboom at the bottom\nnote: run with `RUST_BACKTRACE=1`\n", "boom at the bottom", "thread 'main' panicked at src/util.rs:15:9")]
    [InlineData("loop 4\nthread 'main' panicked at src/main.rs:2:5:\nfirst line\nsecond line\n", "first line\nsecond line", "thread 'main' panicked at src/main.rs:2:5")]
    [InlineData("thread 'main' panicked at 'old style', src/main.rs:2:5\nnote: run with", "old style", "thread 'main' panicked at src/main.rs:2:5")]
    [InlineData("thread 'a' panicked at x.rs:1:1:\nfirst\nthread 'b' panicked at y.rs:2:2:\nsecond\n", "second", "thread 'b' panicked at y.rs:2:2")]
    public void The_panic_message_is_read_from_the_programs_output(string output, string message, string where)
    {
        var info = Quirks.DescribeSyntheticStop(Quirks.SyntheticExceptionFilters[0], output)!;
        Assert.Equal((message, where, "panic"), (info.Description, info.StackTrace, info.ExceptionId));
    }

    [Fact]
    public void No_panic_in_the_output_describes_nothing() => Assert.Null(Quirks.DescribeSyntheticStop(Quirks.SyntheticExceptionFilters[0], "loop 1\nloop 2\n"));

    // ------------------------------------------------------------------ output

    [Theory]
    [InlineData("No source file named /tmp/x/src/main.rs.\n")]
    [InlineData("Breakpoint 1 (-source /tmp/x/src/main.rs -line 97) pending.\n")]
    [InlineData("Breakpoint 4 (-function std::panicking::rust_panic) pending.\n")]
    [InlineData("Function \"rust_panic\" not defined.\n")]
    [InlineData("No symbol table is loaded.  Use the \"file\" command.\n")]
    [InlineData("[Thread 0x7ffff7b6c6c0 (LWP 120172) (id 3) exited]\n")]
    [InlineData("[New Thread 0x7ffff7b6c6c0 (LWP 120172)]\n")]
    [InlineData("[Inferior 1 (process 4242) exited normally]\n")]
    [InlineData("Breakpoint 1 at 0x209bc: file src/main.rs, line 69.\n")]
    public void Gdbs_own_messages_are_debugger_messages(string line) => Assert.Equal("console", Quirks.MapOutputCategory("stdout", line));

    [Theory]
    [InlineData("loop 0\n")]
    [InlineData("Breakpoint reached by the program\n")]
    [InlineData("thread 'main' panicked at src/util.rs:15:9:\n")]
    public void The_programs_own_lines_stay_program_output(string line) => Assert.Equal("stdout", Quirks.MapOutputCategory("stdout", line));

    [Fact]
    public async Task Logpoint_messages_end_their_line_and_gdb_messages_are_console_output()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        var events = new List<DebugOutputEventArgs>();
        session.Output.Subscribe(e => { lock (events) events.Add(e); });
        await h.AdapterFor(0).SendOutputAsync("logged x=41", "console");
        await h.AdapterFor(0).SendOutputAsync("logged x=42", "console");
        await h.AdapterFor(0).SendOutputAsync("Breakpoint 2 (-source /a.rs -line 3) pending.\n", "stdout");
        await h.AdapterFor(0).SendOutputAsync("loop 0\n", "stdout");
        await SessionHarness.Eventually(() => { lock (events) return string.Concat(events.Select(e => e.Output)).Contains("loop 0", StringComparison.Ordinal); });
        lock (events)
        {
            Assert.Equal("logged x=41\nlogged x=42\nBreakpoint 2 (-source /a.rs -line 3) pending.\n", string.Concat(events.Where(e => e.Category == "console").Select(e => e.Output)));
            Assert.Equal("loop 0\n", string.Concat(events.Where(e => e.Category == "stdout").Select(e => e.Output)));
        }
    }

    /// <summary>GDB delivers the program's last lines after <c>terminated</c>: they must still reach the console (the 1M-line flood lost ~2,500).</summary>
    [Fact]
    public async Task Output_that_arrives_after_terminated_still_reaches_the_debug_console()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        var adapter = h.AdapterFor(0);
        await adapter.SendExitedAsync(0);
        await adapter.SendTerminatedAsync();
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(40, Ct);
            await adapter.SendOutputAsync($"late line {i}\n", "stdout");
        }
        await SessionHarness.Eventually(() => session.State == DebugSessionState.Terminated, 10_000, "the session ends");
        var history = string.Concat(h.Service.GetOutputHistory().Select(e => e.Output));
        Assert.Contains("late line 4", history, StringComparison.Ordinal);
        Assert.Equal(0, session.ExitCode);
    }

    [Fact]
    public async Task Stop_while_waiting_for_trailing_output_ends_the_session_at_once()
    {
        await using var h = Harness();
        var session = (DebugSession)await h.StartAsync(Config(h));
        var adapter = h.AdapterFor(0);
        await adapter.SendOutputAsync("line\n", "stdout");
        await adapter.SendTerminatedAsync();
        await Task.Delay(30, Ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.StopAsync(Ct);
        Assert.Equal(DebugSessionState.Terminated, session.State);
        Assert.InRange(sw.ElapsedMilliseconds, 0, 2_000); // not the 5 s stop timeout a terminate without a `terminated` would wait
        Assert.Equal(0, adapter.CountOf("terminate"));
    }

    // ------------------------------------------------------------------ frames, stop location

    [Theory]
    [InlineData("/rustc/48a229ceaefd4985c50990b14116b6d856af0985/library/std/src/panicking.rs", null, true)]
    [InlineData("/nix/store/x-rust-src/lib/rustlib/src/rust/library/std/src/sys/backtrace.rs", null, true)]
    [InlineData("./nptl/./nptl/cancellation.c", 3, true)]
    [InlineData("/home/me/project/src/main.rs", null, false)]
    [InlineData("/home/me/.cargo/registry/src/index/serde-1.0/src/lib.rs", null, false)]
    public void Library_frames_are_deemphasized(string path, int? sourceReference, bool library)
    {
        var frame = new DebugStackFrame(0, "f", new DebugSource(Path.GetFileName(path), path, sourceReference), 1, 0);
        Assert.Equal(library ? "deemphasize" : null, Quirks.MapFrame(frame).Source?.PresentationHint);
    }

    [Fact]
    public void A_frame_without_debug_information_is_deemphasized_too()
    {
        var frame = new DebugStackFrame(5, "__rust_try", null, 0, 0);
        Assert.Equal("deemphasize", Quirks.MapFrame(frame).PresentationHint);
        Assert.Equal("label", Quirks.MapFrame(frame with { PresentationHint = "label" }).PresentationHint); // the adapter's own hint stays
    }

    /// <summary>A pause in libc's sleep or a panic inside std: the stop shows the first frame of the user's code (VS Code's rule).</summary>
    [Fact]
    public async Task The_stop_location_is_the_first_frame_that_is_not_library_code()
    {
        await using var h = Harness((a, _) => a.Frames =
        [
            new DapStackFrame { Id = 0, Name = "__internal_syscall_cancel", Source = new DapSource { Name = "cancellation.c", Path = "./nptl/cancellation.c", SourceReference = 1 }, Line = 44 },
            new DapStackFrame { Id = 1, Name = "std::thread::sleep", Source = new DapSource { Name = "mod.rs", Path = "/rustc/abc/library/std/src/thread/mod.rs" }, Line = 900 },
            new DapStackFrame { Id = 2, Name = "sample::serve", Source = new DapSource { Name = "main.rs", Path = "/work/src/main.rs" }, Line = 41 },
            new DapStackFrame { Id = 3, Name = "sample::main", Source = new DapSource { Name = "main.rs", Path = "/work/src/main.rs" }, Line = 58 },
        ]);
        var session = await h.StartAsync(Config(h));
        await h.AdapterFor(0).SendStoppedAsync("pause");
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        var loc = h.Service.StopLocation!;
        Assert.Equal((2, "/work/src/main.rs", 40), (loc.FrameId, loc.Uri?.LocalPath, loc.Line));
        Assert.Same(session, loc.Session);
    }

    [Fact]
    public async Task Without_the_quirks_the_stop_location_stays_the_first_frame_with_a_source()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.Frames =
        [
            new DapStackFrame { Id = 7, Name = "inner", Source = new DapSource { Name = "a.rs", Path = "/rustc/abc/library/std/src/a.rs" }, Line = 3 },
            new DapStackFrame { Id = 8, Name = "outer", Source = new DapSource { Name = "b.rs", Path = "/work/b.rs" }, Line = 5 },
        ]);
        await h.StartAsync();
        await h.AdapterFor(0).SendStoppedAsync("pause");
        await SessionHarness.Eventually(() => h.Service.StopLocation is not null);
        Assert.Equal(7, h.Service.StopLocation!.FrameId); // only adapters that de-emphasise frames change the choice
    }
}
