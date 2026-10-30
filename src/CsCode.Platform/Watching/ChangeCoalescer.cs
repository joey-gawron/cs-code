using CsCode.Core.Platform;

namespace CsCode.Platform.Watching;

/// <summary>
/// What a backend (or the watcher's own scan) reports. <see cref="Announced"/>: found by the scan of a directory that appeared, or was re-armed,
/// unseen — a creation, also after a Changed in the same window (the new watch saw a write to a file nobody had announced yet).
/// </summary>
internal enum RawKind { Created, Changed, Deleted, Renamed, Announced }

/// <summary>
/// Merges a burst of raw file events into the minimal set of changes, keyed by path:
/// Created+Changed → Created · Created+Deleted → nothing · Changed+Changed → Changed · Changed+Deleted → Deleted ·
/// Deleted+Created → Changed (the file was replaced) · Renamed(a→b) → Deleted(a) + Created(b) · Announced is Created, and turns a Changed into Created.
/// When a directory is deleted, events for everything below it in the same batch are dropped (the parent's deletion says it all).
/// Bounded: past <see cref="MaxPaths"/> distinct paths it degrades to a single "something changed, rescan" marker instead of growing.
/// Not thread-safe; the owner locks.
/// </summary>
internal sealed class ChangeCoalescer
{
    public const int MaxPaths = 100_000;

    private readonly Dictionary<string, FileChangeKind> _pending = new(StringComparer.Ordinal);
    private bool _overflow;

    public int Count => _pending.Count;
    public bool IsEmpty => _pending.Count == 0 && !_overflow;
    public bool Overflowed => _overflow;

    public void Add(RawKind kind, string path, string? oldPath = null)
    {
        if (_overflow) return;
        switch (kind)
        {
            case RawKind.Created: Apply(path, FileChangeKind.Created); break;
            case RawKind.Changed: Apply(path, FileChangeKind.Changed); break;
            case RawKind.Deleted: Apply(path, FileChangeKind.Deleted); break;
            case RawKind.Renamed:
                if (oldPath is not null) Apply(oldPath, FileChangeKind.Deleted);
                Apply(path, FileChangeKind.Created);
                break;
            case RawKind.Announced:
                if (_pending.TryGetValue(path, out var prev) && prev == FileChangeKind.Changed) _pending[path] = FileChangeKind.Created;
                else Apply(path, FileChangeKind.Created);
                break;
        }
    }

    private void Apply(string path, FileChangeKind next)
    {
        if (!_pending.TryGetValue(path, out var prev))
        {
            if (_pending.Count >= MaxPaths) { _pending.Clear(); _overflow = true; return; }
            _pending[path] = next;
            return;
        }

        switch (prev, next)
        {
            case (FileChangeKind.Created, FileChangeKind.Changed):
            case (FileChangeKind.Created, FileChangeKind.Created):
                break;                                              // still a creation
            case (FileChangeKind.Created, FileChangeKind.Deleted):
                _pending.Remove(path);                              // came and went within the window
                break;
            case (FileChangeKind.Changed, FileChangeKind.Changed):
            case (FileChangeKind.Changed, FileChangeKind.Created):
                _pending[path] = FileChangeKind.Changed;
                break;
            case (FileChangeKind.Changed, FileChangeKind.Deleted):
            case (FileChangeKind.Deleted, FileChangeKind.Deleted):
                _pending[path] = FileChangeKind.Deleted;
                break;
            case (FileChangeKind.Deleted, FileChangeKind.Created):
            case (FileChangeKind.Deleted, FileChangeKind.Changed):
                _pending[path] = FileChangeKind.Changed;            // delete + create = replaced
                break;
        }
    }

    /// <summary>Takes everything accumulated so far (sorted by path, descendants of deleted directories removed) and resets.</summary>
    public (List<KeyValuePair<string, FileChangeKind>> Changes, bool Overflowed) Drain()
    {
        var overflowed = _overflow;
        var list = new List<KeyValuePair<string, FileChangeKind>>(_pending);
        _pending.Clear();
        _overflow = false;
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in list) if (kv.Value == FileChangeKind.Deleted) deleted.Add(kv.Key);
        if (deleted.Count > 0)
        {
            list.RemoveAll(kv =>
            {
                for (var i = kv.Key.LastIndexOf('/'); i > 0; i = kv.Key.LastIndexOf('/', i - 1))
                    if (deleted.Contains(kv.Key[..i])) return true;
                return false;
            });
        }
        return (list, overflowed);
    }
}
