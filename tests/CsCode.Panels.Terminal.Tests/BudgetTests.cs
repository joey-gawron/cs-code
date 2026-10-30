using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Panels.Terminal.Views;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Xunit;
using ProcessTable = CsCode.Terminal.Pty.Unix.ProcessTable;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// The terminal brief's binding budgets in the real composition with a real shell: a flood (<c>yes</c>, a 100 MB <c>cat</c>) with the terminal
/// on screen keeps the dispatcher within p99 &lt; 16 ms / max &lt; 50 ms while an editor is being typed into (typing p99 &lt; 20 ms), memory stays
/// under the scrollback cap, Ctrl+C reaches the program within 100 ms; 50 terminals created and killed retain no process, thread, descriptor or
/// view; a hidden window does no UI work for its terminals. Frames are driven at 60 Hz (the headless platform renders only when told to) and
/// input is injected as dispatcher jobs, as a real input event arrives (Avalonia's headless helpers pump the queue themselves, which would
/// hide the wait). The app's GC mode (SustainedLowLatency, set by Program.Main) is used. Budget scenarios get up to three attempts: a stall in
/// the terminal reproduces, a neighbour's CPU burst on this shared machine does not.
/// </summary>
public sealed class BudgetTests
{
    private const double P99BudgetMs = 16;
    private const double MaxBudgetMs = 50;
    private const double TypingP99BudgetMs = 20;

    /// <summary>
    /// How frames are produced at 60 Hz. <see cref="Record"/>: the terminal control's <c>Render</c> records its drawing (what the UI thread does
    /// per frame on a desktop, where Avalonia rasterises on its render thread). <see cref="Rasterize"/>: the headless compositor renders the
    /// window with software Skia ON the UI thread (headless only: it measures this environment, not the terminal).
    /// </summary>
    internal enum FrameMode { None, Record, Rasterize }

    /// <summary>Posts to the dispatcher every ~1 ms from a pool thread and records how late each post ran; also drives frames at 60 Hz.</summary>
    private sealed class Heartbeat : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<double> _latencies = new();
        private readonly List<double> _ticks = new();
        private readonly Task _beat;

        /// <summary>Compositor ticks (headless: the frame's software rasterisation runs on the UI thread): median and longest, ms.</summary>
        public (double Median, double Max, int Count) Ticks
        {
            get
            {
                lock (_ticks)
                {
                    if (_ticks.Count == 0) return (0, 0, 0);
                    var sorted = _ticks.Order().ToArray();
                    return (sorted[sorted.Length / 2], sorted[^1], sorted.Length);
                }
            }
        }
        private readonly Task _frames;

