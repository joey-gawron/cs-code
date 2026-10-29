using System.Diagnostics;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// Finds the live frame of a stop location after the adapter sent <c>invalidated</c>. The session then forgets every frame id it handed
/// out, while the debug service keeps publishing the old location (and does not re-read the stack), so using its frame id fails as stale.
/// One <c>stackTrace</c> request re-registers the ids (adapters with stable ids) or shows the new ones (adapters that renumber them).
/// One instance is shared by every panel's <see cref="DebugFocusTracker"/>: trackers that saw the same invalidation share one request.
/// </summary>
public sealed class FrameRevalidator(IDebugFrameFocus? frameFocus)
{
    /// <summary>Frames read to find the focused one again (the top frame after a stop, a picked caller otherwise; deeper picks fall back to the top).</summary>
    public const int MaxFrames = 100;

    private readonly object _gate = new();
    // The latest request: its location (weakly: a location references its session), when it was sent, and its answer.
    private WeakReference<DebugStopLocation>? _location;
    private long _startedAt;
    private Task<int?>? _request;

    /// <summary>
    /// The frame id that is live now for <paramref name="location"/>'s frame, or null when the stack could not be read. A request sent after
    /// <paramref name="invalidatedAt"/> (when the caller saw the invalidation, <see cref="Stopwatch.GetTimestamp()"/>) is reused, so every
    /// panel that saw the same invalidation shares it. Completes off the UI thread.
    /// </summary>
    public Task<int?> GetLiveFrameAsync(DebugStopLocation location, long invalidatedAt)
    {
        lock (_gate)
        {
            if (_request is { } request && _startedAt >= invalidatedAt && _location is not null && _location.TryGetTarget(out var last) && ReferenceEquals(last, location))
                return request;
            _startedAt = Stopwatch.GetTimestamp();
            _location = new WeakReference<DebugStopLocation>(location);
            return _request = FetchAsync(location);
        }
    }

    private async Task<int?> FetchAsync(DebugStopLocation location)
    {
        IReadOnlyList<DebugStackFrame> frames;
        try
        {
            frames = await location.Session.GetStackTraceAsync(location.ThreadId, 0, MaxFrames).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // resumed, ended or failing: the caller falls back to the location as published
        }
        if (frames.Count == 0) return null;
        var live = Pick(frames, location);
        if (live != location.FrameId && frameFocus is { } focus)
        {
            // Renumbered: the service (editor stop marker, the REPL's frame) must follow too. If it declines, the panels still use the live id.
            try { await focus.SetFocusedFrameAsync(location.Session.Id, location.ThreadId, live).ConfigureAwait(false); }
            catch (Exception) { /* best effort */ }
        }
        return live;
    }

    /// <summary>The same frame by id (stable ids), else the frame at the same source position, else the first frame with source (what a stop shows).</summary>
    internal static int Pick(IReadOnlyList<DebugStackFrame> frames, DebugStopLocation location)
    {
        foreach (var f in frames)
            if (f.Id == location.FrameId) return f.Id;
        if (location.Uri is { } uri)
            foreach (var f in frames)
                if (f.Line == location.Line && f.Column == location.Column && SourceUri(f) == uri) return f.Id;
        return (frames.FirstOrDefault(f => SourceUri(f) is not null) ?? frames[0]).Id;
    }

    private static Uri? SourceUri(DebugStackFrame frame)
    {
        try { return frame.Source?.ToUri(); }
        catch (UriFormatException) { return null; }
    }
}
