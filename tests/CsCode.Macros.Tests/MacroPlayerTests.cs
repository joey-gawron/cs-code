using CsCode.Core.Platform;
using CsCode.Macros.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Macros.Tests;

/// <summary>
/// The player against a fake editor on a stand-in UI thread: order and paths of the steps, abort on error, the runaway guards, cancellation,
/// nesting, the repeat modes, and the slicing that keeps the UI thread responsive.
/// </summary>
public sealed class MacroPlayerTests : IDisposable
{
    private readonly TestUiThread _ui = new();
    private readonly TestCommandService _commands = new();

    public void Dispose() => _ui.Dispose();

    private static KeyStep K(string key) => new(KeyStrokeParser.ParseStroke(key));
    private static TextStep T(string text) => new(text);
    private static CommandStep C(string id) => new(id);
    private static Macro M(params MacroStep[] steps) => new("", steps);
    private static Macro Named(string name, params MacroStep[] steps) => new(name, steps);

    private (MacroPlayer Player, FakeEditorHost Host) Rig(string text = "", MacroLimits? limits = null, TimeProvider? time = null, StepClock? clock = null)
    {
        var host = new FakeEditorHost(text, clock) { Ui = _ui };
        var player = new MacroPlayer(_commands, _ui, () => host, time ?? clock);
        if (limits is not null) player.Limits = () => limits;
        return (player, host);
    }

    private Task<MacroPlayResult> Play(MacroPlayer player, Macro macro, MacroPlayOptions? options = null) => _ui.RunAsync(() => player.PlayAsync(macro, options));

    // ------------------------------------------------------------------------------------------------------------ paths and order

    [Fact]
    public async Task Keys_text_and_commands_run_in_order_through_the_editor_and_the_command_service()
    {
        var (player, host) = Rig("abc");
        var log = host.Received;
        _commands.Register("test.mark", inv => { log.Add("cmd:" + inv.Arg<string>(0)); return ValueTask.FromResult<object?>(null); });

        var result = await Play(player, M(K("end"), T("de"), new CommandStep("test.mark", [System.Text.Json.JsonSerializer.SerializeToElement("x")]), K("home"), T("Z")));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal(5, result.Steps);
        Assert.Equal(["key:end", "text:d", "text:e", "cmd:x", "key:home", "text:Z"], log); // text is typed one character at a time
        Assert.Equal("Zabcde", host.Text);
        Assert.Equal(0, host.WrongThreadCalls);
        Assert.Empty(_ui.Errors);
    }

    [Fact]
    public async Task A_surrogate_pair_is_typed_as_one_character()
    {
        var (player, host) = Rig();
        await Play(player, M(T("a😀b")));
        Assert.Equal(["text:a", "text:😀", "text:b"], host.Received);
    }

    [Fact]
    public async Task A_playback_started_off_the_ui_thread_runs_on_it()
    {
        var (player, host) = Rig();
        var result = await Task.Run(() => player.PlayAsync(M(T("x"), K("left"), T("y"))));
        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal("yx", host.Text);
        Assert.Equal(0, host.WrongThreadCalls);
    }

    [Fact]
    public async Task A_command_that_completes_later_is_awaited_and_playback_continues_on_the_ui_thread()
    {
        var (player, host) = Rig();
        _commands.Register("test.slow", async _ => { await Task.Delay(30).ConfigureAwait(false); return null; });

        var result = await Play(player, M(T("a"), C("test.slow"), T("b")));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal("ab", host.Text);
        Assert.Equal(0, host.WrongThreadCalls);
    }

    // ------------------------------------------------------------------------------------------------------------ abort on error

