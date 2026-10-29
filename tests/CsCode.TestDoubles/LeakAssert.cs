using System.Runtime.CompilerServices;

namespace CsCode.TestDoubles;

/// <summary>
/// Memory-leak assertion helper. Pattern:
/// <code>
/// LeakAssert.NotRetained(() => { var doc = Open(); Close(doc); return [doc]; });
/// </code>
/// The factory runs in a separate non-inlined frame so the JIT cannot keep its locals alive; afterwards the GC is forced
/// repeatedly and every returned object must have been collected.
/// </summary>
public static class LeakAssert
{
    public static void NotRetained(Func<object[]> createAndRelease, int maxAttempts = 10)
    {
        var weak = Create(createAndRelease);
        for (var i = 0; i < maxAttempts && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = weak.Select((w, i) => (w, i)).Where(x => x.w.TryGetTarget(out _)).ToArray();
        if (alive.Length > 0)
        {
            var names = string.Join(", ", alive.Select(x => x.w.TryGetTarget(out var t) ? t.GetType().FullName : "?"));
            throw new LeakException($"{alive.Length} object(s) still reachable after {maxAttempts} full GCs: {names}");
        }
    }

    /// <summary>Async variant for code that needs awaiting inside the creation scope.</summary>
    public static async Task NotRetainedAsync(Func<Task<object[]>> createAndRelease, int maxAttempts = 10)
    {
        var weak = await CreateAsync(createAndRelease);
        for (var i = 0; i < maxAttempts && weak.Any(w => w.TryGetTarget(out _)); i++)
        {
            await Task.Yield();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var alive = weak.Where(w => w.TryGetTarget(out _)).ToArray();
        if (alive.Length > 0)
            throw new LeakException($"{alive.Length} object(s) still reachable after {maxAttempts} full GCs");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object>[] Create(Func<object[]> f) =>
        f().Select(o => new WeakReference<object>(o)).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<object>[]> CreateAsync(Func<Task<object[]>> f) =>
        (await f()).Select(o => new WeakReference<object>(o)).ToArray();
}

public sealed class LeakException(string message) : Exception(message);
