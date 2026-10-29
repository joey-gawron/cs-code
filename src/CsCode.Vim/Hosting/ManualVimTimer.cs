using CsCode.Core.Lifecycle;

namespace CsCode.Vim;

/// <summary>Deterministic <see cref="IVimTimer"/>: time only moves when <see cref="Advance"/> is called. Used by tests; hosts supply a UI-thread timer.</summary>
public sealed class ManualVimTimer : IVimTimer
{
    private sealed class Entry(TimeSpan due, Action callback)
    {
        public TimeSpan Due { get; } = due;
        public Action? Callback { get; set; } = callback;
    }

    private readonly List<Entry> _entries = [];
    private TimeSpan _now;

    public TimeSpan Now => _now;
    public int PendingCount => _entries.Count(e => e.Callback is not null);

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new Entry(_now + delay, callback);
        _entries.Add(entry);
        return Disposable.Create(() => entry.Callback = null);
    }

    /// <summary>Moves the clock forward and runs every callback that became due, in order.</summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            Entry? next = null;
            foreach (var e in _entries)
                if (e.Callback is not null && e.Due <= target && (next is null || e.Due < next.Due)) next = e;
            if (next is null) break;
            _now = next.Due;
            var cb = next.Callback;
            next.Callback = null;
            cb?.Invoke();
        }
        _now = target;
        _entries.RemoveAll(e => e.Callback is null);
    }
}

/// <summary>Real-time <see cref="IVimTimer"/> on a <see cref="System.Threading.Timer"/>; the callback is marshalled through <paramref name="post"/> (e.g. the UI dispatcher).</summary>
public sealed class ThreadingVimTimer(Action<Action> post) : IVimTimer
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var cancelled = false;
        var timer = new Timer(_ => post(() => { if (!Volatile.Read(ref cancelled)) callback(); }), null, delay, Timeout.InfiniteTimeSpan);
        return Disposable.Create(() => { Volatile.Write(ref cancelled, true); timer.Dispose(); });
    }
}
