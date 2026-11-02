using CsCode.Core.Text;

namespace CsCode.Lsp.Client;

/// <summary>
/// Per-document synchronisation state for ONE server session. Two locks on purpose:
/// <see cref="PendingGate"/> is taken by the (UI/editing) thread that reports a change and is only ever held for a few instructions;
/// <see cref="Gate"/> serialises everything that talks to the server about this document (open / flush / request send) and is held by
/// Language-lane threads only — so typing never waits behind a slow serialisation.
/// </summary>
internal sealed class SyncedDocument(ITextDocument document, string uriString)
{
    public ITextDocument Document { get; } = document;
    public Uri Uri { get; } = document.Uri;
    public string UriString { get; } = uriString;

    /// <summary>Long lock: open / flush / "flush-then-send-request" sequences (lane threads only).</summary>
    public readonly object Gate = new();
    /// <summary>Short lock: guards the fields below (taken from the thread that edits).</summary>
    public readonly object PendingGate = new();

    // ---- guarded by PendingGate ----
    public List<TextContentChange>? Pending;
    public int LastSeenVersion;
    public int PendingVersion;
    public bool FlushPosted;
    public bool ForceFull;
    /// <summary>True once didOpen has been sent: only then do change events accumulate (earlier ones are covered by the opened text).</summary>
    public bool AcceptingChanges;
    public CancellationTokenSource? VersionCts;

    // ---- guarded by Gate ----
    public bool OpenOnServer;
    public int ServerVersion;
    public string ServerLanguageId = "";
    public bool Ignored;      // too large for this server
    public bool Detached;
    /// <summary>The last pull result id per diagnostic provider identifier ("" for a provider without one).</summary>
    public Dictionary<string, string?>? PullResultIds;
    public string? LastTraceText;

    /// <summary>Token cancelled whenever the document changes (in-flight requests for stale text are abandoned).</summary>
    public CancellationToken GetVersionToken()
    {
        lock (PendingGate) return (VersionCts ??= new CancellationTokenSource()).Token;
    }
}

/// <summary>A tiny bounded multi-producer mailbox of "latest value per key" with an overall cap (oldest key dropped when over).</summary>
internal sealed class LatestPerKeyMailbox<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _items = new();
    private readonly Queue<TKey> _order = new();
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);
    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Stores the latest value for the key. Returns true if the mailbox was empty before (caller should schedule a drain).</summary>
    public bool Put(TKey key, TValue value)
    {
        lock (_gate)
        {
            var wasEmpty = _items.Count == 0;
            if (!_items.ContainsKey(key))
            {
                while (_items.Count >= capacity && _order.Count > 0)
                {
                    var victim = _order.Dequeue();
                    if (_items.Remove(victim)) Interlocked.Increment(ref _dropped);
                }
                _order.Enqueue(key);
            }
            _items[key] = value;
            return wasEmpty;
        }
    }

    public List<(TKey Key, TValue Value)> Drain(int max = int.MaxValue)
    {
        var result = new List<(TKey, TValue)>();
        lock (_gate)
        {
            while (_order.Count > 0 && result.Count < max)
            {
                var k = _order.Dequeue();
                if (_items.Remove(k, out var v)) result.Add((k, v));
            }
        }
        return result;
    }

    public bool HasItems { get { lock (_gate) return _items.Count > 0; } }
}

/// <summary>Bounded FIFO that drops the OLDEST entries when full (log lines, progress). Counts what it dropped.</summary>
internal sealed class BoundedFifo<T>(int capacity)
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Returns true when the queue was empty (caller schedules a drain).</summary>
    public bool Enqueue(T item)
    {
        lock (_gate)
        {
            var wasEmpty = _items.Count == 0;
            if (_items.Count >= capacity)
            {
                _items.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _items.Enqueue(item);
            return wasEmpty;
        }
    }

    public List<T> Drain(int max)
    {
        var result = new List<T>();
        lock (_gate)
            while (_items.Count > 0 && result.Count < max) result.Add(_items.Dequeue());
        return result;
    }

    public bool HasItems { get { lock (_gate) return _items.Count > 0; } }
}
