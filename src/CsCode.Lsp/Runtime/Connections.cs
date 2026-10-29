using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using CsCode.Core.Languages;

namespace CsCode.Lsp.Runtime;

public enum ProcessPriority { BelowNormal, Normal, Idle }

/// <summary>Everything needed to reach one language server. <see cref="ResolvedCommand"/> is null for socket/pipe endpoints.</summary>
public sealed record LspLaunchRequest(
    LanguageServerDefinition Definition,
    string? ResolvedCommand,
    string? WorkingDirectory,
    ProcessPriority Priority = ProcessPriority.BelowNormal,
    Action<string>? OnStderrLine = null);

/// <summary>A live byte transport to a server (child-process stdio, TCP socket, named pipe / unix socket, or an in-proc pipe in tests).</summary>
public interface ILspConnection : IAsyncDisposable
{
    /// <summary>Stream the client WRITES to (the server's stdin).</summary>
    Stream Input { get; }
    /// <summary>Stream the client READS from (the server's stdout).</summary>
    Stream Output { get; }
    int? ProcessId { get; }
    /// <summary>Completes when the server process exits / the transport is gone.</summary>
    Task Closed { get; }
    /// <summary>Human-readable exit info (exit code, last stderr lines) once closed.</summary>
    string? ExitDescription { get; }
    /// <summary>Hard-kill the server and everything it spawned. Idempotent.</summary>
    ValueTask KillAsync();
}

public interface ILspConnectionFactory
{
    ValueTask<ILspConnection> ConnectAsync(LspLaunchRequest request, CancellationToken ct);
}

/// <summary>Thrown when the server binary cannot be found / started (mapped to a single friendly message, never a stack trace storm).</summary>
public sealed class ServerNotFoundException(string command, Exception? inner = null)
    : Exception($"Language server executable '{command}' was not found", inner)
{
    public string Command { get; } = command;
}

/// <summary>How a definition's <c>Command</c> is to be interpreted.</summary>
internal readonly record struct LspEndpoint(LspEndpointKind Kind, string Target, int Port = 0)
{
    public static LspEndpoint Parse(string command)
    {
        if (command.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) || command.StartsWith("socket://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = command[(command.IndexOf("://", StringComparison.Ordinal) + 3)..];
            var colon = rest.LastIndexOf(':');
            if (colon > 0 && int.TryParse(rest[(colon + 1)..], out var port))
                return new LspEndpoint(LspEndpointKind.Tcp, rest[..colon].Trim('[', ']'), port);
        }
        if (command.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase)) return new LspEndpoint(LspEndpointKind.Pipe, command[7..]);
        if (command.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)) return new LspEndpoint(LspEndpointKind.Unix, command[7..]);
        return new LspEndpoint(LspEndpointKind.Process, command);
    }
}

internal enum LspEndpointKind { Process, Tcp, Pipe, Unix }

