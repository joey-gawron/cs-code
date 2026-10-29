using System.ComponentModel;
using System.Diagnostics;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;

namespace CsCode.App.Startup;

/// <summary>
/// The child processes (language servers, debug adapters) that modules report through <see cref="IMemoryMonitor.TrackChildProcess"/>, so the exit path can
/// kill the ones whose owner did not stop them within the shutdown deadline. Only reported pids are ever killed (never "every descendant": a browser started
/// through <c>xdg-open</c> is a child too), and a pid is only killed while it still belongs to the process that was reported (start-time check against pid reuse).
/// Thread-safe.
/// </summary>
public sealed class ChildProcessTracker
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    private sealed record Entry(string Name, int Pid, DateTime? StartTime);

    /// <summary>Records a child process until the returned token is disposed (its owner stopped it).</summary>
    public IDisposable Track(string name, int pid)
    {
        var entry = new Entry(name, pid, StartTimeOf(pid));
        lock (_gate) _entries.Add(entry);
        return Disposable.Create(() => { lock (_gate) _entries.Remove(entry); });
    }

    /// <summary>The processes currently tracked (name, pid).</summary>
    public IReadOnlyList<(string Name, int Pid)> Tracked
    {
        get
        {
            lock (_gate) return _entries.Select(e => (e.Name, e.Pid)).ToArray();
        }
    }

    /// <summary>
    /// Kills (with their process trees) the tracked processes that are still running, then waits up to <paramref name="wait"/> until they are gone
    /// (SIGKILL is asynchronous). Returns a description of each process it killed. Never throws for a process that is already gone.
    /// </summary>
    public async Task<IReadOnlyList<string>> KillSurvivorsAsync(TimeSpan wait)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var killed = new List<(string Description, Process Process)>();
        foreach (var e in entries)
        {
            if (TryKill(e) is { } p) killed.Add(($"{e.Name} (pid {e.Pid})", p));
        }
        try
        {
            await Task.WhenAll(killed.Select(k => k.Process.WaitForExitAsync())).WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* reported as killed: the signal was delivered, the kernel finishes the job */ }
        foreach (var (_, p) in killed) p.Dispose();
        return killed.Select(k => k.Description).ToArray();
    }

    /// <summary>Synchronous variant for an exit that leaves no time to wait: signals and returns.</summary>
    public IReadOnlyList<string> KillSurvivorsNow()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.ToArray();
        var killed = new List<string>();
        foreach (var e in entries)
        {
            if (TryKill(e) is not { } p) continue;
            killed.Add($"{e.Name} (pid {e.Pid})");
            p.Dispose();
        }
        return killed;
    }

    private static Process? TryKill(Entry e)
    {
        Process p;
        try { p = Process.GetProcessById(e.Pid); }
        catch (ArgumentException) { return null; } // already gone
        try
        {
            // A pid whose start time changed belongs to an unrelated process that reused it: never touch it.
            if (p.HasExited || (e.StartTime is { } started && StartTimeOf(p) != started)) { p.Dispose(); return null; }
            p.Kill(entireProcessTree: true);
            return p;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            p.Dispose(); // exited meanwhile, or not ours to kill
            return null;
        }
    }

    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return StartTimeOf(p);
        }
        catch (ArgumentException) { return null; }
    }

    private static DateTime? StartTimeOf(Process p)
    {
        try { return p.StartTime; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }
}

/// <summary>
/// The shell's <see cref="IMemoryMonitor"/>: forwards everything to CsCode.Profiling's monitor when one is registered and additionally reports every tracked
/// child process to the <see cref="ChildProcessTracker"/>. Until a real monitor is registered it stands in on its own: it tracks children and keeps the
/// trimmables (run by <see cref="TrimNowAsync"/>), but samples nothing (<see cref="Sampled"/> never fires) and enforces no budget.
/// </summary>
public sealed class ShellMemoryMonitor : IMemoryMonitor, IDisposable
{
    private readonly IMemoryMonitor? _inner;
    private readonly bool _ownsInner;
    private readonly ChildProcessTracker _children;
    private readonly object _gate = new();
    private readonly List<IMemoryTrimmable> _trimmables = new();
    private readonly Emitter<MemorySnapshot> _sampled = new();
    private int _disposed;

    /// <param name="inner">The real monitor, or null when none is registered.</param>
    /// <param name="ownsInner">True when the inner monitor was created for this wrapper (the container then never disposes it itself).</param>
    public ShellMemoryMonitor(IMemoryMonitor? inner, bool ownsInner, ChildProcessTracker children)
    {
        _inner = inner;
        _ownsInner = ownsInner;
        _children = children;
    }

    public MemorySnapshot Current => _inner?.Current ?? SnapshotNow();
    public long BudgetBytes => _inner?.BudgetBytes ?? 0;
    public IEventSource<MemorySnapshot> Sampled => _inner?.Sampled ?? _sampled;

    public IReadOnlyList<IMemoryTrimmable> Trimmables
    {
        get
        {
            if (_inner is not null) return _inner.Trimmables;
            lock (_gate) return _trimmables.ToArray();
        }
    }

    public IDisposable Register(IMemoryTrimmable trimmable)
    {
        if (_inner is not null) return _inner.Register(trimmable);
        lock (_gate) _trimmables.Add(trimmable);
        return Disposable.Create(() => { lock (_gate) _trimmables.Remove(trimmable); });
    }

    public async ValueTask TrimNowAsync(MemoryTrimLevel level, CancellationToken ct = default)
    {
        if (_inner is not null) { await _inner.TrimNowAsync(level, ct).ConfigureAwait(false); return; }
        var all = Trimmables;
        // Trimmables are called from a background thread by contract.
        await Task.Run(() => { foreach (var t in all) { ct.ThrowIfCancellationRequested(); t.Trim(level); } }, ct).ConfigureAwait(false);
    }

    public IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null)
    {
        var accounting = _inner?.TrackChildProcess(name, pid, restartAboveBytes, onOverBudget);
        var exitGuard = _children.Track(name, pid);
        return Disposable.Create(() =>
        {
            exitGuard.Dispose();
            accounting?.Dispose();
        });
    }

    private static MemorySnapshot SnapshotNow()
    {
        var info = GC.GetGCMemoryInfo();
        return new MemorySnapshot(DateTimeOffset.Now, Environment.WorkingSet, 0, GC.GetTotalMemory(false), info.TotalCommittedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), 0, GC.GetTotalAllocatedBytes(), 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sampled.Clear();
        lock (_gate) _trimmables.Clear();
        if (!_ownsInner) return;
        if (_inner is IDisposable d) d.Dispose();
        else if (_inner is IAsyncDisposable ad) _ = ad.DisposeAsync().AsTask(); // never blocks the (UI) thread disposing the container
    }
}
