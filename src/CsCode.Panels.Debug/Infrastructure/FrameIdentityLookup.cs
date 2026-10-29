using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// Which function a focused stack frame is in, for change highlighting: Locals and Autos compare what a frame shows with what the SAME frame
/// showed at the previous stop, whatever variables appeared or disappeared in between (Node.js 24 hides a <c>const</c> until it is
/// initialised, js-debug adds a "Return value" after a step, block scopes come and go). The variable names cannot tell a step within one
/// function from a step into another one, and frame ids are renumbered from stop to stop, so the identity is the frame's session, thread,
/// name and source, read with one <c>stackTrace</c> request per stop location that every panel shares.
/// <para>Only plain values are kept (strings, the answer's task): nothing here keeps a session or a view-model reachable.</para>
/// </summary>
public sealed class FrameIdentityLookup
{
    /// <summary>Frames read to find the focused one (the top frame after a stop, a picked caller otherwise); a deeper pick has no identity.</summary>
    public const int MaxFrames = 100;

    private readonly object _gate = new();
    private Key? _key;
    private Task<string?>? _request;
    private string? _identity;

    /// <summary>
    /// A stop location, value for value. Two stops at an equal location are at the same place of the same function (a loop), so they share
    /// the identity; any other stop asks again.
    /// </summary>
    private readonly record struct Key(string SessionId, int ThreadId, int FrameId, string? Uri, int Line, int Column);

    /// <summary>
    /// The identity of <paramref name="frameId"/> (null when the stack cannot be read or the frame is not among its first
    /// <see cref="MaxFrames"/>). <paramref name="location"/> is the debugger's current stop location: it describes the focused frame, so it
    /// keys the shared request. Never throws; completes off the UI thread.
    /// </summary>
    public Task<string?> GetAsync(IDebugSession session, int threadId, int frameId, DebugStopLocation? location)
    {
        if (location is null || !ReferenceEquals(location.Session, session) || location.ThreadId != threadId)
            return FetchAsync(session, threadId, frameId, null); // nothing describes this frame: ask, and share nothing
        var key = new Key(session.Id, threadId, frameId, location.Uri?.AbsoluteUri, location.Line, location.Column);
        lock (_gate)
        {
            // A request that failed (the debuggee resumed meanwhile) is not reused: the next stop at the same place asks again.
            if (_request is { } request && _key == key && (!request.IsCompleted || _identity is not null)) return request;
            _key = key;
            _identity = null;
            return _request = FetchAsync(session, threadId, frameId, key);
        }
    }

    private async Task<string?> FetchAsync(IDebugSession session, int threadId, int frameId, Key? key)
    {
        string? identity = null;
        try
        {
            var frames = await session.GetStackTraceAsync(threadId, 0, MaxFrames).ConfigureAwait(false);
            foreach (var frame in frames)
            {
                if (frame.Id != frameId) continue;
                identity = string.Join('\n', session.Id, threadId.ToString(System.Globalization.CultureInfo.InvariantCulture), frame.Name,
                    frame.Source?.Path ?? frame.Source?.Name ?? "");
                break;
            }
        }
        catch (Exception)
        {
            return null; // resumed, ended or failing: the caller falls back to comparing frames by their variable names
        }
        if (key is { } k && identity is not null)
            lock (_gate) { if (_key == k) _identity = identity; }
        return identity;
    }
}