/// <summary>Default factory: spawns child processes (no shell) or connects to TCP / pipe / unix-socket endpoints.</summary>
public sealed class DefaultLspConnectionFactory : ILspConnectionFactory
{
    public async ValueTask<ILspConnection> ConnectAsync(LspLaunchRequest request, CancellationToken ct)
    {
        var ep = LspEndpoint.Parse(request.Definition.Command);
        switch (ep.Kind)
        {
            case LspEndpointKind.Process:
                return ProcessConnection.Start(request);
            case LspEndpointKind.Tcp:
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await ConnectWithRetryAsync(socket, ep, ct).ConfigureAwait(false);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new StreamConnection(new NetworkStream(socket, ownsSocket: true));
            }
            case LspEndpointKind.Unix:
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(ep.Target), ct).ConfigureAwait(false);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new StreamConnection(new NetworkStream(socket, ownsSocket: true));
            }
            default:
            {
                var pipe = new NamedPipeClientStream(".", ep.Target, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                return new StreamConnection(pipe);
            }
        }
    }

    // A server that is "about to listen" is common (editor started it a moment ago): retry briefly instead of failing instantly.
    private static async Task ConnectWithRetryAsync(Socket socket, LspEndpoint ep, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
        while (true)
        {
            try
            {
                await socket.ConnectAsync(ep.Target, ep.Port, ct).ConfigureAwait(false);
                return;
            }
            catch (SocketException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A duplex stream transport (socket / pipe).</summary>
internal sealed class StreamConnection(Stream duplex) : ILspConnection
{
    public Stream Input => duplex;
    public Stream Output => duplex;
    public int? ProcessId => null;
    public Task Closed { get; } = new TaskCompletionSource().Task; // transport loss is observed by the JSON-RPC reader instead
    public string? ExitDescription => null;
    public ValueTask KillAsync() => duplex.DisposeAsync();
    public ValueTask DisposeAsync() => duplex.DisposeAsync();
}

/// <summary>
/// A child-process server. Started WITHOUT a shell; at below-normal priority via the <c>nice</c> binary on Unix (so grandchildren such as
/// tsserver inherit it — a post-start setpriority would race their creation) or PriorityClass on Windows. stderr is drained into a
/// small bounded ring on its own task. The whole process tree is killed on dispose; descendants that were re-parented (server killed -9)
/// are found through an environment marker (Linux) so nothing is leaked.
/// </summary>
internal sealed class ProcessConnection : ILspConnection
{
    internal const string MarkerVariable = "CSCODE_LSP_SESSION";

    private readonly Process _process;
    private readonly string _marker;
    private readonly LineRing _stderr = new(64);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stderrPump;
    private int _killed;
    private int _disposed;

    private ProcessConnection(Process process, string marker, Action<string>? onStderr)
    {
        _process = process;
        _marker = marker;
        ProcessId = process.Id;
        Input = process.StandardInput.BaseStream;
        Output = process.StandardOutput.BaseStream;
        _stderrPump = PumpStderrAsync(process.StandardError.BaseStream, onStderr);
        process.Exited += OnExited;
        if (process.HasExited) OnExited(null, EventArgs.Empty); // exited before the handler was attached
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public int? ProcessId { get; }
    public Task Closed => _closed.Task;

    public string? ExitDescription
    {
        get
        {
            if (!_process.HasExited) return null;
            var tail = _stderr.Tail(1500);
            return $"exit code {_process.ExitCode}" + (tail.Length > 0 ? $"; stderr: {tail}" : "");
        }
    }

    public static ProcessConnection Start(LspLaunchRequest request)
    {
        var def = request.Definition;
        var command = request.ResolvedCommand ?? throw new ServerNotFoundException(def.Command);
        var marker = Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory is { } wd && Directory.Exists(wd) ? wd : Environment.CurrentDirectory,
        };

        var nice = request.Priority switch { ProcessPriority.BelowNormal => 10, ProcessPriority.Idle => 19, _ => 0 };
        var niceBinary = nice > 0 && !OperatingSystem.IsWindows() ? FindNiceBinary() : null;
        if (niceBinary is not null)
        {
            psi.FileName = niceBinary;
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(nice.ToString(System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(command);
        }
        else psi.FileName = command;
        foreach (var a in def.Args) psi.ArgumentList.Add(a);

        if (def.Env is not null)
            foreach (var (k, v) in def.Env) psi.Environment[k] = v;
        psi.Environment[MarkerVariable] = marker;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            process.Dispose();
            throw new ServerNotFoundException(def.Command, ex);
        }

        if (niceBinary is null && request.Priority != ProcessPriority.Normal)
        {
            try
            {
                process.PriorityClass = request.Priority == ProcessPriority.Idle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* best effort */ }
        }
        return new ProcessConnection(process, marker, request.OnStderrLine);
    }

    private static string? FindNiceBinary()
    {
        foreach (var p in new[] { "/usr/bin/nice", "/bin/nice", "/opt/homebrew/bin/nice" })
            if (File.Exists(p)) return p;
        return ToolResolver.FindOnPath("nice", null);
    }

    private void OnExited(object? sender, EventArgs e) => _closed.TrySetResult();

    private async Task PumpStderrAsync(Stream stderr, Action<string>? onLine)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var line = new StringBuilder();
        try
        {
            while (true)
            {
                var n = await stderr.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0) break;
                var count = decoder.GetChars(buffer, 0, n, chars, 0);
                for (var i = 0; i < count; i++)
                {
                    var c = chars[i];
                    if (c == '\n')
                    {
                        Emit(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else if (line.Length < 8192) line.Append(c);
                }
            }
            if (line.Length > 0) Emit(line.ToString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* process gone */ }

        void Emit(string text)
        {
            _stderr.Add(text);
            try { onLine?.Invoke(text); } catch (Exception) { /* a sink must never kill the drain loop */ }
        }
    }

    public async ValueTask KillAsync()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* already gone */ }
        ProcessReaper.KillMarked(_marker);
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Input.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        await KillAsync().ConfigureAwait(false); // idempotent; also reaps re-parented descendants
        try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
        _process.Exited -= OnExited;
        _process.Dispose();
        _closed.TrySetResult();
    }
}

/// <summary>Finds and kills processes carrying our session marker in their environment (Linux /proc). Only ever kills processes we spawned.</summary>
internal static class ProcessReaper
{
    public static void KillMarked(string marker)
    {
        if (!OperatingSystem.IsLinux()) return;
        var needle = Encoding.UTF8.GetBytes($"{ProcessConnection.MarkerVariable}={marker}");
        foreach (var pid in FindMarked(needle))
        {
            if (pid == Environment.ProcessId) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited meanwhile */ }
        }
    }

    internal static List<int> FindMarked(byte[] needle)
    {
        var result = new List<int>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch (IOException) { return result; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;
            try
            {
                var env = File.ReadAllBytes($"/proc/{pid}/environ");
                if (env.AsSpan().IndexOf(needle) >= 0) result.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours / gone */ }
        }
        return result;
    }

    /// <summary>All live descendants of <paramref name="pid"/> (Linux), for tests asserting "no child process left".</summary>
    public static HashSet<int> Descendants(int pid)
    {
        var result = new HashSet<int>();
        if (!OperatingSystem.IsLinux()) return result;
        var parents = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var p)) continue;
            try
            {
                var stat = File.ReadAllText($"/proc/{p}/stat");
                var close = stat.LastIndexOf(')');
                var fields = stat[(close + 2)..].Split(' ');
                parents[p] = int.Parse(fields[1]);
            }
            catch (Exception ex) when (ex is IOException or FormatException or ArgumentOutOfRangeException) { }
        }
        var frontier = new Queue<int>([pid]);
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            foreach (var (child, parent) in parents)
                if (parent == cur && result.Add(child)) frontier.Enqueue(child);
        }
        return result;
    }
}
