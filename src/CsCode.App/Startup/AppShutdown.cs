using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Startup;

/// <summary>What an orderly shutdown did (logged; asserted by tests).</summary>
public sealed record ShutdownReport(IReadOnlyList<string> TimedOut, IReadOnlyList<string> Killed, IReadOnlyList<string> Failures, TimeSpan Elapsed);

/// <summary>
/// The orderly exit, run on the UI thread after the main window has closed and before the dispatcher stops:
/// <list type="number">
/// <item>flush state, so whatever the user did is on disk before anything below can hang;</item>
/// <item>shut down the services that own sessions and child processes (language servers, debug sessions, breakpoints, launch configurations)
/// gracefully and in parallel, off the UI thread, bounded by the deadline;</item>
/// <item>flush state again: those disposals persist (breakpoints are written on dispose);</item>
/// <item>hard-kill tracked child processes whose owner did not stop them in time;</item>
/// <item>dispose the container synchronously on the UI thread (UI singletons must be disposed on their thread; an async container dispose would
/// continue on a pool thread). The module singletons disposed in step 2 are no-ops by then, so nothing here waits for a server.</item>
/// </list>
/// The awaited part never takes much longer than the deadline, whatever a language server or debug adapter does.
/// </summary>
public static class AppShutdown
{
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Kept for the final state flush even when the graceful phase used up the deadline.</summary>
    private static readonly TimeSpan FinalFlushReserve = TimeSpan.FromMilliseconds(750);

    /// <summary>How long to wait for killed children to be gone (the kill itself is immediate).</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);

    public static async Task<ShutdownReport> RunAsync(ServiceProvider services, TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan Remaining(TimeSpan reserve = default) { var r = deadline - reserve - clock.Elapsed; return r > TimeSpan.Zero ? r : TimeSpan.Zero; }
        var timedOut = new List<string>();
        var failures = new List<string>();
        var log = TryGet<ILoggerFactory>(services, failures)?.CreateLogger("CsCode.Shutdown");
        var state = TryGet<IStateStorage>(services, failures);
        var children = TryGet<ChildProcessTracker>(services, failures);

        // 1. What the user did up to now reaches the disk first.
        if (state is not null) await BoundedAsync("state", () => state.FlushAsync(), Remaining(FinalFlushReserve), timedOut, failures).ConfigureAwait(true);

        // 2. Graceful shutdown of everything that owns a session or a child process, in parallel and off the UI thread.
        var graceful = GracefulParticipants(services, failures)
            .Select(p => BoundedAsync(p.Name, () => p.Service.DisposeAsync().AsTask(), Remaining(FinalFlushReserve), timedOut, failures))
            .ToArray();
        await Task.WhenAll(graceful).ConfigureAwait(true);

        // 3. Final flush (what the disposals above persisted). StateStorage.DisposeAsync flushes and makes its synchronous Dispose a no-op.
        if (state is not null)
        {
            var budget = Remaining() > FinalFlushReserve ? Remaining() : FinalFlushReserve;
            if (state is IAsyncDisposable disposable) await BoundedAsync("state", () => disposable.DisposeAsync().AsTask(), budget, timedOut, failures).ConfigureAwait(true);
            else await BoundedAsync("state", () => state.FlushAsync(), budget, timedOut, failures).ConfigureAwait(true);
        }

        // 4. Children that are still running were not stopped by their owner within the deadline.
        IReadOnlyList<string> killed = [];
        if (children is not null)
        {
            try { killed = await children.KillSurvivorsAsync(KillWait).ConfigureAwait(true); }
            catch (Exception ex) { failures.Add("killing child processes: " + ex.Message); }
        }
        var report = new ShutdownReport(timedOut, killed, failures, clock.Elapsed);
        if (timedOut.Count > 0) log?.LogWarning("Shutdown deadline ({Deadline}) passed before {Services} finished", deadline, string.Join(", ", timedOut));
        if (killed.Count > 0) log?.LogWarning("Killed child processes that were still running at exit: {Processes}", string.Join(", ", killed));
        foreach (var f in failures) log?.LogWarning("Shutdown: {Failure}", f);

        // 5. The rest of the container, synchronously on this (UI) thread.
        try { services.Dispose(); }
        catch (Exception ex) { failures.Add("disposing services: " + ex.Message); }
        return report with { Elapsed = clock.Elapsed };
    }

    /// <summary>
    /// For an exit that bypassed <see cref="RunAsync"/> (the lifetime was ended directly): no time to wait for anything. Kills the tracked children and
    /// writes state synchronously (blocking file I/O is acceptable on the way out of the process). The container is not disposed: some module
    /// disposals wait for their servers.
    /// </summary>
    public static void RunAbrupt(IServiceProvider services)
    {
        try { services.GetService<ChildProcessTracker>()?.KillSurvivorsNow(); }
        catch (Exception) { /* exiting anyway */ }
        try { (services.GetService<IStateStorage>() as IDisposable)?.Dispose(); }
        catch (Exception) { /* exiting anyway */ }
    }

    private static IEnumerable<(string Name, IAsyncDisposable Service)> GracefulParticipants(IServiceProvider services, List<string> failures)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        (string, object?)[] candidates =
        [
            ("language servers", TryGet<ILanguageServerManager>(services, failures)),
            ("debug sessions", TryGet<IDebugService>(services, failures)),
            ("breakpoints", TryGet<IBreakpointManager>(services, failures)),
            ("launch configurations", TryGet<IDebugConfigurationService>(services, failures)),
        ];
        foreach (var (name, service) in candidates)
            if (service is IAsyncDisposable disposable && seen.Add(service)) yield return (name, disposable);
    }

    private static T? TryGet<T>(IServiceProvider services, List<string> failures) where T : class
    {
        try { return services.GetService<T>(); }
        catch (Exception ex) { failures.Add($"resolving {typeof(T).Name}: {ex.Message}"); return null; }
    }

    /// <summary>Runs <paramref name="work"/> on the pool (its synchronous part must not hold the UI thread) and waits at most <paramref name="budget"/>.</summary>
    private static async Task BoundedAsync(string name, Func<Task> work, TimeSpan budget, List<string> timedOut, List<string> failures)
    {
        var task = Task.Run(work);
        try { await task.WaitAsync(budget).ConfigureAwait(true); }
        catch (TimeoutException)
        {
            lock (timedOut) if (!timedOut.Contains(name)) timedOut.Add(name);
            // It keeps running in the background; whatever it throws later is observed here rather than surfacing as an unobserved task exception.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            lock (failures) failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
