using System.Runtime.CompilerServices;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;

namespace CsCode.Remote.Client;

/// <summary>
/// Remote directory listings, kept briefly so the Explorer's expand/refresh storms cost one round trip per directory. Bounded (LRU,
/// <see cref="Capacity"/> directories) and <see cref="IMemoryTrimmable"/>; an entry dies after <see cref="TimeToLive"/>, when the remote watcher
/// reports a change in (or of) the directory, and when this client changes something in it. Thread-safe.
/// </summary>
public sealed class DirectoryCache : IMemoryTrimmable
{
    public const int Capacity = 2048;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly TimeProvider _time;

    private sealed record Entry(string Path, IReadOnlyList<DirectoryEntry> Entries, long Stamp);

    public DirectoryCache(TimeSpan? timeToLive = null, TimeProvider? time = null)
    {
        TimeToLive = timeToLive ?? TimeSpan.FromSeconds(3);
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan TimeToLive { get; }
    public string Name => "remote.directoryCache";
    public int Count { get { lock (_gate) return _map.Count; } }
    public long EstimatedBytes { get { lock (_gate) return _lru.Sum(e => 64L + e.Entries.Sum(x => 48L + x.Name.Length * 2L)); } }

    public bool TryGet(string path, out IReadOnlyList<DirectoryEntry> entries)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(Key(path), out var node))
            {
                if (_time.GetElapsedTime(node.Value.Stamp) <= TimeToLive)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    entries = node.Value.Entries;
                    return true;
                }
                _lru.Remove(node);
                _map.Remove(node.Value.Path);
            }
        }
        entries = [];
        return false;
    }

    public void Put(string path, IReadOnlyList<DirectoryEntry> entries)
    {
        var key = Key(path);
        lock (_gate)
        {
            if (_map.Remove(key, out var old)) _lru.Remove(old);
            _map[key] = _lru.AddFirst(new Entry(key, entries, _time.GetTimestamp()));
            while (_map.Count > Capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Path);
            }
        }
    }

    /// <summary>Drops the listing of <paramref name="path"/> itself and of its parent (a created/deleted entry changes the parent's listing).</summary>
    public void InvalidatePath(string path)
    {
        var key = Key(path);
        lock (_gate)
        {
            Remove(key);
            var slash = key.LastIndexOf('/');
            Remove(slash <= 0 ? "/" : key[..slash]);
        }
    }

    /// <summary>Drops everything at or below <paramref name="path"/> (a directory was deleted or renamed).</summary>
    public void InvalidateTree(string path)
    {
        var key = Key(path);
        lock (_gate)
            foreach (var k in _map.Keys.Where(k => k == key || k.StartsWith(key == "/" ? "/" : key + "/", StringComparison.Ordinal)).ToArray()) Remove(k);
        InvalidatePath(path);
    }

    public void Clear() { lock (_gate) { _map.Clear(); _lru.Clear(); } }

    public long Trim(MemoryTrimLevel level)
    {
        var before = EstimatedBytes;
        Clear(); // every level: listings are one round trip away
        return before;
    }

    private void Remove(string key)
    {
        if (_map.Remove(key, out var node)) _lru.Remove(node);
    }

    private static string Key(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}

/// <summary>Moves the rest of an async method off the caller's thread (UI) before any CPU work (payload encoding, copying chunks).</summary>
internal readonly struct PoolHop : ICriticalNotifyCompletion
{
    public PoolHop GetAwaiter() => this;
    public bool IsCompleted => false;
    public void GetResult() { }
    public void OnCompleted(Action continuation) => ThreadPool.QueueUserWorkItem(static c => ((Action)c!)(), continuation);
    public void UnsafeOnCompleted(Action continuation) => ThreadPool.UnsafeQueueUserWorkItem(static c => ((Action)c!)(), continuation);
}
