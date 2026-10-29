using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using CsCode.Ui.Text;
using Xunit;

namespace CsCode.App.Tests;

public class OutputServiceTests
{
    [Fact]
    public void Channel_text_is_a_bounded_ring_trimmed_at_a_line_boundary()
    {
        var ui = new ManualUiDispatcher();
        var output = new OutputService(ui);
        var ch = output.GetOrCreate("Log");
        var line = new string('x', 99) + "\n";
        for (var i = 0; i < 30_000; i++) ch.Append(line); // ~3 MB
        var text = ch.GetText();
        Assert.True(text.Length <= OutputService.MaxChars, $"retained {text.Length}");
        Assert.True(text.Length > OutputService.MaxChars / 2, "should keep a healthy tail, not nothing");
        Assert.Equal(0, text.Length % line.Length); // never starts mid-line
    }

    [Fact]
    public void Streaming_100k_lines_costs_a_bounded_number_of_dispatcher_posts()
    {
        var ui = new ManualUiDispatcher();
        var output = new OutputService(ui);
        var ch = output.GetOrCreate("Build");
        var received = 0;
        var batches = 0;
        using var sub = ch.Appended.Subscribe(t => { batches++; received += t.Length; });
        for (var i = 0; i < 100_000; i++) ch.AppendLine("line " + i);
        // Nothing runs until the UI turn: exactly one coalesced flush is pending, not 100k posts.
        Assert.True(ui.PendingCount <= 2, $"pending posts: {ui.PendingCount}");
        ui.RunPending();
        Assert.Equal(1, batches);
        Assert.True(received > 0);
        Assert.True(ui.Stats.Posted < 10, $"posted {ui.Stats.Posted}");
    }

    [Fact]
    public void Concurrent_appends_from_many_threads_are_not_lost_or_torn()
    {
        var ui = new ManualUiDispatcher();
        var ch = new OutputService(ui).GetOrCreate("Concurrent");
        Parallel.For(0, 8, t => { for (var i = 0; i < 2000; i++) ch.AppendLine($"t{t}-{i}"); });
        var lines = ch.GetText().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16_000, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("t", l));
    }

    [Fact]
    public void Disposing_a_channel_removes_it_and_drops_its_buffer()
    {
        var ui = new ManualUiDispatcher();
        var output = new OutputService(ui);
        var ch = output.GetOrCreate("A");
        output.GetOrCreate("B");
        Assert.Equal(["A", "B"], output.ChannelNames);
        ch.Append("hello");
        ch.Dispose();
        Assert.Equal(["B"], output.ChannelNames);
        Assert.Equal("", ch.GetText());
        ch.Append("ignored after dispose");
        Assert.Equal("", ch.GetText());
    }

    [Fact]
    public void Disposed_channels_are_not_retained()
    {
        var ui = new ManualUiDispatcher();
        var output = new OutputService(ui);
        LeakAssert.NotRetained(() =>
        {
            var ch = output.GetOrCreate("temp");
            var sub = ch.Appended.Subscribe(_ => { });
            ch.AppendLine("x");
            ch.Dispose();
            ui.RunPending();
            return [ch, sub];
        });
    }
}

public class StatusBarServiceTests
{
    [Fact]
    public void Items_are_ordered_by_alignment_then_priority_and_removed_on_dispose()
    {
        var ui = new ManualUiDispatcher();
        var bar = new StatusBarService(ui);
        bar.Set(new StatusBarItemModel("a", "A", Priority: 1));
        var b = bar.Set(new StatusBarItemModel("b", "B", Priority: 5));
        bar.Set(new StatusBarItemModel("r", "R", Alignment: StatusBarAlignment.Right, Priority: 1));
        Assert.Equal(["b", "a", "r"], bar.Items.Select(i => i.Id));
        b.Dispose();
        Assert.Equal(["a", "r"], bar.Items.Select(i => i.Id));
    }

    [Fact]
    public void A_replaced_item_is_not_removed_by_the_older_registration()
    {
        var bar = new StatusBarService(new ManualUiDispatcher());
        var first = bar.Set(new StatusBarItemModel("x", "one"));
        bar.Set(new StatusBarItemModel("x", "two"));
        first.Dispose();
        Assert.Equal("two", Assert.Single(bar.Items).Text);
    }

