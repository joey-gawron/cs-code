namespace CsCode.Core.Debugging;

/// <summary>
/// Optional companion of <see cref="IDebugService"/> (additive, like the interfaces in DebugExtras): what the debug adapter of a type said it
/// supports the last time a session of that type ran in this app run. Lets a UI gate features such as hit-count breakpoints and logpoints
/// (netcoredbg has neither, js-debug has both) while no session is running.
/// </summary>
public interface IDebugKnownCapabilities
{
    /// <summary>Capabilities of the most recent initialized session of <paramref name="debugType"/>; null when none ran yet (unknown).</summary>
    DebugCapabilities? GetKnownCapabilities(string debugType);
}

/// <summary>The capabilities a breakpoint editor should honour right now, and the wording when one is missing.</summary>
public static class DebugCapabilityGate
{
    /// <summary>
    /// The active session's capabilities once it is initialized; else the last-known ones of the selected launch configuration's type
    /// (<see cref="IDebugKnownCapabilities"/>); null when unknown — then nothing should be gated (the session reports unsupported kinds itself).
    /// </summary>
    public static (DebugCapabilities Capabilities, string DebugType)? Current(IDebugService debug)
    {
        if (debug.ActiveSession is { State: DebugSessionState.Running or DebugSessionState.Stopped } session)
            return (session.Capabilities, session.Configuration.Type);
        if (debug is IDebugKnownCapabilities known && debug.Configurations.Selected?.Type is { Length: > 0 } type && known.GetKnownCapabilities(type) is { } caps)
            return (caps, type);
        return null;
    }

    /// <summary>Why a hit count cannot be set, or null when it can (or support is unknown).</summary>
    public static string? HitCountUnsupported(IDebugService debug) =>
        Current(debug) is { Capabilities.SupportsHitConditionalBreakpoints: false } c ? $"The '{c.DebugType}' debug adapter does not support hit-count breakpoints." : null;

    /// <summary>Why a logpoint cannot be set, or null when it can (or support is unknown).</summary>
    public static string? LogPointUnsupported(IDebugService debug) =>
        Current(debug) is { Capabilities.SupportsLogPoints: false } c ? $"The '{c.DebugType}' debug adapter does not support logpoints." : null;

    /// <summary>Why a condition cannot be set, or null when it can (or support is unknown).</summary>
    public static string? ConditionUnsupported(IDebugService debug) =>
        Current(debug) is { Capabilities.SupportsConditionalBreakpoints: false } c ? $"The '{c.DebugType}' debug adapter does not support conditional breakpoints." : null;
}
