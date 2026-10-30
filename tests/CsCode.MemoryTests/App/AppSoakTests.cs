using Avalonia.Headless.XUnit;
using CsCode.Core.Diagnostics;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.MemoryTests.App;

/// <summary>
/// MUST 5 against the REAL merged app (headless): every app-level scenario (tabs, scroll, typing, themes, tool windows, a language server, a
/// terminal, a debug session with js-debug, a remote session through the loopback transport) runs as a soak. The default run is the FAST variant
/// (a few iterations: it proves the scenario works and catches megabyte-per-iteration leaks); <c>CSCODE_SOAK=1</c> runs each for
/// <c>CSCODE_SOAK_MINUTES</c>. Asserted: live-heap growth per iteration (95 % interval above the limit fails), threads and descriptors back near
/// their baseline, no child process left behind; the summary line of every run is in the test output and the series under .scratch/soak.
/// </summary>
[Trait("Category", SoakSettings.Category)]
public sealed class AppSoakTests(ITestOutputHelper output)
{
    /// <summary>
    /// Per-iteration live-heap growth allowed in the fast runs: an editor view, a terminal or a debug session that leaked would retain hundreds of
    /// kilobytes to megabytes per iteration; the few iterations of a fast run cannot resolve smaller slopes from noise, the long run can.
    /// </summary>
    private static readonly SoakThresholds FastUi = new() { MaxHeapBytesPerIteration = 64 * 1024, ThreadTolerance = 6, FdTolerance = 8 };
    private static readonly SoakThresholds LongUi = new() { MaxHeapBytesPerIteration = 4 * 1024, MaxHeapMbPerHour = 4, ThreadTolerance = 6, FdTolerance = 8 };

    private async Task<SoakReport> SoakAsync(Func<MemoryAppHost, IScenario> create, MemoryAppHostOptions? options = null, int warmup = 2, int iterations = 8,
        Action<MemoryAppHost>? seed = null)
    {
        await using var host = await MemoryAppHost.StartAsync(options, seed);
        var children = ResourceCensus.ChildPids().Count;
        var monitor = host.Get<MemoryMonitor>();
        var soak = SoakSettings.Long
            ? new SoakOptions { Duration = SoakSettings.Duration, WarmupIterations = Math.Max(warmup, 5), SampleInterval = TimeSpan.FromSeconds(5), ChildBytes = () => monitor.Current.ChildProcessWorkingSetBytes }
            : new SoakOptions { Iterations = iterations, WarmupIterations = warmup, SampleInterval = TimeSpan.Zero, ChildBytes = () => monitor.Current.ChildProcessWorkingSetBytes };
        var report = await SoakRunner.RunAsync(create(host), soak);
        output.WriteLine(report.Summary());
        // Child processes a scenario started (servers, shells, adapters) are gone once it ended.
        Assert.True(await MemoryAppHost.WaitFor(() => ResourceCensus.ChildPids().Count <= children, 15_000),
            "child processes left: " + string.Join(", ", ProcessTree.Descendants(Environment.ProcessId).Select(p => $"{p.Pid} {p.Name}")));
        report.AssertStable(SoakSettings.Long ? LongUi : FastUi);
        return report;
    }

    [AvaloniaFact(Timeout = 600_000)]
    public Task Opening_and_closing_twenty_documents() =>
        SoakAsync(h => new TabsScenario(h, AppWorkspace.Seed(h.Files)), warmup: 2, iterations: 6);

    [AvaloniaFact(Timeout = 600_000)]
    public Task Scrolling_a_twenty_thousand_line_file() =>
        SoakAsync(h => new ScrollScenario(h, AppWorkspace.Big(h.Files)), warmup: 4, iterations: 6);

    [AvaloniaFact(Timeout = 600_000)]
    public Task Typing_into_a_document_and_closing_it_without_saving() =>
        SoakAsync(h => new TypingScenario(h, AppWorkspace.Seed(h.Files, 1)[0]), warmup: 2, iterations: 8);

    [AvaloniaFact(Timeout = 600_000)]
    public Task Switching_themes() => SoakAsync(h => new ThemeScenario(h), warmup: 2, iterations: 8);

    [AvaloniaFact(Timeout = 600_000)]
    public Task Showing_and_hiding_tool_windows() => SoakAsync(h => new ToolWindowScenario(h), warmup: 2, iterations: 10);

