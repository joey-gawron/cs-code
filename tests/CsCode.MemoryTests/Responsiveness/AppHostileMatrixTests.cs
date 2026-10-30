using System.Globalization;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.MemoryTests.App;
using CsCode.Profiling.Testing;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.MemoryTests.Responsiveness;

/// <summary>
/// The hostile-load matrix (MUST 9) against the REAL app, headless, while a user types into a real editor. Measured the way docs/MEMORY.md
/// "Measuring UI budgets headless" prescribes (T23): every key is posted to the UI thread as an input-priority dispatcher job (as the platform
/// delivers input — calling the handler directly would hide the wait), the <see cref="ResponsivenessProbe"/> posts a tick every 10 ms, and NO
/// frames are rasterised during the measured window (the headless compositor rasterises in software ON the UI thread, which desktops do on
/// their render thread): that cost is measured separately, for the same load, and reported, not budgeted. Budget scenarios get up to three
/// attempts — a stall that comes from the app reproduces, a neighbour's CPU burst on this shared machine does not — and every attempt's numbers are
/// written to the test output and .scratch/w6-budgets.log.
/// </summary>
public sealed class AppHostileMatrixTests(ITestOutputHelper output)
{
    public static IEnumerable<TheoryDataRow<string>> Loads() => HostileMatrix.Loads.Select(l => new TheoryDataRow<string>(l.Name));

    [AvaloniaTheory(Timeout = 600_000)]
    [MemberData(nameof(Loads))]
    public async Task Typing_stays_within_budget_under_hostile_load(string loadName)
    {
        var load = HostileMatrix.Loads.Single(l => l.Name == loadName);
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // The app's GC mode (AppStartup.ConfigureRuntime sets SustainedLowLatency in Program.Main; a test process must set it itself).
            var mode = System.Runtime.GCSettings.LatencyMode;
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            (bool Ok, string Report) result;
            try { result = await RunOnceAsync(load, attempt); }
            finally { System.Runtime.GCSettings.LatencyMode = mode; }
            var (ok, report) = result;
            Note(report);
            if (ok) return;
            failures.Add(report);
        }
        Assert.Fail("budgets missed on every attempt:\n" + string.Join('\n', failures));
    }

    private static async Task<(bool Ok, string Report)> RunOnceAsync(HostileLoad load, int attempt)
    {
        await using var host = await MemoryAppHost.StartAsync();
        var files = AppWorkspace.Seed(host.Files, 2);
        var file = files[0];
        await host.Get<INavigationService>().OpenAsync(new Uri(file));
        var area = await TypingScenario.TextAreaAsync(host);
        var doc = host.Get<INavigationService>().ActiveDocument!;
        var context = new HostileContext(host.Get<IDiagnosticService>(), host.Get<IOutputService>(), host.Get<ILanguageFeatureRegistry>(), host.Get<ICommandService>(),
            doc.Uri, doc.LanguageId, f => Dispatcher.UIThread.InvokeAsync(f));
        if (load.Name == "output-flood") host.Shell.Tools.Show("output");
        await MemoryAppHost.SettleAsync(20);
        FullGc.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var ui = host.Get<IUiDispatcher>();

        using var gc = new GcPauseRecorder();
        var running = load.Start(context, CancellationToken.None);
        var stopped = false;
        try
        {
            await Task.Delay(500); // the load is under way before measuring
            // T55: what the UI thread did in the measured window — its CPU time and run-queue wait (a loaded machine), Avalonia's own layout /
            // render / input timings (real UI-thread work) and the GC pauses of this window only — so a miss says where it came from.
            using var activity = new ThreadActivityRecorder("Avalonia.Diagnostic.Meter");
            using var gcWindow = new GcPauseRecorder();
            using var probe = new ResponsivenessProbe(a => Dispatcher.UIThread.Post(a)).Start();
            var typing = new TypingLatencyProbe(a => Dispatcher.UIThread.Post(a, DispatcherPriority.Input),
                c => area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString() }));
            await typing.TypeAsync(string.Concat(Enumerable.Repeat("abc.def ", 10)), TimeSpan.FromMilliseconds(50)); // 80 keys over 4 s
            var typed = await typing.DrainAsync();
            var dispatcher = await probe.StopAsync();
            activity.Stop();
            gcWindow.Stop();
            var window = $"in the measured window: {activity.Report()}; {gcWindow.Report()}";

            // the same load with the headless compositor rasterising frames on the UI thread: reported, not budgeted
            var raster = await RasterisedWindowAsync();

            // queues drained: once the load stops, the UI catches up within 3 s
            stopped = true;
            await running.DisposeAsync();
            var drained = await MemoryAppHost.WaitFor(() => ui.Stats.Posted - ui.Stats.Executed <= 2, 3000);
            FullGc.Collect();
            var heapGrowthMb = (GC.GetTotalMemory(true) - heapBefore) / 1024.0 / 1024.0;
            var ok = dispatcher.P99Ms <= HostileMatrix.DispatcherP99Ms && dispatcher.MaxMs <= HostileMatrix.DispatcherMaxMs && dispatcher.Dropped == 0
                && typed.Total.P99Ms <= HostileMatrix.TypingP99Ms && typed.Missing == 0 && heapGrowthMb <= HostileMatrix.HeapGrowthMb && drained;
            var report = string.Create(CultureInfo.InvariantCulture,
                $"{load.Name} attempt {attempt} (load {LoadAverage()}): {dispatcher}; {typed}; heap {heapGrowthMb:+0.0;-0.0} MB after; queues drained {drained} " +
                $"(posted-executed {ui.Stats.Posted - ui.Stats.Executed}); {window}; whole run (incl. the rasterised second): {gc.Report()}; " +
                $"with headless rasterisation on the UI thread: {raster}");
            return (ok, report);
        }
        finally
        {
            if (!stopped) await running.DisposeAsync();
        }
    }

    /// <summary>One second of dispatcher latency with the compositor ticked at 60 Hz (software Skia on the UI thread, headless only).</summary>
    private static async Task<ResponsivenessReport> RasterisedWindowAsync()
    {
        using var probe = new ResponsivenessProbe(a => Dispatcher.UIThread.Post(a)).Start();
        var frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
        frames.Start();
        await Task.Delay(1000);
        frames.Stop();
        return await probe.StopAsync();
    }

    private static string LoadAverage() => File.ReadAllText("/proc/loadavg").Split(' ')[0];

    private void Note(string text)
    {
        output.WriteLine(text);
        try
        {
            var dir = Path.Combine(AppWorkspace.RepoRoot() ?? Path.GetTempPath(), ".scratch");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "w6-budgets.log"), $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch (IOException) { }
    }
}
