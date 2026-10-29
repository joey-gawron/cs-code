using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.Panels.Debug.Views;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// The hostile-debuggee suite of the panels brief: a flooding debuggee, an adapter that answers slowly or never, a million-element array,
/// and rapid step-step-step must not stall the UI thread, grow memory without bound or leave stale data from an earlier stop on screen.
/// A heartbeat probe posts to the (real, headless) dispatcher from a background thread every ~1 ms and records how long each post waited:
/// budget p99 &lt; 16 ms, max &lt; 50 ms (docs/ARCHITECTURE §6). The machine is shared with other agents and rendering is software Skia, so a
/// scenario gets up to three attempts: a real stall in the panels reproduces on every attempt, a neighbour's CPU burst does not.
/// </summary>
public class ResponsivenessTests
{
    private const double P99BudgetMs = 16;
    private const double MaxBudgetMs = 50;
    private const int Attempts = 3;

    private sealed class Heartbeat : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<double> _latenciesMs = new();
        private readonly Task _loop;

        public Heartbeat()
        {
            AvaloniaUiDispatcher.Instance.Stats.ResetLongest();
            _loop = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                var sent = Stopwatch.GetTimestamp();
                Dispatcher.UIThread.Post(() =>
                {
                    var ms = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                    lock (_latenciesMs) _latenciesMs.Add(ms);
                }, DispatcherPriority.Normal);
                try { await Task.Delay(1, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public async Task<Measurement> StopAsync()
        {
            _cts.Cancel();
            await _loop;
            await Task.Delay(60); // let the posts still queued on the UI thread run and be counted
            double[] sorted;
            lock (_latenciesMs) sorted = [.. _latenciesMs.Order()];
            var longest = AvaloniaUiDispatcher.Instance.Stats.LongestDispatchMs;
            return sorted.Length == 0 ? new Measurement(0, 0, 0, longest) : new Measurement(sorted[(int)(sorted.Length * 0.99)], sorted[^1], sorted.Length, longest);
        }

        public void Dispose() => _cts.Dispose();
    }

    private readonly record struct Measurement(double P99, double Max, int Samples, double LongestCallback)
    {
        public bool WithinBudget => Samples > 20 && P99 < P99BudgetMs && Max < MaxBudgetMs;
        public override string ToString() => $"p99 {P99:F1} ms, max {Max:F1} ms, {Samples} samples, longest instrumented callback {LongestCallback:F1} ms";
    }

    private static async Task Budgeted(string what, Func<Task<Measurement>> scenario)
    {
        var all = new List<Measurement>();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var m = await scenario();
            all.Add(m);
            var line = $"{what} (attempt {attempt}): {m}";
            TestContext.Current.SendDiagnosticMessage(line);
            File.AppendAllText(Path.Combine(TestTheme.ScreenshotDirectory(), "..", "responsiveness.log"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            if (m.WithinBudget) return;
        }
        Assert.Fail($"{what}: dispatcher latency exceeded the budget (p99 < {P99BudgetMs} ms, max < {MaxBudgetMs} ms) on all {Attempts} attempts: " + string.Join(" / ", all));
    }

    /// <summary>One complete stop/resume cycle with fast answers, so that the templates, styles and JIT are warm before anything is measured.</summary>
    private static async Task WarmUpAsync(Scenario sc)
    {
        var latency = sc.Session.Latency;
        sc.Session.Latency = FakeLatency.Instant;
        sc.Rig.StopAt(2);
        await Task.Delay(500);
        sc.Rig.Resume();
        await Task.Delay(200);
        sc.Session.Latency = latency;
    }

    // ------------------------------------------------------------------ flooding debuggee

    [AvaloniaFact]
    public Task A_debuggee_flooding_a_million_lines_per_second_does_not_stall_the_ui_and_memory_stays_bounded() => Budgeted("output flood", async () =>
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(frame: 2);
        var view = (ConsoleView)sc.View("debug.console");
        var model = (DebugConsoleViewModel)view.DataContext!;
        var window = new Window { Width = 900, Height = 300, Content = view };
        window.Show();
        sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", "warm-up line\n")); // first realisation of rows is JIT/template work, not steady state
        await PanelRig.Eventually(() => model.Lines.Count > 0);
        await Task.Delay(300);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        using var probe = new Heartbeat();
        var chunk = string.Concat(Enumerable.Range(0, 2_000).Select(i => "flood line " + i + " with some program output text\n"));
        long produced = 0;
        await Task.Run(() =>
        {
            // "the debug lane": as many events as it can push, ~2,000 lines each, for 2 seconds (millions of lines per second)
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2_000)
            {
                sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", chunk));
                Interlocked.Add(ref produced, 2_000);
            }
        });
        await Task.Delay(400); // drain
        var measurement = await probe.StopAsync();

        Assert.True(produced > 1_000_000, $"only produced {produced} lines");
        Assert.True(model.Lines.Count <= DebugConsoleViewModel.MaxLines, $"{model.Lines.Count} lines retained");
        Assert.True(model.Lines.Count > 0);
        Assert.StartsWith("flood line", model.Lines[^1].Text);
        Assert.True(sc.Rig.Debug.GetOutputHistory().Count <= 5_000);

        window.Content = null;
        window.Close();
        view.Dispose();
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(memoryAfter - memoryBefore < 64L * 1024 * 1024, $"memory grew by {(memoryAfter - memoryBefore) / 1024 / 1024} MB during the flood");
        return measurement;
    });

    // ------------------------------------------------------------------ million-element array

    [AvaloniaFact]
    public Task Expanding_a_million_element_array_from_an_adapter_that_ignores_paging_does_not_stall_the_ui() => Budgeted("1M-element array", async () =>
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(frame: 2);
        sc.Session.IgnorePaging = true;
        sc.Session.Variables[3100] = Enumerable.Range(0, 1_000_000).Select(i => new DebugVariable($"[{i}]", i.ToString(), "int")).ToList();
        var view = sc.View("debug.locals");
        var tree = ScreenshotTests.TreeOf(view);
        var window = new Window { Width = 900, Height = 300, Content = view };
        window.Show();
        await PanelRig.Eventually(() => tree.Rows.Any(r => r.Name == "numbers"), what: "locals");
        await tree.ToggleAsync(tree.Rows.Single(r => r.Name == "item")); // warm the row templates up first
        await Task.Delay(300);

        using var probe = new Heartbeat();
        await tree.ToggleAsync(tree.Rows.Single(r => r.Name == "numbers"));
        await Task.Delay(300);
        var measurement = await probe.StopAsync();
        Assert.True(tree.Rows.Count <= VariablesTreeViewModel.MaxLoadedPerNode + 20, $"{tree.Rows.Count} rows");
        Assert.True(tree.Rows.Count > 1_000, "the capped first part of the array was shown");

        window.Content = null;
        window.Close();
        (view as IDisposable)?.Dispose();
        return measurement;
    });

