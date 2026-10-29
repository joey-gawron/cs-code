using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>What the debugger is currently showing: the focused session, whether it is stopped, and the thread/frame the stop location points at.</summary>
/// <param name="Epoch">Increases on every stop, resume, invalidation and session switch: "is this a new stop?" is <c>Epoch</c> changed.</param>
/// <param name="LocationPending">Stopped, but the stack-derived stop location of THIS stop has not been published yet (frame id still unknown).</param>
public readonly record struct DebugFocus(IDebugSession? Session, DebugSessionState? State, int? ThreadId, int? FrameId, long Epoch, bool LocationPending)
{
    public bool IsStopped => Session is not null && State == DebugSessionState.Stopped;
    public bool HasFrame => IsStopped && FrameId is not null && ThreadId is not null;
}

/// <summary>
/// One panel's view of "which session/stop/frame is focused". Subscribes to the debug service and the active session, coalesces the
/// burst of events a single step produces into ONE callback on the UI thread (latest wins), and tags each stop with an epoch so a panel can
/// tell a new stop from a frame change. Every subscription is released by <see cref="Dispose"/>; events arriving afterwards are ignored.
/// </summary>
public sealed class DebugFocusTracker : IDisposable
{
    private static int s_ids;

    /// <summary>How long a stopped session may go without a fresh stop location before the existing one is accepted (the location event is
    /// suppressed when a stop lands on a byte-identical location).</summary>
    private static readonly TimeSpan PendingGrace = TimeSpan.FromMilliseconds(400);

    private readonly DebugPanelServices _s;
    private readonly Action<DebugFocus> _onChanged;
    private readonly string _key = "debug.focus." + Interlocked.Increment(ref s_ids);
    private readonly DisposableStore _subs = new();
    private readonly object _gate = new();
    private DisposableStore? _sessionSubs;
    private IDebugSession? _bound;
    private long _epoch;
    private long _locationEpoch = -1;
    private DebugFocus _last;
    private bool _delivered;
    private Timer? _graceTimer;
    private int _disposed;

    public DebugFocusTracker(DebugPanelServices services, Action<DebugFocus> onChanged)
    {
        _s = services;
        _onChanged = onChanged;
        var debug = services.Debug;
        _subs.Add(debug.ActiveSessionChanged.Subscribe(OnActiveSessionChanged));
        _subs.Add(debug.StopLocationChanged.Subscribe(OnStopLocation));
        _subs.Add(debug.StateChanged.Subscribe(_ => Schedule()));
        _subs.Add(debug.SessionTerminated.Subscribe(_ => Schedule()));
        Bind(debug.ActiveSession);
    }

    /// <summary>Delivers the current focus (now when called on the UI thread, otherwise on its next turn).</summary>
    public void Start()
    {
        if (_s.Ui.CheckAccess()) Flush();
        else Schedule();
    }

    private void OnActiveSessionChanged(IDebugSession? session)
    {
        Interlocked.Increment(ref _epoch);
        Bind(session);
        Schedule();
    }

    private void OnStopLocation(DebugStopLocation? location)
    {
        Volatile.Write(ref _locationEpoch, Volatile.Read(ref _epoch));
        Schedule();
    }

    private void Bind(IDebugSession? session)
    {
        DisposableStore? old;
        DisposableStore? fresh = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (ReferenceEquals(_bound, session)) return;
            old = _sessionSubs;
            _bound = session;
            if (session is not null)
            {
                fresh = new DisposableStore();
                fresh.Add(session.Stopped.Subscribe(_ => Bump()));
                fresh.Add(session.Continued.Subscribe(_ => Bump()));
                fresh.Add(session.Invalidated.Subscribe(_ => Bump()));
                fresh.Add(session.StateChanged.Subscribe(_ => Schedule()));
                fresh.Add(session.Terminated.Subscribe(_ => Schedule()));
            }
            _sessionSubs = fresh;
        }
        old?.Dispose();
    }

    private void Bump()
    {
        Interlocked.Increment(ref _epoch);
        Schedule();
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _s.Ui.PostCoalesced(_key, Flush);
    }

    private void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var session = _s.Debug.ActiveSession;
        var state = session?.State;
        var location = _s.Debug.StopLocation;
        var epoch = Volatile.Read(ref _epoch);
        var stopped = session is not null && state == DebugSessionState.Stopped;
        var locationMatches = stopped && location is not null && ReferenceEquals(location.Session, session);
        var fresh = locationMatches && Volatile.Read(ref _locationEpoch) >= epoch;
        var pending = stopped && !fresh;

        if (pending && locationMatches) ArmGraceTimer(epoch);

        var focus = new DebugFocus(
            session, state,
            fresh ? location!.ThreadId : stopped ? session!.FocusedThreadId : null,
            fresh ? location!.FrameId : null,
            epoch, pending);
        if (_delivered && focus == _last) return;
        _last = focus;
        _delivered = true;
        _onChanged(focus);
    }

    private void ArmGraceTimer(long epoch)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _graceTimer?.Dispose();
            _graceTimer = new Timer(_ =>
            {
                // Still the same stop and still no fresh location: the debug service suppressed the event because the location is identical.
                if (Volatile.Read(ref _epoch) != epoch) return;
                Volatile.Write(ref _locationEpoch, epoch);
                Schedule();
            }, null, PendingGrace, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisposableStore? sessionSubs;
        Timer? timer;
        lock (_gate)
        {
            sessionSubs = _sessionSubs;
            timer = _graceTimer;
            _sessionSubs = null;
            _graceTimer = null;
            _bound = null;
        }
        timer?.Dispose();
        sessionSubs?.Dispose();
        _subs.Dispose();
    }
}