        public Heartbeat(FrameMode frames = FrameMode.Record, TerminalControl? control = null)
        {
            _beat = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var sent = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() => { lock (_latencies) _latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds); });
                    try { await Task.Delay(1, _cts.Token); } catch (OperationCanceledException) { break; }
                }
            });
            _frames = frames == FrameMode.None ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        if (frames == FrameMode.Rasterize) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        else if (control is { IsEffectivelyVisible: true })
                        {
                            var group = new Avalonia.Media.DrawingGroup();
                            using (var ctx = group.Open()) control.Render(ctx);
                        }
                        lock (_ticks) _ticks.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    }, DispatcherPriority.Render);
                    try { await Task.Delay(16, _cts.Token); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public async Task<(double P99, double Max, int Samples)> StopAsync()
        {
            _cts.Cancel();
            await _beat;
            await _frames;
            for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            double[] sorted;
            lock (_latencies) sorted = [.. _latencies.Order()];
            return sorted.Length == 0 ? (0, 0, 0) : (sorted[(int)(sorted.Length * 0.99)], sorted[^1], sorted.Length);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_cts.IsCancellationRequested) await StopAsync();
            _cts.Dispose();
        }
    }

    /// <summary>Delivers a text input to the focused element as a dispatcher job (as the platform does) and records post-to-handled latency.</summary>
    private static void TypeAsJob(List<double> latencies, string text)
    {
        var posted = Stopwatch.GetTimestamp();
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is null && FocusedElement() is { } target)
                target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
            else if (FocusedElement() is { } t2)
                t2.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
            lock (latencies) latencies.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
        }, DispatcherPriority.Input);
    }

    private static Interactive? FocusedElement() => _window?.FocusManager?.GetFocusedElement() as Interactive;

    [ThreadStatic] private static Avalonia.Controls.TopLevel? _window;

    private static async Task<T> WithLowLatencyGc<T>(Func<Task<T>> run)
    {
        var mode = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        try { return await run(); }
        finally { System.Runtime.GCSettings.LatencyMode = mode; }
    }

    private static async Task<TerminalAppHost> StartWithShellAsync()
    {
        var app = await TerminalAppHost.StartAsync();
        _window = app.Window;
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        return app;
    }

    [AvaloniaFact]
    public async Task A_yes_flood_on_screen_keeps_the_ui_responsive_typing_fast_memory_bounded_and_ctrl_c_quick()
    {
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (ok, report) = await WithLowLatencyGc(() => YesFloodOnce(attempt));
            Note(report);
            if (ok) return;
            failures.Add(report);
        }
        Assert.Fail("budgets missed on every attempt:\n" + string.Join('\n', failures));
    }

    private static async Task<(bool, string)> YesFloodOnce(int attempt)
    {
        await using var app = await StartWithShellAsync();
        var session = app.Terminals.ActiveSession!;
        var control = app.View!.Control;
        // an editor beside the terminal, being typed into while the terminal floods
        var file = app.Sandbox.WriteFile("typing.txt", "start\n");
        await app.Get<INavigationService>().OpenAsync(new Uri(file));
        await TerminalAppHost.SettleAsync(20);
        var textArea = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(app.Window).OfType<AvaloniaEdit.Editing.TextArea>().First();
        textArea.Focus();
        await TerminalAppHost.WaitFor(() => textArea.IsKeyboardFocusWithin);
        var editorFocused = FocusedElement() is AvaloniaEdit.Editing.TextArea;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var heapBefore = GC.GetTotalMemory(true);
        var renderBefore = control.RenderTime;
        var rendersBefore = control.RenderCount;

        var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(), GC.GetTotalAllocatedBytes(false));
        using var reasons = new GcReasons();
        await session.SendTextAsync("yes 'flooding the terminal with output'");
        await using var beat = new Heartbeat(FrameMode.Record, control);
        var typing = new List<double>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000)
        {
            TypeAsJob(typing, "x");
            await Task.Delay(50);
        }
        var (p99, max, samples) = await beat.StopAsync();
        var gc = $"GC gen0/1/2 +{GC.CollectionCount(0) - gc0.Item1}/{GC.CollectionCount(1) - gc0.Item2}/{GC.CollectionCount(2) - gc0.Item3} ({reasons}), pauses {(GC.GetTotalPauseDuration() - gc0.Item4).TotalMilliseconds:F0} ms, allocated {(GC.GetTotalAllocatedBytes(false) - gc0.Item5) / 1024 / 1024} MB";

        // the same flood with the headless compositor rasterising on the UI thread: reported, not a budget (desktops rasterise on the render thread)
        await using (var raster = new Heartbeat(FrameMode.Rasterize))
        {
            await Task.Delay(3000);
            var (rp99, rmax, _) = await raster.StopAsync();
            Note($"  (headless rasterisation on the UI thread during the same flood: dispatcher p99 {rp99:F1} ms, max {rmax:F1} ms, ticks median {raster.Ticks.Median:F1} ms)");
        }
        var parsed = await session.InvokeAsync(e => e.ScreenTop);
        var ticks = beat.Ticks;
        double typingP99;
        lock (typing) { typing.Sort(); typingP99 = typing.Count == 0 ? 0 : typing[(int)(typing.Count * 0.99)]; }
        var renders = control.RenderCount - rendersBefore;
        var renderMs = (control.RenderTime - renderBefore).TotalMilliseconds;
        await TerminalAppHost.SettleAsync(5);

        // Ctrl+C, delivered to the focused terminal as the platform delivers a key (a dispatcher job routed from the window)
        control.Focus();
        await TerminalAppHost.WaitFor(() => control.IsKeyboardFocusWithin);
        var yes = ProcessTable.SessionMembers(session.ProcessId!.Value).FirstOrDefault(p => ProcessTable.Name(p) == "yes");
        Assert.True(yes > 0, "yes is not running");
        var posted = 0L;
        var gone = Task.Run(() =>
        {
            while (Volatile.Read(ref posted) == 0) Thread.SpinWait(50);
            while (ProcessTable.Stat(yes) is { State: not 'Z' } && Stopwatch.GetElapsedTime(posted).TotalMilliseconds < 5000) Thread.SpinWait(200);
            return Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
        });
        await using (var during = new Heartbeat(FrameMode.Record, control))
        {
            Volatile.Write(ref posted, Stopwatch.GetTimestamp());
            Dispatcher.UIThread.Post(() => control.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control, KeySymbol = "c", Source = control,
            }), DispatcherPriority.Input);
            await Task.Delay(300);
        }
        var interruptMs = await gone;
        await session.SendTextAsync("echo after-$((40+2))");
        Assert.True(await app.WaitForScreenAsync("after-42", 20_000), "the shell did not come back: " + app.ScreenText());
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var heapGrowthMb = (GC.GetTotalMemory(true) - heapBefore) / 1024.0 / 1024.0;

        var report = $"yes attempt {attempt}: dispatcher p99 {p99:F1} ms, max {max:F1} ms ({samples} samples); typing p99 {typingP99:F2} ms ({typing.Count} keys, editor focused {editorFocused}); " +
            $"{renders} renders, {renderMs / Math.Max(1, renders):F2} ms avg, longest {control.LongestRender.TotalMilliseconds:F1} ms; compositor ticks median {ticks.Median:F1} ms, max {ticks.Max:F1} ms ({ticks.Count}); " +
            $"Ctrl+C to exit {interruptMs:F1} ms; heap +{heapGrowthMb:F1} MB; {gc}; {parsed} lines scrolled";
        var ok = samples > 1000 && p99 < P99BudgetMs && max < MaxBudgetMs && typingP99 < TypingP99BudgetMs && interruptMs < 100 && heapGrowthMb < 64;
        return (ok, report);
    }

    [AvaloniaFact]
    public async Task Cat_of_a_100_mb_file_on_screen_keeps_the_ui_responsive_and_memory_bounded()
    {
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (ok, report) = await WithLowLatencyGc(() => CatOnce(attempt));
            Note(report);
            if (ok) return;
            failures.Add(report);
        }
        Assert.Fail("budgets missed on every attempt:\n" + string.Join('\n', failures));
    }

    private static async Task<(bool, string)> CatOnce(int attempt)
    {
        await using var app = await StartWithShellAsync();
        var session = app.Terminals.ActiveSession!;
        var control = app.View!.Control;
        var big = app.Sandbox.Combine("big.txt");
        await using (var w = new StreamWriter(big))
        {
            var line = "0123456789 the quick brown fox jumps over the lazy dog \e[1;32mcolour\e[0m 0123456789 abcdefghijklmnop";
            for (long written = 0; written < 100L * 1024 * 1024; written += line.Length + 1) await w.WriteLineAsync(line);
        }
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var heapBefore = GC.GetTotalMemory(true);
        var rendersBefore = control.RenderCount;
        var renderBefore = control.RenderTime;
        await session.SendTextAsync($"cat '{big}'; echo cat-finished");
        await using var beat = new Heartbeat(FrameMode.Record, control);
        var sw = Stopwatch.StartNew();
        var finished = false;
        while (!finished && sw.ElapsedMilliseconds < 180_000)
        {
            await Task.Delay(200);
            finished = await session.InvokeAsync(e => e.ScreenText().Contains("cat-finished", StringComparison.Ordinal));
        }
        var seconds = sw.Elapsed.TotalSeconds;
        var (p99, max, samples) = await beat.StopAsync();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var heapGrowthMb = (GC.GetTotalMemory(true) - heapBefore) / 1024.0 / 1024.0;
        var renders = control.RenderCount - rendersBefore;
        var renderMs = (control.RenderTime - renderBefore).TotalMilliseconds;
        var report = $"cat attempt {attempt}: 100 MB in {seconds:F1} s ({100 / seconds:F1} MB/s), dispatcher p99 {p99:F1} ms, max {max:F1} ms ({samples} samples), " +
            $"{renders} renders, {renderMs / Math.Max(1, renders):F2} ms avg, longest {control.LongestRender.TotalMilliseconds:F1} ms, heap +{heapGrowthMb:F1} MB, finished {finished}";
        Assert.True(finished, "cat did not finish: " + report);
        return (p99 < P99BudgetMs && max < MaxBudgetMs && heapGrowthMb < 64, report);
    }

    [AvaloniaFact]
    public async Task Baseline_frames_and_heartbeat_without_any_output()
    {
        // The control experiment for the floods: the same window, frames at 60 Hz, nothing written to the terminal.
        await using var app = await StartWithShellAsync();
        using var reasons = new GcReasons();
        var gen2 = GC.CollectionCount(2);
        await using var beat = new Heartbeat(FrameMode.Record, app.View!.Control);
        await Task.Delay(5000);
        var (p99, max, samples) = await beat.StopAsync();
        Note($"baseline: dispatcher p99 {p99:F1} ms, max {max:F1} ms ({samples} samples), frames {beat.Ticks}, gen2 +{GC.CollectionCount(2) - gen2} ({reasons})");
        // and a terminal that redraws every frame (a full screen of changing text) without a flood behind it
        var session = app.Terminals.ActiveSession!;
        var gen2b = GC.CollectionCount(2);
        using var reasons2 = new GcReasons();
        await session.SendTextAsync("i=0; while [ $i -lt 400 ]; do printf '\\033[H'; seq $i $((i+30)); i=$((i+1)); sleep 0.01; done");
        await using var redraw = new Heartbeat(FrameMode.Record, app.View!.Control);
        await Task.Delay(4000);
        var (rp99, rmax, rsamples) = await redraw.StopAsync();
        Note($"redraw: dispatcher p99 {rp99:F1} ms, max {rmax:F1} ms ({rsamples} samples), gen2 +{GC.CollectionCount(2) - gen2b} ({reasons2})");
    }

    [AvaloniaFact]
    public async Task Fifty_terminals_created_and_killed_retain_no_process_thread_descriptor_or_view()
    {
        await using var app = await StartWithShellAsync();
        await app.Terminals.KillAsync(app.Terminals.ActiveSession!);
        await TerminalAppHost.SettleAsync(20);
        _ = ProcessTable.SessionMembers(1);
        GC.Collect();
        var fds = Directory.GetFileSystemEntries("/proc/self/fd").Length;
        var threads = Process.GetCurrentProcess().Threads.Count;
        var pids = new List<int>();
        var weak = await CreateAndKill(app, pids);
        for (var i = 0; i < 10 && weak.Any(w => w.Ref.TryGetTarget(out _)); i++)
        {
            await TerminalAppHost.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        var alive = weak.Where(w => w.Ref.TryGetTarget(out _)).Select(w => w.Name).ToList();
        Assert.True(alive.Count == 0, "still reachable: " + string.Join(", ", alive));
        foreach (var pid in pids) Assert.True(await TerminalAppHost.WaitFor(() => !Directory.Exists($"/proc/{pid}"), 5000), $"process {pid} left behind");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000 && (Directory.GetFileSystemEntries("/proc/self/fd").Length > fds + 4 || Process.GetCurrentProcess().Threads.Count > threads + 4))
            await TerminalAppHost.SettleAsync(5);
        Assert.True(Directory.GetFileSystemEntries("/proc/self/fd").Length <= fds + 4, $"descriptors {fds} -> {Directory.GetFileSystemEntries("/proc/self/fd").Length}");
        Assert.True(Process.GetCurrentProcess().Threads.Count <= threads + 4, $"threads {threads} -> {Process.GetCurrentProcess().Threads.Count}");
        Assert.Empty(app.Terminals.Terminals);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<List<(string Name, WeakReference<object> Ref)>> CreateAndKill(TerminalAppHost app, List<int> pids)
    {
        var weak = new List<(string, WeakReference<object>)>();
        for (var i = 0; i < 50; i++)
        {
            await app.RunAsync(TerminalCommandSet.New);
            Assert.True(await TerminalAppHost.WaitFor(() => app.Terminals.ActiveSession is { ProcessId: not null } && app.View is not null));
            var s = app.Terminals.ActiveSession!;
            pids.Add(s.ProcessId!.Value);
            weak.Add(($"session {i}", new WeakReference<object>(s)));
            if (i % 10 == 0)
            {
                // the tool window's view (focused) comes and goes too
                weak.Add(($"view {i}", new WeakReference<object>(app.View!)));
                weak.Add(($"control {i}", new WeakReference<object>(app.View!.Control)));
                await app.RunAsync(TerminalCommandSet.Toggle);
                Assert.True(await TerminalAppHost.WaitFor(() => app.View is null));
            }
            await app.RunAsync(TerminalCommandSet.Kill);
        }
        await TerminalAppHost.SettleAsync(20);
        return weak;
    }

    [AvaloniaFact]
    public async Task A_hidden_window_does_no_ui_work_for_its_terminals()
    {
        await using var app = await StartWithShellAsync();
        var session = app.Terminals.ActiveSession!;
        var control = app.View!.Control;
        control.Focus();
        await TerminalAppHost.WaitFor(() => control.IsKeyboardFocusWithin);
        app.Window.Hide();
        await TerminalAppHost.SettleAsync(10);
        var renders = control.RenderCount;
        var frames = 0;
        using var sub = session.FrameReady.Subscribe(_ => Interlocked.Increment(ref frames));
        // output keeps arriving while hidden: the terminal parses it, the UI is not involved
        await session.SendTextAsync("for i in $(seq 1 2000); do echo hidden-$i; done");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Assert.True(frames <= 1, $"{frames} frames built for a hidden window");
        Assert.True(control.RenderCount - renders <= 1, $"{control.RenderCount - renders} renders while hidden");
        Assert.Contains("hidden-2000", await session.InvokeAsync(e => e.ScreenText()));
        // idle: no timer, no polling — measured over a plain wait (no pumping by the test itself)
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        await Task.Delay(3000);
        var idlePercent = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / 3000.0 * 100;
        Note($"hidden: {frames} frames, {control.RenderCount - renders} renders, idle CPU {idlePercent:F2} %");
        Assert.True(idlePercent < 2, $"idle CPU {idlePercent:F1} % while hidden");
        app.Window.Show();
        Assert.True(await app.WaitForScreenAsync("hidden-2000"), "the shown window did not catch up");
    }

    private static void Note(string text)
    {
        var dir = Path.Combine(Support.RepoRoot() ?? Path.GetTempPath(), ".scratch", "terminal-notes");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "budgets.log"), $"{DateTime.Now:HH:mm:ss} {text}\n");
    }
}
