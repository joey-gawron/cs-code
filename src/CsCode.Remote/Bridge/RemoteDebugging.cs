using System.Collections.Concurrent;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// R3 remote debugging, client side. The local debug service keeps its DAP client, breakpoints and UI; for a session of a remote workspace the
/// adapter runs on the server and only its DAP byte stream crosses the connection (a resumable channel):
/// <list type="bullet">
/// <item>an <see cref="IDebugAdapterFactory"/> registered for every debug type (after the built-ins, so it is asked first) answers a stream
/// descriptor when the configuration's folder is remote, else nothing (the built-in factory runs locally);</item>
/// <item>an <see cref="IDebugConfigurationProvider"/> registered after the built-in ones sends the configuration to the server, whose own providers
/// resolve it there (where <c>${workspaceFolder}</c> exists: cargo artifacts, project outputs, cwd);</item>
/// <item>the <see cref="IDebugPathMapper"/>: breakpoints go to the adapter by their remote paths, stack frames' paths come back as the workspace's
/// <c>ssh:</c> URIs, and <c>${workspaceFolder}</c>/<c>${file}</c> resolve to remote paths.</item>
/// </list>
/// Adapter servers (js-debug) are reached again by their child sessions through the same server (<c>dap/connect</c>).
/// </summary>
public sealed class RemoteDebugging(RemoteConnectionManager manager, Func<IWorkspaceService?> workspace, ILogger? log = null)
    : IDebugPathMapper, IDebugAdapterFactory, IDebugConfigurationProvider
{
    /// <summary>Starting an adapter may wait for the user (an installer's download prompt is forwarded from the server).</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(10);
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>Registers the factory and provider for every debug type the service knows (call once the debug service exists).</summary>
    public IDisposable RegisterWith(IDebugService debug)
    {
        var store = new DisposableStore();
        foreach (var type in debug.DebuggerTypes.Select(t => t.Type).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            store.Add(debug.RegisterAdapterFactory(type, this));
            store.Add(debug.RegisterConfigurationProvider(type, this));
        }
        return store;
    }

    private Uri? FolderOf(DebugConfiguration c, Uri? folder = null) => folder ?? c.WorkspaceFolder ?? workspace()?.PrimaryFolder;

    private bool IsRemote(Uri? folder) => folder is not null && manager.MapperFor(folder) is not null;

    // ------------------------------------------------------------------ IDebugPathMapper

    public string? ToAdapterPath(Uri uri) => manager.MapperFor(uri) is { } m ? m.ToRemotePath(uri) : null;

    public Uri? ToUri(string adapterPath, Uri? workspaceFolder)
    {
        if (workspaceFolder is null || manager.MapperFor(workspaceFolder) is not { } m) return null;
        var windows = adapterPath.Length >= 3 && char.IsAsciiLetter(adapterPath[0]) && adapterPath[1] == ':' && adapterPath[2] is '/' or '\\';
        return adapterPath.StartsWith('/') || windows ? m.FromRemotePath(adapterPath) : null;
    }

    // ------------------------------------------------------------------ IDebugConfigurationProvider (runs after the built-in providers)

    public async ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        var f = FolderOf(configuration, folder);
        if (!IsRemote(f)) return configuration;
        var session = await manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        var r = await session.Peer.InvokeAsync<DapResolveParams, DapResolveResult>(RemoteMethods.DapResolve,
            new DapResolveParams(f, configuration with { WorkspaceFolder = configuration.WorkspaceFolder ?? f }), ct, StartTimeout).ConfigureAwait(false);
        return r?.Configuration;
    }

    // ------------------------------------------------------------------ IDebugAdapterFactory (asked before the built-in one)

    public ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        var folder = FolderOf(configuration);
        if (!IsRemote(folder)) return ValueTask.FromResult<DebugAdapterDescriptor?>(null);
        var config = configuration with { WorkspaceFolder = configuration.WorkspaceFolder ?? folder };
        return ValueTask.FromResult<DebugAdapterDescriptor?>(new DebugAdapterStreamDescriptor(
            $"{config.Type} adapter on {manager.HostName}",
            token => ConnectAsync(RemoteMethods.DapStart, new DapStartParams(config), token)));
    }

    private async ValueTask<IDebugAdapterStreamConnection> ConnectAsync<TParams>(string method, TParams args, CancellationToken ct)
    {
        var session = await manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.Debug))
            throw new NotSupportedException($"The server on {manager.HostName} cannot run debug adapters (reinstall it).");
        var channels = manager.Channels ?? throw new RemoteDisconnectedException("Not connected");
        var r = await session.Peer.InvokeAsync<TParams, DapStartResult>(method, args, ct, StartTimeout).ConfigureAwait(false)
                ?? throw new IOException("The server sent no answer for the debug adapter.");
        var connection = new RemoteAdapterConnection(r, channels, manager.HostName ?? "remote");
        if (r.Shareable)
            connection.SharedEndpoint = new DebugAdapterStreamDescriptor($"{r.Description} (child)",
                token => ConnectAsync(RemoteMethods.DapConnect, new DapConnectParams(r.Channel), token));
        _log.LogInformation("Remote debug adapter {Channel}: {Description}", r.Channel, r.Description);
        return connection;
    }
}

