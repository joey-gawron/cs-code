using CsCode.Core.Debugging;

namespace CsCode.Dap.Transport;

/// <summary>
/// An adapter reached through streams another module provides (<see cref="DebugAdapterStreamDescriptor"/>): a remote workspace's adapter runs on its
/// server and the Remote module tunnels its DAP stream. Closing and disposal are the connection's.
/// </summary>
public sealed class StreamTransport : IDapTransport
{
    private readonly IDebugAdapterStreamConnection _connection;
    private int _disposed;

    private StreamTransport(IDebugAdapterStreamConnection connection) => _connection = connection;

    public static async ValueTask<StreamTransport> ConnectAsync(DebugAdapterStreamDescriptor descriptor, CancellationToken ct) =>
        new(await descriptor.Connect(ct).ConfigureAwait(false));

    public Stream Input => _connection.Input;
    public Stream Output => _connection.Output;
    public Task Completion => _connection.Completion;
    public int? ProcessId => _connection.ProcessId;
    public string Description => _connection.Description;
    public string StandardErrorTail => _connection.StandardErrorTail;

    /// <summary>How a child session reaches the same adapter (an adapter server), or null.</summary>
    public DebugAdapterDescriptor? SharedEndpoint => _connection.SharedEndpoint;

    public ValueTask CloseAsync(TimeSpan grace) => _connection.CloseAsync(grace);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
