using System.Diagnostics;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;

namespace CsCode.Profiling;

/// <summary>
/// The process's <see cref="IMemoryMonitor"/> (docs/MEMORY.md). A dedicated below-normal-priority thread samples the process every 2 s (every 10 s
/// once the user has been idle for 30 s) without allocating beyond the published <see cref="MemorySnapshot"/>, reads the tracked child processes'
/// resident sets, runs the pressure policy against <c>cscode.memory.budgetMb</c> (<see cref="MemoryPressurePolicy"/>), calls the over-budget
/// callbacks of children above their caps, and runs the idle trim (<c>cscode.memory.trimOnIdle</c>) once per idle period. Every trim — policy,
/// idle or <see cref="TrimNowAsync"/> — runs the registered trimmables off the caller's thread, one at a time, each isolated from the others'
/// exceptions and timed, then compacts the GC heap and returns free pages to the OS (<see cref="OsMemory"/>). Trims never overlap.
/// Nothing runs until <see cref="Start"/> (the app calls it once its window exists); configuration is read lazily, on the sampling thread.
/// Every member is thread-safe; <see cref="Dispose"/> does not block.
/// </summary>
public sealed class MemoryMonitor : IMemoryMonitor, IUserActivityMonitor, IDisposable
{
    public const string IdleTrimSecondsSetting = "cscode.memory.idleTrimSeconds";
    public const string LogIntervalSecondsSetting = "cscode.memory.logIntervalSeconds";

    private readonly MemoryMonitorOptions _options;
    private readonly Func<IConfigurationService?> _configuration;
    private readonly ILogger? _log;
    private readonly ProcessSampler _sampler = new();
    private readonly MemoryPressurePolicy _policy;
    private readonly object _policyGate = new();
    private readonly Emitter<MemorySnapshot> _sampled = new();
    private readonly Emitter<MemoryTrimReport> _trimmed = new();
    private readonly SemaphoreSlim _trimGate = new(1, 1);
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private readonly DisposableStore _subscriptions = new();
    private readonly long _createdAt = Stopwatch.GetTimestamp();
    private IMemoryTrimmable[] _trimmables = [];
    private Child[] _children = [];
    private MemorySnapshot? _current;
    private ProcessMemorySample _details;
    private MemoryTrimReport? _lastTrim;
    private MemoryMetrics? _metrics;
    private Thread? _thread;
    private long _lastActivity;
    private long _idleTrimmedFor = -1;
    private int _slow, _disposed, _configRead, _configDirty, _samples, _trims;
    private long _budgetBytes;
    private bool _trimOnIdle = true;
    private TimeSpan _idleTrimAfter;
    private long _growthLoggedAt = long.MinValue / 2;

    private sealed class Child(string name, int pid, long? cap, Func<ValueTask>? onOverBudget, ProcFile? file, Process? process)
    {
        public string Name { get; } = name;
        public int Pid { get; } = pid;
        public long? Cap { get; } = cap;
        public Func<ValueTask>? OnOverBudget { get; } = onOverBudget;
        public ProcFile? File { get; } = file;
        public Process? Process { get; } = process;
        public long WorkingSet;
        public bool Exited = file is null && process is null;
        public int OverSamples;
        public int InCallback;
        public int Callbacks;
        public long LastCallbackMs = long.MinValue / 2;
        /// <summary>Descendants found at the last tree scan, each with its status file opened once (sampling thread only; Linux).</summary>
        public List<(int Pid, ProcFile File)> Descendants { get; } = new();
        public long NextTreeScan;
        public int Processes = 1;

        public void Close()
        {
            File?.Dispose();
            Process?.Dispose();
            lock (Descendants)
            {
                foreach (var (_, f) in Descendants) f.Dispose();
                Descendants.Clear();
            }
        }
    }

    /// <param name="configuration">Resolved lazily, on the sampling thread (null: defaults — no budget, idle trim on).</param>
    public MemoryMonitor(MemoryMonitorOptions? options = null, Func<IConfigurationService?>? configuration = null, ILoggerFactory? loggers = null)
    {
        _options = options ?? new MemoryMonitorOptions();
        _configuration = configuration ?? (() => null);
        _log = loggers?.CreateLogger("CsCode.Profiling.Memory");
        _policy = new MemoryPressurePolicy(_options.Policy);
        _idleTrimAfter = _options.IdleTrimAfter;
        _lastActivity = _createdAt;
    }

    // ------------------------------------------------------------------ IMemoryMonitor

    public MemorySnapshot Current => Volatile.Read(ref _current) ?? SampleNow();

