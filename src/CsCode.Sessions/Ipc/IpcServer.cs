using System.IO.Pipes;
using System.Net.Sockets;

namespace CsCode.Sessions.Ipc;

/// <summary>Answers requests on a process's endpoint. Called on a pool thread, never on the UI thread; implementations hop to the UI themselves.</summary>
public interface IIpcHandler
{
    Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct);
}

public sealed class IpcServerOptions
{
    /// <summary>Only peers running as this uid are served (default: this process's effective uid). Tests set another uid to prove rejection.</summary>
    public uint? ExpectedUid { get; init; }

    /// <summary>Serve the test-only verbs (<see cref="IpcVerbs.IsTestOnly"/>): only for <c>--headless-test</c> processes.</summary>
    public bool AllowTestVerbs { get; init; }

    /// <summary>How long a connected peer may take to send a complete request.</summary>
    public TimeSpan RequestReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Connections served at once; more are closed immediately (a launcher retries or falls back).</summary>
    public int MaxConnections { get; init; } = 16;
}

/// <summary>
/// The per-process IPC endpoint: a Unix-domain socket (<see cref="RuntimeDirectory.EndpointOf"/>, mode 0600, in a 0700 directory) on Linux/macOS, a
/// current-user-only named pipe on Windows. No network listener, ever. Every connection's peer credentials are checked (SO_PEERCRED /
/// LOCAL_PEERCRED): a peer running as another user is disconnected before anything is read. Accepting runs on a dedicated thread; each connection
/// is served asynchronously (requests are rare: one per launcher run), and a handler that takes long (show + wait) never blocks other connections.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly IIpcHandler _handler;
    private readonly IpcServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private readonly uint? _expectedUid;
    private Socket? _listener;
    private Thread? _acceptThread;
    private int _rejected, _served, _disposed;

    public IpcServer(string endpoint, IIpcHandler handler, IpcServerOptions? options = null)
    {
        Endpoint = endpoint;
        _handler = handler;
        _options = options ?? new IpcServerOptions();
        _slots = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
        _expectedUid = _options.ExpectedUid ?? UnixNative.EffectiveUid();
    }

    public string Endpoint { get; }

    /// <summary>Connections refused because the peer was another user (or its credentials could not be read).</summary>
    public int RejectedConnections => Volatile.Read(ref _rejected);

    public int ServedRequests => Volatile.Read(ref _served);

    /// <summary>Binds the endpoint (it exists when this returns) and starts accepting.</summary>
    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            _acceptThread = new Thread(AcceptPipes) { IsBackground = true, Name = "CsCode IPC" };
            _acceptThread.Start();
            return;
        }
        // A file at our endpoint belongs to a dead process that had our pid (the pid is ours now): replace it.
        if (File.Exists(Endpoint)) File.Delete(Endpoint);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(Endpoint));
            File.SetUnixFileMode(Endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        _listener = listener;
        _acceptThread = new Thread(AcceptSockets) { IsBackground = true, Name = "CsCode IPC" };
        _acceptThread.Start();
    }

    /// <remarks>
    /// macOS waits in a cancellable asynchronous accept: there, closing a listening socket does not end an <c>accept</c> blocked on it in another
    /// thread, and the runtime's close waits for that call, so Dispose hung (and with it every quit of a session process). A connection of our
    /// own cannot end it reliably either: the endpoint's directory may be gone by then.
    /// </remarks>
    private void AcceptSockets()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = OperatingSystem.IsMacOS() ? _listener!.AcceptAsync(_stop.Token).AsTask().GetAwaiter().GetResult() : _listener!.Accept(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { return; } // disposed
            if (!_slots.Wait(0)) { client.Dispose(); continue; }
            _ = Task.Run(() => ServeSocketAsync(client));
        }
    }

    private async Task ServeSocketAsync(Socket client)
    {
        try
        {
            using (client)
            {
                if (_expectedUid is { } uid && (UnixNative.PeerCredentials(client) is not { } peer || peer.Uid != uid))
                {
                    Interlocked.Increment(ref _rejected);
                    return;
                }
                await using var stream = new NetworkStream(client, ownsSocket: false);
                await ServeAsync(stream).ConfigureAwait(false);
            }
        }
        finally { _slots.Release(); }
    }

    private void AcceptPipes()
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // CurrentUserOnly: the pipe's ACL admits only this user, and the client side verifies the server's owner.
                pipe = new NamedPipeServerStream(Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection(); // dedicated thread; Dispose unblocks it by connecting once
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { return; }
            if (_stop.IsCancellationRequested) { pipe.Dispose(); return; }
            if (!pipe.IsConnected) { pipe.Dispose(); continue; }
            if (!_slots.Wait(0)) { pipe.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await using (pipe) await ServeAsync(pipe).ConfigureAwait(false); }
                finally { _slots.Release(); }
            });
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            IpcMessage? request;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(_options.RequestReadTimeout);
                try { request = await IpcFraming.ReadAsync(stream, read.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
            }
            if (request is null) return; // the peer is done
            IpcMessage reply;
            if (IpcVerbs.IsTestOnly(request.Type) && !_options.AllowTestVerbs) reply = IpcMessage.Failure($"'{request.Type}' is only served by test processes");
            else
            {
                try { reply = await _handler.HandleAsync(request, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { reply = IpcMessage.Failure($"{request.Type} failed: {ex.Message}"); }
            }
            Interlocked.Increment(ref _served);
            try { await IpcFraming.WriteAsync(stream, reply, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { return; }
        }
    }

    /// <summary>Stops accepting, closes the endpoint and removes its file. In-flight handlers are cancelled.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _listener?.Dispose(); } catch (SocketException) { }
        if (!OperatingSystem.IsWindows())
        {
            try { if (File.Exists(Endpoint)) File.Delete(Endpoint); }
            catch (IOException) { /* the launcher removes stale endpoints */ }
        }
        else
        {
            // Unblock a WaitForConnection by connecting once.
            try { using var poke = new NamedPipeClientStream(".", Endpoint, PipeDirection.InOut); poke.Connect(50); }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
        }
        if (_acceptThread is { } t && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(2));
    }
}
