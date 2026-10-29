using CsCode.Lsp.Runtime;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>Shared test helpers: every await in a test is bounded by a timeout AND the xUnit test cancellation token.</summary>
public static class TestHelpers
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static Task Within(this Task task, TimeSpan? timeout = null) =>
        task.WaitAsync(timeout ?? DefaultTimeout, TestContext.Current.CancellationToken);

    public static Task<T> Within<T>(this Task<T> task, TimeSpan? timeout = null) =>
        task.WaitAsync(timeout ?? DefaultTimeout, TestContext.Current.CancellationToken);

    public static Task<T> Within<T>(this ValueTask<T> task, TimeSpan? timeout = null) =>
        task.AsTask().WaitAsync(timeout ?? DefaultTimeout, TestContext.Current.CancellationToken);

    public static Task Within(this ValueTask task, TimeSpan? timeout = null) =>
        task.AsTask().WaitAsync(timeout ?? DefaultTimeout, TestContext.Current.CancellationToken);

    /// <summary>Text of an output channel (empty if it was never created).</summary>
    public static string Text(this CsCode.Core.Platform.IOutputService output, string channel) =>
        output.ChannelNames.Contains(channel) ? output.GetOrCreate(channel).GetText() : "";

    /// <summary>RunAsync bound to the xUnit test cancellation token.</summary>
    public static ValueTask<T?> Run<T>(this LanguageWorkQueue q, LanePriority priority, WorkKey key, Func<WorkContext, ValueTask<T?>> work) =>
        q.RunAsync(priority, key, work, TestContext.Current.CancellationToken);

    /// <summary>Same, with a failure description computed at failure time (so it can include the state that never converged).</summary>
    public static async Task EventuallyAsync(Func<bool> condition, Func<string> describe, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + describe());
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds (tests of asynchronous effects), failing with <paramref name="what"/> on timeout.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Occupies every Language-lane thread until released, so backlogs (coalescing, shedding) can be observed deterministically.</summary>
public sealed class LaneBlock : IDisposable
{
    private readonly ManualResetEventSlim _gate = new(false);

    public LaneBlock(LanguageWorkQueue q)
    {
        var entered = new CountdownEvent(q.ThreadCount);
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < q.ThreadCount; i++) q.Post(LanePriority.Interactive, () => { entered.Signal(); _gate.Wait(ct); }, exempt: true);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), ct), "lane threads did not pick up the blockers");
    }

    public void Release() => _gate.Set();
    public void Dispose() => _gate.Set();
}