    /// <summary>The last sample with the Linux breakdown and GC details the Core snapshot has no room for.</summary>
    public ProcessMemorySample CurrentDetails
    {
        get
        {
            if (Volatile.Read(ref _current) is null) SampleNow();
            lock (_gate) return _details;
        }
    }

    public long BudgetBytes
    {
        get
        {
            EnsureConfiguration();
            return Interlocked.Read(ref _budgetBytes);
        }
    }

    public IEventSource<MemorySnapshot> Sampled => _sampled;

    /// <summary>Fires after every trim (on the trimming thread) with what it did.</summary>
    public IEventSource<MemoryTrimReport> Trimmed => _trimmed;

    public MemoryTrimReport? LastTrim => Volatile.Read(ref _lastTrim);

    /// <summary>Samples taken and trims run since creation (diagnostics, tests).</summary>
    public int SampleCount => Volatile.Read(ref _samples);
    public int TrimCount => Volatile.Read(ref _trims);

    public bool IsStarted => Volatile.Read(ref _thread) is not null;

    public IReadOnlyList<IMemoryTrimmable> Trimmables => Volatile.Read(ref _trimmables);

    public IDisposable Register(IMemoryTrimmable trimmable)
    {
        ArgumentNullException.ThrowIfNull(trimmable);
        lock (_gate) _trimmables = [.. _trimmables, trimmable];
        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                var i = Array.IndexOf(_trimmables, trimmable);
                if (i >= 0) _trimmables = [.. _trimmables[..i], .. _trimmables[(i + 1)..]];
            }
        });
    }

    public async ValueTask TrimNowAsync(MemoryTrimLevel level, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _trimGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Trimmables run off the caller's (possibly UI) thread by contract; the caller only pays for the semaphore and the enqueue.
            await Task.Run(() => TrimCore(level, "requested", ct), ct).ConfigureAwait(false);
        }
        finally { _trimGate.Release(); }
    }

    public IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null)
    {
        ProcFile? file = null;
        Process? process = null;
        if (OperatingSystem.IsLinux()) file = ProcFile.TryOpen($"/proc/{pid}/status");
        else
        {
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { } // already gone: tracked as exited
        }
        var child = new Child(name, pid, restartAboveBytes, onOverBudget, file, process);
        lock (_gate) _children = [.. _children, child];
        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                var i = Array.IndexOf(_children, child);
                if (i >= 0) _children = [.. _children[..i], .. _children[(i + 1)..]];
            }
            child.Close();
        });
    }

    /// <summary>The tracked children as last sampled.</summary>
    public IReadOnlyList<ChildProcessInfo> Children =>
        Volatile.Read(ref _children).Select(c => new ChildProcessInfo(c.Name, c.Pid, Interlocked.Read(ref c.WorkingSet), c.Cap, c.Exited, Volatile.Read(ref c.Callbacks),
            Volatile.Read(ref c.Processes))).ToArray();

    // ------------------------------------------------------------------ activity

    public void NotifyActivity()
    {
        Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
        // Back to fast sampling at once when the loop had slowed down (one event set per idle period, not per key).
        if (Volatile.Read(ref _slow) != 0 && Interlocked.Exchange(ref _slow, 0) != 0 && Volatile.Read(ref _disposed) == 0)
        {
            try { _wake.Set(); }
            catch (ObjectDisposedException) { } // input arriving while the app shuts down
        }
    }

    public TimeSpan IdleTime => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity));

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts the sampling thread (idempotent). Cheap: creates one thread; the configuration is read on it.</summary>
    public MemoryMonitor Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return this;
        lock (_gate)
        {
            if (_thread is not null) return this;
            _metrics = new MemoryMetrics(this);
            var thread = new Thread(Loop) { IsBackground = true, Name = "cscode-memory", Priority = ThreadPriority.BelowNormal };
            Volatile.Write(ref _thread, thread);
            thread.Start();
        }
        return this;
    }

    /// <summary>Takes one sample now on the calling thread, publishes it and returns it (diagnostics, tests). No policy decision.</summary>
    public MemorySnapshot SampleNow()
    {
        var snapshot = Sample();
        return snapshot;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subscriptions.Dispose();
        _sampled.Clear();
        _trimmed.Clear();
        Child[] children;
        lock (_gate)
        {
            children = _children;
            _children = [];
            _trimmables = [];
        }
        foreach (var c in children) c.Close();
        _metrics?.Dispose();
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
        // The thread disposes the sampler and the wait handle when it leaves its loop; without one, nothing else uses them.
        if (Volatile.Read(ref _thread) is null)
        {
            _sampler.Dispose();
            _wake.Dispose();
        }
    }

    // ------------------------------------------------------------------ sampling thread

    private void Loop()
    {
        try
        {
            EnsureConfiguration();
            while (Volatile.Read(ref _disposed) == 0)
            {
                var slow = IdleTime >= _options.SlowSamplingAfter;
                Volatile.Write(ref _slow, slow ? 1 : 0);
                _wake.WaitOne(slow ? _options.IdleSampleInterval : _options.SampleInterval);
                if (Volatile.Read(ref _disposed) != 0) break;
                try { Tick(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.LogError(ex, "Memory monitor sample failed");
                }
            }
        }
        finally
        {
            _sampler.Dispose();
            _wake.Dispose();
        }
    }

    /// <summary>One iteration of the sampling thread: sample, policy, idle trim, children. Internal so tests drive it without the thread.</summary>
    internal void Tick()
    {
        EnsureConfiguration();
        if (Interlocked.Exchange(ref _configDirty, 0) != 0) ReadConfiguration();
        var snapshot = Sample();
        var nowMs = NowMs();
        MemoryPressureDecision decision;
        lock (_policyGate) decision = _policy.Evaluate(nowMs, snapshot.WorkingSetBytes, Interlocked.Read(ref _budgetBytes));
        if (ShouldLogGrowth(decision, nowMs - _createdAt * 1000 / Stopwatch.Frequency, nowMs - _growthLoggedAt, _options))
        {
            _growthLoggedAt = nowMs;
            _log?.LogWarning("Memory is growing steadily: working set {WorkingSetMb} MB ({Reason})", snapshot.WorkingSetBytes / 1024 / 1024, decision.Reason);
        }
        if (decision.Level is { } level) TryTrimFromPolicy(level, decision.Reason + $" ({decision.Ratio:P0})");
        else if (_trimOnIdle && IdleTime >= _idleTrimAfter && Volatile.Read(ref _lastActivity) is var activity && Interlocked.Read(ref _idleTrimmedFor) != activity)
        {
            if (TryTrimFromPolicy(MemoryTrimLevel.Idle, $"idle for {IdleTime.TotalSeconds:F0} s")) Interlocked.Exchange(ref _idleTrimmedFor, activity);
        }
        CheckChildren(nowMs, aggressive: decision.Level == MemoryTrimLevel.Aggressive);
    }

    /// <summary>
    /// Whether a sample's sustained growth goes to the log: not when the policy acts on it anyway (a trim is logged), not within
    /// <see cref="MemoryMonitorOptions.GrowthLogGrace"/> of start, and at most once per growth window.
    /// </summary>
    internal static bool ShouldLogGrowth(MemoryPressureDecision decision, long sinceStartMs, long sinceLastLoggedMs, MemoryMonitorOptions options) =>
        decision.SustainedGrowth && decision.Level is null
        && sinceStartMs >= options.GrowthLogGrace.TotalMilliseconds
        && sinceLastLoggedMs > options.Policy.GrowthWindow.TotalMilliseconds;

    private MemorySnapshot Sample()
    {
        var p = _sampler.Sample();
        long children = 0;
        var all = Volatile.Read(ref _children);
        foreach (var child in all)
        {
            if (child.Exited) continue;
            long rss;
            if (child.File is { } file) rss = _sampler.ReadChildResident(file);
            else rss = ReadChildProcess(child.Process!);
            if (rss < 0)
            {
                child.Exited = true;
                Interlocked.Exchange(ref child.WorkingSet, 0);
                Volatile.Write(ref child.Processes, 0);
                CloseDescendants(child); // an orphan left behind is no longer this child's
                continue;
            }
            // lsp-core-4: the child's memory is its process TREE (typescript-language-server's tsserver, an adapter's debuggee, a launcher script's
            // real server). The tree is rescanned every ChildTreeRescanInterval; the members are read on every sample through their open files.
            if (child.File is not null) rss += TreeResident(child, all);
            Interlocked.Exchange(ref child.WorkingSet, rss);
            children += rss;
        }
        var snapshot = new MemorySnapshot(DateTimeOffset.UtcNow, p.WorkingSetBytes, p.PrivateBytes, p.GcHeapBytes, p.GcCommittedBytes, p.Gen0Collections,
            p.Gen1Collections, p.Gen2Collections, p.LargeObjectHeapBytes, p.AllocatedBytesTotal, children, p.ThreadCount);
        lock (_gate) _details = p;
        Volatile.Write(ref _current, snapshot);
        Interlocked.Increment(ref _samples);
        _sampled.Fire(snapshot);
        return snapshot;
    }

    /// <summary>The summed VmRSS of <paramref name="child"/>'s descendants (rescanning the tree when due); updates its process count.</summary>
    private long TreeResident(Child child, Child[] all)
    {
        lock (child.Descendants)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= child.NextTreeScan)
            {
                child.NextTreeScan = now + (long)(_options.ChildTreeRescanInterval.TotalSeconds * Stopwatch.Frequency);
                RescanTree(child, all);
            }
            long sum = 0;
            var processes = 1;
            for (var i = child.Descendants.Count - 1; i >= 0; i--)
            {
                var rss = _sampler.ReadChildResident(child.Descendants[i].File);
                if (rss < 0)
                {
                    child.Descendants[i].File.Dispose(); // exited (or a zombie): its handle stays bound to it, a reused pid is never read
                    child.Descendants.RemoveAt(i);
                    continue;
                }
                sum += rss;
                processes++;
            }
            Volatile.Write(ref child.Processes, processes);
            return sum;
        }
    }

    /// <summary>Looks the child's descendants up again, keeping the open files of those still there. Another tracked child's subtree is its own.</summary>
    private static void RescanTree(Child child, Child[] all)
    {
        var stop = new HashSet<int>();
        foreach (var c in all)
            if (!ReferenceEquals(c, child)) stop.Add(c.Pid);
        var pids = ProcessTreeScanner.Descendants(child.Pid, stop, MaxTreeProcesses);
        var open = new Dictionary<int, ProcFile>();
        foreach (var (pid, file) in child.Descendants) open[pid] = file;
        child.Descendants.Clear();
        foreach (var pid in pids)
        {
            if (open.Remove(pid, out var kept)) child.Descendants.Add((pid, kept));
            else if (ProcFile.TryOpen($"/proc/{pid}/status") is { } opened) child.Descendants.Add((pid, opened));
        }
        foreach (var gone in open.Values) gone.Dispose();
    }

    /// <summary>A tree larger than this (a terminal running a parallel build) is counted up to it.</summary>
    private const int MaxTreeProcesses = 512;

    private static void CloseDescendants(Child child)
    {
        lock (child.Descendants)
        {
            foreach (var (_, file) in child.Descendants) file.Dispose();
            child.Descendants.Clear();
        }
    }

    private static long ReadChildProcess(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited ? -1 : process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return -1; }
    }

    private void CheckChildren(long nowMs, bool aggressive)
    {
        foreach (var child in Volatile.Read(ref _children))
        {
            if (child.Cap is not { } cap || child.Exited) { child.OverSamples = 0; continue; }
            if (Interlocked.Read(ref child.WorkingSet) <= cap) { child.OverSamples = 0; continue; }
            child.OverSamples++;
            if (child.OverSamples < (aggressive ? 1 : _options.ChildOverBudgetSamples)) continue;
            if (child.OnOverBudget is not { } callback || nowMs - child.LastCallbackMs < _options.ChildCallbackCooldown.TotalMilliseconds) continue;
            if (Interlocked.Exchange(ref child.InCallback, 1) != 0) continue;
            child.LastCallbackMs = nowMs;
            Interlocked.Increment(ref child.Callbacks);
            var rssMb = Interlocked.Read(ref child.WorkingSet) / 1024 / 1024;
            _log?.LogWarning("{Child} (pid {Pid}) uses {WorkingSetMb} MB, above its {CapMb} MB cap: asking its owner to restart it",
                child.Name, child.Pid, rssMb, cap / 1024 / 1024);
            _ = Task.Run(async () =>
            {
                try { await callback().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.LogError(ex, "Over-budget callback of {Child} failed", child.Name); }
                finally { Volatile.Write(ref child.InCallback, 0); }
            });
        }
    }

    // ------------------------------------------------------------------ trims

    private bool TryTrimFromPolicy(MemoryTrimLevel level, string reason)
    {
        if (!_trimGate.Wait(0)) return false; // a trim is already running: that one counts
        try
        {
            // A Moderate trim fires while the user may be typing (70 % of the budget): it collects in the background instead of pausing every
            // thread for a compacting collection (~60-120 ms with the app's ~60 MB live heap, docs/MEMORY.md). Idle and Aggressive compact.
            TrimCore(level, reason, CancellationToken.None, compact: level != MemoryTrimLevel.Moderate);
            return true;
        }
        finally { _trimGate.Release(); }
    }

    private void TrimCore(MemoryTrimLevel level, string reason, CancellationToken ct, bool compact = true)
    {
        var before = _sampler.Sample();
        var all = Volatile.Read(ref _trimmables);
        var results = new List<TrimmableResult>(all.Length);
        long released = 0;
        var start = Stopwatch.GetTimestamp();
        foreach (var t in all)
        {
            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) break;
            var t0 = Stopwatch.GetTimestamp();
            string name;
            try { name = t.Name; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { name = t.GetType().Name; }
            try
            {
                var bytes = t.Trim(level);
                released += Math.Max(0, bytes);
                results.Add(new TrimmableResult(name, bytes, Stopwatch.GetElapsedTime(t0), null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One broken cache must not stop the others or the collection.
                results.Add(new TrimmableResult(name, 0, Stopwatch.GetElapsedTime(t0), ex.GetType().Name + ": " + ex.Message));
                _log?.LogError(ex, "Trimmable {Trimmable} failed at {Level}", name, level);
            }
        }
        var trimmablesElapsed = Stopwatch.GetElapsedTime(start);
        ct.ThrowIfCancellationRequested();
        var collect = Stopwatch.GetTimestamp();
        if (compact) OsMemory.CompactAndRelease(level);
        else OsMemory.CollectInBackgroundAndRelease();
        var collectElapsed = Stopwatch.GetElapsedTime(collect);
        var after = _sampler.Sample();
        var report = new MemoryTrimReport(level, reason, DateTimeOffset.UtcNow, released, results, before.WorkingSetBytes, after.WorkingSetBytes,
            before.GcHeapBytes, after.GcHeapBytes, trimmablesElapsed, collectElapsed);
        lock (_policyGate) _policy.OnTrimmed(NowMs(), level);
        Volatile.Write(ref _lastTrim, report);
        Interlocked.Increment(ref _trims);
        _metrics?.OnTrimmed(report);
        _log?.LogInformation("Memory: {Report}", report.ToString());
        foreach (var r in results)
            if (r.Error is null) _log?.LogDebug("  {Trimmable}: {ReleasedKb} KB in {Ms:F1} ms", r.Name, r.ReleasedBytes / 1024, r.Elapsed.TotalMilliseconds);
        if (Volatile.Read(ref _disposed) != 0) return;
        _trimmed.Fire(report);
        Sample(); // the status bar shows the effect at once
    }

    // ------------------------------------------------------------------ configuration

    private void EnsureConfiguration()
    {
        if (Volatile.Read(ref _configRead) != 0) return;
        lock (_gate)
        {
            if (_configRead != 0) return;
            _configRead = 1;
        }
        IConfigurationService? config;
        try { config = _configuration(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { config = null; } // composition failed or shutting down
        if (config is null) return;
        _subscriptions.Add(config.RegisterProperties(
        [
            new ConfigurationProperty(IdleTrimSecondsSetting, System.Text.Json.JsonSerializer.SerializeToElement((int)_options.IdleTrimAfter.TotalSeconds),
                "Seconds without input before the idle trim (cscode.memory.trimOnIdle) releases caches and compacts memory.",
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "integer", minimum = 10 })),
            new ConfigurationProperty(LogIntervalSecondsSetting, System.Text.Json.JsonSerializer.SerializeToElement(60),
                "Seconds between the memory summary lines of the Memory output channel (0 = only trims are logged).",
                System.Text.Json.JsonSerializer.SerializeToElement(new { type = "integer", minimum = 0 })),
        ]));
        _subscriptions.Add(config.Changed.Subscribe(e =>
        {
            if (e.Affects("cscode.memory")) { Volatile.Write(ref _configDirty, 1); }
        }));
        ConfigurationSource = config;
        ReadConfiguration();
    }

    private IConfigurationService? ConfigurationSource { get; set; }

    private void ReadConfiguration()
    {
        if (ConfigurationSource is not { } config) return;
        var mb = Math.Max(0, config.Get(SettingKeys.MemoryBudgetMb, 0));
        Interlocked.Exchange(ref _budgetBytes, mb * 1024L * 1024);
        _trimOnIdle = config.Get(SettingKeys.MemoryTrimOnIdle, true);
        var defaultSeconds = (int)_options.IdleTrimAfter.TotalSeconds;
        var seconds = config.Get(IdleTrimSecondsSetting, defaultSeconds);
        // The registered default stands for the option itself (tests shorten it below the 10 s floor users get).
        _idleTrimAfter = seconds == defaultSeconds ? _options.IdleTrimAfter : TimeSpan.FromSeconds(Math.Max(10, seconds));
    }

    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
}
