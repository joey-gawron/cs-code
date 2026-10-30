using System.Diagnostics;
using CsCode.App.Services;
using CsCode.Core.Contributions;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.MemoryTests.Soak;

/// <summary>An <see cref="IUiDispatcher"/> that runs posts inline (the output service's ring is what is measured, not a UI thread).</summary>
internal sealed class InlineUiDispatcher : UiDispatcherBase
{
    public override bool CheckAccess() => true;
    protected override void PostCore(Action action, UiPriority priority) => action();
}

/// <summary>
/// MUST 5, Core level: churn scenarios over Core's primitives, each run as a soak (warm-up, then the live heap after every sample, threads and
/// descriptors before/after) with explicit thresholds. The default run is the FAST variant (a few hundred iterations, seconds); with
/// <c>CSCODE_SOAK=1</c> the long variant runs each scenario for <c>CSCODE_SOAK_MINUTES</c>. Thresholds: live heap growth per iteration whose
/// 95 % interval lies above the limit fails (noise passes), threads/descriptors back within 4, gen2 not growing super-linearly.
/// </summary>
[Trait("Category", SoakSettings.Category)]
public sealed class CoreSoakTests(ITestOutputHelper output)
{
    private static readonly SoakThresholds Flat = new() { MaxHeapBytesPerIteration = 64, ThreadTolerance = 4, FdTolerance = 4 };

    public static IEnumerable<TheoryDataRow<string>> Scenarios() =>
        ["documents", "registry", "diagnostics", "emitter", "contributions", "monitor-trimmables", "child-tracking", "output-ring"];

