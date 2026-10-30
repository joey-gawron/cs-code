using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using CsCode.Core.Debugging;
using CsCode.Dap.Configuration;
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

/// <summary>A transport that owns an adapter process and can end it synchronously (a container disposed synchronously cannot wait for async shutdown).</summary>
public interface IKillableTransport
{
    /// <summary>Kills the adapter's process tree now, without waiting for it; never throws. The async close still runs later.</summary>
    void KillNow();
}

public static class DapTransportFactory
{
    /// <summary>Opens the transport a descriptor describes. Executables are started at normal priority; TCP retries until <paramref name="connectTimeout"/>.</summary>
    public static async ValueTask<IDapTransport> OpenAsync(DebugAdapterDescriptor descriptor, ILogger logger, TimeSpan connectTimeout, CancellationToken ct) =>
        descriptor switch
        {
            DebugAdapterExecutable exe => ProcessTransport.Start(exe, logger),
            DebugAdapterStdioProcess process => ProcessTransport.Start(process, logger),
            DebugAdapterServer server => await TcpTransport.ConnectAsync(server.Host, server.Port, connectTimeout, ct).ConfigureAwait(false),
            DebugAdapterNamedPipe pipe => await PipeTransport.ConnectAsync(pipe.Path, connectTimeout, ct).ConfigureAwait(false),
            DebugAdapterServerProcess server => await ServerProcessTransport.StartAsync(server, logger, ct).ConfigureAwait(false),
            DebugAdapterStreamDescriptor stream => await StreamTransport.ConnectAsync(stream, ct).ConfigureAwait(false), // a remote workspace's adapter
            _ => throw new NotSupportedException($"Unknown debug adapter descriptor {descriptor.GetType().Name}"),
        };

    /// <summary>
    /// How to reach the adapter SERVER a transport is connected to — its private socket / named pipe (<see cref="DebugAdapterNamedPipe"/>) or
    /// its TCP port (<see cref="DebugAdapterServer"/>) — else null (a stdio adapter). A child session of the same debug type (or one carrying
    /// js-debug's <c>__pendingTargetId</c>) connects to the same server; see DebugService.
    /// </summary>
    public static DebugAdapterDescriptor? EndpointOf(IDapTransport transport) => transport switch
    {
        ServerProcessTransport s => s.Endpoint,
        TcpTransport t => new DebugAdapterServer(t.Host, t.Port),
        PipeTransport p => new DebugAdapterNamedPipe(p.Path),
        StreamTransport s => s.SharedEndpoint,
        _ => null,
    };
}

/// <summary>
/// A stdio debug adapter this module starts itself (GDB's native DAP, <c>gdb -i=dap</c>): like <see cref="DebugAdapterExecutable"/>, plus
/// below-normal OS priority (ARCHITECTURE §6: <c>nice -n 10</c> on Unix, so the debuggee the adapter launches inherits it) and an environment
/// marker (<c>CSCODE_DAP_ADAPTER=&lt;guid&gt;</c>) that the adapter passes on to everything it starts, so a debuggee the adapter orphaned when
/// it died can be found and ended (see <see cref="ProcessTransport"/>).
/// </summary>
public sealed record DebugAdapterStdioProcess(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Cwd = null) : DebugAdapterDescriptor
{
    public bool BelowNormalPriority { get; init; } = true;
    /// <summary>Name used in messages and the memory monitor ("gdb").</summary>
    public string DisplayName { get; init; } = "debug adapter";
    /// <summary>Environment variables removed from the adapter's environment (and so from the debuggee's): the user's ssh-agent socket, …</summary>
    public IReadOnlyList<string> RemoveEnv { get; init; } = [];
    /// <summary>
    /// A private (0700) directory of startup files the adapter reads that must not be on its command line, which every local user can read
    /// through <c>/proc/&lt;pid&gt;/cmdline</c> (GDB's launch environment: <c>-ix</c> commands). Deleted when the adapter exited or is closed.
    /// </summary>
    public string? PrivateDirectory { get; init; }
}

/// <summary>
/// Adapter as a child process talking DAP over stdin/stdout. The process tree is killed on close: no zombies.
/// <para>Started from a <see cref="DebugAdapterStdioProcess"/> it also runs below normal priority and carries an environment marker. A stdio
/// adapter normally takes its debuggee down with it (its tree is killed on close); if it DIED on its own (crash, external kill), a debuggee it
/// launched may have been re-parented out of the tree: the processes still carrying the marker and still in the adapter's session are reaped
/// (Linux), as <see cref="ServerProcessTransport"/> does for adapter servers. A process attached to (not launched) carries no marker.</para>
/// </summary>
public sealed class ProcessTransport : IDapTransport, IKillableTransport
{
    private const int StderrTailBytes = 16 * 1024;
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;
    private readonly int? _sessionId;
    private int _disposed;
    private int _killedByUs;

    private ProcessTransport(Process process, ILogger logger, string? marker = null, string? name = null)
    {
        _process = process;
        _logger = logger;
        ProcessId = process.Id;
        Marker = marker;
        Description = $"{name ?? process.StartInfo.FileName} (pid {process.Id})";
        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        _stderrPump = Task.Run(PumpStderrAsync);
        // Read right after the start, while the adapter is certainly alive (its pid cannot have been reused yet).
        _sessionId = marker is null ? null : AdapterProcessReaper.SessionOf(process.Id);
        if (process.HasExited) _completion.TrySetResult(); // exited before the handler was attached
    }

