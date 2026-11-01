using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class ConsolePumpTests
{
    [Fact]
    public void Line_counting_and_tailing_work_on_partial_and_terminated_text()
    {
        Assert.Equal(0, ConsoleOutputPump.CountLines(""));
        Assert.Equal(1, ConsoleOutputPump.CountLines("a"));
        Assert.Equal(1, ConsoleOutputPump.CountLines("a\n"));
        Assert.Equal(3, ConsoleOutputPump.CountLines("a\nb\nc"));
        Assert.Equal(3, ConsoleOutputPump.CountLines("a\nb\nc\n"));
        Assert.Equal("b\nc\n", ConsoleOutputPump.TailLines("a\nb\nc\n", 2));
        Assert.Equal("b\nc", ConsoleOutputPump.TailLines("a\nb\nc", 2));
        Assert.Equal("c\n", ConsoleOutputPump.TailLines("a\nb\nc\n", 1));
        Assert.Equal("a\nb\n", ConsoleOutputPump.TailLines("a\nb\n", 5));
    }

    [Fact]
    public void A_million_lines_collapse_to_the_newest_five_thousand_plus_a_note_without_splitting_the_rest()
    {
        var buffer = new ConsoleBuffer();
        using var pump = new ConsoleOutputPump(buffer, _ => true, _ => { });
        var text = string.Concat(Enumerable.Range(0, 1_000_000).Select(i => "L" + i + "\n"));
        var sw = Stopwatch.StartNew();
        var lines = pump.ToLines([new DebugOutputEventArgs(new FakeDebugSession(), "stdout", text)], ConsoleBuffer.DefaultCapacity);
        sw.Stop();
        Assert.Equal(ConsoleBuffer.DefaultCapacity + 1, lines.Count);
        Assert.Equal(ConsoleLineKind.Info, lines[0].Kind);
        Assert.Contains("995,000 lines", lines[0].Text);
        Assert.Equal("L999999", lines[^1].Text);
        Assert.Equal("L995000", lines[1].Text);
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Output_is_categorised_and_carriage_returns_and_long_lines_are_handled()
    {
        var buffer = new ConsoleBuffer();
        using var pump = new ConsoleOutputPump(buffer, _ => true, _ => { });
        var s = new FakeDebugSession();
        var lines = pump.ToLines(
        [
            new DebugOutputEventArgs(s, "stdout", "out1\r\nout2\n"),
            new DebugOutputEventArgs(s, "stderr", "boom\n"),
            new DebugOutputEventArgs(s, "console", "Loaded 'x.dll'\n"),
            new DebugOutputEventArgs(s, "important", "Heads up\n"),
            new DebugOutputEventArgs(s, "weird", "?\n"),
            new DebugOutputEventArgs(s, "stdout", new string('q', 10_000) + "\n"),
        ], 100);
        Assert.Equal(
            [ConsoleLineKind.Output, ConsoleLineKind.Output, ConsoleLineKind.Error, ConsoleLineKind.Console, ConsoleLineKind.Important, ConsoleLineKind.Console, ConsoleLineKind.Output],
            lines.Select(l => l.Kind));
        Assert.Equal("out1", lines[0].Text);
        Assert.Equal(ConsoleOutputPump.MaxLineChars + 1, lines[^1].Text.Length);
        Assert.Equal("err", lines[2].StyleClass);
        Assert.True(lines.Zip(lines.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence));
    }

    [Fact]
    public void Repl_lines_follow_the_output_they_followed_and_those_whose_output_left_the_history_come_first()
    {
        using var pump = new ConsoleOutputPump(new ConsoleBuffer(), _ => true, _ => { });
        var s = new FakeDebugSession();
        var trimmed = new DebugOutputEventArgs(s, "stdout", "old\n");
        var first = new DebugOutputEventArgs(s, "stdout", "one\ntwo\n");
        var second = new DebugOutputEventArgs(s, "stdout", "three\n");
        var lines = pump.ToLines([first, second], 100,
        [
            new ReplLine(ConsoleLineKind.Input, "> a", trimmed.Sequence),
            new ReplLine(ConsoleLineKind.Input, "> b", first.Sequence),
            new ReplLine(ConsoleLineKind.Result, "2", first.Sequence),
        ]);
        Assert.Equal(["> a", "one", "two", "> b", "2", "three"], lines.Select(l => l.Text));
        Assert.True(lines.Zip(lines.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence));
        GC.KeepAlive(trimmed);
    }

    [Fact]
    public void Repl_lines_find_the_archived_copies_of_an_ended_sessions_output_but_not_output_that_merely_has_the_same_text()
    {
        // Regression (dbgp-life-1, review): DebugService.ArchiveHistory replaces an ended session's history entries with `with { Session = ended }`
        // copies; the copy keeps the event's sequence, a different event with the same text does not have it.
        using var pump = new ConsoleOutputPump(new ConsoleBuffer(), _ => true, _ => { });
        var s = new FakeDebugSession();
        var ended = new FakeDebugSession(s.Id, s.Name);
        var tick1 = new DebugOutputEventArgs(s, "stdout", "tick\n");
        var tick2 = new DebugOutputEventArgs(s, "stdout", "tick\n");
        var lost = new DebugOutputEventArgs(s, "stdout", "lost\n"); // left the history (dropped from a flood): goes after what came before it
        var done = new DebugOutputEventArgs(s, "stdout", "done\n");
        DebugOutputEventArgs[] archived = [tick1 with { Session = ended }, tick2 with { Session = ended }, done with { Session = ended }];
        var lines = pump.ToLines([.. archived], 100,
        [
            new ReplLine(ConsoleLineKind.Input, "> a", tick2.Sequence),
            new ReplLine(ConsoleLineKind.Result, "1", tick2.Sequence),
            new ReplLine(ConsoleLineKind.Input, "> b", lost.Sequence),
            new ReplLine(ConsoleLineKind.Input, "> c", done.Sequence),
        ]);
        Assert.Equal(["tick", "tick", "> a", "1", "> b", "done", "> c"], lines.Select(l => l.Text));
        GC.KeepAlive(lost);
    }

    [Fact]
    public async Task A_repl_line_survives_output_overflowing_the_queue_while_a_drain_is_busy()
    {
        var delivered = new ConcurrentQueue<ConsoleLine>();
        using var busy = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var pump = new ConsoleOutputPump(new ConsoleBuffer(), _ => true, b =>
        {
            foreach (var l in b) delivered.Enqueue(l);
            busy.Set();
            release.Wait(TimeSpan.FromSeconds(10)); // the first batch holds the drain: the UI is behind, the queue fills
        });
        var s = new FakeDebugSession();
        var first = new DebugOutputEventArgs(s, "stdout", "first\n");
        pump.Enqueue(first);
        Assert.True(busy.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        pump.EnqueueRepl(new ReplLine(ConsoleLineKind.Input, "> x", first.Sequence));
        pump.EnqueueRepl(new ReplLine(ConsoleLineKind.Result, "42", first.Sequence));
        // > 4 MB of 4,000-character lines: the oldest output is dropped, never the user's lines
        var line = new string('o', ConsoleOutputPump.MaxLineChars - 1) + "\n";
        for (var i = 0; i < ConsoleOutputPump.MaxQueuedChars / line.Length + 100; i++)
            pump.Enqueue(new DebugOutputEventArgs(s, "stdout", line));
        release.Set();

        var sw = Stopwatch.StartNew();
        while (!delivered.Any(l => l.Text == "42") && sw.ElapsedMilliseconds < 5_000)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        var texts = delivered.Select(l => l.Text).ToList();
        Assert.Contains("> x", texts);
        Assert.Contains("42", texts);
        Assert.True(texts.IndexOf("> x") < texts.IndexOf("42"));
        // placed after the output it followed: "first" came in the earlier batch, so the echo heads the next one (after the drop note)
        Assert.True(texts.IndexOf("first") < texts.IndexOf("> x"));
    }

    [Fact]
    public async Task The_producer_side_only_enqueues_and_a_flood_stays_bounded()
    {
        var buffer = new ConsoleBuffer();
        var delivered = 0;
        var batches = 0;
        using var pump = new ConsoleOutputPump(buffer, _ => true, b => { Interlocked.Add(ref delivered, b.Count); Interlocked.Increment(ref batches); });
        var s = new FakeDebugSession();
        var chunk = string.Concat(Enumerable.Repeat("a line of program output\n", 2_000));
        var sw = Stopwatch.StartNew();
        long calls = 0, worst = 0;
        // ~1M lines/s for 400 ms from "the debug lane"
        while (sw.ElapsedMilliseconds < 400)
        {
            var t = Stopwatch.GetTimestamp();
            pump.Enqueue(new DebugOutputEventArgs(s, "stdout", chunk));
            worst = Math.Max(worst, Stopwatch.GetTimestamp() - t);
            calls++;
            if (calls % 200 == 0) await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await Task.Delay(300, TestContext.Current.CancellationToken); // let the last window drain
        Assert.True(delivered > 0);
        Assert.True(buffer.Count <= ConsoleBuffer.DefaultCapacity);
        var workerMs = worst * 1000.0 / Stopwatch.Frequency;
        Assert.True(workerMs < 20, $"one Enqueue took {workerMs:F2} ms on the caller (the debug lane)");
        // at most one batch per ≥ 16 ms window
        Assert.True(batches <= 400 / 16 + 8, $"{batches} batches in ~0.7 s");
    }
}

public class ConsoleViewModelTests
{
    private static DebugOutputEventArgs Out(PanelRig rig, string category, string text) => new(rig.Session, category, text);

    private static async Task<DebugConsoleViewModel> Open(PanelRig rig)
    {
        var vm = new DebugConsoleViewModel(rig.Services);
        await Task.Yield();
        return vm;
    }

    [AvaloniaFact]
    public async Task Program_output_appears_split_into_lines_with_their_categories()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "hello\nworld\n"));
        rig.Debug.EmitOutput(Out(rig, "stderr", "oops\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 3, what: "3 lines");
        Assert.Equal(["hello", "world", "oops"], vm.Lines.Select(l => l.Text));
        Assert.Equal([ConsoleLineKind.Output, ConsoleLineKind.Output, ConsoleLineKind.Error], vm.Lines.Select(l => l.Kind));
    }

    [AvaloniaFact]
    public async Task Output_that_started_before_the_console_opened_is_replayed_from_history_exactly_once()
    {
        using var rig = new PanelRig();
        rig.Debug.EmitOutput(Out(rig, "stdout", "early1\nearly2\n"));
        using var vm = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "late\n"));
        await PanelRig.Eventually(() => vm.Lines.Count >= 3, what: "history + live");
        await Task.Delay(100);
        Assert.Equal(["early1", "early2", "late"], vm.Lines.Select(l => l.Text));
    }

    [AvaloniaFact]
    public async Task Retained_lines_are_bounded_to_five_thousand()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        for (var i = 0; i < 40; i++)
            rig.Debug.EmitOutput(Out(rig, "stdout", string.Concat(Enumerable.Range(i * 500, 500).Select(n => "line " + n + "\n"))));
        await PanelRig.Eventually(() => vm.Lines.Count > 0 && vm.Lines[^1].Text == "line 19999", what: "newest line shown");
        Assert.True(vm.Lines.Count <= DebugConsoleViewModel.MaxLines, $"{vm.Lines.Count} lines retained");
        Assert.Equal("line 19999", vm.Lines[^1].Text);
    }

    [AvaloniaFact]
    public async Task Clear_empties_the_console_and_the_history_for_late_subscribers()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "a\nb\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 2);
        vm.ClearCommand.Execute(null);
        await PanelRig.Eventually(() => vm.Lines.Count == 0);
        Assert.Empty(rig.Debug.GetOutputHistory());
        rig.Debug.EmitOutput(Out(rig, "stdout", "c\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 1);
        Assert.Equal("c", vm.Lines[0].Text);
    }

    [AvaloniaFact]
    public async Task The_repl_history_and_transcript_survive_the_console_being_hidden_and_shown_again()
    {
        // Regression (dbgp-life-1): the shell disposes the console's view-model whenever the window is hidden (also the automatic hide at the
        // end of every session). Program output came back from IDebugOutputHistory, but the '> expr' lines, their results and the ↑ history did not.
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult(e == "1 + 1" ? "2" : "?", "int");
        rig.StopAt(2);
        var first = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "before\n"));
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "before"), what: "output");
        first.Input = "1 + 1";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "2"), what: "result");
        first.Input = "nope";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "?"), what: "second result");
        rig.Debug.EmitOutput(Out(rig, "stdout", "after\n"));
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "after"), what: "more output");
        string[] shown = [.. first.Lines.Select(l => l.Text)];
        Assert.Equal(["before", "> 1 + 1", "2", "> nope", "?", "after"], shown);
        first.Dispose();

        using var second = await Open(rig);
        await PanelRig.Eventually(() => second.Lines.Count >= shown.Length, what: "re-seeded");
        await Task.Delay(50);
        Assert.Equal(shown, second.Lines.Select(l => l.Text));
        Assert.Equal([ConsoleLineKind.Output, ConsoleLineKind.Input, ConsoleLineKind.Result], second.Lines.Take(3).Select(l => l.Kind));
        Assert.True(second.HistoryPrevious());
        Assert.Equal("nope", second.Input);
        Assert.True(second.HistoryPrevious());
        Assert.Equal("1 + 1", second.Input);

        // Clear empties the transcript too, and the history is persisted (application scope) for the next run.
        second.ClearCommand.Execute(null);
        await PanelRig.Eventually(() => second.Lines.Count == 0);
        second.Dispose();
        using var third = await Open(rig);
        await Task.Delay(100);
        Assert.Empty(third.Lines);
        Assert.Equal(["1 + 1", "nope"], rig.State.Get<List<string>>(ReplState.HistoryKey));
        using var restarted = new PanelRig();
        restarted.State.Set(ReplState.HistoryKey, new List<string> { "a", "b" });
        using var fresh = new DebugConsoleViewModel(new DebugPanelServices(restarted.Debug, restarted.Ui, state: restarted.State));
        Assert.Equal(["a", "b"], fresh.HistoryEntries);
    }

    [AvaloniaFact]
    public async Task The_transcript_keeps_its_place_in_the_output_when_the_session_ended_before_the_console_was_shown_again()
    {
        // Regression (dbgp-life-1, review): at the end of a session the real DebugService replaces every history entry with a copy whose
        // Session is an EndedSession (ArchiveHistory), and the console is hidden automatically. The console opened for the next session never
        // saw the event instances the REPL lines were anchored to, so it replayed them all as one block before the program output.
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult(e == "1 + 1" ? "2" : "?", "int");
        rig.StopAt(2);
        var first = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "before\n"));
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "before"), what: "output");
        first.Input = "1 + 1";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "2"), what: "result");
        rig.Debug.EmitOutput(Out(rig, "stdout", "middle\n"));
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "middle"), what: "more output");
        first.Input = "nope";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "?"), what: "second result");
        rig.Debug.EmitOutput(Out(rig, "stdout", "exited\n"));
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "exited"), what: "last output");
        string[] shown = [.. first.Lines.Select(l => l.Text)];
        Assert.Equal(["before", "> 1 + 1", "2", "middle", "> nope", "?", "exited"], shown);

        // The session ends: the history is archived (copies) and the console is hidden.
        rig.Session.Terminate();
        rig.Debug.RemoveSession(rig.Session);
        first.Dispose();
        Assert.All(rig.Debug.GetOutputHistory(), e => Assert.NotSame(rig.Session, e.Session));

        // The next session's console shows the previous run in the same order, then the new output.
        var next = new FakeDebugSession("s2", "Next").WithDefaultScript();
        rig.Debug.AddSession(next);
        using var second = await Open(rig);
        rig.Debug.EmitOutput(new DebugOutputEventArgs(next, "stdout", "next run\n"));
        await PanelRig.Eventually(() => second.Lines.Any(l => l.Text == "next run"), what: "re-seeded and new output");
        await Task.Delay(50);
        Assert.Equal([.. shown, "next run"], second.Lines.Select(l => l.Text));
    }

    [AvaloniaFact]
    public async Task Clearing_from_elsewhere_clears_this_console_too()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "a\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 1);
        rig.Debug.ClearOutputHistory();
        await PanelRig.Eventually(() => vm.Lines.Count == 0);
    }

    [AvaloniaFact]
    public async Task The_filter_narrows_existing_and_new_lines_and_can_be_cleared()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        rig.Debug.EmitOutput(Out(rig, "stdout", "alpha\nbeta\nALPHA two\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 3);
        vm.Filter = "alpha";
        await PanelRig.Eventually(() => vm.Lines.Count == 2, what: "filtered");
        Assert.Equal(["alpha", "ALPHA two"], vm.Lines.Select(l => l.Text));

        rig.Debug.EmitOutput(Out(rig, "stdout", "gamma\nalpha three\n"));
        await PanelRig.Eventually(() => vm.Lines.Count == 3);
        Assert.Equal("alpha three", vm.Lines[^1].Text);

        vm.Filter = "";
        await PanelRig.Eventually(() => vm.Lines.Count == 5, what: "unfiltered");
    }

    [AvaloniaFact]
    public async Task Submitting_evaluates_in_the_focused_frame_with_context_repl_and_appends_echo_and_result()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult("42");
        rig.StopAt(2);
        using var vm = await Open(rig);
        vm.Input = "x + 1";
        await vm.SubmitAsync();
        await PanelRig.Eventually(() => vm.Lines.Count == 2, what: "echo + result");
        Assert.Equal(["> x + 1", "42"], vm.Lines.Select(l => l.Text));
        Assert.Equal([ConsoleLineKind.Input, ConsoleLineKind.Result], vm.Lines.Select(l => l.Kind));
        Assert.Equal(("x + 1", 102, "repl"), rig.Session.EvaluateRequests.Single());
        Assert.Equal("", vm.Input);
    }

    [AvaloniaFact]
    public async Task Evaluation_errors_show_as_error_lines_and_empty_input_does_nothing()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => throw new InvalidOperationException("error: unknown identifier 'q'");
        rig.StopAt(2);
        using var vm = await Open(rig);
        vm.Input = "   ";
        await vm.SubmitAsync();
        Assert.Empty(vm.Lines);
        vm.Input = "q";
        await vm.SubmitAsync();
        await PanelRig.Eventually(() => vm.Lines.Count == 2);
        Assert.Equal(ConsoleLineKind.ResultError, vm.Lines[1].Kind);
        Assert.Contains("unknown identifier", vm.Lines[1].Text);
    }

    [AvaloniaFact]
    public async Task Without_a_session_submitting_reports_it()
    {
        using var rig = new PanelRig(withSession: false);
        using var vm = await Open(rig);
        vm.Input = "1+1";
        await vm.SubmitAsync();
        await PanelRig.Eventually(() => vm.Lines.Count == 2);
        Assert.Equal("There is no active debug session.", vm.Lines[1].Text);
    }

    [AvaloniaFact]
    public async Task Multi_line_input_is_echoed_and_evaluated_as_one_expression()
    {
        using var rig = new PanelRig();
        rig.StopAt(2);
        using var vm = await Open(rig);
        vm.Input = "var a = 1;\nvar b = 2;";
        await vm.SubmitAsync();
        await PanelRig.Eventually(() => vm.Lines.Count == 2);
        Assert.Equal("> var a = 1;\n  var b = 2;", vm.Lines[0].Text);
        Assert.Equal("var a = 1;\nvar b = 2;", rig.Session.EvaluateRequests.Single().Expression);
    }

    [AvaloniaFact]
    public async Task Up_and_down_walk_the_history_and_return_to_the_draft()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        foreach (var e in new[] { "one", "two", "two", "three" }) { vm.Input = e; await vm.SubmitAsync(); }
        Assert.Equal(["one", "two", "three"], vm.HistoryEntries); // consecutive duplicates collapse

        vm.Input = "draft";
        Assert.True(vm.HistoryPrevious());
        Assert.Equal("three", vm.Input);
        Assert.True(vm.HistoryPrevious());
        Assert.Equal("two", vm.Input);
        Assert.True(vm.HistoryPrevious());
        Assert.Equal("one", vm.Input);
        Assert.False(vm.HistoryPrevious());
        Assert.True(vm.HistoryNext());
        Assert.Equal("two", vm.Input);
        Assert.True(vm.HistoryNext());
        Assert.True(vm.HistoryNext());
        Assert.Equal("draft", vm.Input);
        Assert.False(vm.HistoryNext());
    }

    [AvaloniaFact]
    public async Task Completions_come_from_the_adapter_and_accepting_one_replaces_the_fragment()
    {
        using var rig = new PanelRig();
        rig.Session.CompletionHandler = (text, column) => [new DebugCompletionItem("Count", "Count", "property", 2, 1), new DebugCompletionItem("Capacity", "Capacity", "property", 2, 1)];
        rig.StopAt(2);
        using var vm = await Open(rig);
        vm.Input = "o.C";
        await PanelRig.Eventually(() => vm.CompletionsOpen, what: "completion list");
        Assert.Equal(["Count", "Capacity"], vm.Completions.Select(c => c.Label));
        Assert.Equal("Count", vm.SelectedCompletion?.Label);

        vm.SelectNextCompletion(+1);
        Assert.Equal("Capacity", vm.SelectedCompletion?.Label);
        vm.AcceptCompletion(vm.SelectedCompletion!);
        Assert.Equal("o.Capacity", vm.Input);
        Assert.False(vm.CompletionsOpen);
        Assert.Equal("o.Capacity".Length, vm.CaretAfterCompletion);
    }

    [AvaloniaFact]
    public async Task Typing_quickly_asks_the_adapter_once_and_stale_answers_are_ignored()
    {
        using var rig = new PanelRig();
        rig.Session.CompletionHandler = (text, _) => [new DebugCompletionItem(text + "!", text + "!", null, 0, text.Length)];
        rig.StopAt(2);
        using var vm = await Open(rig);
        vm.Input = "a";
        vm.Input = "ab";
        vm.Input = "abc";
        await PanelRig.Eventually(() => vm.CompletionsOpen);
        Assert.Equal("abc!", vm.Completions.Single().Label);
        Assert.Equal(1, rig.Session.CompletionCalls);
    }

    [AvaloniaFact]
    public async Task No_completions_are_requested_while_running_or_when_unsupported()
    {
        using var rig = new PanelRig();
        using var vm = await Open(rig);
        vm.Input = "abc"; // running
        await Task.Delay(250);
        Assert.Equal(0, rig.Session.CompletionCalls);
        Assert.False(vm.CompletionsOpen);

        rig.Session.Capabilities = rig.Session.Capabilities with { SupportsCompletionsRequest = false };
        rig.StopAt(2);
        vm.Input = "abcd";
        await Task.Delay(250);
        Assert.Equal(0, rig.Session.CompletionCalls);
    }

    [AvaloniaFact]
    public async Task Disposing_unsubscribes_from_the_debug_service()
    {
        using var rig = new PanelRig();
        var vm = await Open(rig);
        Assert.True(rig.Debug.OutputEmitter.SubscriberCount > 0);
        vm.Dispose();
        Assert.Equal(0, rig.Debug.OutputEmitter.SubscriberCount);
        Assert.Equal(0, rig.Debug.OutputClearedEmitter.SubscriberCount);
        rig.Debug.EmitOutput(Out(rig, "stdout", "after dispose\n"));
        await Task.Delay(50);
        Assert.Empty(vm.Lines);
    }
}
