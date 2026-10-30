namespace CsCode.Core.Debugging;

/// <summary>
/// Optional companion of <see cref="IDebugSession"/> (additive, like the interfaces in DebugExtras): when the frame ids it handed out die.
/// A DAP <c>invalidated</c> event of <c>variables</c> only keeps the frames of the stop, one of <c>stacks</c> (or <c>all</c>) kills them, and
/// a UI tells the two apart by comparing <see cref="FrameGeneration"/> with the value it saw at the previous stop or invalidation.
/// </summary>
public interface IDebugSessionFrames
{
    /// <summary>
    /// Changes whenever frame ids die (resume, new stop, an invalidation of stacks), not when only variables were invalidated. Readable from
    /// any thread, and already changed when <see cref="IDebugSession.Stopped"/>, <see cref="IDebugSession.Continued"/> or
    /// <see cref="IDebugSession.Invalidated"/> fire.
    /// </summary>
    long FrameGeneration { get; }
}
