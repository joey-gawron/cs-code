using System.Diagnostics.CodeAnalysis;
using CsCode.Core.Debugging;

namespace CsCode.Dap.Session;

/// <summary>Tiny weight-bounded LRU (weight = number of items a cached value holds), not thread-safe: lane-affine.</summary>
internal sealed class BoundedLru<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, int Weight)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value, int Weight)> _order = new();
    private readonly int _maxWeight;
    private readonly Func<TValue, int> _weigh;
    private int _weight;

    public BoundedLru(int maxWeight, Func<TValue, int> weigh)
    {
        _maxWeight = maxWeight;
        _weigh = weigh;
    }

    public int Count => _map.Count;
    public int Weight => _weight;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var w = _weigh(value) + 1;
        if (w > _maxWeight / 2) return; // one huge page must not evict the whole cache
        if (_map.Remove(key, out var old))
        {
            _order.Remove(old);
            _weight -= old.Value.Weight;
        }
        _map[key] = _order.AddFirst((key, value, w));
        _weight += w;
        while (_weight > _maxWeight && _order.Last is { } last)
        {
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
            _weight -= last.Value.Weight;
        }
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
        _weight = 0;
    }
}

/// <summary>
/// DAP <c>variablesReference</c>s (and frame ids) are only valid while the debuggee stays stopped. This tracks which handles were
/// issued during the CURRENT stop, remembers the frames handed out (so a frame picked anywhere in a paged call stack can be found again
/// without re-asking the adapter), caches variable pages for cheap re-expansion, and forgets everything on resume/step/invalidated.
/// All of it is bounded: at most <c>maxCachedVariables</c> cached items, <c>maxTrackedHandles</c> remembered handles and
/// <c>maxTrackedFrames</c> remembered frames (beyond that we stop tracking and stop rejecting, never grow).
/// </summary>
internal sealed class VariableHandleCache(int maxCachedVariables = 20_000, int maxTrackedHandles = 100_000, int maxTrackedFrames = 10_000)
{
    internal readonly record struct VariablesKey(int Reference, string? Filter, int? Start, int? Count);

    private readonly HashSet<int> _handles = new();
    private readonly Dictionary<int, DebugStackFrame> _frames = new();
    private readonly BoundedLru<VariablesKey, IReadOnlyList<DebugVariable>> _pages = new(maxCachedVariables, v => v.Count);
    private readonly BoundedLru<int, IReadOnlyList<DebugScope>> _scopes = new(2_000, v => v.Count);
    private bool _overflowed, _framesOverflowed;

    /// <summary>Changes whenever variable data dies (every invalidation): a response requested under an older generation is stale.</summary>
    public long Generation { get; private set; }
    /// <summary>Changes only when frame ids die (resume, new stop, an invalidation of stacks), not when only variables were invalidated.</summary>
    public long FrameGeneration { get; private set; }
    public int TrackedHandles => _handles.Count;
    public int CachedPages => _pages.Count;

    public void Invalidate()
    {
        FrameGeneration++;
        _frames.Clear();
        _framesOverflowed = false;
        _frameRuns.Clear();
        _frameRunTotal = 0;
        InvalidateVariables();
    }

    /// <summary>One thread's frames as read from the top during the current stop, for an adapter that renumbers frames on every request.</summary>
    internal sealed class FrameRun
    {
        public List<DebugStackFrame> Frames { get; } = new();
        /// <summary>The adapter gave fewer frames than asked for: there are no more.</summary>
        public bool Complete { get; set; }
    }

    private readonly Dictionary<int, FrameRun> _frameRuns = new();
    private int _frameRunTotal;

    public FrameRun FrameRunOf(int threadId)
    {
        if (!_frameRuns.TryGetValue(threadId, out var run)) _frameRuns[threadId] = run = new FrameRun();
        return run;
    }

