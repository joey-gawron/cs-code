namespace CsCode.Profiling;

/// <summary>
/// Timings of <see cref="MemoryMonitor"/> (DI: register an instance before <c>AddCsCodeProfiling</c> to override; tests shorten them). The user-facing
/// knobs are settings: <c>cscode.memory.budgetMb</c>, <c>cscode.memory.trimOnIdle</c>, <c>cscode.memory.idleTrimSeconds</c>,
/// <c>cscode.memory.logIntervalSeconds</c>.
/// </summary>
public sealed record MemoryMonitorOptions
{
    /// <summary>Sampling period while the user is active.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Sampling period once the user has been idle for <see cref="SlowSamplingAfter"/> (adaptive: an idle editor wakes up less).</summary>
    public TimeSpan IdleSampleInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SlowSamplingAfter { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Idle time before the idle trim when <c>cscode.memory.idleTrimSeconds</c> is not set.</summary>
    public TimeSpan IdleTrimAfter { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>A child above its cap for this many consecutive samples gets its over-budget callback (one sample under Aggressive pressure).</summary>
    public int ChildOverBudgetSamples { get; init; } = 2;
    /// <summary>The over-budget callback of a child runs at most this often.</summary>
    public TimeSpan ChildCallbackCooldown { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// How often a tracked child's descendants are looked up again (Linux): a child's memory is its whole process tree (tsserver under
    /// typescript-language-server, a debuggee under its adapter); the members' resident sets are read on every sample, the tree only this often.
    /// </summary>
    public TimeSpan ChildTreeRescanInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Sustained growth is not logged this soon after start: opening a folder, loading grammars and starting language servers grow the
    /// working set steadily by design, and the warning used to fire about a minute into every session.
    /// </summary>
    public TimeSpan GrowthLogGrace { get; init; } = TimeSpan.FromMinutes(3);
    public MemoryPolicyOptions Policy { get; init; } = new();
}

/// <summary>
/// User-activity signal for the idle trim (MUST 2): the shell calls <see cref="NotifyActivity"/> on input (keys, text, pointer). Costs one timestamp
/// write per call, so it can sit on the input path. Implemented by <see cref="MemoryMonitor"/>; resolve it from DI.
/// </summary>
public interface IUserActivityMonitor
{
    void NotifyActivity();
    /// <summary>Time since the last <see cref="NotifyActivity"/> (since the monitor was created when there was none).</summary>
    TimeSpan IdleTime { get; }
}
