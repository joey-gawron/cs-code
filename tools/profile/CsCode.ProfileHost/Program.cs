using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using CsCode.App.Startup;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Terminal;
using CsCode.MemoryTests.App;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;

namespace CsCode.ProfileHost;

/// <summary>
/// <c>cscode-profile COMMAND [options]</c> (docs/MEMORY.md; the scripts in tools/profile run it):
/// <list type="bullet">
/// <item><c>synthetic [--steps N]</c> — the GC matrix's synthetic workload; prints one <c>RESULT {json}</c> line.</item>
/// <item><c>app [--iterations N]</c> — the GC matrix's real-app workload (headless app: open 20 files, type, scroll, close; themes; tool windows).</item>
/// <item><c>budget [--server typescript]</c> — the memory budget table: idle, folder, 20 documents, a language server, a debug session, a terminal,
/// a remote session (cumulative), each settled, with child processes; one RESULT line per stage.</item>
/// <item><c>soak [--minutes N]</c> — the real-app soak (mixed scenario), CSV/JSON under .scratch/soak, summary line.</item>
/// <item><c>trim-effect</c> — what each trim level returns in the real app after 20 documents were opened and closed.</item>
/// <item><c>bench [BenchmarkDotNet args]</c> — Core hot-path benchmarks (Release only).</item>
/// </list>
/// The app commands run Avalonia's headless platform with Skia (no display is ever opened) and the app's own runtime settings
/// (<c>AppStartup.ConfigureRuntime</c>: SustainedLowLatency); <c>CSCODE_HOME</c> is a temp directory.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: cscode-profile synthetic|app|budget|soak|trim-effect|bench [options]"); return 2; }
        var options = Options.Parse(args.Skip(1));
        switch (args[0])
        {
            case "synthetic":
            {
                AppStartup.ConfigureRuntime();
                using var metrics = new RunMetrics();
                var checksum = SyntheticWorkload.Run(options.Int("steps", 3000));
                Measurement(metrics.Finish("synthetic", new Dictionary<string, object?> { ["checksum"] = checksum }));
                return 0;
            }
            case "app": return Headless(() => AppWorkloadAsync(options.Int("iterations", 12)));
            case "budget": return Headless(() => BudgetAsync(options.String("server", "typescript")));
            case "soak": return Headless(() => SoakAsync(options.Double("minutes", 12)));
            case "trim-effect": return Headless(TrimEffectAsync);
            case "hold": return Headless(() => HoldAsync(options.Int("documents", 20), options.Int("seconds", 120)));
            case "census": return Headless(() => CensusAsync(options.String("scenario", "scroll"), options.Int("iterations", 20),
                options.String("pause-at", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet(), options.Int("pause-seconds", 60)));
            case "bench":
                // In-process (the tool's assembly name is not its project's, which BenchmarkDotNet's per-benchmark project generation needs).
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args.Skip(1).ToArray(),
                    BenchmarkDotNet.Configs.DefaultConfig.Instance.AddJob(BenchmarkDotNet.Jobs.Job.ShortRun
                        .WithToolchain(BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain.Instance)));
                return 0;
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    private static void Measurement(Dictionary<string, object?> result) => RunMetrics.Emit(result);

    /// <summary>Runs <paramref name="work"/> on the headless Avalonia UI thread (this thread) until it completes.</summary>
    private static int Headless(Func<Task<int>> work)
    {
        AppStartup.ConfigureRuntime();
        AppBuilder.Configure<CsCode.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().WithCodeFonts()
            .SetupWithoutStarting();
        var exit = 1;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { exit = await work(); }
            catch (Exception ex) { Console.Error.WriteLine("FAILED: " + ex); exit = 1; }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return exit;
    }

    // ------------------------------------------------------------------ app workload (GC matrix)

    private static async Task<int> AppWorkloadAsync(int iterations)
    {
        using var metrics = new RunMetrics();
        await using var host = await MemoryAppHost.StartAsync();
        var files = AppWorkspace.Seed(host.Files);
        var big = AppWorkspace.Big(host.Files, 10_000);
        IScenario[] scenarios = [new TabsScenario(host, files), new TypingScenario(host, files[0]), new ThemeScenario(host), new ToolWindowScenario(host)];
        foreach (var s in scenarios) await s.InitializeAsync(default);
        var scroll = new ScrollScenario(host, big);
        await scroll.InitializeAsync(default);
        for (var i = 0; i < iterations; i++)
        {
            await scroll.RunIterationAsync(i, default);
            foreach (var s in scenarios) await s.RunIterationAsync(i, default);
        }
        await scroll.DisposeAsync();
        await MemoryAppHost.SettleAsync(50);
        Measurement(metrics.Finish("app", new Dictionary<string, object?> { ["iterations"] = iterations }));
        return 0;
    }

    // ------------------------------------------------------------------ budget table

    private const string TsConfig = """{ "compilerOptions": { "target": "ES2020", "module": "commonjs", "strict": true, "outDir": "out" }, "include": ["ts/**/*.ts"] }""";

    private static async Task<int> BudgetAsync(string server)
    {
        var budgetStages = new List<Dictionary<string, object?>>();
        using var serverData = new TempDirectory("cscode-budget-remote-");
        var marker = "cscode-budget-" + Guid.NewGuid().ToString("N")[..12];
        var settings = DebugScenario.Settings();
        // A first host with no folder: the idle row (empty workspace).
        await using (var bare = await MemoryAppHost.StartAsync())
        {
            await StageAsync(bare, "idle, no folder", budgetStages);
        }
        FullGc.Collect();

        var options = new MemoryAppHostOptions
        {
            EnabledServers = [server],
            Settings = settings,
            Args = h => [h.Files.Path],
            Configure = (h, s) => RemoteScenario.Configure(h, s, serverData, marker),
        };
        await using var host = await MemoryAppHost.StartAsync(options, h =>
        {
            DebugScenario.Seed(h.Files);
            h.Files.WriteFile("tsconfig.json", TsConfig);
            h.Files.WriteFile("ts/main.ts", "import { add } from './util';\nconst total: number = add(1, 2);\nconsole.log(total.toFixed(2));\n");
            h.Files.WriteFile("ts/util.ts", "export function add(a: number, b: number): number {\n  return a + b;\n}\n");
        });
        await StageAsync(host, "folder open (Explorer)", budgetStages);

        var files = AppWorkspace.Seed(host.Files);
        foreach (var f in files) await host.Get<INavigationService>().OpenAsync(new Uri(f));
        await StageAsync(host, "20 open documents", budgetStages);

        var manager = host.Get<ILanguageServerManager>();
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine("ts", "main.ts")));
        var lspOk = await MemoryAppHost.WaitFor(() => manager.Servers.Any(s => s.Id == server && s.State == LanguageServerState.Running), 60_000);
        await MemoryAppHost.SettleAsync(300); // the server indexes the project
        await StageAsync(host, $"+ 1 language server ({server}{(lspOk ? "" : ", NOT running: " + string.Join(", ", manager.Servers.Select(s => $"{s.Id}={s.State} {s.LastError}")))})", budgetStages);

        if (DebugScenario.SkipReason() is null)
        {
            var debug = host.Get<IDebugService>();
            await MemoryAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "Node: main", 10_000);
            debug.Breakpoints.Add(new Uri(host.Files.Combine("main.js")), 2);
            _ = host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStart).AsTask();
            var stopped = await MemoryAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 90_000);
            await StageAsync(host, "+ debug session (js-debug + node, stopped at a breakpoint" + (stopped ? ")" : ", NOT stopped)"), budgetStages);
        }

        var terminal = await host.Get<ITerminalService>().CreateAsync();
        host.Shell.Tools.Show("terminal");
        await MemoryAppHost.WaitFor(() => terminal.ProcessId is not null, 10_000);
        await terminal.SendTextAsync("ls -la /usr/lib | head -100");
        await StageAsync(host, "+ terminal (bash, tool window shown)", budgetStages);

        var remote = host.Get<IRemoteConnectionManager>();
        await remote.ConnectAsync("tester@box");
        var connected = await MemoryAppHost.WaitFor(() => remote.Status.IsConnected, 30_000);
        if (connected && remote.ToUri(host.Files.Combine("main.js")) is { } remoteFile) await host.Get<INavigationService>().OpenAsync(remoteFile);
        await StageAsync(host, "+ remote session (loopback cscode-server, one remote file open)" + (connected ? "" : " NOT connected"), budgetStages);

        // leave nothing running: debug, remote, terminals, servers
        await host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStop);
        await remote.DisconnectAsync();
        await host.Get<ITerminalService>().KillAsync(terminal);
        await manager.StopAllAsync();
        await MemoryAppHost.SettleAsync(50);
        return 0;
    }

    /// <summary>Settles 15 s with frames, then measures the process and its child process tree; then once more after an Idle trim.</summary>
    private static async Task StageAsync(MemoryAppHost host, string stage, List<Dictionary<string, object?>> stages)
    {
        for (var i = 0; i < 15; i++) await MemoryAppHost.SettleAsync(100);
        var monitor = host.Get<MemoryMonitor>();
        var d = monitor.CurrentDetails;
        monitor.SampleNow();
        d = monitor.CurrentDetails;
        var breakdown = MemoryBreakdown.Capture();
        var children = ProcessTree.Descendants(Environment.ProcessId);
        await monitor.TrimNowAsync(MemoryTrimLevel.Idle);
        var trimmed = monitor.LastTrim!;
        var result = new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["appRssMb"] = RunMetrics.Mb(d.WorkingSetBytes),
            ["appPssMb"] = RunMetrics.Mb(breakdown.ProportionalBytes),
            ["appAnonMb"] = RunMetrics.Mb(d.AnonymousBytes),
            ["appFileMb"] = RunMetrics.Mb(d.FileBackedBytes),
            ["gcHeapMb"] = RunMetrics.Mb(d.GcHeapBytes),
            ["gcCommittedMb"] = RunMetrics.Mb(d.GcCommittedBytes),
            ["nativeAnonMb"] = RunMetrics.Mb(breakdown.NativeAnonymousBytes),
            ["threads"] = d.ThreadCount,
            ["childrenRssMb"] = RunMetrics.Mb(children.Sum(c => c.ResidentBytes)),
            ["children"] = string.Join(", ", children.GroupBy(c => c.Label).Select(g => $"{g.Key}×{g.Count()} {RunMetrics.Mb(g.Sum(c => c.ResidentBytes))} MB")),
            ["appRssAfterIdleTrimMb"] = RunMetrics.Mb(trimmed.WorkingSetAfter),
            ["gcHeapAfterIdleTrimMb"] = RunMetrics.Mb(trimmed.GcHeapAfter),
            ["regions"] = string.Join("; ", breakdown.Regions.Take(8).Select(r => $"{r.Name} {RunMetrics.Mb(r.ResidentBytes)}")),
        };
        stages.Add(result);
        Measurement(result);
    }

    // ------------------------------------------------------------------ hold (for gcdump / counters / trace from another shell)

    /// <summary>Opens N documents, prints <c>READY pid</c>, then keeps the app alive (pumping, idle) for the given seconds.</summary>
    private static async Task<int> HoldAsync(int documents, int seconds)
    {
        await using var host = await MemoryAppHost.StartAsync();
        if (documents > 0)
            foreach (var f in AppWorkspace.Seed(host.Files, documents)) await host.Get<INavigationService>().OpenAsync(new Uri(f));
        await MemoryAppHost.SettleAsync(200);
        Console.WriteLine($"READY {Environment.ProcessId}");
        for (var waited = 0; waited < seconds * 10; waited++) await MemoryAppHost.SettleAsync(10, frames: false);
        return 0;
    }

    // ------------------------------------------------------------------ census (does a scenario's live heap plateau?)

    /// <summary>Runs one app scenario N times and prints the live heap (full GC) after each iteration: growth that stops is a cache, growth that does not is a leak.</summary>
    private static async Task<int> CensusAsync(string name, int iterations, HashSet<int> pauseAt, int pauseSeconds)
    {
        await using var host = await MemoryAppHost.StartAsync();
        IScenario scenario = name switch
        {
            "scroll" => new ScrollScenario(host, AppWorkspace.Big(host.Files)),
            "tabs" => new TabsScenario(host, AppWorkspace.Seed(host.Files)),
            "typing" => new TypingScenario(host, AppWorkspace.Seed(host.Files, 1)[0]),
            _ => throw new ArgumentException("unknown scenario " + name),
        };
        await scenario.InitializeAsync(default);
        for (var i = 0; i < iterations; i++)
        {
            await scenario.RunIterationAsync(i, default);
            FullGc.Collect();
            Console.WriteLine(FormattableString.Invariant($"CENSUS {name} {i} live {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB rss {RunMetrics.Mb(host.Get<MemoryMonitor>().SampleNow().WorkingSetBytes)} MB"));
            if (!pauseAt.Contains(i)) continue;
            // a moment for `dotnet-dump collect -p PID` from another shell (tools/profile: compare two dumps' dumpheap -stat)
            Console.WriteLine($"PAUSE {i} {Environment.ProcessId}");
            for (var w = 0; w < pauseSeconds * 10; w++) await MemoryAppHost.SettleAsync(10, frames: false);
        }
        await scenario.DisposeAsync();
        return 0;
    }

    // ------------------------------------------------------------------ trim effect

    private static async Task<int> TrimEffectAsync()
    {
        await using var host = await MemoryAppHost.StartAsync();
        var monitor = host.Get<MemoryMonitor>();
        var files = AppWorkspace.Seed(host.Files);
        var big = AppWorkspace.Big(host.Files);
        var tabs = new TabsScenario(host, [.. files, big]);
        for (var i = 0; i < 3; i++) await tabs.RunIterationAsync(i, default);
        foreach (var f in files) await host.Get<INavigationService>().OpenAsync(new Uri(f));
        await MemoryAppHost.SettleAsync(100);
        foreach (var level in new[] { MemoryTrimLevel.Idle, MemoryTrimLevel.Moderate, MemoryTrimLevel.Aggressive })
        {
            await MemoryAppHost.SettleAsync(100);
            var before = monitor.SampleNow();
            var mallocBefore = monitor.CurrentDetails.WorkingSetBytes;
            await monitor.TrimNowAsync(level);
            var r = monitor.LastTrim!;
            Measurement(new Dictionary<string, object?>
            {
                ["level"] = level.ToString(), ["rssBeforeMb"] = RunMetrics.Mb(r.WorkingSetBefore), ["rssAfterMb"] = RunMetrics.Mb(r.WorkingSetAfter),
                ["gcHeapBeforeMb"] = RunMetrics.Mb(r.GcHeapBefore), ["gcHeapAfterMb"] = RunMetrics.Mb(r.GcHeapAfter),
                ["trimmablesReleasedMb"] = RunMetrics.Mb(r.ReleasedByTrimmables), ["trimmablesMs"] = Math.Round(r.TrimmablesElapsed.TotalMilliseconds, 1),
                ["collectMs"] = Math.Round(r.CollectElapsed.TotalMilliseconds, 1), ["trimmables"] = string.Join(", ", r.Trimmables.Select(t => $"{t.Name} {t.ReleasedBytes / 1024} KB")),
                ["sampleRssMb"] = RunMetrics.Mb(before.WorkingSetBytes + 0 * mallocBefore),
            });
            // what malloc_trim alone gives after the collection already ran
            await MemoryAppHost.SettleAsync(50);
            var pre = monitor.SampleNow().WorkingSetBytes;
            OsMemory.ReturnFreePages();
            var post = monitor.SampleNow().WorkingSetBytes;
            Measurement(new Dictionary<string, object?> { ["level"] = level + " then malloc_trim alone", ["rssBeforeMb"] = RunMetrics.Mb(pre), ["rssAfterMb"] = RunMetrics.Mb(post) });
        }
        return 0;
    }

    // ------------------------------------------------------------------ soak

    private static async Task<int> SoakAsync(double minutes)
    {
        await using var host = await MemoryAppHost.StartAsync();
        var monitor = host.Get<MemoryMonitor>();
        var files = AppWorkspace.Seed(host.Files);
        var big = AppWorkspace.Big(host.Files, 10_000);
        var scenario = new MixedScenario(host, files, big);
        var report = await SoakRunner.RunAsync(scenario, new SoakOptions
        {
            Duration = TimeSpan.FromMinutes(minutes), WarmupIterations = 10, SampleInterval = TimeSpan.FromSeconds(10),
            ChildBytes = () => monitor.Current.ChildProcessWorkingSetBytes,
        });
        Console.WriteLine("SOAK " + report.Summary());
        Console.WriteLine("CSV " + report.CsvPath);
        Measurement(new Dictionary<string, object?>
        {
            ["workload"] = "soak", ["minutes"] = minutes, ["iterations"] = report.Iterations,
            ["heapBytesPerIteration"] = Math.Round(report.HeapPerIteration.Slope, 1), ["heapBytesPerIterationCi95"] = Math.Round(report.HeapPerIteration.SlopeConfidence95, 1),
            ["heapMbPerHour"] = Math.Round(report.HeapMbPerHour, 2), ["heapMbPerHourCi95"] = Math.Round(report.HeapMbPerHourConfidence, 2),
            ["rssMbPerHour"] = Math.Round(report.WorkingSetMbPerHour, 2), ["rssMbPerHourCi95"] = Math.Round(report.WorkingSetMbPerHourConfidence, 2),
            ["rssPeaksByThirdMb"] = new[] { report.WorkingSetPeaksMb.First, report.WorkingSetPeaksMb.Middle, report.WorkingSetPeaksMb.Last }.Select(v => Math.Round(v, 1)).ToArray(),
            ["gen2PerIteration"] = new[] { report.Gen2Rates.First, report.Gen2Rates.Second }, ["before"] = report.Before.ToString(), ["after"] = report.After.ToString(),
            ["csv"] = report.CsvPath,
        });
        return 0;
    }

    /// <summary>
    /// The soak's iteration: open five of the 20 files, type 50 characters into one, scroll the big file a little, close all; every 5th iteration
    /// switch themes and show/hide tool windows and create/kill a terminal.
    /// </summary>
    private sealed class MixedScenario(MemoryAppHost host, IReadOnlyList<string> files, string big) : AppScenario(host, "app-mixed")
    {
        private readonly ThemeScenario _themes = new(host);
        private readonly ToolWindowScenario _tools = new(host);
        private readonly TerminalScenario _terminal = new(host);

        public override async Task RunIterationAsync(int iteration, CancellationToken ct)
        {
            for (var i = 0; i < 5; i++) await OpenAsync(files[(iteration * 5 + i) % files.Count]);
            var area = await TypingScenario.TextAreaAsync(Host);
            for (var i = 0; i < 50; i++)
            {
                var ch = (char)('a' + i % 26);
                Dispatcher.UIThread.Post(() => area.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = ch.ToString() }), DispatcherPriority.Input);
                if (i % 10 == 9) await MemoryAppHost.SettleAsync(1);
            }
            await OpenAsync(big);
            await MemoryAppHost.SettleAsync(5);
            await CloseAllAsync();
            if (iteration % 5 == 0)
            {
                await _themes.RunIterationAsync(iteration, ct);
                await _tools.RunIterationAsync(iteration, ct);
                await _terminal.RunIterationAsync(iteration, ct);
            }
        }
    }

    private sealed class Options(Dictionary<string, string> values)
    {
        public static Options Parse(IEnumerable<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string? key = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) { key = a[2..]; values[key] = "true"; }
                else if (key is not null) { values[key] = a; key = null; }
            }
            return new Options(values);
        }

        public int Int(string name, int fallback) => values.TryGetValue(name, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        public double Double(string name, double fallback) => values.TryGetValue(name, out var v) && double.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        public string String(string name, string fallback) => values.GetValueOrDefault(name, fallback);
    }
}