/// <summary>
/// A remote adapter's DAP stream (<see cref="IDebugAdapterStreamConnection"/>) over a channel: <see cref="Input"/> reads what arrived (acknowledging
/// as it is read, which is the flow control), <see cref="Output"/> writes into the channel (enqueue only; held across a short reconnect).
/// </summary>
internal sealed class RemoteAdapterConnection : IDebugAdapterStreamConnection, IChannelSink
{
    private readonly ClientChannel _channel;
    private readonly ConcurrentQueue<ReadOnlyMemory<byte>> _incoming = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOnlyMemory<byte> _current;
    private volatile bool _ended;
    private string _tail = "";
    private int _disposed;

    public RemoteAdapterConnection(DapStartResult started, RemoteChannels channels, string host)
    {
        ProcessId = started.ProcessId;
        Description = $"{started.Description} on {host}";
        Input = new ReadSide(this);
        Output = new WriteSide(this);
        _channel = channels.Open(started.Channel, this);
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public Task Completion => _completion.Task;
    public int? ProcessId { get; }
    public string Description { get; }
    public string StandardErrorTail => _tail;
    public DebugAdapterDescriptor? SharedEndpoint { get; set; }

    // sink: connection handler thread, queue only
    void IChannelSink.Deliver(ReadOnlyMemory<byte> data)
    {
        _incoming.Enqueue(data.ToArray());
        _available.Release();
    }

    void IChannelSink.Lost(long bytes) { } // DAP framing cannot survive a gap: the session will fail on the next message and report it

    void IChannelSink.Closed(int? exitCode, string? reason)
    {
        _tail = reason ?? "";
        _ended = true;
        _available.Release();
        _completion.TrySetResult();
    }

    private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_current.Length == 0 && _incoming.TryDequeue(out var next)) _current = next;
            if (_current.Length > 0)
            {
                var n = Math.Min(buffer.Length, _current.Length);
                _current.Span[..n].CopyTo(buffer.Span);
                _current = _current[n..];
                _channel.Consumed(n);
                return n;
            }
            if (_ended && _incoming.IsEmpty) return 0;
            await _available.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current.Length == 0 && _incoming.TryDequeue(out var next)) _current = next;
            if (_current.Length > 0)
            {
                var n = Math.Min(buffer.Length, _current.Length);
                _current.Span[..n].CopyTo(buffer);
                _current = _current[n..];
                _channel.Consumed(n);
                return n;
            }
            if (_ended && _incoming.IsEmpty) return 0;
            _available.Wait();
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        if (_ended || !_channel.Write(data)) throw new IOException("The remote debug adapter is gone (or too much is waiting for the reconnect).");
    }

    public ValueTask CloseAsync(TimeSpan grace)
    {
        _channel.Close(grace);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Dispose();
        // The server ends the adapter (its process tree) when the channel goes; give its close a moment to arrive, never more.
        await Task.WhenAny(_completion.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _ended = true;
        _available.Release();
        _completion.TrySetResult();
    }

    private sealed class ReadSide(RemoteAdapterConnection owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => owner.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => owner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => owner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => owner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WriteSide(RemoteAdapterConnection owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => owner.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => owner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            owner.Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
    }
}
