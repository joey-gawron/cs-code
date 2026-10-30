using System.Diagnostics;
using CsCode.Sessions.Launcher;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Sessions.Tests;

public class DebouncedSaverTests
{
    [Fact]
    public async Task A_burst_of_changes_saves_once_at_most_the_delay_after_the_first()
    {
        var clock = new ManualTimeProvider();
        var saves = 0;
        using var saver = new DebouncedSaver(clock, TimeSpan.FromSeconds(1.5), _ => { saves++; return Task.CompletedTask; });

        saver.MarkDirty();
        clock.Advance(TimeSpan.FromSeconds(1));
        saver.MarkDirty(); // does not push the save out: continuous typing still saves every 1.5 s
        clock.Advance(TimeSpan.FromSeconds(0.5));
        await WaitFor(() => saves == 1);
        Assert.False(saver.IsDirty);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, saves); // nothing changed: no timer, no save
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task A_change_during_a_save_arms_the_next_save()
    {
        var clock = new ManualTimeProvider();
        var gate = new TaskCompletionSource();
        var saves = 0;
        using var saver = new DebouncedSaver(clock, TimeSpan.FromSeconds(1), async _ => { saves++; if (saves == 1) await gate.Task; });

        saver.MarkDirty();
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => saves == 1);
        saver.MarkDirty(); // while the first save is still running
        gate.SetResult();
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => saves == 2);
    }

    [Fact]
    public async Task A_failed_save_is_recorded_and_retried_by_the_next_flush()
    {
        var clock = new ManualTimeProvider();
        var attempts = 0;
        using var saver = new DebouncedSaver(clock, TimeSpan.FromSeconds(1), _ => ++attempts == 1 ? throw new IOException("disk full") : Task.CompletedTask);

        saver.MarkDirty();
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => saver.FailureCount == 1);
        Assert.IsType<IOException>(saver.LastError);
        Assert.True(saver.IsDirty);

        await saver.SaveAsync(onlyIfDirty: true);
        Assert.Equal(2, attempts);
        Assert.Null(saver.LastError);
        Assert.Equal(1, saver.SaveCount);
    }

    [Fact]
    public async Task SaveAsync_always_saves_and_does_nothing_after_dispose()
    {
        var saves = 0;
        var saver = new DebouncedSaver(TimeProvider.System, TimeSpan.FromSeconds(1), _ => { saves++; return Task.CompletedTask; });
        await saver.SaveAsync();
        Assert.Equal(1, saves);
        saver.Dispose();
        await saver.SaveAsync();
        saver.MarkDirty();
        Assert.Equal(1, saves);
    }

    [Fact]
    public void MarkDirty_is_cheap_on_the_calling_thread()
    {
        using var saver = new DebouncedSaver(TimeProvider.System, TimeSpan.FromSeconds(30), _ => Task.CompletedTask);
        saver.MarkDirty();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++) saver.MarkDirty();
        Assert.True(clock.ElapsedMilliseconds < 100, $"100k MarkDirty calls took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_disposed_saver_is_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var saver = new DebouncedSaver(TimeProvider.System, TimeSpan.FromMilliseconds(10), _ => Task.CompletedTask);
            saver.MarkDirty();
            saver.Dispose();
            return [saver];
        });
    }

    internal static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met in time");
            await Task.Delay(5);
        }
    }
}

public class HiddenSessionPolicyTests
{
    private static (HiddenSessionPolicy Policy, ManualTimeProvider Clock, List<string> Log) Create(HiddenPolicySettings? settings = null, Func<bool>? debugging = null)
    {
        var clock = new ManualTimeProvider();
        var log = new List<string>();
        var policy = new HiddenSessionPolicy(clock, () => settings ?? HiddenPolicySettings.Default, () => log.Add("trim"), () => log.Add("stop-servers"), debugging);
        return (policy, clock, log);
    }

    [Fact]
    public void A_hidden_session_is_trimmed_then_its_servers_stop()
    {
        var (policy, clock, log) = Create();
        policy.OnHidden();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(log);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(["trim"], log);
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(["trim", "stop-servers"], log);
        Assert.Equal((true, true), policy.OnShown());
        Assert.Equal(0, clock.ActiveTimers); // nothing periodic while shown or after the work is done
    }

    [Fact]
    public void Showing_before_the_deadline_cancels_everything()
    {
        var (policy, clock, log) = Create();
        policy.OnHidden();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal((false, false), policy.OnShown());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(log);
    }