    /// <summary>Whether <paramref name="count"/> more frames fit (all threads' runs together hold at most as many frames as the registry tracks).</summary>
    public bool CanKeepFrames(int count) => _frameRunTotal + count <= maxTrackedFrames;

    public void AppendFrames(FrameRun run, IEnumerable<DebugStackFrame> frames)
    {
        foreach (var frame in frames)
        {
            run.Frames.Add(frame);
            _frameRunTotal++;
        }
    }

    /// <summary>
    /// Variable data only (DAP <c>invalidated</c> with area <c>variables</c>): every variablesReference, cached page and scope list dies, while
    /// the stack frames of this stop stay valid (the spec invalidates them only for <c>stacks</c>/<c>all</c>).
    /// </summary>
    public void InvalidateVariables()
    {
        Generation++;
        _handles.Clear();
        _pages.Clear();
        _allChildren = null;
        _scopes.Clear();
        _overflowed = false;
    }

    /// <summary>Only the cached pages (e.g. after setVariable changed a value); handles stay valid.</summary>
    public void InvalidatePages()
    {
        _pages.Clear();
        _allChildren = null;
    }

    private (int Reference, string? Filter, IReadOnlyList<DebugVariable> Children)? _allChildren;

    /// <summary>
    /// Every child of <paramref name="reference"/> from an adapter that ignores paging: in the page cache when they fit, and the latest such list
    /// also in a slot of its own, because a list too big for the page cache is still paged by the UI a hundred rows at a time and must not be
    /// fetched again for each page. Bounded: one list (the session keeps at most its per-request cap of children).
    /// </summary>
    /// <param name="filter">The filter the list was asked with ("indexed" | "named" | null for both): a detected paging-ignoring adapter may honour it.</param>
    public void SetAllChildren(int reference, string? filter, IReadOnlyList<DebugVariable> children)
    {
        _pages.Set(new VariablesKey(reference, filter, null, null), children);
        _allChildren = (reference, filter, children);
    }

    public bool TryGetAllChildren(int reference, string? filter, [NotNullWhen(true)] out IReadOnlyList<DebugVariable>? children)
    {
        if (_allChildren is { } slot && slot.Reference == reference && slot.Filter == filter)
        {
            children = slot.Children;
            return true;
        }
        return _pages.TryGet(new VariablesKey(reference, filter, null, null), out children);
    }

    public void RegisterHandle(int reference)
    {
        if (reference <= 0) return;
        if (_handles.Count >= maxTrackedHandles) { _overflowed = true; return; }
        _handles.Add(reference);
    }

    public void RegisterFrame(DebugStackFrame frame)
    {
        if (_frames.Count >= maxTrackedFrames && !_frames.ContainsKey(frame.Id)) { _framesOverflowed = true; return; }
        _frames[frame.Id] = frame;
    }

    /// <summary>0 is "no children". Unknown handles are rejected unless tracking overflowed (then we can't know).</summary>
    public bool IsValidHandle(int reference) => reference <= 0 || _overflowed || _handles.Contains(reference);

    public bool IsValidFrame(int frameId) => _framesOverflowed || _frames.ContainsKey(frameId);

    /// <summary>A frame handed out during the current stop (any thread, any page). False for a stale id and for one handed out after the cap.</summary>
    public bool TryGetFrame(int frameId, [MaybeNullWhen(false)] out DebugStackFrame frame) => _frames.TryGetValue(frameId, out frame);

    public bool TryGetPage(VariablesKey key, out IReadOnlyList<DebugVariable> page) => _pages.TryGet(key, out page!);
    public void SetPage(VariablesKey key, IReadOnlyList<DebugVariable> page) => _pages.Set(key, page);
    public bool TryGetScopes(int frameId, out IReadOnlyList<DebugScope> scopes) => _scopes.TryGet(frameId, out scopes!);
    public void SetScopes(int frameId, IReadOnlyList<DebugScope> scopes) => _scopes.Set(frameId, scopes);
}