    [Fact]
    public void Burst_updates_raise_one_coalesced_changed_event_on_the_ui_turn()
    {
        var ui = new ManualUiDispatcher();
        var bar = new StatusBarService(ui);
        var fired = 0;
        using var sub = bar.Changed.Subscribe(_ => fired++);
        for (var i = 0; i < 500; i++) bar.Set(new StatusBarItemModel("pos", $"Ln {i}"));
        Assert.Equal(0, fired);
        ui.RunPending();
        Assert.Equal(1, fired);
        Assert.Equal("Ln 499", bar.Items.Single().Text);
    }
}

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("mw", "MainWindow.cs", true)]
    [InlineData("xyz", "MainWindow.cs", false)]
    [InlineData("", "anything", true)]
    [InlineData("MAIN", "mainwindow", true)]
    [InlineData("wm", "MainWindow.cs", false)] // no 'm' after the 'W'
    [InlineData("mm", "m", false)]
    public void Subsequence_matching_is_case_insensitive(string pattern, string text, bool expected) =>
        Assert.Equal(expected, FuzzyMatcher.Score(pattern, text) >= 0);

    [Fact]
    public void Word_boundary_and_filename_matches_outrank_scattered_matches()
    {
        var good = FuzzyMatcher.Score("mw", "src/App/MainWindow.cs");
        var scattered = FuzzyMatcher.Score("mw", "src/Common/Models/Workflows/x.cs");
        Assert.True(good > scattered, $"{good} vs {scattered}");
        Assert.True(FuzzyMatcher.Score("main", "src/Main.cs") > FuzzyMatcher.Score("main", "src/maintenance/Windowing.cs"));
    }

    [Fact]
    public void Positions_point_at_the_matched_characters()
    {
        var p = FuzzyMatcher.Positions("mw", "MainWindow");
        Assert.Equal([0, 4], p);
        Assert.Empty(FuzzyMatcher.Positions("zz", "MainWindow"));
    }

    [Fact]
    public void Scoring_200k_paths_is_fast_enough_for_a_background_ranker()
    {
        var paths = Enumerable.Range(0, 200_000).Select(i => $"src/module{i % 500}/sub{i % 37}/File{i}.cs").ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = 0;
        foreach (var p in paths) if (FuzzyMatcher.Score("m12f9", p) >= 0) hits++;
        sw.Stop();
        Assert.True(hits > 0);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }
}

public class QuickInputTests
{
    private static QuickPickItem[] Items(params string[] labels) => labels.Select(l => new QuickPickItem(l)).ToArray();