    [Fact]
    public void Idle_timeout_zero_or_null_settings()
    {
        var (never, clock, log) = Create(new HiddenPolicySettings(TimeSpan.Zero, null));
        never.OnHidden();
        clock.Advance(TimeSpan.Zero);
        Assert.Equal(["trim"], log);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(["trim"], log); // 0 = never stop servers
    }

    [Fact]
    public void A_debugging_session_keeps_its_servers()
    {
        var debugging = true;
        var (policy, clock, log) = Create(debugging: () => debugging);
        policy.OnHidden();
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(["trim"], log);
        debugging = false;
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(["trim", "stop-servers"], log);
    }

    [Fact]
    public void A_disposed_policy_is_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var policy = new HiddenSessionPolicy(TimeProvider.System, () => HiddenPolicySettings.Default, () => { }, () => { });
            policy.OnHidden();
            policy.Dispose();
            return [policy];
        });
    }

    [Fact]
    public void Hiding_twice_does_not_rearm_and_dispose_stops_the_timers()
    {
        var (policy, clock, log) = Create();
        policy.OnHidden();
        clock.Advance(TimeSpan.FromSeconds(20));
        policy.OnHidden();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(["trim"], log);
        policy.Dispose();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(["trim"], log);
        Assert.Equal(0, clock.ActiveTimers);
    }
}

public class LaunchSettingsTests
{
    [Fact]
    public void Settings_are_read_from_flat_or_nested_keys_with_comments()
    {
        using var dir = new TempDirectory("cscode-ls-");
        dir.WriteFile("settings.json", """
            // user settings
            {
              "cscode.sessions.default": "work",
              "cscode.background.warmSpare": false,
              "cscode": { "background": { "maxResidentSessions": 5, "restoreOnLogin": true } },
            }
            """);
        var s = LaunchSettings.Read(dir.Path);
        Assert.Equal(("work", false, 5, true), (s.DefaultSession, s.WarmSpare, s.MaxResidentSessions, s.RestoreOnLogin));
    }

    [Fact]
    public void Missing_or_broken_settings_give_the_defaults()
    {
        using var dir = new TempDirectory("cscode-ls-");
        Assert.Equal(new LaunchSettings(), LaunchSettings.Read(dir.Path));
        dir.WriteFile("settings.json", "{ not json");
        Assert.Equal(new LaunchSettings(), LaunchSettings.Read(dir.Path));
        dir.WriteFile("settings.json", """{ "cscode.background.maxResidentSessions": "three", "cscode.background.warmSpare": 1 }""");
        Assert.Equal(new LaunchSettings(), LaunchSettings.Read(dir.Path));
    }
}

public class SerialLaneTests
{
    [Fact]
    public async Task Work_runs_in_order_on_the_lane_thread_and_the_caller_only_enqueues()
    {
        using var lane = new SerialLane("test lane");
        var order = new List<int>();
        var threads = new HashSet<int>();
        var gate = new ManualResetEventSlim();
        var clock = Stopwatch.StartNew();
        var first = lane.RunAsync(() => { gate.Wait(); lock (order) order.Add(1); threads.Add(Environment.CurrentManagedThreadId); });
        var second = lane.RunAsync(() => { lock (order) order.Add(2); threads.Add(Environment.CurrentManagedThreadId); });
        Assert.True(clock.ElapsedMilliseconds < 50, "enqueueing must not wait for the work");
        gate.Set();
        await Task.WhenAll(first, second);
        Assert.Equal([1, 2], order);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
        Assert.Single(threads);
        Assert.Equal(42, await lane.RunAsync(() => 42));
    }

    [Fact]
    public async Task Exceptions_reach_the_caller_and_the_lane_keeps_running()
    {
        using var lane = new SerialLane("test lane");
        await Assert.ThrowsAsync<IOException>(() => lane.RunAsync(() => throw new IOException("x")));
        Assert.Equal(1, await lane.RunAsync(() => 1));
    }

    [Fact]
    public void A_disposed_lane_is_not_retained_and_its_thread_ends()
    {
        Thread? thread = null;
        LeakAssert.NotRetained(() =>
        {
            var lane = new SerialLane("leak lane");
            lane.RunAsync(() => thread = Thread.CurrentThread).GetAwaiter().GetResult();
            lane.Dispose();
            return [lane];
        });
        Assert.False(thread!.IsAlive);
    }

    [Fact]
    public async Task After_dispose_new_work_fails_fast()
    {
        var lane = new SerialLane("test lane");
        lane.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lane.RunAsync(() => { }));
    }
}
