using CsCode.Core.Diagnostics;
using CsCode.Profiling;
using Xunit;

namespace CsCode.MemoryTests.Monitor;

/// <summary>MUST 2: the pressure policy as a pure state machine on a manual clock.</summary>
public sealed class MemoryPressurePolicyTests
{
    private const long Mb = 1024 * 1024;
    private const long Budget = 1000 * Mb;

    [Theory]
    [InlineData(500, null)]
    [InlineData(699, null)]
    [InlineData(700, MemoryTrimLevel.Moderate)]
    [InlineData(899, MemoryTrimLevel.Moderate)]
    [InlineData(900, MemoryTrimLevel.Aggressive)]
    [InlineData(1500, MemoryTrimLevel.Aggressive)]
    public void Levels_follow_the_fraction_of_the_budget(long usedMb, MemoryTrimLevel? expected)
    {
        var policy = new MemoryPressurePolicy();
        Assert.Equal(expected, policy.Evaluate(0, usedMb * Mb, Budget).Level);
    }

    [Fact]
    public void No_budget_means_no_trim_whatever_the_usage()
    {
        var policy = new MemoryPressurePolicy();
        var d = policy.Evaluate(0, 100_000 * Mb, 0);
        Assert.Null(d.Level);
        Assert.Equal("no budget", d.Reason);
    }

    [Fact]
    public void A_level_that_fired_waits_for_the_minimum_interval_then_rearms_only_below_the_hysteresis_band()
    {
        var policy = new MemoryPressurePolicy();
        Assert.Equal(MemoryTrimLevel.Moderate, policy.Evaluate(0, 750 * Mb, Budget).Level);
        policy.OnTrimmed(0, MemoryTrimLevel.Moderate);
        Assert.Null(policy.Evaluate(10_000, 760 * Mb, Budget).Level); // too soon
        Assert.Equal("too soon after the last trim", policy.Evaluate(20_000, 760 * Mb, Budget).Reason);
        Assert.Null(policy.Evaluate(31_000, 760 * Mb, Budget).Level); // still above 70 %, already trimmed at this level: wait for the repeat interval
        Assert.Equal("already trimmed at this level", policy.Evaluate(60_000, 760 * Mb, Budget).Reason);
        Assert.Null(policy.Evaluate(61_000, 640 * Mb, Budget).Level); // below 65 %: re-armed
        Assert.Equal(MemoryTrimLevel.Moderate, policy.Evaluate(62_000, 710 * Mb, Budget).Level);
    }

    [Fact]
    public void Dipping_just_under_the_threshold_does_not_rearm_it()
    {
        var policy = new MemoryPressurePolicy();
        policy.OnTrimmed(0, MemoryTrimLevel.Moderate);
        Assert.Null(policy.Evaluate(40_000, 690 * Mb, Budget).Level); // 69 %: inside the 5 % band
        Assert.Null(policy.Evaluate(41_000, 720 * Mb, Budget).Level);
    }

    [Fact]
    public void A_level_above_the_last_trims_may_follow_after_the_shorter_escalation_interval()
    {
        var policy = new MemoryPressurePolicy();
        policy.OnTrimmed(0, MemoryTrimLevel.Moderate);
        Assert.Null(policy.Evaluate(5_000, 950 * Mb, Budget).Level);
        Assert.Equal(MemoryTrimLevel.Aggressive, policy.Evaluate(11_000, 950 * Mb, Budget).Level);
    }

    [Fact]
    public void Staying_above_a_threshold_repeats_the_trim_at_the_repeat_interval()
    {
        var policy = new MemoryPressurePolicy(new MemoryPolicyOptions { RepeatInterval = TimeSpan.FromMinutes(5) });
        policy.OnTrimmed(0, MemoryTrimLevel.Aggressive);
        Assert.Null(policy.Evaluate(299_000, 950 * Mb, Budget).Level);
        Assert.Equal(MemoryTrimLevel.Aggressive, policy.Evaluate(301_000, 950 * Mb, Budget).Level);
    }

    [Fact]
    public void Sustained_growth_above_seventy_percent_escalates_to_aggressive_but_one_spike_does_not()
    {
        var growing = new MemoryPressurePolicy();
        MemoryPressureDecision d = default;
        // 2 s samples for two minutes, +1 MB per sample (30 MB/min > 2 % of the budget per minute)
        for (var i = 0; i <= 60; i++) d = growing.Evaluate(i * 2000L, (650 + i) * Mb, Budget);
        Assert.True(d.SustainedGrowth);
        Assert.Equal(MemoryTrimLevel.Aggressive, d.Level);
        Assert.Contains("sustained growth", d.Reason);

        var spike = new MemoryPressurePolicy();
        for (var i = 0; i < 60; i++) d = spike.Evaluate(i * 2000L, 650 * Mb + (i % 2) * Mb, Budget);
        d = spike.Evaluate(120_000, 760 * Mb, Budget);
        Assert.False(d.SustainedGrowth);
        Assert.Equal(MemoryTrimLevel.Moderate, d.Level);
    }

    [Fact]
    public void Growth_is_judged_only_once_the_window_is_half_covered()
    {
        var policy = new MemoryPressurePolicy();
        MemoryPressureDecision d = default;
        for (var i = 0; i < 20; i++) d = policy.Evaluate(i * 100L, (700 + 10 * i) * Mb, Budget); // 2 s of steep growth
        Assert.False(d.SustainedGrowth);
        Assert.Null(policy.GrowthFit(1900));
    }

    [Fact]
    public void Without_a_budget_steady_growth_is_reported_for_the_log()
    {
        var policy = new MemoryPressurePolicy();
        MemoryPressureDecision d = default;
        for (var i = 0; i <= 60; i++) d = policy.Evaluate(i * 2000L, (300 + 2 * i) * Mb, 0); // 60 MB/min
        Assert.True(d.SustainedGrowth);
        Assert.Null(d.Level);
    }

    // From the desktop: "Memory is growing steadily: working set 472 MB" 62 s into a session, every session — the startup ramp, not a leak.
    [Fact]
    public void The_startup_ramp_is_not_logged_as_steady_growth_but_later_growth_is()
    {
        var policy = new MemoryPressurePolicy();
        MemoryPressureDecision decision = default;
        for (long t = 0; t <= 62_000; t += 2_000) decision = policy.Evaluate(t, 300 * Mb + t * 3 * Mb / 1000, 0); // ~180 MB a minute
        Assert.True(decision.SustainedGrowth);
        var options = new MemoryMonitorOptions();

        Assert.False(MemoryMonitor.ShouldLogGrowth(decision, sinceStartMs: 62_000, sinceLastLoggedMs: long.MaxValue / 2, options));
        Assert.True(MemoryMonitor.ShouldLogGrowth(decision, sinceStartMs: 4 * 60_000, sinceLastLoggedMs: long.MaxValue / 2, options));
        Assert.False(MemoryMonitor.ShouldLogGrowth(decision, sinceStartMs: 4 * 60_000, sinceLastLoggedMs: 60_000, options)); // once per window
        Assert.False(MemoryMonitor.ShouldLogGrowth(decision with { Level = MemoryTrimLevel.Aggressive }, 4 * 60_000, long.MaxValue / 2, options));
    }
}
