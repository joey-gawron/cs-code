using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Keybindings;
using CsCode.Platform.State;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The process lifecycle over the REAL composition (every real module, <c>CSCODE_HOME</c> in a temp directory): start-up threading, the orderly exit
/// (language servers stopped, state on disk, the container disposed on the UI thread) and the hard kill of child processes that outlive the deadline.
/// </summary>
public class LifecycleTests
{
    // ------------------------------------------------------------------ exit

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_the_main_window_stops_language_servers_and_puts_state_written_just_before_exit_on_disk()
    {
        if (FakeLanguageServer.Python is not { } python) { Assert.Skip("python3 is not on PATH: the fake language server cannot run"); return; }
        using var env = new RealAppHome();
        env.WriteUserSettings(FakeLanguageServer.Settings(env, python, "polite", shutdownTimeoutMs: 3000));
        var file = env.Workspace.WriteFile("notes" + FakeLanguageServer.Extension, "hello\nworld\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        await run.Services.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(file));
        var pid = await FakeLanguageServer.WaitForPidAsync(run.Services);
        using var cleanup = ProcessProbe.KillOnDispose(pid);

        // Written right before the exit: neither the breakpoint debounce (400 ms) nor the state flush debounce (500 ms) has fired yet.
        run.Services.GetRequiredService<IBreakpointManager>().Add(new Uri(file), 1);
        run.Services.GetRequiredService<IStateStorage>().Set("lifecycle.test", "written-at-exit");
        main.Close(); // the real close path: layout saved, tabs closed (nothing dirty), then the orderly shutdown

        var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, run.Lifetime.ShutdownExitCode);
        Assert.NotNull(report);
        Assert.True(report.TimedOut.Count == 0 && report.Failures.Count == 0, $"timed out: {string.Join(", ", report.TimedOut)}; failures: {string.Join(" | ", report.Failures)}");
        Assert.Empty(report.Killed); // the server answered shutdown/exit: nothing needed killing
        Assert.True(await ProcessProbe.WaitUntilGoneAsync(pid), $"the language server (pid {pid}) is still running after the exit");
        Assert.Null(run.Session.Services); // the container was disposed

        // On disk: a fresh container over the same CSCODE_HOME reads both back.
        await using var after = AppServices.Build([], env.Configure);
        var state = after.GetRequiredService<IStateStorage>();
        Assert.Equal("written-at-exit", state.Get<string>("lifecycle.test"));
        await after.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var breakpoints = state.Get<JsonElement>("debug.breakpoints", StorageScope.Workspace);
        Assert.Contains("notes" + FakeLanguageServer.Extension, breakpoints.GetRawText());
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_language_server_that_ignores_shutdown_is_killed_once_the_exit_deadline_has_passed()
    {
        if (FakeLanguageServer.Python is not { } python) { Assert.Skip("python3 is not on PATH: the fake language server cannot run"); return; }
        using var env = new RealAppHome();
        // The client itself would wait 30 s for the shutdown answer before killing: only the exit deadline can end this sooner.
        env.WriteUserSettings(FakeLanguageServer.Settings(env, python, "stubborn", shutdownTimeoutMs: 30_000));
        var file = env.Workspace.WriteFile("hung" + FakeLanguageServer.Extension, "x\n");
        await using var run = new RealAppRun(env, shutdownDeadline: TimeSpan.FromSeconds(1));
        var main = await run.MainWindowAsync();
        await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(file));
        var pid = await FakeLanguageServer.WaitForPidAsync(run.Services);
        using var cleanup = ProcessProbe.KillOnDispose(pid);

        var clock = Stopwatch.StartNew();
        main.Close();
        var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Stop();

        Assert.NotNull(report);
        Assert.Contains("language servers", report.TimedOut);
        Assert.Contains(report.Killed, k => k.Contains($"pid {pid}", StringComparison.Ordinal));
        Assert.True(await ProcessProbe.WaitUntilGoneAsync(pid, 2000), $"the stubborn language server (pid {pid}) survived the exit");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the exit took {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(0, run.Lifetime.ShutdownExitCode);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Every_service_of_the_real_app_is_disposed_on_the_UI_thread_without_blocking_or_throwing()
    {
        using var env = new RealAppHome();
        IServiceCollection? registered = null;
        var sp = AppServices.Build([], s => { env.Configure(s); registered = s; });
        var types = registered!.Where(d => !d.ServiceType.IsGenericTypeDefinition).Select(d => d.ServiceType).Distinct().ToArray();
        var failures = new List<string>();
        // Resolve everything (as CompositionTests does, off the UI thread), then shut down the way the app does: on the UI thread.
        await Task.Run(() =>
        {
            foreach (var type in types)
            {
                try { _ = sp.GetServices(type).ToArray(); }
                catch (Exception ex) { failures.Add($"{type.FullName}: {ex.Message}"); }
            }
        });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        var clock = Stopwatch.StartNew();
        var report = await AppShutdown.RunAsync(sp, AppShutdown.DefaultDeadline);
        Assert.True(Dispatcher.UIThread.CheckAccess());
        Assert.True(report.Failures.Count == 0 && report.TimedOut.Count == 0, $"timed out: {string.Join(", ", report.TimedOut)}; failures: {string.Join(" | ", report.Failures)}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"shutting an idle app down took {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Throws<ObjectDisposedException>(() => sp.GetService<IStateStorage>());
    }

    // ------------------------------------------------------------------ child processes

    [Fact]
    public async Task Only_tracked_children_that_are_still_running_are_killed()
    {
        if (FakeLanguageServer.FindOnPath("sleep") is not { } sleep) { Assert.Skip("no sleep binary on PATH"); return; }
        using var tracked = Process.Start(new ProcessStartInfo(sleep, "300") { UseShellExecute = false })!;
        using var released = Process.Start(new ProcessStartInfo(sleep, "300") { UseShellExecute = false })!;
        using var untracked = Process.Start(new ProcessStartInfo(sleep, "300") { UseShellExecute = false })!;
        try
        {
            var tracker = new ChildProcessTracker();
            using var token = tracker.Track("server", tracked.Id);
            tracker.Track("stopped by its owner", released.Id).Dispose();

            var killed = await tracker.KillSurvivorsAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([$"server (pid {tracked.Id})"], killed);
            Assert.True(tracked.WaitForExit(5000));
            Assert.False(released.HasExited); // untracked by its owner: never touched
            Assert.False(untracked.HasExited); // never reported (e.g. a browser started by xdg-open): never touched
            Assert.Empty(await tracker.KillSurvivorsAsync(TimeSpan.FromSeconds(1))); // already gone: nothing to do, no exception
        }
        finally
        {
            foreach (var p in new[] { tracked, released, untracked }) if (!p.HasExited) p.Kill(); // only the pids this test started
        }
    }

    [Fact]
    public void The_app_memory_monitor_records_child_processes_and_stands_in_while_profiling_registers_none()
    {
        using var sp = AppServices.Build([]);
        var monitor = sp.GetRequiredService<IMemoryMonitor>();
        var tracker = sp.GetRequiredService<ChildProcessTracker>();
        var token = monitor.TrackChildProcess("lsp:x", Environment.ProcessId);
        Assert.Equal([("lsp:x", Environment.ProcessId)], tracker.Tracked);
        token.Dispose();
        Assert.Empty(tracker.Tracked);
        using var reg = monitor.Register(new NamedTrimmable("cache"));
        Assert.Contains(monitor.Trimmables, t => t.Name == "cache");
    }

    [Fact]
    public void A_registered_memory_monitor_is_wrapped_not_replaced()
    {
        var inner = new RecordingMemoryMonitor();
        using var sp = AppServices.Build([], s => s.AddSingleton<IMemoryMonitor>(inner));
        var monitor = sp.GetRequiredService<IMemoryMonitor>();
        using (monitor.TrackChildProcess("debug adapter", Environment.ProcessId, 1024))
        {
            Assert.Equal([("debug adapter", Environment.ProcessId, (long?)1024)], inner.Children);
            Assert.Equal([("debug adapter", Environment.ProcessId)], sp.GetRequiredService<ChildProcessTracker>().Tracked);
        }
        Assert.Empty(inner.Children);
        Assert.Empty(sp.GetRequiredService<ChildProcessTracker>().Tracked);
        using var reg = monitor.Register(new NamedTrimmable("cache"));
        Assert.Contains(inner.Trimmables, t => t.Name == "cache");
    }

    // ------------------------------------------------------------------ start-up

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Settings_state_and_keybindings_read_their_files_off_the_UI_thread_at_startup()
    {
        using var env = new RealAppHome();
        env.WriteUserSettings("{ \"editor.tabSize\": 3 }");
        var constructedOnUi = new ConcurrentDictionary<string, bool>();
        await using var run = new RealAppRun(env, s =>
        {
            RecordConstructionThread<ConfigurationService>(s, constructedOnUi);
            RecordConstructionThread<StateStorage>(s, constructedOnUi);
            RecordConstructionThread<KeybindingService>(s, constructedOnUi);
        });
        var main = await run.MainWindowAsync();

        Assert.Equal(["ConfigurationService", "KeybindingService", "StateStorage"], constructedOnUi.Keys.Order(StringComparer.Ordinal));
        foreach (var (service, onUi) in constructedOnUi) Assert.False(onUi, service + " read its files on the UI thread");
        Assert.Equal(3, run.Services.GetRequiredService<IConfigurationService>().Get("editor.tabSize", 0)); // and it did read them

        main.Close();
        await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_main_window_that_fails_while_opening_is_replaced_by_the_crash_window_and_the_run_exits_with_code_1()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env);
        Window? failed = null;
        var failedClosed = false;
        // A failure inside Window.Show (as a windowing-system or GL error would raise): the real main window throws while it opens.
        using var refuse = FailWhileOpening(run, w =>
        {
            if (w is not MainWindow) return false;
            failed = w;
            w.Closed += (_, _) => failedClosed = true;
            return true;
        });

        Assert.True(await DevHost.WaitFor(() => run.Session.Window is CrashWindow, 60_000), $"no crash window; the session shows {run.Session.Window?.GetType().Name ?? "nothing"}");
        var crash = (CrashWindow)run.Session.Window!;
        Assert.Contains(crash.GetLogicalDescendantTexts(), t => t.Contains(RefusedMessage, StringComparison.Ordinal));
        Assert.Same(crash, run.Lifetime.MainWindow);
        Assert.True(crash.IsVisible);
        Assert.NotNull(failed);
        Assert.True(failedClosed, "the main window that failed to open is still open next to the crash window");
        Assert.Contains(FatalCrashLogs(env), log => log.Contains(RefusedMessage, StringComparison.Ordinal));
        Assert.False(run.Session.Ended.IsCompleted); // the run lasts until the user has read the crash window

        crash.Close();
        var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, run.Lifetime.ShutdownExitCode);
        Assert.NotNull(report); // the services that were already running were shut down in order
        Assert.Null(run.Session.Services);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task When_no_window_can_be_shown_the_run_reports_on_stderr_and_exits_with_code_1_instead_of_running_without_a_window()
    {
        using var env = new RealAppHome();
        var stderr = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(stderr);
        try
        {
            await using var run = new RealAppRun(env);
            using var refuse = FailWhileOpening(run, _ => true); // neither the main window nor the crash window can be shown

            var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(60)); // with the exit taken over, nothing else would end the process
            Assert.Equal(1, run.Lifetime.ShutdownExitCode);
            Assert.NotNull(report);
            Assert.Null(run.Session.Services);
            Assert.Contains(FatalCrashLogs(env), log => log.Contains(RefusedMessage, StringComparison.Ordinal));
            Assert.Contains("CsCode could not start", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(RefusedMessage, stderr.ToString(), StringComparison.Ordinal);
        }
        finally { Console.SetError(previousError); }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_startup_failure_shows_the_crash_window_and_the_run_exits_with_code_1_when_it_closes()
    {
        using var env = new RealAppHome();
        // A module the shell needs fails while the services are built off the UI thread.
        await using var run = new RealAppRun(env, s => s.AddSingleton<IWorkspaceService>(_ => throw new InvalidOperationException("workspace module is broken")));

        Assert.True(await DevHost.WaitFor(() => run.Session.Window is not null, 60_000));
        var crash = Assert.IsType<CrashWindow>(run.Session.Window);
        Assert.Contains(crash.GetLogicalDescendantTexts(), t => t.Contains("workspace module is broken", StringComparison.Ordinal));
        Assert.Contains(FatalCrashLogs(env), log => log.Contains("workspace module is broken", StringComparison.Ordinal));

        crash.Close();
        await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, run.Lifetime.ShutdownExitCode);
    }

    private const string RefusedMessage = "the windowing system refused the window";

    /// <summary>Makes the windows of <paramref name="run"/> that <paramref name="refuse"/> picks throw from inside <see cref="Window.Show()"/> (while
    /// they raise Opened). Scoped to the run: only the window the session is presenting is affected.</summary>
    private static IDisposable FailWhileOpening(RealAppRun run, Func<Window, bool> refuse) =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
        {
            if (ReferenceEquals(w, run.Lifetime.MainWindow) && refuse(w)) throw new InvalidOperationException(RefusedMessage);
        });

    private static string[] FatalCrashLogs(RealAppHome env) =>
        Directory.GetFiles(env.CrashLogs.Path, "crash-*.log").Select(File.ReadAllText).Where(t => t.Contains("FATAL", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void The_process_runs_with_sustained_low_latency_garbage_collection()
    {
        var before = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            Assert.Equal(0, Program.Main(["--version"]));
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
        }
        finally { GCSettings.LatencyMode = before; }
    }

    private static void RecordConstructionThread<T>(IServiceCollection services, ConcurrentDictionary<string, bool> onUi) where T : class
    {
        var original = services.Last(d => d.ServiceType == typeof(T));
        services.Remove(original);
        services.AddSingleton(sp =>
        {
            onUi[typeof(T).Name] = Dispatcher.UIThread.CheckAccess();
            return (T)(original.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));
        });
    }

    private sealed record NamedTrimmable(string Name) : IMemoryTrimmable
    {
        public long EstimatedBytes => 0;
        public long Trim(MemoryTrimLevel level) => 0;
    }

    private sealed class RecordingMemoryMonitor : IMemoryMonitor
    {
        private readonly Emitter<MemorySnapshot> _sampled = new();
        public List<(string Name, int Pid, long? RestartAbove)> Children { get; } = new();
        public List<IMemoryTrimmable> Trimmables { get; } = new();
        public MemorySnapshot Current => new(DateTimeOffset.Now, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public long BudgetBytes => 0;
        public IEventSource<MemorySnapshot> Sampled => _sampled;
        IReadOnlyList<IMemoryTrimmable> IMemoryMonitor.Trimmables => Trimmables;
        public IDisposable Register(IMemoryTrimmable trimmable) { Trimmables.Add(trimmable); return Disposable.Create(() => Trimmables.Remove(trimmable)); }
        public ValueTask TrimNowAsync(MemoryTrimLevel level, CancellationToken ct = default) => ValueTask.CompletedTask;
        public IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null)
        {
            var entry = (name, pid, restartAboveBytes);
            Children.Add(entry);
            return Disposable.Create(() => Children.Remove(entry));
        }
    }
}