    [Fact]
    public async Task A_command_that_throws_stops_playback_at_that_step_and_says_why()
    {
        var (player, host) = Rig();
        _commands.Register("test.boom", _ => throw new InvalidOperationException("kaboom"));

        var macro = M(T("a"), C("test.boom"), T("b"));
        var result = await Play(player, macro, new MacroPlayOptions(MacroRepeat.Times, 5));

        Assert.Equal(MacroPlayStatus.Failed, result.Status);
        Assert.Equal(2, result.Step);
        Assert.Equal(1, result.Runs);
        Assert.Contains("'test.boom' failed: kaboom", result.Message);
        Assert.Equal("a", host.Text);
        Assert.Equal("The last recorded macro stopped at step 2 of 3 (run 1): Command 'test.boom' failed: kaboom",
            MacroPlayResultText.Describe(macro, new MacroPlayOptions(MacroRepeat.Times, 5), result));
    }

    [Fact]
    public async Task An_unknown_command_a_failing_async_command_and_a_read_only_file_stop_playback()
    {
        var (player, host) = Rig();
        _commands.Register("test.asyncBoom", async _ => { await Task.Yield(); throw new IOException("disk"); });

        Assert.Contains("'nope.cmd' is not available", (await Play(player, M(C("nope.cmd"), T("x")))).Message);
        Assert.Contains("failed: disk", (await Play(player, M(C("test.asyncBoom"), T("x")))).Message);
        host.ReadOnly = true;
        var ro = await Play(player, M(T("x")));
        Assert.Equal(MacroPlayStatus.Failed, ro.Status);
        Assert.Contains("read-only", ro.Message);
        Assert.Equal("", host.Text);
    }

    [Fact]
    public async Task Switching_vim_on_or_closing_the_editor_mid_playback_stops_at_the_next_typing_step()
    {
        var (player, host) = Rig();
        _commands.Register("test.vimOn", _ => { host.Unavailable = "Vim emulation was switched on."; return ValueTask.FromResult<object?>(null); });
        _commands.Register("test.close", _ => { host.HasEditor = false; return ValueTask.FromResult<object?>(null); });

        var vim = await Play(player, M(T("a"), C("test.vimOn"), T("b")));
        Assert.Equal(MacroPlayStatus.Failed, vim.Status);
        Assert.Equal(3, vim.Step);
        Assert.Contains("Vim emulation was switched on", vim.Message);

        host.Unavailable = null;
        var closed = await Play(player, M(C("test.close"), K("left")));
        Assert.Equal(MacroPlayStatus.Failed, closed.Status);
        Assert.Contains("No editor is open", closed.Message);
    }

