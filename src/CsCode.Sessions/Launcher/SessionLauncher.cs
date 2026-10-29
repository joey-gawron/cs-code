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

/// <summary>What one launcher run did (printed with <c>--verbose</c>; asserted by tests).</summary>
public sealed record LaunchOutcome(int ExitCode, LaunchPlan Plan, int? TargetPid, TimeSpan Elapsed, IReadOnlyList<int> Started);

/// <summary>
/// The client mode of the <c>cscode</c> executable: runs before Avalonia initialises, talks to the running CsCode processes over IPC and exits.
/// Concurrent launches are serialised by the launcher lock (two <c>cscode --session x</c> at once end with one process owning <c>x</c>); the lock
/// is held until the target process answered for its session, then released before a <c>--wait</c> blocks. A process that does not answer within
/// the handshake timeout is never waited for: the launcher falls back to starting a fresh process and says so.
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

    /// <summary>
    /// The launcher process's synchronous entry point: it has no UI thread (Avalonia is not initialised in client mode), so blocking its main
    /// thread on the run is what a console program does.
    /// </summary>
    public LaunchOutcome Run(LaunchRequest request) => RunAsync(request).GetAwaiter().GetResult();

    /// <summary>Exit code 0 on success, 1 when the request failed, 2 when IPC is unusable (the caller then runs the app in-process).</summary>
    public async Task<LaunchOutcome> RunAsync(LaunchRequest request, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        if (_runtime.Ensure() is { } problem)
        {
            await _err.WriteLineAsync("cscode: background mode unavailable: " + problem).ConfigureAwait(false);
            return new LaunchOutcome(2, new LaunchPlan(LaunchAction.Fail) { Error = problem }, null, clock.Elapsed, []);
        }
        var started = new List<int>();
        var gate = await FileLock.AcquireAsync(_runtime.LauncherLockPath, _timeouts.LauncherLock, ct).ConfigureAwait(false);
        if (gate is null) await _err.WriteLineAsync("cscode: another launcher is still busy; continuing without waiting for it").ConfigureAwait(false);
        try
        {
            var world = await GatherAsync(ct).ConfigureAwait(false);
            var plan = LaunchPlanner.Plan(request, world, _settings);
            foreach (var w in plan.Warnings) await _err.WriteLineAsync("cscode: " + w).ConfigureAwait(false);
            int? target = null;
            var code = 0;
            switch (plan.Action)
            {
                case LaunchAction.Fail:
                    await _err.WriteLineAsync("cscode: " + plan.Error).ConfigureAwait(false);
                    return new LaunchOutcome(1, plan, null, clock.Elapsed, started);
                case LaunchAction.List:
                    await PrintListAsync(world).ConfigureAwait(false);
                    return new LaunchOutcome(0, plan, null, clock.Elapsed, started);
                case LaunchAction.Quit:
                    code = await QuitAsync(plan.QuitPids, ct).ConfigureAwait(false);
                    return new LaunchOutcome(code, plan, null, clock.Elapsed, started);
                case LaunchAction.Background:
                    foreach (var name in plan.StartHidden)
                    {
                        var host = await StartAsync(name, seedFrom: null, newUnnamed: false, hidden: true, ct).ConfigureAwait(false);
                        if (host is { } pid) started.Add(pid);
                    }
                    if (plan.EnsureSpare) started.Add(SpawnSpare());
                    return new LaunchOutcome(0, plan, null, clock.Elapsed, started);
            }

            // Open: show / claim / start, falling back to a fresh process when a target does not answer.
            // Folders go along too: the session opens one it does not have (a session found by its folder already has it).
            var paths = request.Targets.Select(t => t.ToIpc()).ToArray();
            var session = plan.Session!;
            switch (plan.Action)
            {
                case LaunchAction.Show:
                    target = plan.Pid;
                    break;
                case LaunchAction.Claim:
                    // The spare takes the session and restores it while still hidden; the show below makes it visible (one path for every action).
                    var claim = await TryRequestAsync(plan.Pid!.Value, new IpcMessage { Type = IpcVerbs.Claim, Session = session, Hidden = true, Unnamed = plan.NewUnnamed }, _timeouts.ShowReply, ct).ConfigureAwait(false);
                    if (claim is { Ok: true }) target = plan.Pid;
                    else await _err.WriteLineAsync($"cscode: the warm spare (pid {plan.Pid}) did not take the session ({claim?.Error ?? "no answer"}); starting a new process").ConfigureAwait(false);
                    break;
            }
            if (target is null)
            {
                target = await StartAsync(session, plan.SeedFrom, plan.NewUnnamed, hidden: false, ct).ConfigureAwait(false);
                if (target is { } pid) started.Add(pid);
                else return new LaunchOutcome(1, plan, null, clock.Elapsed, started);
            }

            // The target owns the session now: concurrent launchers may proceed (they will find it).
            gate?.Dispose();
            gate = null;
            if (plan.EnsureSpare || (plan.Action == LaunchAction.Claim && _settings.WarmSpare)) started.Add(SpawnSpare());
            foreach (var pid in plan.Evict) _ = TryRequestAsync(pid, new IpcMessage { Type = IpcVerbs.Quit, Force = false }, TimeSpan.FromSeconds(2), CancellationToken.None);

            var show = new IpcMessage { Type = IpcVerbs.Show, Paths = paths, Wait = request.Wait && paths.Length > 0 };
            var reply = await TryRequestAsync(target.Value, show, show.Wait == true ? null : _timeouts.ShowReply, ct).ConfigureAwait(false);
            if (reply is not { Ok: true })
            {
                await _err.WriteLineAsync($"cscode: session '{session}' (pid {target}) did not confirm it was shown: {reply?.Error ?? "no answer in time"}").ConfigureAwait(false);
                code = 1;
            }
            return new LaunchOutcome(code, plan, target, clock.Elapsed, started);
        }
        finally { gate?.Dispose(); }
    }

    private async Task<LaunchWorld> GatherAsync(CancellationToken ct)
    {
        var processes = ResidentDiscovery.DiscoverAsync(_runtime, _timeouts.Handshake, ct);
        IReadOnlyList<SessionSummary> saved;
        string? last;
        try
        {
            saved = _store.List();
            last = _store.ReadLastActive();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _err.WriteLineAsync("cscode: cannot read the saved sessions: " + ex.Message).ConfigureAwait(false);
            saved = [];
            last = null;
        }
        return new LaunchWorld(await processes.ConfigureAwait(false), saved, last, name => FileLock.IsHeldElsewhere(_runtime.SessionLockPath(name)));
    }

    private async Task<IpcMessage?> TryRequestAsync(int pid, IpcMessage request, TimeSpan? replyTimeout, CancellationToken ct)
    {
        try { return await IpcClient.RequestAsync(_runtime.EndpointOf(pid), request, _timeouts.Handshake, replyTimeout, ct).ConfigureAwait(false); }
        catch (IpcException ex) { return IpcMessage.Failure(ex.Failure + ": " + ex.Message); }
    }

    /// <summary>Starts a session process and waits until it answers for the session; null when it did not come up.</summary>
    private async Task<int?> StartAsync(string session, string? seedFrom, bool newUnnamed, bool hidden, CancellationToken ct)
    {
        var args = new List<string> { "--host", "--session", session };
        if (seedFrom is not null) { args.Add("--seed-from"); args.Add(seedFrom); }
        if (newUnnamed) args.Add("--unnamed");
        if (hidden) args.Add("--hidden");
        ISpawnedHost host;
        try { host = _spawner.Spawn(args); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            await _err.WriteLineAsync("cscode: could not start CsCode: " + ex.Message).ConfigureAwait(false);
            return null;
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < _timeouts.StartHello)
        {
            if (await ResidentDiscovery.ProbeAsync(_runtime, host.Pid, _timeouts.Handshake, ct).ConfigureAwait(false) is { Responsive: true } p)
            {
                if (p.Session == session) return host.Pid;
            }
            if (host.HasExited)
            {
                // Exit code 3: another process took the session first (a launch that did not go through this lock). Show that one instead.
                var others = await ResidentDiscovery.DiscoverAsync(_runtime, _timeouts.Handshake, ct).ConfigureAwait(false);
                if (others.FirstOrDefault(o => o.OwnsSession(session)) is { } owner) return owner.Pid;
                await _err.WriteLineAsync($"cscode: CsCode exited during start-up (exit code {host.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}); see the crash logs").ConfigureAwait(false);
                return null;
            }
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        await _err.WriteLineAsync($"cscode: CsCode (pid {host.Pid}) did not answer within {_timeouts.StartHello.TotalSeconds:0} s").ConfigureAwait(false);
        return null;
    }

    private int SpawnSpare()
    {
        try { return _spawner.Spawn(["--host", "--spare"]).Pid; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _err.WriteLine("cscode: could not start the warm spare: " + ex.Message);
            return 0;
        }
    }

    private async Task<int> QuitAsync(IReadOnlyList<int> pids, CancellationToken ct)
    {
        var requests = pids.Select(pid => TryRequestAsync(pid, new IpcMessage { Type = IpcVerbs.Quit, Force = true }, TimeSpan.FromSeconds(5), ct)).ToArray();
        await Task.WhenAll(requests).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var remaining = pids.ToList();
        while (remaining.Count > 0 && clock.Elapsed < _timeouts.QuitWait)
        {
            remaining.RemoveAll(pid => !ResidentDiscovery.IsCsCodeAlive(pid) || !File.Exists(_runtime.EndpointOf(pid)) && !OperatingSystem.IsWindows());
            if (remaining.Count > 0) await Task.Delay(50, ct).ConfigureAwait(false);
        }
        foreach (var pid in remaining) await _err.WriteLineAsync($"cscode: process {pid} has not exited yet").ConfigureAwait(false);
        return remaining.Count == 0 ? 0 : 1;
    }

    private async Task PrintListAsync(LaunchWorld world)
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
            await _out.WriteLineAsync(string.Join("  ", r.Select((c, i) => i == r.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd()).ConfigureAwait(false);
    }
}
