namespace CsCode.Sessions;

/// <summary>The resource policy of a hidden session (settings <c>cscode.background.trimAfterSeconds</c> and <c>idleServerTimeoutMinutes</c>).</summary>
public sealed record HiddenPolicySettings(TimeSpan TrimAfter, TimeSpan? IdleServerTimeout)
{
    public static HiddenPolicySettings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
}

/// <summary>
/// What happens to a session while its window is hidden: after <see cref="HiddenPolicySettings.TrimAfter"/> the shell trims it (editor views
/// disposed, caches dropped, compacting GC, <c>malloc_trim</c>), after <see cref="HiddenPolicySettings.IdleServerTimeout"/> its language servers are
/// stopped (restarted when it is shown again). A session with a running debug session is never trimmed of its servers. Showing the window cancels
/// whatever has not happened yet. Two one-shot timers, armed only while hidden: a hidden session has no periodic work at all (≈0 % CPU).
/// The callbacks run on a timer thread; the shell marshals what needs the UI.
/// </summary>
public sealed class HiddenSessionPolicy : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<HiddenPolicySettings> _settings;
    private readonly Action _trim;
    private readonly Action _stopServers;
    private readonly Func<bool> _isDebugging;
    private readonly ITimer _trimTimer;
    private readonly ITimer _serverTimer;
    private bool _hidden, _disposed;

    public HiddenSessionPolicy(TimeProvider time, Func<HiddenPolicySettings> settings, Action trim, Action stopServers, Func<bool>? isDebugging = null)
    {
        _settings = settings;
        _trim = trim;
        _stopServers = stopServers;
        _isDebugging = isDebugging ?? (() => false);
        _trimTimer = time.CreateTimer(static s => ((HiddenSessionPolicy)s!).OnTrim(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _serverTimer = time.CreateTimer(static s => ((HiddenSessionPolicy)s!).OnStopServers(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsHidden { get { lock (_gate) return _hidden; } }
    public bool Trimmed { get; private set; }
    public bool ServersStopped { get; private set; }

    public void OnHidden()
    {
        var s = _settings();
        lock (_gate)
        {
            if (_disposed || _hidden) return;
            _hidden = true;
            _trimTimer.Change(s.TrimAfter < TimeSpan.Zero ? TimeSpan.Zero : s.TrimAfter, Timeout.InfiniteTimeSpan);
            if (s.IdleServerTimeout is { } idle) _serverTimer.Change(idle < TimeSpan.Zero ? TimeSpan.Zero : idle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Cancels pending work. Returns whether the servers had been stopped (the caller restarts them) and whether the session was trimmed.</summary>
    public (bool ServersWereStopped, bool WasTrimmed) OnShown()
    {
        lock (_gate)
        {
            _hidden = false;
            _trimTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _serverTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var result = (ServersStopped, Trimmed);
            ServersStopped = false;
            Trimmed = false;
            return result;
        }
    }

    private void OnTrim()
    {
        lock (_gate)
        {
            if (!_hidden || _disposed) return;
            Trimmed = true;
        }
        _trim();
    }

    private void OnStopServers()
    {
        lock (_gate)
        {
            if (!_hidden || _disposed) return;
            if (_isDebugging())
            {
                // A running debug session keeps its process un-evictable and its servers alive; look again later.
                if (_settings().IdleServerTimeout is { } idle) _serverTimer.Change(idle, Timeout.InfiniteTimeSpan);
                return;
            }
            ServersStopped = true;
        }
        _stopServers();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _trimTimer.Dispose();
        _serverTimer.Dispose();
    }
}
