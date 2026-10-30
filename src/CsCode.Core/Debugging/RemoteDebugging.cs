namespace CsCode.Core.Debugging;

// Additive Core contracts (Remote R3, wave 3): debug adapters that run on the remote host of a remote workspace. Implemented by CsCode.Remote;
// consumed by CsCode.Dap (one more descriptor kind, and URI <-> adapter path mapping for sessions of a remote workspace).

/// <summary>
/// An adapter reached through a byte stream that another module provides (VS Code's inline adapter idea): the remote module tunnels the DAP stream of
/// an adapter running on the remote host over its connection. <see cref="Connect"/> runs once per session, off the UI thread.
/// </summary>
public sealed record DebugAdapterStreamDescriptor(string Description, Func<CancellationToken, ValueTask<IDebugAdapterStreamConnection>> Connect) : DebugAdapterDescriptor;

/// <summary>One live stream to an adapter (see <see cref="DebugAdapterStreamDescriptor"/>).</summary>
public interface IDebugAdapterStreamConnection : IAsyncDisposable
{
    /// <summary>Bytes from the adapter.</summary>
    Stream Input { get; }

    /// <summary>Bytes to the adapter.</summary>
    Stream Output { get; }

    /// <summary>Completes when the adapter is gone (exited, or its connection was lost for good). Never faults.</summary>
    Task Completion { get; }

    /// <summary>The adapter's process id where it runs (informational), if known.</summary>
    int? ProcessId { get; }

    string Description { get; }

    /// <summary>The tail of the adapter's stderr or the reason it ended, for "adapter crashed" messages.</summary>
    string StandardErrorTail { get; }

    /// <summary>A descriptor that reaches the same adapter again — an adapter server whose child sessions connect to it (js-debug) — else null.</summary>
    DebugAdapterDescriptor? SharedEndpoint { get; }

    /// <summary>Asks the adapter to go away: closes our side, then the other side ends it after <paramref name="grace"/>.</summary>
    ValueTask CloseAsync(TimeSpan grace);
}

/// <summary>
/// Maps the URIs of a workspace that is not on this machine (a remote workspace, <c>ssh://host/path</c>) to the paths a debug adapter running there
/// uses, and back. Sessions of such a workspace send breakpoints and resolve stack frames through it; <c>${workspaceFolder}</c>, <c>${file}</c> and
/// the like resolve to the remote paths.
/// </summary>
public interface IDebugPathMapper
{
    /// <summary>The adapter-side path of <paramref name="uri"/>, or null when this mapper does not handle it (local files keep their own rules).</summary>
    string? ToAdapterPath(Uri uri);

    /// <summary>The URI of a path an adapter reported, for a session whose workspace folder is <paramref name="workspaceFolder"/>; null when not handled.</summary>
    Uri? ToUri(string adapterPath, Uri? workspaceFolder);
}