    private static IScenario Create(string name) => name switch
    {
        "documents" => DocumentChurn(),
        "registry" => RegistryChurn(),
        "diagnostics" => DiagnosticsChurn(),
        "emitter" => EmitterChurn(),
        "contributions" => ContributionChurn(),
        "monitor-trimmables" => MonitorChurn(),
        "child-tracking" => ChildTrackingChurn(),
        "output-ring" => OutputRing(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Fast_soak_stays_flat(string scenario)
    {
        var iterations = scenario is "monitor-trimmables" or "child-tracking" ? 150 : scenario is "output-ring" ? 60 : 400;
        var report = await SoakRunner.RunAsync(Create(scenario), new SoakOptions
        {
            Iterations = iterations, WarmupIterations = 20, SampleInterval = TimeSpan.FromSeconds(1), SampleEveryIterations = iterations / 20,
        });
        output.WriteLine(report.Summary());
        report.AssertStable(Flat);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Long_soak_stays_flat(string scenario)
    {
        Assert.SkipUnless(SoakSettings.Long, "long soak: set CSCODE_SOAK=1 (and CSCODE_SOAK_MINUTES)");
        var report = await SoakRunner.RunAsync(Create(scenario), new SoakOptions
        {
            Duration = SoakSettings.Duration, WarmupIterations = 200, SampleInterval = TimeSpan.FromSeconds(5),
        });
        output.WriteLine(report.Summary());
        report.AssertStable(Flat with { MaxHeapMbPerHour = 1 });
    }

    // ------------------------------------------------------------------ scenarios

    /// <summary>Open a document through the DocumentManager, edit it ten times, close it; a listener sees every change.</summary>
    private static IScenario DocumentChurn()
    {
        var manager = new DocumentManager();
        var audit = new SubscriptionAudit().Scan(manager);
        IReadOnlyDictionary<string, int>? baseline = null;
        var changes = 0;
        var sub = manager.Changed.Subscribe(_ => changes++);
        return Scenario.Create("core-documents", i =>
        {
            var doc = new InMemoryTextDocument(new Uri($"file:///soak/doc{i % 50}.cs"), "csharp", "class C {\n  void M() { }\n}\n");
            var reg = manager.Register(doc);
            for (var e = 0; e < 10; e++) doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 2), "int x" + e + ";\n")]);
            reg.Dispose();
            baseline ??= audit.Snapshot();
        }, teardown: () =>
        {
            audit.AssertNoGrowth(baseline!);
            Assert.Empty(manager.Documents);
            sub.Dispose();
        });
    }

    private sealed class Hover(int n) : IHoverProvider
    {
        public ValueTask<CsCode.Core.Languages.Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
            ValueTask.FromResult<CsCode.Core.Languages.Hover?>(new CsCode.Core.Languages.Hover([MarkupContent.Plain("hover " + n)]));
    }

    /// <summary>Register providers for several languages, query them for a document, unregister (a language server restarting).</summary>
    private static IScenario RegistryChurn()
    {
        var registry = new LanguageFeatureRegistry();
        var doc = new InMemoryTextDocument(new Uri("file:///soak/a.cs"), "csharp");
        return Scenario.Create("core-registry", i =>
        {
            using var store = new DisposableStore();
            for (var p = 0; p < 10; p++) store.Add(registry.Register<IHoverProvider>(DocumentSelector.ForLanguages("csharp", "fsharp"), new Hover(p), "server" + i));
            if (registry.GetProviders<IHoverProvider>(doc).Count != 10) throw new InvalidOperationException("providers missing");
        }, teardown: () => Assert.Empty(registry.GetAll<IHoverProvider>()));
    }

    /// <summary>Publish diagnostics for 20 files (50 each), then clear the owner (a server restart).</summary>
    private static IScenario DiagnosticsChurn()
    {
        var service = new DiagnosticService();
        var uris = Enumerable.Range(0, 20).Select(i => new Uri($"file:///soak/f{i}.cs")).ToArray();
        var payload = CsCode.Profiling.Testing.Load.GiantPayload.Diagnostics(50);
        var fired = 0;
        var sub = service.Changed.Subscribe(_ => fired++);
        return Scenario.Create("core-diagnostics", i =>
        {
            foreach (var uri in uris) service.Set(uri, "owner", payload);
            _ = service.Counts;
            service.Clear("owner");
        }, teardown: () =>
        {
            Assert.Empty(service.GetAll());
            sub.Dispose();
        });
    }

    /// <summary>Subscribe 100 handlers, fire, dispose them all.</summary>
    private static IScenario EmitterChurn()
    {
        var emitter = new Emitter<int>();
        var total = 0L;
        return Scenario.Create("core-emitter", i =>
        {
            using var store = new DisposableStore();
            for (var s = 0; s < 100; s++) store.Add(emitter.Subscribe(v => total += v));
            emitter.Fire(1);
        }, teardown: () => Assert.Equal(0, emitter.SubscriberCount));
    }

    /// <summary>A user-data folder's contributions reloaded: contribute languages and keybindings, read them, remove the source.</summary>
    private static IScenario ContributionChurn()
    {
        var registry = new ContributionRegistry();
        return Scenario.Create("core-contributions", i =>
        {
            var languages = Enumerable.Range(0, 20).Select(n => new LanguageContribution("lang" + n, ["Lang " + n], [".l" + n], [], [])).ToArray();
            var keys = Enumerable.Range(0, 50).Select(n => new KeybindingContribution("cmd" + n, "ctrl+" + (char)('a' + n % 26))).ToArray();
            using var l = registry.Contribute("user", "/soak", languages);
            using var k = registry.Contribute("user", "/soak", keys);
            if (registry.Of<LanguageContribution>().Items.Count != 20) throw new InvalidOperationException("contributions missing");
        }, teardown: () => Assert.Empty(registry.Of<LanguageContribution>().Items));
    }

    /// <summary>Register a trimmable, sample, run an Idle trim (trimmables + compacting GC + malloc_trim), unregister.</summary>
    private static IScenario MonitorChurn()
    {
        var monitor = new MemoryMonitor();
        return Scenario.CreateAsync("core-monitor-trimmables", async (i, ct) =>
        {
            var cache = new List<byte[]> { new byte[64 * 1024] };
            using (monitor.Register(new DelegateTrimmable("cache", () => { var n = cache.Count * 64 * 1024; cache.Clear(); return n; })))
            {
                monitor.SampleNow();
                await monitor.TrimNowAsync(MemoryTrimLevel.Idle, ct);
            }
            monitor.SampleNow();
        }, teardown: () =>
        {
            Assert.Empty(monitor.Trimmables);
            monitor.Dispose();
            return Task.CompletedTask;
        });
    }

    private sealed class DelegateTrimmable(string name, Func<long> trim) : IMemoryTrimmable
    {
        public string Name => name;
        public long EstimatedBytes => 0;
        public long Trim(MemoryTrimLevel level) => trim();
    }

    /// <summary>A language server's process reported, sampled with the monitor and released — with a real child process per iteration.</summary>
    private static IScenario ChildTrackingChurn()
    {
        var monitor = new MemoryMonitor();
        return Scenario.CreateAsync("core-child-tracking", async (i, ct) =>
        {
            using var child = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!;
            using (monitor.TrackChildProcess("child", child.Id, restartAboveBytes: long.MaxValue, onOverBudget: () => ValueTask.CompletedTask))
            {
                if (monitor.SampleNow().ChildProcessWorkingSetBytes <= 0) throw new InvalidOperationException("child not accounted");
            }
            child.Kill();
            await child.WaitForExitAsync(ct);
        }, teardown: () =>
        {
            Assert.Empty(monitor.Children);
            monitor.Dispose();
            return Task.CompletedTask;
        });
    }

    /// <summary>The app's output channel ring: 1M lines (100 chars) per iteration into one channel stay within its 2 MB cap.</summary>
    private static IScenario OutputRing()
    {
        var output = new OutputService(new InlineUiDispatcher());
        var channel = output.GetOrCreate("Soak");
        var line = new string('l', 100);
        return Scenario.Create("core-output-ring", i =>
        {
            for (var n = 0; n < 1_000_000 / 60 + 1; n++) channel.AppendLine(line); // the fast variant's 60 iterations write 1M lines in all
            if (channel.GetText().Length > OutputService.MaxChars + 200) throw new InvalidOperationException("the ring is not bounded");
        }, teardown: () => channel.Dispose());
    }
}