    /// <summary>The environment marker of an adapter started from a <see cref="DebugAdapterStdioProcess"/> (null otherwise).</summary>
    public string? Marker { get; }

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
        DotnetRootFold.Apply(psi.Environment, exe.Env?.Keys); // the adapter's debuggees inherit it

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

    /// <summary>
    /// Starts <paramref name="descriptor"/> as <c>[nice -n 10] command args…</c> (no shell; nice execs the command, so <see cref="ProcessId"/> is
    /// the adapter's own pid and the debuggee it launches inherits the lower priority — a post-start setpriority would race its first child),
    /// with the environment marker set.
    /// </summary>
    public static ProcessTransport Start(DebugAdapterStdioProcess descriptor, ILogger logger)
    {
        var marker = Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var nice = descriptor.BelowNormalPriority && !OperatingSystem.IsWindows() ? FindNice() : null;
        var launch = new List<string>();
        if (nice is not null) launch.AddRange([nice, "-n", "10"]);
        launch.Add(descriptor.Command);
        psi.FileName = launch[0];
        foreach (var a in launch.Skip(1).Concat(descriptor.Args)) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(descriptor.Cwd) && Directory.Exists(descriptor.Cwd)) psi.WorkingDirectory = descriptor.Cwd;
        foreach (var name in descriptor.RemoveEnv) psi.Environment.Remove(name);
        if (descriptor.Env is not null)
            foreach (var (k, v) in descriptor.Env) psi.Environment[k] = v;
        DotnetRootFold.Apply(psi.Environment, descriptor.Env?.Keys); // the adapter's debuggees inherit it
        psi.Environment[ServerProcessTransport.MarkerVariable] = marker;

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {descriptor.DisplayName} '{descriptor.Command}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            process.Dispose();
            DeletePrivateDirectory(descriptor.PrivateDirectory);
            throw new InvalidOperationException($"Could not start {descriptor.DisplayName} '{descriptor.Command}': {ex.Message}", ex);
        }
        catch
        {
            process.Dispose();
            DeletePrivateDirectory(descriptor.PrivateDirectory);
            throw;
        }
        if (descriptor.BelowNormalPriority && nice is null)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* best effort */ }
        }
        logger.LogDebug("Started {Name} {Command} (pid {Pid})", descriptor.DisplayName, descriptor.Command, process.Id);
        return new ProcessTransport(process, logger, marker, descriptor.DisplayName) { _privateDirectory = descriptor.PrivateDirectory };
    }

    /// <summary>The adapter's private startup files (<see cref="DebugAdapterStdioProcess.PrivateDirectory"/>); deleted once it exited or is closed.</summary>
    private string? _privateDirectory;

    private static void DeletePrivateDirectory(string? directory)
    {
        if (directory is null) return;
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gone, or a stale-directory sweep will take it */ }
    }

    private void ReleasePrivateDirectory() => DeletePrivateDirectory(Interlocked.Exchange(ref _privateDirectory, null));

    internal static string? FindNice()
    {
        foreach (var p in new[] { "/usr/bin/nice", "/bin/nice", "/opt/homebrew/bin/nice", "/run/current-system/sw/bin/nice" })
            if (File.Exists(p)) return p;
        return null;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        ReleasePrivateDirectory();
        _completion.TrySetResult();
    }

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
        if (_process.HasExited)
        {
            // An adapter that DIED (killed by a signal or failed, not by us) may have orphaned the debuggee it launched: reap what carries its
            // marker. One that quit normally (GDB after `disconnect`: exit code 0) took its debuggee down or detached from it on purpose.
            if (Marker is { } marker && Volatile.Read(ref _killedByUs) == 0 && ExitedAbnormally()) await ReapOrphansAsync(marker).ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
            return;
        }
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

    private bool ExitedAbnormally()
    {
        try { return _process.ExitCode != 0; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task ReapOrphansAsync(string marker)
    {
        var reaped = AdapterProcessReaper.KillMarked(ServerProcessTransport.MarkerVariable, marker, _sessionId);
        if (reaped.Count > 0) _logger.LogDebug("Reaped {Count} process(es) the dead debug adapter pid {Pid} left behind", reaped.Count, ProcessId);
        await AdapterProcessReaper.WaitUntilGoneAsync(reaped, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private void KillTree()
    {
        Interlocked.Exchange(ref _killedByUs, 1);
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Kill of debug adapter pid {Pid} failed (already gone?)", ProcessId);
        }
    }

    public void KillNow() => KillTree();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CloseAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Closing adapter transport"); }
        _process.Exited -= OnExited;
        ReleasePrivateDirectory();
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

    private PipeTransport(IDisposable owner, Stream stream, string path, string description)
    {
        _owner = owner;
        _stream = stream;
        Path = path;
        Description = description;
    }

    /// <summary>The socket path (Unix) or pipe path (<c>\\.\pipe\name</c>, Windows) this transport connected to.</summary>
    public string Path { get; }

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
            return new PipeTransport(pipe, pipe, path, $"pipe:{path}");
        }

        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token).ConfigureAwait(false);
                return new PipeTransport(socket, new NetworkStream(socket, ownsSocket: false), path, $"unix:{path}");
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
