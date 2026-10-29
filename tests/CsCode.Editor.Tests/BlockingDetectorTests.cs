using Avalonia.Headless.XUnit;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>Proves the sync-over-async detector detects (so a clean run of the hostile-provider tests means something).</summary>
public class BlockingDetectorTests
{
    [AvaloniaFact]
    public void A_blocking_wait_on_the_ui_thread_is_recorded()
    {
        using var detector = new BlockingDetector();
        var never = new TaskCompletionSource();
        using var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            never.SetResult();
        });
#pragma warning disable xUnit1031 // deliberately blocking: this is the thing being detected
        never.Task.Wait();
#pragma warning restore xUnit1031
        Assert.True(detector.Count > 0, "Task.Wait on the UI thread must be reported");
    }

    [AvaloniaFact]
    public void Awaiting_is_not_a_blocking_wait()
    {
        using var detector = new BlockingDetector();
        var task = Task.Delay(20);
        Assert.False(task.IsCompleted && detector.Count > 0);
        Assert.Equal(0, detector.Count);
    }
}