    [AvaloniaFact(Timeout = 600_000)]
    public async Task Starting_and_stopping_a_language_server()
    {
        Assert.SkipWhen(MemoryAppHost.FindOnPath("python3") is null, "python3 is not on PATH (the minimal language server is a python script)");
        string? file = null;
        await SoakAsync(h => new LanguageServerScenario(h, LanguageServerScenario.FakeId, file!), warmup: 1, iterations: 5,
            options: new MemoryAppHostOptions { Settings = LanguageServerScenario.FakeServerSettings(MemoryAppHost.FindOnPath("python3")!, FakeScriptPath()) },
            seed: h => file = h.Files.WriteFile("a" + LanguageServerScenario.FakeExtension, "hello\n"));
    }

    private static string FakeScriptPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cscode-memfake-{Environment.ProcessId}.py");
        File.WriteAllText(path, LanguageServerScenario.FakeServerScript);
        return path;
    }

    [AvaloniaFact(Timeout = 600_000)]
    public Task Creating_and_killing_terminals() => SoakAsync(h => new TerminalScenario(h), warmup: 2, iterations: 8);

    [AvaloniaFact(Timeout = 600_000)]
    public async Task Debug_sessions_with_js_debug()
    {
        Assert.SkipWhen(DebugScenario.SkipReason() is { } reason, DebugScenario.SkipReason() ?? "");
        await SoakAsync(h => new DebugScenario(h), warmup: 1, iterations: 3,
            options: new MemoryAppHostOptions { Settings = DebugScenario.Settings(), Args = h => [h.Files.Path] },
            seed: h => DebugScenario.Seed(h.Files));
    }

    [AvaloniaFact(Timeout = 600_000)]
    public async Task Remote_sessions_through_the_loopback_transport()
    {
        Assert.SkipUnless(File.Exists(RemoteScenario.ServerAssembly), "cscode-server.dll is not next to the tests");
        using var serverData = new TempDirectory("cscode-mem-remote-");
        var marker = "cscode-memtest-" + Guid.NewGuid().ToString("N")[..12];
        await SoakAsync(h => new RemoteScenario(h), warmup: 1, iterations: 4,
            options: new MemoryAppHostOptions { Configure = (h, s) => RemoteScenario.Configure(h, s, serverData, marker) });
    }

    /// <summary>The monitor runs in the real composition: started by the shell, sampling, wrapped by the shell's child-process tracker.</summary>
    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_real_app_starts_the_memory_monitor_and_wraps_it_for_the_exit_path()
    {
        await using var host = await MemoryAppHost.StartAsync();
        var monitor = host.Get<MemoryMonitor>();
        Assert.True(monitor.IsStarted, "the shell did not start the memory monitor");
        var shellMonitor = host.Get<IMemoryMonitor>();
        Assert.IsNotType<MemoryMonitor>(shellMonitor); // the shell's wrapper (ChildProcessTracker) around it
        var seen = 0;
        using var sub = shellMonitor.Sampled.Subscribe(_ => Interlocked.Increment(ref seen));
        Assert.True(await MemoryAppHost.WaitFor(() => Volatile.Read(ref seen) > 0, 10_000), "no sample through the shell's monitor");
        Assert.Contains(shellMonitor.Trimmables, t => t.Name.Contains("icon", StringComparison.OrdinalIgnoreCase));
        await shellMonitor.TrimNowAsync(MemoryTrimLevel.Moderate);
        Assert.NotNull(monitor.LastTrim);
        Assert.Contains(monitor.LastTrim!.Trimmables, t => t.Name.Contains("icon", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Input reaches the monitor's activity signal (the idle trim waits for the user to stop).</summary>
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Typing_in_the_real_app_resets_the_idle_clock()
    {
        await using var host = await MemoryAppHost.StartAsync();
        var activity = host.Get<IUserActivityMonitor>();
        await Task.Delay(300);
        Assert.True(activity.IdleTime >= TimeSpan.FromMilliseconds(250));
        await host.Get<CsCode.Core.Platform.INavigationService>().OpenAsync(new Uri(AppWorkspace.Seed(host.Files, 1)[0]));
        var area = await TypingScenario.TextAreaAsync(host);
        await Task.Delay(300);
        area.RaiseEvent(new Avalonia.Input.TextInputEventArgs { RoutedEvent = Avalonia.Input.InputElement.TextInputEvent, Text = "x" });
        Assert.True(activity.IdleTime < TimeSpan.FromMilliseconds(200), $"idle {activity.IdleTime.TotalMilliseconds:F0} ms after a key");
    }
}
