namespace CsCode.Platform;

/// <summary>
/// Tunables for the Platform services. Defaults are the production values; tests shrink the timings.
/// Register a configured instance with <c>AddCsCodePlatform(configure)</c>.
/// </summary>
public sealed class PlatformOptions
{
    /// <summary>Upper bound of dedicated IO-lane threads (file system, watchers, workspace walks).</summary>
    public int IoThreads { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 4);

    /// <summary>Quiet period after the last raw file event before a coalesced batch is delivered.</summary>
    public TimeSpan WatchDebounce { get; set; } = TimeSpan.FromMilliseconds(75);

    /// <summary>A continuously busy watch still delivers at least this often (prevents starvation under event floods).</summary>
    public TimeSpan WatchMaxDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Interval of the coarse polling fallback used when the OS watch limit is exhausted.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Settings/keybindings reload is delayed by this much after a file event so half-written files are not parsed.</summary>
    public TimeSpan ConfigReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>A started chord (e.g. Ctrl+K) is abandoned after this long without the next stroke.</summary>
    public TimeSpan ChordTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>State storage coalesces writes for this long after the last <c>Set</c>.</summary>
    public TimeSpan StateFlushDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Clock used for chord timeouts (tests substitute a manual one).</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
