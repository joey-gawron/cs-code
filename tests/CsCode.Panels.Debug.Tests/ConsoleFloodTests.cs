using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// The Debug Console under a flood: one apply pass per batch the pump delivers (one Add, at most one Remove), and REPL lines that keep their
/// place after an oversize batch.
/// </summary>
public class ConsoleFloodTests
{
    private static string Lines(int from, int count) => string.Concat(Enumerable.Range(from, count).Select(i => "flood line " + i + "\n"));

    [AvaloniaFact]
    public async Task A_batch_of_thousands_of_lines_reaches_the_list_in_one_add_and_trimming_is_one_remove()
    {
        using var rig = new PanelRig();
        using var vm = new DebugConsoleViewModel(rig.Services);
        var changes = new List<(NotifyCollectionChangedAction Action, int Count, int Index)>();
        vm.Lines.CollectionChanged += (_, e) => changes.Add((e.Action, (e.NewItems ?? e.OldItems)?.Count ?? 0, e.Action == NotifyCollectionChangedAction.Add ? e.NewStartingIndex : e.OldStartingIndex));

        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", Lines(0, 3_000)));
        await PanelRig.Eventually(() => vm.Lines.Count == 3_000, what: "first batch");
        // Before: 150 lines per UI callback, 20 notifications (and as many row realisations) for one batch.
        Assert.Equal([(NotifyCollectionChangedAction.Add, 3_000, 0)], changes);

        changes.Clear();
        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", Lines(3_000, 4_000)));
        await PanelRig.Eventually(() => vm.Lines.Count > 0 && vm.Lines[^1].Text == "flood line 6999", what: "second batch");
        Assert.Equal([(NotifyCollectionChangedAction.Remove, 2_000, 0), (NotifyCollectionChangedAction.Add, 4_000, 1_000)], changes);
        Assert.Equal(DebugConsoleViewModel.MaxLines, vm.Lines.Count);
        Assert.Equal("flood line 2000", vm.Lines[0].Text);
    }

    [AvaloniaFact]
    public async Task Many_events_within_one_frame_are_applied_together()
    {
        using var rig = new PanelRig();
        using var vm = new DebugConsoleViewModel(rig.Services);
        await Task.Delay(50); // the (empty) seed has been drained
        var adds = 0;
        vm.Lines.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) adds++; };
        var applied = vm.Applied;
        // One console.log per line: 2,000 events arriving faster than a frame.
        await Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++) rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", "log line " + i + "\n"));
        }, TestContext.Current.CancellationToken);
        await PanelRig.Eventually(() => vm.Lines.Count > 0 && vm.Lines[^1].Text == "log line 1999", what: "all lines");
        Assert.Equal(2_000, vm.Lines.Count);
        Assert.True(adds <= 4 && vm.Applied - applied == adds, $"{adds} adds, {vm.Applied - applied} apply passes for 2,000 events");
    }

    [AvaloniaFact]
    public async Task A_repl_line_keeps_its_place_after_an_oversize_batch_whose_head_the_history_cut()
    {
        // Follow-up "REPL line placement after a flood": the real history keeps an oversize batch as `with { Output = tail }` (OutputHistory
        // cuts its head), a new instance with a new string. The line entered after the batch was replayed before it (it matched neither).
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => new DebugEvaluateResult("42");
        rig.StopAt(2);
        var first = new DebugConsoleViewModel(rig.Services);
        var batch = new DebugOutputEventArgs(rig.Session, "stdout", Lines(0, 6_000));
        rig.Debug.EmitOutput(batch);
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "flood line 5999"), what: "the batch");
        first.Input = "x";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "42"), what: "the result");
        first.Dispose();

        // What the real OutputHistory holds after cutting the batch to fit its 5,000 lines (no console is open: the transcript stays).
        rig.Debug.ClearOutputHistory();
        var cut = batch with { Output = ConsoleOutputPump.TailLines(batch.Output, 4_000) };
        Assert.Equal(batch.Sequence, cut.Sequence);
        rig.Debug.EmitOutput(cut);
        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", "later\n"));

        using var second = new DebugConsoleViewModel(rig.Services);
        await PanelRig.Eventually(() => second.Lines.Any(l => l.Text == "later"), what: "re-seeded");
        string[] tail = [.. second.Lines.Skip(second.Lines.Count - 4).Select(l => l.Text)];
        Assert.Equal(["flood line 5999", "> x", "42", "later"], tail);
    }
}
