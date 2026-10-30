using System.Diagnostics;
using System.Globalization;
using CsCode.Sessions.Ipc;

namespace CsCode.Sessions.Launcher;

public sealed record LauncherTimeouts
{
    /// <summary>Connect + handshake with an existing process; past it the process counts as wedged (never waited for).</summary>
    public TimeSpan Handshake { get; init; } = IpcClient.DefaultConnectTimeout;
    /// <summary>A shown/claimed window replies once it painted; a cold process may take longer (restore of a big session).</summary>
    public TimeSpan ShowReply { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A started process must answer its first hello within this.</summary>
    public TimeSpan StartHello { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Concurrent launchers wait this long for each other.</summary>
    public TimeSpan LauncherLock { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long <c>--quit</c> waits for processes to be gone.</summary>
    public TimeSpan QuitWait { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>What one launcher run did (asserted by tests).</summary>
public sealed record LaunchOutcome(int ExitCode, LaunchPlan Plan, int? TargetPid, TimeSpan Elapsed, IReadOnlyList<int> Started);

/// <summary>
/// The client mode of the <c>cscode</c> executable: runs before Avalonia initialises, talks to the running CsCode processes over IPC and exits.
/// Concurrent launches are serialised by the launcher lock (two <c>cscode --session x</c> at once end with one process owning <c>x</c>); the lock
/// is held until the target process answered for its session, then released before a <c>--wait</c> blocks. A process that does not answer within
/// the handshake timeout is never waited for: the launcher falls back to starting a fresh process and says so.
/// <para>Synchronous on purpose: the launcher is a fresh process on every invocation, and blocking calls on its one thread avoid the start-up
/// cost of the async socket engine and the thread pool (docs/SESSIONS.md, latency). Callers inside CsCode use <see cref="RunAsync"/>, which runs
/// it on a pool thread.</para>
/// </summary>
public sealed class SessionLauncher
{
    private readonly RuntimeDirectory _runtime;
    private readonly SessionStore _store;
    private readonly LaunchSettings _settings;
    private readonly IHostSpawner _spawner;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly LauncherTimeouts _timeouts;

    public SessionLauncher(RuntimeDirectory runtime, SessionStore store, LaunchSettings settings, IHostSpawner spawner, TextWriter output, TextWriter error, LauncherTimeouts? timeouts = null)
    {
        _runtime = runtime;
        _store = store;
        _settings = settings;
        _spawner = spawner;
        _out = output;
        _err = error;
        _timeouts = timeouts ?? new LauncherTimeouts();
    }

    /// <summary>Runs the launcher on a pool thread (callers inside a CsCode process; never blocks the caller).</summary>
    public Task<LaunchOutcome> RunAsync(LaunchRequest request, CancellationToken ct = default) => Task.Run(() => Run(request), ct);

    /// <summary>Exit code 0 on success, 1 when the request failed, 2 when IPC is unusable (the caller then runs the app in-process).</summary>
    public LaunchOutcome Run(LaunchRequest request)
    {
        var clock = Stopwatch.StartNew();
        if (_runtime.Ensure() is { } problem)
        {
            _err.WriteLine("cscode: background mode unavailable: " + problem);
            return new LaunchOutcome(2, new LaunchPlan(LaunchAction.Fail) { Error = problem }, null, clock.Elapsed, []);
        }
        var started = new List<int>();
        SessionTrace.Mark("launcher: start " + request.Kind);
        // A second round only when the show reached a process that had begun to quit (between the discovery and the show).
        for (var round = 0; ; round++)
        {
            if (RunRound(request, retryAllowed: round == 0, clock, started) is { } outcome) return outcome;
        }
    }

    /// <summary>One discovery, plan and action under the launcher lock; null when the target turned out to be quitting (the caller runs again).</summary>
    private LaunchOutcome? RunRound(LaunchRequest request, bool retryAllowed, Stopwatch clock, List<int> started)
    {
        var gate = FileLock.Acquire(_store.LauncherLockPath, _timeouts.LauncherLock);
        if (gate is null) _err.WriteLine("cscode: another launcher is still busy; continuing without waiting for it");
        try
        {
            var world = Gather();
            var plan = LaunchPlanner.Plan(request, world, _settings);
            if (plan.Action == LaunchAction.AwaitQuit) (world, plan) = AwaitQuit(request, plan);
            SessionTrace.Mark($"launcher: planned {plan.Action} {plan.Session} pid {plan.Pid} ({world.Processes.Count} processes)");
            foreach (var w in plan.Warnings) _err.WriteLine("cscode: " + w);
            switch (plan.Action)
            {
                case LaunchAction.Fail:
                    _err.WriteLine("cscode: " + plan.Error);
                    return new LaunchOutcome(1, plan, null, clock.Elapsed, started);
                case LaunchAction.List:
                    PrintList(world);
                    return new LaunchOutcome(0, plan, null, clock.Elapsed, started);
                case LaunchAction.Quit:
                    return new LaunchOutcome(Quit(plan.QuitPids), plan, null, clock.Elapsed, started);
                case LaunchAction.Background:
                    // The warm spare is off by default: an autostart entry then starts nothing, and the log (journal) should say why.
                    if (!_settings.WarmSpare && !_settings.RestoreOnLogin)
                        _out.WriteLine($"cscode: nothing to start in the background ({LaunchSettings.WarmSpareKey} and {LaunchSettings.RestoreOnLoginKey} are off)");
                    foreach (var name in plan.StartHidden)
                        if (Start(name, seedFrom: null, newUnnamed: false, hidden: true) is { } pid) started.Add(pid);
                    if (plan.EnsureSpare && SpawnSpare() is { } spare) started.Add(spare);
                    return new LaunchOutcome(0, plan, null, clock.Elapsed, started);
            }

            // Open: show / claim / start, falling back to a fresh process when a target does not answer.
            // Folders go along too: the session opens one it does not have (a session found by its folder already has it).
            var paths = request.Targets.Select(t => t.ToIpc()).ToArray();
            var session = plan.Session!;
            int? target = null;
            if (plan.Action == LaunchAction.Show) target = plan.Pid;
            else if (plan.Action == LaunchAction.Claim)
            {
                // The spare takes the session and restores it while still hidden; the show below makes it visible (one path for every action).
                var claim = TryRequest(plan.Pid!.Value, new IpcMessage { Type = IpcVerbs.Claim, Session = session, Hidden = true, Unnamed = plan.NewUnnamed }, _timeouts.ShowReply);
                if (claim is { Ok: true }) target = plan.Pid;
                else _err.WriteLine($"cscode: the warm spare (pid {plan.Pid}) did not take the session ({claim.Error ?? "no answer"}); starting a new process");
            }
            if (target is null)
            {
                target = Start(session, plan.SeedFrom, plan.NewUnnamed, hidden: false);
                if (target is { } pid) started.Add(pid);
                else return new LaunchOutcome(1, plan, null, clock.Elapsed, started);
            }

            // The target owns the session now: concurrent launchers may proceed (they will find it).
            gate?.Dispose();
            gate = null;

            var show = new IpcMessage
            {
                Type = IpcVerbs.Show, Paths = paths, Wait = request.Wait && paths.Length > 0,
                Remotes = request.RemoteTargets.Count > 0 ? request.RemoteTargets : null, // cscode ssh://host/path: the session connects and opens it
            };
            SessionTrace.Mark("launcher: show sent");
            var reply = TryRequest(target.Value, show, show.Wait == true ? null : _timeouts.ShowReply);
            SessionTrace.Mark("launcher: show replied " + (reply.Ok == true ? "ok" : reply.Error));
            if (reply.Error == IpcMessage.QuittingError && retryAllowed)
            {
                // Its window began to close after the discovery: let it finish, then open the session afresh.
                WaitForRelease(session);
                return null;
            }
            var code = 0;
            if (reply is not { Ok: true })
            {
                _err.WriteLine($"cscode: session '{session}' (pid {target}) did not confirm it was shown: {reply.Error ?? "no answer in time"}");
                code = 1;
            }

            // After the window is up (none of this may delay it): a new spare, and the evictions beyond maxResidentSessions (which a session that
            // would have to ask the user anything refuses: an eviction never pops a window up).
            if (plan.EnsureSpare && SpawnSpare() is { } newSpare) started.Add(newSpare);
            foreach (var pid in plan.Evict) TryRequest(pid, new IpcMessage { Type = IpcVerbs.Quit, Force = false, Evict = true }, TimeSpan.FromSeconds(5));
            return new LaunchOutcome(code, plan, target, clock.Elapsed, started);
        }
        finally { gate?.Dispose(); }
    }

    /// <summary>
    /// The session's process is quitting and still holds its lock: waits (at most <see cref="LauncherTimeouts.QuitWait"/>) until it lets go, then
    /// plans again. A process that keeps the lock that long counts as wedged (the plan opens a copy, with the warning).
    /// </summary>
    private (LaunchWorld World, LaunchPlan Plan) AwaitQuit(LaunchRequest request, LaunchPlan plan)
    {
        SessionTrace.Mark($"launcher: waiting for pid {plan.Pid} to finish quitting '{plan.Session}'");
        var released = WaitForRelease(plan.Session!);
        var world = Gather();
        var next = LaunchPlanner.Plan(request, world, _settings);
        if (next.Action != LaunchAction.AwaitQuit) return (world, next);
        if (released) SessionTrace.Mark("launcher: the session was taken again by a quitting process");
        var wedged = world with { Processes = world.Processes.Select(p => p.IsLeaving(plan.Session!) ? p with { Responsive = false } : p).ToArray() };
        return (wedged, LaunchPlanner.Plan(request, wedged, _settings));
    }

    /// <summary>True once nobody holds the session's lock (bounded by <see cref="LauncherTimeouts.QuitWait"/>).</summary>
    private bool WaitForRelease(string session)
    {
        var clock = Stopwatch.StartNew();
        var path = _store.LockPathOf(session);
        while (FileLock.IsHeldElsewhere(path))
        {
            if (clock.Elapsed >= _timeouts.QuitWait) return false;
            Thread.Sleep(25);
        }
        return true;
    }

    private LaunchWorld Gather()
    {
        SessionTrace.Mark("launcher: locked");
        var found = ResidentDiscovery.Discover(_runtime, _timeouts.Handshake);
        SessionTrace.Mark("launcher: discovered");
        // The saved sessions are read only when the decision needs them (showing a running session never does).
        var saved = new LazySummaries(() =>
        {
            try { return _store.List(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _err.WriteLine("cscode: cannot read the saved sessions: " + ex.Message);
                return [];
            }
        });
        return new LaunchWorld(found, saved, _store.ReadLastActive(), name => FileLock.IsHeldElsewhere(_store.LockPathOf(name)));
    }

    /// <summary>The reply, or a failure message describing why there was none.</summary>
    private IpcMessage TryRequest(int pid, IpcMessage request, TimeSpan? replyTimeout)
    {
        try { return IpcClient.Request(_runtime.EndpointOf(pid), request, _timeouts.Handshake, replyTimeout); }
        catch (IpcException ex) { return IpcMessage.Failure(ex.Failure + ": " + ex.Message); }
    }

    /// <summary>Starts a session process and waits until it answers for the session; null when it did not come up.</summary>
    private int? Start(string session, string? seedFrom, bool newUnnamed, bool hidden)
    {
        var args = new List<string> { "--host", "--session", session };
        if (seedFrom is not null) { args.Add("--seed-from"); args.Add(seedFrom); }
        if (newUnnamed) args.Add("--unnamed");
        if (hidden) args.Add("--hidden");
        ISpawnedHost host;
        try { host = _spawner.Spawn(args); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _err.WriteLine("cscode: could not start CsCode: " + ex.Message);
            return null;
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < _timeouts.StartHello)
        {
            if (ResidentDiscovery.Probe(_runtime, host.Pid, _timeouts.Handshake) is { Responsive: true } p && p.Session == session) return host.Pid;
            if (host.HasExited)
            {
                // Exit code 3: another process took the session first (a launch that did not go through this lock). Show that one instead.
                if (ResidentDiscovery.Discover(_runtime, _timeouts.Handshake).FirstOrDefault(o => o.OwnsSession(session)) is { } owner) return owner.Pid;
                _err.WriteLine($"cscode: CsCode exited during start-up (exit code {host.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}); see the crash logs");
                return null;
            }
            Thread.Sleep(15);
        }
        _err.WriteLine($"cscode: CsCode (pid {host.Pid}) did not answer within {_timeouts.StartHello.TotalSeconds:0} s");
        return null;
    }

    private int? SpawnSpare()
    {
        try { return _spawner.Spawn(["--host", "--spare"]).Pid; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _err.WriteLine("cscode: could not start the warm spare: " + ex.Message);
            return null;
        }
    }

    private int Quit(IReadOnlyList<int> pids)
    {
        var refused = new List<int>();
        foreach (var pid in pids)
        {
            var reply = TryRequest(pid, new IpcMessage { Type = IpcVerbs.Quit, Force = true }, TimeSpan.FromSeconds(10));
            if (reply.Ok != true && reply.Error?.StartsWith(nameof(IpcFailure.NoEndpoint), StringComparison.Ordinal) != true)
            {
                _err.WriteLine($"cscode: process {pid} did not quit: {reply.Error}");
                refused.Add(pid);
            }
        }
        var clock = Stopwatch.StartNew();
        var remaining = pids.Except(refused).ToList();
        while (remaining.Count > 0 && clock.Elapsed < _timeouts.QuitWait)
        {
            remaining.RemoveAll(pid => !ResidentDiscovery.IsCsCodeAlive(pid) || (!OperatingSystem.IsWindows() && !File.Exists(_runtime.EndpointOf(pid))));
            if (remaining.Count > 0) Thread.Sleep(50);
        }
        foreach (var pid in remaining) _err.WriteLine($"cscode: process {pid} has not exited yet");
        return remaining.Count == 0 && refused.Count == 0 ? 0 : 1;
    }

    private void PrintList(LaunchWorld world)
    {
        var rows = new List<string[]> { new[] { "SESSION", "STATE", "PID", "MEMORY", "LAST USED", "FOLDER" } };
        var names = world.Saved.Select(s => s.Name).Concat(world.Processes.Select(p => p.Session).OfType<string>()).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var saved = world.Saved.FirstOrDefault(s => s.Name == name);
            var proc = world.Processes.FirstOrDefault(p => p.Session == name);
            var folder = saved?.Folders.FirstOrDefault() is { } f && Uri.TryCreate(f, UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.ToString()) : "";
            rows.Add(
            [
                name + (saved?.IsUnnamed == true ? " (unnamed)" : ""),
                proc is null ? "saved" : proc.Responsive ? proc.State : "not responding",
                proc?.Pid.ToString(CultureInfo.InvariantCulture) ?? "",
                proc?.WorkingSetBytes is { } ws ? $"{ws / (1024 * 1024)} MB" : "",
                saved is null || saved.LastUsedUtc == DateTimeOffset.MinValue ? "" : saved.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                folder,
            ]);
        }
        foreach (var spare in world.Processes.Where(p => p.IsSpare))
            rows.Add(["(warm spare)", ProcessStates.Spare, spare.Pid.ToString(CultureInfo.InvariantCulture), spare.WorkingSetBytes is { } ws ? $"{ws / (1024 * 1024)} MB" : "", "", ""]);
        var widths = Enumerable.Range(0, 6).Select(i => rows.Max(r => r[i].Length)).ToArray();
        foreach (var r in rows)
            _out.WriteLine(string.Join("  ", r.Select((c, i) => i == r.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd());
    }
}

/// <summary>A session list loaded on first use.</summary>
internal sealed class LazySummaries(Func<IReadOnlyList<SessionSummary>> load) : IReadOnlyList<SessionSummary>
{
    private readonly Lazy<IReadOnlyList<SessionSummary>> _items = new(load, LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsLoaded => _items.IsValueCreated;
    public SessionSummary this[int index] => _items.Value[index];
    public int Count => _items.Value.Count;
    public IEnumerator<SessionSummary> GetEnumerator() => _items.Value.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
