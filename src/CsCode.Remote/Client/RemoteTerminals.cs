using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// R3 remote terminals: the <see cref="ITerminalBackendProvider"/> the Terminal module asks for every new terminal. While the workspace is remote
/// (its primary folder is on the configured host) terminals run on the server (<c>term/create</c>: the server resolves the shell, environment and
/// directory and runs a PTY); otherwise the answer is null and the terminal is local. Debuggees started with <c>runInTerminal</c> and anything else
/// that creates terminals follow the same rule.
/// </summary>
public sealed class RemoteTerminals(RemoteConnectionManager manager, Func<IWorkspaceService?> workspace) : ITerminalBackendProvider
{
    public ITerminalBackend? TryCreate(TerminalCreateOptions options)
    {
        if (workspace()?.PrimaryFolder is not { } folder || manager.MapperFor(folder) is null || manager.Channels is not { } channels) return null;
        return new RemoteTerminalBackend(manager, channels, manager.Logger);
    }
}

/// <summary>
/// One remote terminal (<see cref="ITerminalBackend"/>): a server PTY behind a channel. Output is delivered on this terminal's own thread (blocking
/// there — the terminal's worker is behind — holds the acknowledgements back and so throttles the remote program); input, resizes and kills only
/// enqueue. A short loss of the connection is invisible: input waits in the channel's ring, missed output is replayed when the client reattaches to
/// the same (persistent) server. If the server did not keep the program, the terminal ends with exit code -1 and says why.
/// </summary>
public sealed class RemoteTerminalBackend : ITerminalBackend, IChannelSink
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NamePollInterval = TimeSpan.FromSeconds(1);

    private readonly RemoteConnectionManager _manager;
    private readonly RemoteChannels _channels;
    private readonly ILogger _log;
    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>()); // byte[] / Exit / LostMarker
    private readonly ManualResetEventSlim _attached = new(false);
    private ITerminalBackendListener? _listener;
    private ClientChannel? _channel;
    private Thread? _delivery;
    private volatile bool _exited;
    private int _exitCode = int.MinValue;
    private int _pid;
    private long _size = -1;
    private string? _foreground;
    private long _foregroundAt;
    private int _foregroundBusy, _disposed;

    private sealed record Exit(int Code, string? Reason);

    public RemoteTerminalBackend(RemoteConnectionManager manager, RemoteChannels channels, ILogger? log = null)
    {
        _manager = manager;
        _channels = channels;
        _log = log ?? NullLogger.Instance;
    }

    public long ChannelId => _channel?.Id ?? 0;

    public TerminalBackendStarted Start(TerminalCreateOptions options, int columns, int rows)
    {
        // Blocking by contract: the terminal service runs this off the UI thread, as it forks local programs there.
        using var cts = new CancellationTokenSource(StartTimeout);
        try
        {
            var session = _manager.WaitForSessionAsync(StartTimeout, cts.Token).GetAwaiter().GetResult();
            if (!session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.Terminals))
                throw new NotSupportedException($"The server on {_manager.HostName} cannot run terminals.");
            var r = session.Peer.InvokeAsync<TermCreateParams, TermCreateResult>(RemoteMethods.TermCreate, new TermCreateParams(options, columns, rows), cts.Token, StartTimeout)
                        .GetAwaiter().GetResult() ?? throw new IOException("The server sent no answer for the new terminal.");
            _pid = r.ProcessId;
            _size = ((long)columns << 32) | (uint)rows;
            _channel = _channels.Open(r.Channel, this);
            _delivery = new Thread(DeliveryLoop) { IsBackground = true, Name = "CsCode.Remote-terminal-" + r.Channel };
            _delivery.Start();
            return new TerminalBackendStarted(r.ProcessId, r.Program, r.WorkingDirectory);
        }
        catch (OperationCanceledException) { throw new TimeoutException($"Starting a terminal on {_manager.HostName} took more than {StartTimeout.TotalSeconds:0} s."); }
        catch (NotSupportedException ex) { throw new InvalidOperationException(ex.Message, ex); }
    }

    public void Attach(ITerminalBackendListener listener)
    {
        _listener = listener;
        _attached.Set();
    }

    // ------------------------------------------------------------------ IChannelSink (connection handler thread: queue only)

    void IChannelSink.Deliver(ReadOnlyMemory<byte> data) => TryAdd(data.ToArray());

    void IChannelSink.Lost(long bytes) =>
        TryAdd(System.Text.Encoding.UTF8.GetBytes($"\r\n\e[33m[{bytes} bytes of output were lost while the connection was down]\e[0m\r\n"));

    void IChannelSink.Closed(int? exitCode, string? reason) => TryAdd(new Exit(exitCode ?? -1, reason));

    private void TryAdd(object item)
    {
        try { _queue.Add(item); } catch (InvalidOperationException) { } // disposed
    }

    private void DeliveryLoop()
    {
        try
        {
            _attached.Wait();
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                var listener = _listener;
                if (listener is null) continue;
                if (item is byte[] data)
                {
                    listener.OnOutput(data); // may block: the terminal's worker is behind (back-pressure)
                    _channel?.Consumed(data.Length);
                }
                else if (item is Exit exit)
                {
                    if (exit.Code == -1 && exit.Reason is { } why) listener.OnOutput(System.Text.Encoding.UTF8.GetBytes($"\r\n\e[31m{why}\e[0m\r\n"));
                    Volatile.Write(ref _exitCode, exit.Code);
                    _exited = true;
                    _channel?.Consumed(0);
                    listener.OnExited(exit.Code);
                    return;
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ------------------------------------------------------------------ ITerminalBackend

    public int ProcessId => _pid;
    public bool HasExited => _exited;
    public int? ExitCode => _exited ? Volatile.Read(ref _exitCode) : null;

    public bool Write(ReadOnlySpan<byte> data, bool urgent = false) => _channel is { } c && !_exited && c.Write(data);

    public long PendingWriteBytes => _channel?.PendingWriteBytes ?? 0;
    public long MaxPendingWriteBytes => ClientChannel.InputRingBytes;

    public void Resize(int columns, int rows)
    {
        var size = ((long)columns << 32) | (uint)rows;
        if (Interlocked.Exchange(ref _size, size) == size || _channel is not { } c) return;
        _channels.Peer?.Notify(RemoteMethods.TermResize, new TermResizeParams(c.Id, columns, rows));
    }

    public void Kill(TimeSpan? grace = null) => _channel?.Close(grace ?? TimeSpan.FromSeconds(2));

    public string? GetForegroundProcessName()
    {
        // Answered from the last report; refreshed in the background at most once a second (the terminal polls this).
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _foregroundAt) >= NamePollInterval.TotalMilliseconds && _channel is { } c && !_exited
            && _channels.Peer is { } peer && Interlocked.Exchange(ref _foregroundBusy, 1) == 0)
        {
            Interlocked.Exchange(ref _foregroundAt, now);
            _ = RefreshForegroundAsync(peer, c.Id);
        }
        return Volatile.Read(ref _foreground);
    }

    private async Task RefreshForegroundAsync(RpcPeer peer, long id)
    {
        try
        {
            var r = await peer.InvokeAsync<ChanIdParams, TermInfoResult>(RemoteMethods.TermInfo, new ChanIdParams(id), CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Volatile.Write(ref _foreground, r?.ForegroundProcess);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { _log.LogDebug("term/info failed: {Error}", ex.Message); }
        finally { Volatile.Write(ref _foregroundBusy, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel?.Dispose(); // ends the program on the server (SIGHUP, SIGKILL after a second) unless it already exited
        _queue.CompleteAdding();
        _attached.Set();
        if (_delivery is { } t && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(2));
        _attached.Dispose();
    }
}
