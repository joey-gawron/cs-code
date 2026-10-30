using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>What the debugger is currently showing: the focused session, whether it is stopped, and the thread/frame the stop location points at.</summary>
/// <param name="Epoch">Increases on every stop, resume, invalidation and session switch: "is this a new stop?" is <c>Epoch</c> changed.</param>
/// <param name="LocationPending">Stopped, but the stack-derived stop location of THIS stop has not been published yet (frame id still unknown).</param>
/// <param name="StopReason">Why the current stop happened (<c>breakpoint</c>, <c>step</c>, <c>exception</c>…), null while running.</param>
/// <param name="StopDescription">The adapter's one-line description of the stop (for an exception: its type and message), if it gave one.</param>
public readonly record struct DebugFocus(IDebugSession? Session, DebugSessionState? State, int? ThreadId, int? FrameId, long Epoch, bool LocationPending,
    string? StopReason = null, string? StopDescription = null, int? StopThreadId = null)
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
    private (string Reason, string? Description, int? ThreadId)? _stop;
    private DebugFocus _last;
    private bool _delivered;
    private Timer? _graceTimer;
    // `invalidated` handling: the epoch the latest invalidation produced (no stop/continue since), when it was seen, which epoch's
    // revalidation was started, and the live frame that replaces a location whose frame id died (adapters that renumber frames; valid for
    // that epoch only and cleared by the next Flush or Bind after it, so it never keeps an ended session reachable).
    private long _invalidatedEpoch = -1;
    private long _invalidatedAt;
    private long _revalidatedEpoch = -1;
    private (long Epoch, DebugStopLocation Location, int FrameId)? _liveFrame;
    // The bound session's IDebugSessionFrames.FrameGeneration at its latest stop, resume or invalidation (null: it does not tell, so every
    // invalidation is treated as one of stacks).
    private long? _frameGeneration;
    private readonly CancellationTokenSource _lifetime = new();
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
            _stop = null;
            _liveFrame = null;
            _frameGeneration = (session as IDebugSessionFrames)?.FrameGeneration;
            if (session is not null)
            {
                fresh = new DisposableStore();
                fresh.Add(session.Stopped.Subscribe(e =>
                {
                    lock (_gate)
                    {
                        _stop = (e.Reason, e.Text is { Length: > 0 } text ? (e.Description is { Length: > 0 } d ? d + ": " + text : text) : e.Description, e.ThreadId);
                        _frameGeneration = (session as IDebugSessionFrames)?.FrameGeneration;
                    }
                    Bump();
                }));
                fresh.Add(session.Continued.Subscribe(_ =>
                {
                    lock (_gate)
                    {
                        _stop = null;
                        _frameGeneration = (session as IDebugSessionFrames)?.FrameGeneration;
                    }
                    Bump();
                }));
                fresh.Add(session.Invalidated.Subscribe(_ => OnInvalidated(session)));
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

    /// <summary>
    /// The adapter asked for a refresh (DAP <c>invalidated</c>): values may have changed (a new epoch, so panels reload) and the session forgot
    /// its frame ids, while the stop location keeps the old one. The location is accepted again only once the stack was re-read (see
    /// <see cref="Revalidate"/>), not after the grace period, which would hand the panels a dead frame id.
    /// When only variables were invalidated (the session's <see cref="IDebugSessionFrames.FrameGeneration"/> did not change), the frame ids
    /// survive, so a location that was current stays current for the new epoch: the panels reload at once, without a <c>stackTrace</c>
    /// round-trip spent showing "Loading…".
    /// </summary>
    private void OnInvalidated(IDebugSession session)
    {
        lock (_gate)
        {
            var generation = (session as IDebugSessionFrames)?.FrameGeneration;
            var bound = ReferenceEquals(_bound, session); // an event that raced a session switch is a plain new epoch
            var framesSurvived = bound && generation is not null && generation == _frameGeneration;
            if (bound) _frameGeneration = generation;
            var epoch = Interlocked.Increment(ref _epoch);
            if (framesSurvived && Volatile.Read(ref _locationEpoch) >= epoch - 1)
            {
                AcceptLocation(epoch);
                if (_liveFrame is { } live && live.Epoch == epoch - 1) _liveFrame = live with { Epoch = epoch }; // still the live frame
            }
            else
            {
                _invalidatedEpoch = epoch;
                _invalidatedAt = Stopwatch.GetTimestamp();
            }
        }
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

        if (pending && locationMatches)
        {
            if (Volatile.Read(ref _invalidatedEpoch) == epoch) Revalidate(epoch, location!);
            else ArmGraceTimer(epoch);
        }

        (string Reason, string? Description, int? ThreadId)? stop;
        int? frameId = fresh ? location!.FrameId : null;
        lock (_gate)
        {
            stop = stopped ? _stop : null;
            if (_liveFrame is { } live)
            {
                // Only for the stop it was found for: dropped at the next stop, resume, invalidation or session change, because it holds the
                // location and through it the session (the runtime's tracker lives as long as the app).
                if (live.Epoch != epoch || !ReferenceEquals(live.Location.Session, session)) _liveFrame = null;
                else if (fresh && ReferenceEquals(live.Location, location)) frameId = live.FrameId;
            }
        }
        var focus = new DebugFocus(
            session, state,
            fresh ? location!.ThreadId : stopped ? session!.FocusedThreadId : null,
            frameId,
            epoch, pending, stop?.Reason, stop?.Description, stop?.ThreadId);
        if (_delivered && focus == _last) return;
        _last = focus;
        _delivered = true;
        _onChanged(focus);
    }

    /// <summary>Re-reads the stack once per invalidation (shared with the other panels), then accepts the location with a frame id that is live.</summary>
    private void Revalidate(long epoch, DebugStopLocation location)
    {
        long seenAt;
        lock (_gate)
        {
            if (_revalidatedEpoch == epoch) return;
            _revalidatedEpoch = epoch;
            seenAt = _invalidatedAt;
        }
        _ = RevalidateAsync(epoch, location, seenAt);
    }

    private async Task RevalidateAsync(long epoch, DebugStopLocation location, long seenAt)
    {
        int? live;
        try
        {
            // Through OnUi with the tracker's lifetime: a hung adapter cannot keep a disposed panel's tracker reachable.
            live = await _s.Ui.OnUi(new ValueTask<int?>(_s.FrameRevalidation.GetLiveFrameAsync(location, seenAt)), _lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _epoch) != epoch) return;
        // The stack was re-read, so the location's frame id is registered again, or replaced by the frame that is live now. When it could not
        // be read at all (live == null), the location is accepted as before and the panels report whatever the adapter answers.
        lock (_gate) _liveFrame = live is { } id && id != location.FrameId ? (epoch, location, id) : null;
        AcceptLocation(epoch);
        Schedule();
    }

    /// <summary>Marks the location as current for <paramref name="epoch"/> (never moving the mark back, so a newer stop's location stays fresh).</summary>
    private void AcceptLocation(long epoch)
    {
        var seen = Volatile.Read(ref _locationEpoch);
        while (seen < epoch)
        {
            var was = Interlocked.CompareExchange(ref _locationEpoch, epoch, seen);
            if (was == seen) return;
            seen = was;
        }
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
            _liveFrame = null;
        }
        timer?.Dispose();
        sessionSubs?.Dispose();
        _subs.Dispose();
        try { _lifetime.Cancel(); }
        catch (AggregateException) { }
        _lifetime.Dispose();
    }
}