    [Fact]
    public async Task A_macro_that_types_does_not_start_while_typing_is_unavailable_but_a_command_only_macro_plays()
    {
        var (player, host) = Rig();
        host.Unavailable = "Vim emulation is on.";
        var ran = 0;
        _commands.Register("test.count", _ => { ran++; return ValueTask.FromResult<object?>(null); });

        var refused = await Play(player, M(C("test.count"), T("x")));
        var commands = await Play(player, M(C("test.count"), C("test.count")));

        Assert.Equal(MacroPlayStatus.Refused, refused.Status);
        Assert.Contains("Vim emulation is on", refused.Message);
        Assert.Equal(MacroPlayStatus.Completed, commands.Status);
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task An_exception_from_the_editor_ends_playback_cleanly()
    {
        var (player, host) = Rig();
        host.OnKey = _ => throw new NullReferenceException("editor bug");

        var result = await Play(player, M(K("left"), T("x")));

        Assert.Equal(MacroPlayStatus.Failed, result.Status);
        Assert.Contains("editor bug", result.Message);
        Assert.False(player.IsPlaying);
        Assert.Empty(_ui.Errors);
    }

    // ------------------------------------------------------------------------------------------------------------ runaway guard

    [Fact]
    public async Task The_step_budget_stops_a_runaway_playback()
    {
        var (player, host) = Rig(limits: new MacroLimits { MaxSteps = 1000 });

        var result = await Play(player, M(T("x"), K("backspace")), new MacroPlayOptions(MacroRepeat.Times, 1_000_000));

        Assert.Equal(MacroPlayStatus.Stopped, result.Status);
        Assert.Equal(1000, result.Steps);
        Assert.Contains(MacroSettings.MaxPlaybackSteps, result.Message);
    }

    [Fact]
    public async Task The_time_budget_stops_a_runaway_playback()
    {
        var clock = new StepClock();
        var (player, host) = Rig(limits: new MacroLimits { MaxDuration = TimeSpan.FromSeconds(10) }, clock: clock);
        host.VirtualCost = TimeSpan.FromMilliseconds(100);

        var result = await Play(player, M(K("left")), new MacroPlayOptions(MacroRepeat.Times, 1_000_000));

        Assert.Equal(MacroPlayStatus.Stopped, result.Status);
        Assert.InRange(result.Steps, 100, 102);
        Assert.Contains(MacroSettings.MaxPlaybackSeconds, result.Message);
    }

    [Fact]
    public async Task A_macro_that_runs_itself_is_stopped_and_nesting_is_bounded()
    {
        var (player, _) = Rig(limits: new MacroLimits { MaxDepth = 3 });
        var macros = new Dictionary<string, Macro>();
        foreach (var name in new[] { "self", "a", "b", "c", "d", "e" })
            _commands.Register("macro.run." + name, async _ => await player.PlayAsync(macros[name]));
        macros["self"] = Named("self", T("x"), C("macro.run.self"));
        macros["a"] = Named("a", C("macro.run.b"));
        macros["b"] = Named("b", C("macro.run.c"));
        macros["c"] = Named("c", C("macro.run.d"));
        macros["d"] = Named("d", C("macro.run.e"));
        macros["e"] = Named("e", T("deep"));

        var self = await Play(player, macros["self"]);
        var deep = await Play(player, macros["a"]);

        Assert.Equal(MacroPlayStatus.Stopped, self.Status);
        Assert.Contains("runs itself", self.Message);
        Assert.Equal(MacroPlayStatus.Stopped, deep.Status);
        Assert.Contains("nested more than 3", deep.Message);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task A_nested_macro_runs_inside_the_playback_and_a_second_playback_is_refused()
    {
        var (player, host) = Rig();
        var inner = Named("inner", T("[in]"));
        Task<MacroPlayResult>? concurrent = null;
        _commands.Register("macro.run.inner", async _ => await player.PlayAsync(inner));
        _commands.Register("test.other", async _ =>
        {
            using (ExecutionContext.SuppressFlow()) // a request from elsewhere (a menu click), not from this playback's flow: refused
                concurrent = Task.Run(() => player.PlayAsync(M(T("never"))));
            await concurrent; // while this playback still runs
            return null;
        });

        var result = await Play(player, M(T("a"), C("macro.run.inner"), C("test.other"), T("b")), new MacroPlayOptions(MacroRepeat.Times, 2));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal("a[in]ba[in]b", host.Text);
        Assert.Equal(2, result.Runs);
        Assert.Equal(10, result.Steps); // 4 per run + the nested text step
        Assert.Equal(MacroPlayStatus.Refused, (await concurrent!).Status);
    }

    // ------------------------------------------------------------------------------------------------------------ cancellation

    [Fact]
    public async Task Cancel_stops_between_steps_and_the_rest_does_not_run()
    {
        var (player, host) = Rig();
        var keys = 0;
        host.OnKey = _ => { if (++keys == 50) player.Cancel(); };

        var result = await Play(player, M(K("right"), T("x")), new MacroPlayOptions(MacroRepeat.Times, 10_000));

        Assert.Equal(MacroPlayStatus.Cancelled, result.Status);
        Assert.Equal(50, keys);
        Assert.Equal(49, host.Text.Length); // the 50th run stopped after its key
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Cancel_does_not_wait_for_a_command_that_never_finishes()
    {
        var (player, host) = Rig();
        var never = new TaskCompletionSource<object?>();
        var started = new TaskCompletionSource();
        _commands.Register("test.prompt", _ => { started.SetResult(); return new ValueTask<object?>(never.Task); });

        var playing = Play(player, M(C("test.prompt"), T("x")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(player.IsPlaying);
        await _ui.Run(player.Cancel);
        var result = await playing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MacroPlayStatus.Cancelled, result.Status);
        Assert.Equal("", host.Text);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task A_pause_waits_without_blocking_the_ui_thread_and_cancel_ends_it()
    {
        var (player, host) = Rig();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Play(player, M(T("a"), new PauseStep(150), T("b")));
        Assert.True(sw.ElapsedMilliseconds >= 140, $"paused {sw.ElapsedMilliseconds} ms");
        Assert.Equal("ab", host.Text);
        Assert.Equal(MacroPlayStatus.Completed, result.Status);

        var playing = Play(player, M(new PauseStep(60_000), T("c")));
        await Task.Delay(50);
        var beat = await _ui.Run(() => 42).WaitAsync(TimeSpan.FromSeconds(5)); // the UI thread is free during the pause
        Assert.Equal(42, beat);
        await _ui.Run(player.Cancel);
        Assert.Equal(MacroPlayStatus.Cancelled, (await playing.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal("ab", host.Text);
    }

    // ------------------------------------------------------------------------------------------------------------ repeat modes

    [Fact]
    public async Task Until_end_of_file_plays_once_per_line_from_the_caret_to_the_last_line()
    {
        var (player, host) = Rig("one\ntwo\nthree\nfour");
        host.Line = 1;

        var result = await Play(player, M(K("home"), T("// "), K("down")), new MacroPlayOptions(MacroRepeat.UntilEndOfFile));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal(3, result.Runs);
        Assert.Equal("one\n// two\n// three\n// four", host.Text);
    }

    [Fact]
    public async Task Until_end_of_file_counts_deleted_lines_as_progress_and_stops_a_macro_that_does_not_move()
    {
        var (player, host) = Rig("1\n2\n3\n4\n5");
        _commands.Register("test.deleteLine", _ => { host.DeleteLine(); return ValueTask.FromResult<object?>(null); });

        var deleted = await Play(player, M(C("test.deleteLine")), new MacroPlayOptions(MacroRepeat.UntilEndOfFile));
        Assert.Equal(5, deleted.Runs);
        Assert.Equal("", host.Text);

        var (player2, host2) = Rig("a\nb");
        var stuck = await Play(player2, M(T("x")), new MacroPlayOptions(MacroRepeat.UntilEndOfFile));
        Assert.Equal(1, stuck.Runs);
        Assert.Equal("xa\nb", host2.Text);
    }

    [Fact]
    public async Task Until_search_fails_repeats_until_the_search_step_finds_nothing()
    {
        var (player, host) = Rig();
        var found = 0;
        _commands.Register(MacroCommands.Find, _ => ++found <= 4 ? ValueTask.FromResult<object?>(null) : throw new MacroSearchFailedException("'foo' was not found."));

        var macro = M(new CommandStep(MacroCommands.Find), T("x"));
        var result = await Play(player, macro, new MacroPlayOptions(MacroRepeat.UntilSearchFails));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        Assert.Equal(4, result.Runs);
        Assert.Equal("xxxx", host.Text);
        Assert.Equal("The last recorded macro played 4 times. The search found nothing more.", MacroPlayResultText.Describe(macro, new MacroPlayOptions(MacroRepeat.UntilSearchFails), result));

        found = 100;
        var once = await Play(player, macro);
        Assert.Equal(MacroPlayStatus.Failed, once.Status); // played once, a failing search is an error
        Assert.Contains("was not found", once.Message);
    }

    [Fact]
    public async Task Until_search_fails_needs_a_search_step_and_stops_a_run_without_change()
    {
        var (player, host) = Rig("abc");
        var refused = await Play(player, M(T("x")), new MacroPlayOptions(MacroRepeat.UntilSearchFails));
        Assert.Equal(MacroPlayStatus.Refused, refused.Status);

        _commands.Register(MacroCommands.Find, _ => ValueTask.FromResult<object?>(null)); // always "finds" without moving
        var stuck = await Play(player, M(new CommandStep(MacroCommands.Find)), new MacroPlayOptions(MacroRepeat.UntilSearchFails));
        Assert.Equal(MacroPlayStatus.Completed, stuck.Status);
        Assert.Equal(1, stuck.Runs);
        Assert.Contains("no change", stuck.Message);
    }

    [Fact]
    public async Task On_selected_lines_plays_from_the_start_of_each_line_and_follows_inserted_lines()
    {
        var (player, host) = Rig("0\n1\n2\n3\n4");
        host.Anchor = (1, 2);
        host.Line = 4;
        host.Column = 0; // a selection ending at the start of a line does not include that line

        var prefixed = await Play(player, M(T("- ")), new MacroPlayOptions(MacroRepeat.OnSelectedLines));
        Assert.Equal(3, prefixed.Runs);
        Assert.Equal("0\n- 1\n- 2\n- 3\n4", host.Text);

        var (player2, host2) = Rig("a\nb\nc");
        host2.Anchor = (0, 0);
        host2.Line = 2;
        host2.Column = 1;
        var grown = await Play(player2, M(K("end"), K("enter"), T(">")), new MacroPlayOptions(MacroRepeat.OnSelectedLines));
        Assert.Equal(3, grown.Runs);
        Assert.Equal("a\n>\nb\n>\nc\n>", host2.Text);
    }

    // ------------------------------------------------------------------------------------------------------------ slicing (UI budget)

    [Fact]
    public async Task Slices_end_before_the_budget_with_virtual_step_costs()
    {
        var clock = new StepClock();
        var (player, host) = Rig(clock: clock);
        host.VirtualCost = TimeSpan.FromMilliseconds(0.5);
        var posted = _ui.Posted;

        var result = await Play(player, M(K("left")), new MacroPlayOptions(MacroRepeat.Times, 300));

        Assert.Equal(MacroPlayStatus.Completed, result.Status);
        var slices = player.SliceDurations;
        Assert.Equal(1, MacroLimits.Default.SliceBudget.TotalMilliseconds);
        Assert.InRange(slices.Count, 149, 152); // two 0.5 ms steps per slice: a third would take it past the 1 ms target
        Assert.All(slices, s => Assert.InRange(s, 0, 1.0001));
        Assert.True(_ui.Posted - posted >= 149);
    }

    [Fact]
    public async Task Playing_ten_thousand_times_keeps_the_ui_thread_within_its_budget()
    {
        // wall-clock: the machine is shared with other builds, so the best of three attempts counts (as in the shell's ResponsivenessTests)
        string? failure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (player, host) = Rig("start");
            host.StepCost = TimeSpan.FromMilliseconds(0.05);
            using var probe = new ResponsivenessProbe(_ui);
            var result = await Play(player, M(T("ab"), K("left"), K("backspace"), K("right")), new MacroPlayOptions(MacroRepeat.Times, 10_000));
            var (samples, p50, p99, max) = probe.Stop();
            var slices = player.SliceDurations.OrderBy(x => x).ToArray();
            var sliceP99 = slices[(int)(slices.Length * 0.99)];

            Assert.Equal(MacroPlayStatus.Completed, result.Status);
            Assert.Equal(40_000, result.Steps);
            Assert.Equal(string.Concat(Enumerable.Repeat("b", 10_000)) + "start", host.Text);
            failure = sliceP99 > 4 || p99 >= 16 || max >= 50
                ? $"slices p99 {sliceP99:F2} ms max {slices[^1]:F2} ms ({slices.Length}); heartbeat p50 {p50:F2} p99 {p99:F2} max {max:F2} ms ({samples})"
                : null;
            if (failure is null) break;
        }
        Assert.True(failure is null, failure);
    }

    [Fact]
    public async Task A_finished_playback_keeps_nothing_alive()
    {
        var (player, _) = Rig();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var macro = M(T(new string('x', 100)), K("left"));
            var result = await Play(player, macro, new MacroPlayOptions(MacroRepeat.Times, 20));
            Assert.Equal(MacroPlayStatus.Completed, result.Status);
            await _ui.DrainAsync(); // the test thread's loop holds its last callback until the next one
            return [macro, result];
        });
        Assert.False(player.IsPlaying);
    }
}
