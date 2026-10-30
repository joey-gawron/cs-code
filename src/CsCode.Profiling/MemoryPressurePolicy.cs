using CsCode.Core.Diagnostics;

namespace CsCode.Profiling;

/// <summary>Thresholds of <see cref="MemoryPressurePolicy"/>. The defaults are the brief's (70 % / 90 %) plus the anti-storm timings.</summary>
public sealed record MemoryPolicyOptions
{
    /// <summary>At or above this fraction of the budget the policy asks for a <see cref="MemoryTrimLevel.Moderate"/> trim.</summary>
    public double ModerateRatio { get; init; } = 0.70;
    /// <summary>At or above this fraction (or sustained growth above <see cref="ModerateRatio"/>) it asks for <see cref="MemoryTrimLevel.Aggressive"/>.</summary>
    public double AggressiveRatio { get; init; } = 0.90;
    /// <summary>A level fires again only after usage fell this far below its threshold (or after <see cref="RepeatInterval"/>).</summary>
    public double Hysteresis { get; init; } = 0.05;
    /// <summary>No two policy trims closer than this (trim storms: every trim is a blocking compacting collection).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A higher level than the last trim's may follow it after this shorter pause.</summary>
    public TimeSpan EscalationInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>While usage stays above a threshold that already fired, the trim is repeated at most this often (caches refill).</summary>
    public TimeSpan RepeatInterval { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>The window over which growth is judged sustained.</summary>
    public TimeSpan GrowthWindow { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Samples needed in the window before growth is judged (and they must span at least half of it).</summary>
    public int GrowthMinSamples { get; init; } = 10;
    /// <summary>How straight the growth must be (R² of the linear fit): steady growth, not one spike.</summary>
    public double GrowthMinRSquared { get; init; } = 0.8;
    /// <summary>Growth counts as sustained above this fraction of the budget per minute (with no budget: <see cref="GrowthBytesPerMinuteWithoutBudget"/>).</summary>
    public double GrowthBudgetFractionPerMinute { get; init; } = 0.02;
    public long GrowthBytesPerMinuteWithoutBudget { get; init; } = 32L * 1024 * 1024;
}

/// <summary>What the policy decided for one sample. <see cref="Level"/> null = do nothing.</summary>
public readonly record struct MemoryPressureDecision(MemoryTrimLevel? Level, double Ratio, bool SustainedGrowth, string Reason);

/// <summary>
/// The pressure policy (MUST 2) as a pure state machine over (time, used bytes, budget) samples, so it is tested with a manual clock:
/// below 70 % of the budget nothing; at 70 % a Moderate trim; at 90 %, or with sustained growth above 70 %, an Aggressive one. Hysteresis
/// (a level re-arms only after usage fell <see cref="MemoryPolicyOptions.Hysteresis"/> below its threshold) and minimum intervals keep it from
/// trimming in a loop. With no budget (0 = unlimited) it never trims but still reports sustained growth (for the log).
/// Not thread-safe: the monitor calls it from its sampling thread only.
/// </summary>
public sealed class MemoryPressurePolicy
{
    private const int Capacity = 512;
    private readonly MemoryPolicyOptions _options;
    private readonly long[] _times = new long[Capacity];
    private readonly double[] _used = new double[Capacity];
    private readonly double[] _x = new double[Capacity];
    private readonly double[] _y = new double[Capacity];
    private int _head, _count;
    private bool _moderateArmed = true, _aggressiveArmed = true;
    private long _lastTrimMs = long.MinValue / 2;
    private MemoryTrimLevel? _lastLevel;

    public MemoryPressurePolicy(MemoryPolicyOptions? options = null) => _options = options ?? new MemoryPolicyOptions();

    public MemoryPolicyOptions Options => _options;

    /// <summary>Records a sample and decides. <paramref name="nowMs"/> is any monotonic millisecond clock.</summary>
    public MemoryPressureDecision Evaluate(long nowMs, long usedBytes, long budgetBytes)
    {
        _times[_head] = nowMs;
        _used[_head] = usedBytes;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;

        var growing = SustainedGrowth(nowMs, budgetBytes);
        if (budgetBytes <= 0) return new MemoryPressureDecision(null, 0, growing, "no budget");
        var ratio = (double)usedBytes / budgetBytes;
        if (ratio < _options.AggressiveRatio - _options.Hysteresis) _aggressiveArmed = true;
        if (ratio < _options.ModerateRatio - _options.Hysteresis) _moderateArmed = true;

        MemoryTrimLevel desired;
        string reason;
        if (ratio >= _options.AggressiveRatio) (desired, reason) = (MemoryTrimLevel.Aggressive, "over 90 % of the budget");
        else if (ratio >= _options.ModerateRatio && growing) (desired, reason) = (MemoryTrimLevel.Aggressive, "sustained growth above 70 % of the budget");
        else if (ratio >= _options.ModerateRatio) (desired, reason) = (MemoryTrimLevel.Moderate, "over 70 % of the budget");
        else return new MemoryPressureDecision(null, ratio, growing, "under the budget");

        var sinceLast = nowMs - _lastTrimMs;
        var escalation = _lastLevel is { } last && desired > last;
        if (sinceLast < (escalation ? _options.EscalationInterval : _options.MinInterval).TotalMilliseconds)
            return new MemoryPressureDecision(null, ratio, growing, "too soon after the last trim");
        var armed = desired == MemoryTrimLevel.Aggressive ? _aggressiveArmed : _moderateArmed;
        if (!armed && sinceLast < _options.RepeatInterval.TotalMilliseconds)
            return new MemoryPressureDecision(null, ratio, growing, "already trimmed at this level");
        return new MemoryPressureDecision(desired, ratio, growing, reason);
    }

    /// <summary>Tells the policy a trim ran (policy-driven or not), which disarms its level and the levels below it.</summary>
    public void OnTrimmed(long nowMs, MemoryTrimLevel level)
    {
        _lastTrimMs = nowMs;
        _lastLevel = level;
        if (level >= MemoryTrimLevel.Moderate) _moderateArmed = false;
        if (level >= MemoryTrimLevel.Aggressive) _aggressiveArmed = false;
    }

    /// <summary>Slope (bytes per minute) of the samples in the growth window, when there are enough of them; else null.</summary>
    public LinearFit? GrowthFit(long nowMs)
    {
        var windowMs = (long)_options.GrowthWindow.TotalMilliseconds;
        var n = 0;
        long oldest = nowMs;
        for (var i = 0; i < _count; i++)
        {
            var idx = (_head - 1 - i + Capacity) % Capacity;
            if (nowMs - _times[idx] > windowMs) break;
            oldest = _times[idx];
            _x[n] = _times[idx] / 60_000.0; // minutes
            _y[n] = _used[idx];
            n++;
        }
        if (n < _options.GrowthMinSamples || nowMs - oldest < windowMs / 2) return null;
        return LinearRegression.Fit(_x.AsSpan(0, n), _y.AsSpan(0, n));
    }

    private bool SustainedGrowth(long nowMs, long budgetBytes)
    {
        if (GrowthFit(nowMs) is not { } fit) return false;
        var threshold = budgetBytes > 0 ? budgetBytes * _options.GrowthBudgetFractionPerMinute : _options.GrowthBytesPerMinuteWithoutBudget;
        return fit.Slope >= threshold && fit.RSquared >= _options.GrowthMinRSquared;
    }
}