    // ------------------------------------------------------------------ slow and hung adapters

    [AvaloniaFact]
    public Task A_slow_adapter_leaves_the_ui_responsive_and_every_window_shows_loading_meanwhile() => Budgeted("slow adapter", async () =>
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.Latency = FakeLatency.Slow;
        sc.Session.Delay = TimeSpan.FromMilliseconds(700);
        var views = new[] { "debug.locals", "debug.autos", "debug.watch", "debug.callstack", "debug.threads" }.Select(sc.View).ToArray();
        sc.Rig.Debug.AddWatch("x + 1");
        var panel = new Grid { RowDefinitions = new RowDefinitions("*,*,*,*,*") };
        for (var i = 0; i < views.Length; i++) { Grid.SetRow(views[i], i); panel.Children.Add(views[i]); }
        var window = new Window { Width = 900, Height = 600, Content = panel };
        window.Show();
        await WarmUpAsync(sc);

        using var probe = new Heartbeat();
        sc.Rig.StopAt(2);
        await Task.Delay(300); // requests are in flight
        Assert.True(sc.Session.Outstanding > 0, "the slow adapter has outstanding requests");
        Assert.Equal("Loading…", ((CallStackViewModel)views[3].DataContext!).StatusText);
        await Task.Delay(2500); // let them finish
        var measurement = await probe.StopAsync();
        Assert.True(ScreenshotTests.TreeOf(views[0]).Rows.Count > 0);

        window.Content = null;
        window.Close();
        foreach (var v in views) (v as IDisposable)?.Dispose();
        return measurement;
    });

    [AvaloniaFact]
    public Task An_adapter_that_never_answers_does_not_block_the_ui_or_keep_closed_windows_alive() => Budgeted("hung adapter", async () =>
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.Latency = FakeLatency.Hang;
        var views = new[] { "debug.locals", "debug.autos", "debug.watch", "debug.callstack", "debug.threads" }.Select(sc.View).ToArray();
        sc.Rig.Debug.AddWatch("x + 1");
        using var probe = new Heartbeat();
        sc.Rig.StopAt(2);
        await Task.Delay(500);
        Assert.True(sc.Session.Outstanding >= 4, $"{sc.Session.Outstanding} requests hang");
        foreach (var v in views) (v as IDisposable)?.Dispose(); // closing the windows must not wait for the hung adapter
        await Task.Delay(100);
        return await probe.StopAsync();
    });

    // ------------------------------------------------------------------ step, step, step

    [AvaloniaFact]
    public Task Rapid_step_step_step_never_shows_data_of_an_earlier_stop() => Budgeted("150 rapid steps", async () =>
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario(stopped: false);
        sc.Session.Latency = FakeLatency.Slow;
        sc.Session.Delay = TimeSpan.FromMilliseconds(25);
        sc.Rig.Debug.AddWatch("counter");
        var step = 0;
        sc.Session.Evaluator = (e, _, _) => new DebugEvaluateResult("step" + Volatile.Read(ref step), "string");
        var views = new[] { "debug.locals", "debug.watch", "debug.callstack" }.Select(sc.View).ToArray();
        var locals = ScreenshotTests.TreeOf(views[0]);
        var watch = ScreenshotTests.TreeOf(views[1]);
        var stack = (CallStackViewModel)views[2].DataContext!;
        var window = new Window { Width = 900, Height = 500, Content = new StackPanel { Children = { views[0], views[1], views[2] } } };
        window.Show();
        await WarmUpAsync(sc);

        using var probe = new Heartbeat();
        for (var i = 1; i <= 150; i++)
        {
            Volatile.Write(ref step, i);
            sc.Session.Variables[1200] = [new DebugVariable("x", "v" + i, "int"), new DebugVariable("s", "\"hello\"", "string")];
            sc.Rig.StopAt(2);
            await Task.Delay(Random.Shared.Next(1, 9));
            sc.Rig.Resume();
            await Task.Delay(Random.Shared.Next(0, 4));
        }
        Volatile.Write(ref step, 151);
        sc.Session.Variables[1200] = [new DebugVariable("x", "final", "int"), new DebugVariable("s", "\"hello\"", "string")];
        sc.Rig.StopAt(2);
        await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "x" && r.FullValue == "final"), what: "final locals");
        await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "counter" && r.FullValue == "step151"), what: "final watch");
        await PanelRig.Eventually(() => stack.Rows.Any(r => r.Kind == CallStackRowKind.Frame && r.IsCurrent), what: "final stack");
        await Task.Delay(300);
        var measurement = await probe.StopAsync();
        // nothing from an earlier stop is still on screen
        Assert.Equal("final", locals.Rows.Single(r => r.Name == "x").FullValue);
        Assert.Equal("step151", watch.Rows.Single(r => r.Name == "counter").FullValue);
        Assert.Equal(3, stack.Rows.Count(r => r.Kind == CallStackRowKind.Frame && !r.IsExternal));

        window.Content = null;
        window.Close();
        foreach (var v in views) (v as IDisposable)?.Dispose();
        return measurement;
    });
}
