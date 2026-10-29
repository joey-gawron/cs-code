using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using CsCode.Core.Debugging;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Transport;

/// <summary>One live byte channel to a debug adapter (stdio of a child process, a TCP socket, a named pipe / unix socket).</summary>
public interface IDapTransport : IAsyncDisposable
{
    /// <summary>Bytes coming FROM the adapter.</summary>
    Stream Input { get; }
    /// <summary>Bytes going TO the adapter.</summary>
    Stream Output { get; }
    /// <summary>Completes when the channel is gone (process exited / socket closed). Never faults.</summary>
    Task Completion { get; }
    /// <summary>PID of the adapter process when the transport owns one.</summary>
    int? ProcessId { get; }
    string Description { get; }
    /// <summary>Last few KB of the adapter's stderr (diagnostics for "adapter crashed" messages).</summary>
    string StandardErrorTail { get; }
    /// <summary>Asks the adapter to go away: closes our side, waits <paramref name="grace"/>, then kills (process trees included).</summary>
    ValueTask CloseAsync(TimeSpan grace);
}

public static class DapTransportFactory
{
    /// <summary>Opens the transport a descriptor describes. Executables are started at normal priority; TCP retries until <paramref name="connectTimeout"/>.</summary>
    public static async ValueTask<IDapTransport> OpenAsync(DebugAdapterDescriptor descriptor, ILogger logger, TimeSpan connectTimeout, CancellationToken ct) =>
        descriptor switch
        {
            DebugAdapterExecutable exe => ProcessTransport.Start(exe, logger),
            DebugAdapterServer server => await TcpTransport.ConnectAsync(server.Host, server.Port, connectTimeout, ct).ConfigureAwait(false),
            DebugAdapterNamedPipe pipe => await PipeTransport.ConnectAsync(pipe.Path, connectTimeout, ct).ConfigureAwait(false),
            DebugAdapterServerProcess server => await ServerProcessTransport.StartAsync(server, logger, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown debug adapter descriptor {descriptor.GetType().Name}"),
        };

    /// <summary>The TCP endpoint of an adapter SERVER a transport is connected to (child sessions connect to the same server), else null.</summary>
    public static (string Host, int Port)? EndpointOf(IDapTransport transport) => transport switch
    {
        ServerProcessTransport s => (s.Host, s.Port),
        TcpTransport t => (t.Host, t.Port),
        _ => null,
    };
}

/// <summary>Adapter as a child process talking DAP over stdin/stdout. The process tree is killed on close: no zombies.</summary>
public sealed class ProcessTransport : IDapTransport
{
    private const int StderrTailBytes = 16 * 1024;
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;
    private int _disposed;

    private ProcessTransport(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
        ProcessId = process.Id;
        Description = $"{process.StartInfo.FileName} (pid {process.Id})";
        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        _stderrPump = Task.Run(PumpStderrAsync);
        if (process.HasExited) _completion.TrySetResult(); // exited before the handler was attached
    }

    public Stream Input => _process.StandardOutput.BaseStream;
    public Stream Output => _process.StandardInput.BaseStream;
    public Task Completion => _completion.Task;
    public int? ProcessId { get; }
    public string Description { get; }

    public string StandardErrorTail
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static ProcessTransport Start(DebugAdapterExecutable exe, ILogger logger)
    {
        var psi = new ProcessStartInfo(exe.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in exe.Args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(exe.Cwd)) psi.WorkingDirectory = exe.Cwd;
        if (exe.Env is not null)
            foreach (var (k, v) in exe.Env) psi.Environment[k] = v;

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start debug adapter '{exe.Command}'.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        logger.LogDebug("Started debug adapter {Command} (pid {Pid})", exe.Command, process.Id);
        return new ProcessTransport(process, logger);
    }

    private void OnExited(object? sender, EventArgs e) => _completion.TrySetResult();

    private async Task PumpStderrAsync()
    {
        var buf = new char[2048];
        try
        {
            while (true)
            {
                var n = await _process.StandardError.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                if (n <= 0) break;
                lock (_stderr)
                {
                    _stderr.Append(buf, 0, n);
                    if (_stderr.Length > StderrTailBytes) _stderr.Remove(0, _stderr.Length - StderrTailBytes);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // process went away mid-read
        }
    }

    public async ValueTask CloseAsync(TimeSpan grace)
    {
        if (_process.HasExited) { await DrainAsync().ConfigureAwait(false); return; }
        try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        using var cts = new CancellationTokenSource(grace);
        try { await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            KillTree();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
        }
        await DrainAsync().ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
    }

    private void KillTree()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Kill of debug adapter pid {Pid} failed (already gone?)", ProcessId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CloseAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Closing adapter transport"); }
        _process.Exited -= OnExited;
        _completion.TrySetResult();
        _process.Dispose();
    }
}

/// <summary>Adapter reachable over TCP (DebugAdapterServer descriptors, attach to a remote host:port).</summary>
public sealed class TcpTransport : IDapTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private TcpTransport(TcpClient client, string host, int port)
    {
        _client = client;
        _stream = client.GetStream();
        Host = host;
        Port = port;
        Description = $"tcp://{host}:{port}";
    }

    public Stream Input => _stream;
    public Stream Output => _stream;
    public Task Completion => _completion.Task;
    public int? ProcessId => null;
    public string Description { get; }
    public string StandardErrorTail => "";
    public string Host { get; }
    public int Port { get; }

    public static async ValueTask<TcpTransport> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Exception? last = null;
        while (true)
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
                return new TcpTransport(client, host, port);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                client.Dispose();
                last = ex;
            }
            catch
            {
                client.Dispose();
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Could not connect to debug adapter at {host}:{port} within {timeout.TotalSeconds:0.#}s" + (last is null ? "" : $" ({last.Message})"), last);
            }
            try { await Task.Delay(100, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Could not connect to debug adapter at {host}:{port} within {timeout.TotalSeconds:0.#}s ({last?.Message})", last);
            }
        }
    }

    public ValueTask CloseAsync(TimeSpan grace)
    {
        try { _client.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        _client.Dispose();
    }
}

/// <summary>Named pipe on Windows, unix domain socket path elsewhere (what VS Code calls DebugAdapterNamedPipeServer).</summary>
public sealed class PipeTransport : IDapTransport
{
    private readonly IDisposable _owner;
    private readonly Stream _stream;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private PipeTransport(IDisposable owner, Stream stream, string description)
    {
        _owner = owner;
        _stream = stream;
        Description = description;
    }

    public Stream Input => _stream;
    public Stream Output => _stream;
    public Task Completion => _completion.Task;
    public int? ProcessId => null;
    public string Description { get; }
    public string StandardErrorTail => "";

    public static async ValueTask<PipeTransport> ConnectAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        if (OperatingSystem.IsWindows())
        {
            var name = path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? path[9..] : path;
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
            return new PipeTransport(pipe, pipe, $"pipe:{path}");
        }

        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
                return new PipeTransport(socket, new NetworkStream(socket, ownsSocket: false), $"unix:{path}");
            }
            catch (SocketException) when (!deadline.IsCancellationRequested)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Could not connect to debug adapter socket '{path}' within {timeout.TotalSeconds:0.#}s");
            }
            try { await Task.Delay(100, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Could not connect to debug adapter socket '{path}' within {timeout.TotalSeconds:0.#}s");
            }
        }
    }

    public ValueTask CloseAsync(TimeSpan grace)
    {
        try { _stream.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        _stream.Dispose();
        _owner.Dispose();
    }
}
