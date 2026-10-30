using System.Runtime.CompilerServices;

namespace CsCode.Profiling.Testing;

/// <summary>A memory assertion failed (leak, growth slope, resource count). Thrown by the toolkit's Assert helpers, so any test framework reports it.</summary>
public sealed class MemoryAssertionException(string message) : Exception(message);

/// <summary>Forced, complete garbage collection for measurements: twice with finalizers in between, so finalizable garbage is gone too.</summary>
public static class FullGc
{
    public static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>
    /// The live managed heap right after <see cref="Collect"/>: <see cref="GC.GetTotalMemory(bool)"/>. On macOS that figure is inflated (a 64 KB
    /// array kept per run read as 128 KB per run on Apple arm64, .NET 10), so there it is the last collection's heap size minus its fragmentation.
    /// </summary>
    public static long LiveBytes()
    {
        if (!OperatingSystem.IsMacOS()) return GC.GetTotalMemory(forceFullCollection: false);
        var info = GC.GetGCMemoryInfo();
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}

/// <summary>
/// Tracks instances of <typeparamref name="T"/> weakly and asserts how many survive a full collection: the "after N open/close cycles at most one
/// editor view is alive" kind of check. Instances are held through <see cref="WeakReference{T}"/> (the probe never keeps anything alive) and
/// de-duplicated through a <see cref="ConditionalWeakTable{TKey,TValue}"/>. Thread-safe.
/// <code>
/// var views = new LeakProbe&lt;EditorView&gt;();
/// for (var i = 0; i &lt; 20; i++) { var v = Open(); views.Track(v); Close(v); }
/// views.AssertAliveAtMost(0);
/// </code>
/// </summary>
public sealed class LeakProbe<T> where T : class
{
    private readonly object _gate = new();
    private readonly List<(WeakReference<T> Ref, string? Label)> _tracked = new();
    private readonly ConditionalWeakTable<T, object> _seen = new();

    public int TrackedCount { get { lock (_gate) return _tracked.Count; } }

    /// <summary>Starts tracking <paramref name="instance"/> (again tracking a known instance does nothing). Returns it for chaining.</summary>
    public T Track(T instance, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        lock (_gate)
        {
            if (_seen.TryGetValue(instance, out _)) return instance;
            _seen.Add(instance, label ?? "");
            _tracked.Add((new WeakReference<T>(instance), label));
        }
        return instance;
    }

    /// <summary>Collects (up to <paramref name="attempts"/> full GCs, stopping early when the count is low enough) and counts survivors.</summary>
    public int CountAlive(int attempts = 5, int stopAt = 0)
    {
        var alive = Alive();
        for (var i = 0; i < attempts && alive > stopAt; i++)
        {
            FullGc.Collect();
            alive = Alive();
        }
        return alive;
    }

    /// <summary>Labels (or type names) of the instances still alive, without collecting.</summary>
    public IReadOnlyList<string> AliveLabels()
    {
        lock (_gate)
            return _tracked.Where(t => t.Ref.TryGetTarget(out _)).Select(t => t.Label ?? (t.Ref.TryGetTarget(out var o) ? o.GetType().Name : "?")).ToArray();
    }

    /// <summary>Throws <see cref="MemoryAssertionException"/> when more than <paramref name="max"/> tracked instances survive full collections.</summary>
    public void AssertAliveAtMost(int max, string? because = null)
    {
        var alive = CountAlive(stopAt: max);
        if (alive <= max) return;
        throw new MemoryAssertionException($"{alive} of {TrackedCount} tracked {typeof(T).Name} instance(s) still alive after full GCs (allowed {max})" +
            (because is null ? "" : ": " + because) + ". Alive: " + string.Join(", ", AliveLabels().Take(20)));
    }

    private int Alive()
    {
        lock (_gate) return _tracked.Count(t => t.Ref.TryGetTarget(out _));
    }
}