    [Fact]
    public async Task Small_lists_show_instantly_and_enter_resolves_the_highlighted_item()
    {
        var ui = new ManualUiDispatcher();
        var vm = new QuickInputViewModel(ui);
        var task = vm.PickAsync(new StaticQuickPickSource(Items("Alpha", "Beta", "Gamma"), null), "Pick");
        Assert.True(vm.IsVisible);
        Assert.Equal(3, vm.Items.Count); // no dispatcher turn needed for the first paint
        Assert.Equal(0, vm.SelectedIndex);
        vm.MoveSelection(+1);
        vm.Accept();
        var entry = await task;
        Assert.Equal("Beta", entry!.Label);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Typing_filters_with_fuzzy_ranking_and_highlight_spans()
    {
        var ui = new ManualUiDispatcher();
        var vm = new QuickInputViewModel(ui);
        var task = vm.PickAsync(new StaticQuickPickSource(Items("Open File", "Open Folder", "Toggle Terminal"), null));
        vm.Text = "tt";
        Assert.Equal("Toggle Terminal", Assert.Single(vm.Items).Label);
        Assert.NotEmpty(vm.Items[0].LabelMatches);
        vm.Cancel();
        Assert.Null(await task);
    }

    [Fact]
    public async Task Escape_resolves_null_and_a_new_session_cancels_the_previous_one()
    {
        var vm = new QuickInputViewModel(new ManualUiDispatcher());
        var first = vm.PickAsync(new StaticQuickPickSource(Items("a"), null));
        var second = vm.PickAsync(new StaticQuickPickSource(Items("b"), null));
        Assert.Null(await first);
        Assert.True(vm.IsVisible);
        vm.Cancel();
        Assert.Null(await second);
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public async Task Multi_pick_returns_the_checked_entries()
    {
        var vm = new QuickInputViewModel(new ManualUiDispatcher());
        var task = vm.PickManyAsync(new StaticQuickPickSource(Items("a", "b", "c"), new QuickPickOptions(CanPickMany: true)));
        vm.ToggleChecked();
        vm.MoveSelection(+1);
        vm.MoveSelection(+1);
        vm.ToggleChecked();
        vm.Accept();
        var picked = await task;
        Assert.Equal(["a", "c"], picked!.Select(p => p.Label));
    }

    [Fact]
    public async Task Input_box_validates_each_keystroke_and_blocks_invalid_accept()
    {
        var vm = new QuickInputViewModel(new ManualUiDispatcher());
        var task = vm.InputAsync(new InputBoxOptions(Prompt: "Name", Value: "", Validate: s => s.Contains(' ') ? "No spaces" : null));
        vm.Text = "a b";
        Assert.Equal("No spaces", vm.Validation);
        vm.Accept();
        Assert.True(vm.IsVisible); // invalid: stays open
        vm.Text = "ab";
        Assert.Null(vm.Validation);
        vm.Accept();
        Assert.Equal("ab", await task);
    }

    private sealed class SlowSource : IQuickPickSource
    {
        public string? Placeholder => null;
        public IReadOnlyList<QuickPickEntry>? TryQuerySync(string text) => null;
        public int Calls;
        public async Task<IReadOnlyList<QuickPickEntry>> QueryAsync(string text, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(text == "slow" ? 300 : 5, ct);
            return [new QuickPickEntry("result for " + text)];
        }
    }

    [Fact]
    public async Task Stale_async_results_never_overwrite_a_newer_query()
    {
        var ui = new ManualUiDispatcher();
        var vm = new QuickInputViewModel(ui);
        var src = new SlowSource();
        var task = vm.PickAsync(src, initialText: "slow");
        vm.Text = "fast";
        var deadline = Environment.TickCount64 + 3000;
        while (vm.Items.Count == 0 && Environment.TickCount64 < deadline) { await Task.Delay(10); ui.RunPending(); }
        await Task.Delay(400);
        ui.RunPending();
        Assert.Equal("result for fast", Assert.Single(vm.Items).Label);
        vm.Cancel();
        await task;
    }
}

public class WindowServiceTests
{
    private sealed class Rig
    {
        public ManualUiDispatcher Ui { get; } = new();
        public NotificationCenter Notifications { get; } = new();
        public QuickInputViewModel Quick { get; }
        public StatusBarService Status { get; }
        public FakeShellHost Host { get; } = new();
        public WindowService Window { get; }
        public Rig()
        {
            Quick = new QuickInputViewModel(Ui);
            Status = new StatusBarService(Ui);
            Window = new WindowService(Ui, Notifications, Quick, Status, Host);
        }

        /// <summary>Synchronously pumps the "UI thread" (the test thread) until <paramref name="done"/> is true. Sync on purpose: an async
        /// continuation would hop off the thread the manual dispatcher treats as the UI thread.</summary>
        public void PumpUntil(Func<bool> done, int timeoutMs = 5000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (!done() && Environment.TickCount64 < deadline) { Ui.RunPending(); Thread.Sleep(2); }
            Ui.RunPending();
            Assert.True(done(), "timed out pumping the dispatcher");
        }

        public T Pump<T>(Task<T> task)
        {
            PumpUntil(() => task.IsCompleted);
            return task.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void ShowMessage_with_actions_resolves_with_the_clicked_action_and_removes_the_toast()
    {
        var rig = new Rig();
        var task = Task.Run(async () => await rig.Window.ShowMessageAsync(MessageSeverity.Warning, "Reload?", [new MessageAction("Reload"), new MessageAction("Ignore")]));
        rig.PumpUntil(() => rig.Notifications.Toasts.Count > 0);
        var toast = Assert.Single(rig.Notifications.Toasts);
        toast.InvokeCommand.Execute(toast.Actions[0]);
        Assert.Equal("Reload", rig.Pump(task));
        Assert.Empty(rig.Notifications.Toasts);
    }

    [Fact]
    public void Calls_from_a_worker_thread_cost_the_caller_only_an_enqueue()
    {
        var rig = new Rig();
        // SetStatusBarMessage is synchronous: it must return without waiting for the UI turn (the UI thread is not pumping here).
        var t = Task.Run(() => { var s = System.Diagnostics.Stopwatch.StartNew(); rig.Window.SetStatusBarMessage("hi"); return s.Elapsed.TotalMilliseconds; });
        var ms = rig.Pump(t);
        Assert.True(ms < 50, $"caller blocked for {ms} ms");
        Assert.Empty(rig.Notifications.Toasts);
    }

    [Fact]
    public void Progress_updates_are_coalesced_and_the_notification_is_removed_when_work_ends()
    {
        var rig = new Rig();
        var result = rig.Pump(Task.Run(() => rig.Window.WithProgressAsync("Indexing", cancellable: true, async (p, ct) =>
        {
            for (var i = 0; i < 10_000; i++) p.Report("file " + i, i / 100.0);
            await Task.Yield();
            return 42;
        })));
        Assert.Equal(42, result);
        rig.Ui.RunPending();
        Assert.Empty(rig.Notifications.History);
        Assert.DoesNotContain(rig.Status.Items, i => i.Id.StartsWith("progress."));
        Assert.True(rig.Ui.Stats.Posted < 200, $"progress reporting posted {rig.Ui.Stats.Posted} times");
    }

    [Fact]
    public void ShowQuickPick_goes_through_the_overlay_and_returns_the_picked_item()
    {
        var rig = new Rig();
        var task = Task.Run(async () => await rig.Window.ShowQuickPickAsync([new QuickPickItem("one"), new QuickPickItem("two")], new QuickPickOptions(Title: "t")));
        rig.PumpUntil(() => rig.Quick.IsVisible);
        rig.Quick.MoveSelection(1);
        rig.Quick.Accept();
        var picked = rig.Pump(task);
        Assert.Equal("two", picked!.Label);
    }

    [Fact]
    public async Task Clipboard_and_launch_delegate_to_the_host()
    {
        var rig = new Rig();
        await rig.Window.CopyToClipboardAsync("abc");
        await rig.Window.OpenExternalAsync(new Uri("https://example.com"));
        Assert.Equal("abc", rig.Host.Clipboard);
        Assert.Single(rig.Host.Launched);
    }
}

internal static class TaskExtensions
{
    public static Task<T> AsTask<T>(this Task<T> t) => t;
}
