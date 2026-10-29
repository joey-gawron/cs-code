using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Transport;

/// <summary>
/// An adapter that runs as a SERVER process (js-debug's <c>dapDebugServer.js</c>): it is started, says that it listens, and the client connects —
/// to a private socket the transport chose (<see cref="PrivateSocket"/>), or to the TCP port the server printed. The process belongs to the
/// session that started it — closing that session's transport kills it with its whole tree (debuggees it spawned included) — while child
/// sessions (<c>startDebugging</c>) open their own connections to the same server.
/// </summary>
/// <param name="ListeningPattern">Regex matched against each stdout line until it matches. TCP: group <c>port</c> (required) and <c>host</c>
/// (optional). Private socket: any match (the path is the transport's own).</param>
public sealed record DebugAdapterServerProcess(
    string Command,
    IReadOnlyList<string> Args,
    string ListeningPattern,
    IReadOnlyDictionary<string, string>? Env = null,
    string? Cwd = null) : DebugAdapterDescriptor
{
    /// <summary>How long the server may take to print its listening line (and accept the connection).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Start the server below normal OS priority (ARCHITECTURE §6): <c>nice -n 10</c> on Unix, so the debuggees it spawns inherit it.</summary>
    public bool BelowNormalPriority { get; init; } = true;
    /// <summary>Host to connect to when the pattern has no <c>host</c> group.</summary>
    public string DefaultHost { get; init; } = "127.0.0.1";
    /// <summary>Name used in messages and the memory monitor ("js-debug").</summary>
    public string DisplayName { get; init; } = "debug adapter server";

    /// <summary>
    /// Listen on a PRIVATE socket instead of a TCP port any local process can connect to: the transport creates a fresh directory only this user
    /// can enter (0700, under <see cref="SocketRoot"/>) and passes <c>&lt;directory&gt;/dap.sock</c> in place of each argument equal to
    /// <see cref="SocketPathArgument"/> (Windows: a named pipe <c>\\.\pipe\&lt;prefix&gt;-&lt;random&gt;</c>). The client and every child session
    /// connect to that path; the directory goes with the transport (stale ones of a killed CsCode go at the next start).
    /// </summary>
    public bool PrivateSocket { get; init; }

    /// <summary>Where the private socket's directory is created; null: <c>$XDG_RUNTIME_DIR</c> when set, else the temp directory.</summary>
    public string? SocketRoot { get; init; }

    /// <summary>Name prefix of the private socket's directory (<c>&lt;prefix&gt;-&lt;pid&gt;-&lt;start&gt;-&lt;random&gt;</c>).</summary>
    public string SocketPrefix { get; init; } = "cscode-dap";

    /// <summary>The argument replaced by the private socket's path.</summary>
    public const string SocketPathArgument = "${cscode:socketPath}";
}

/// <summary>
/// Connection to an adapter server this transport started (<see cref="DebugAdapterServerProcess"/>): a private Unix domain socket / named pipe,
/// or TCP. Owns the server process: on close the connection is closed, then the server's process tree is killed (a server never exits by itself)
/// and its private socket directory deleted, so no adapter, debuggee or socket outlives its session.
/// <para>Processes that LEFT the tree are left alone on a normal close: a program may detach a process on purpose (a dev server opening the
/// browser through xdg-open, a daemon started with setsid), and VS Code leaves those running too. Only when the server DIED on its own (crashed,
/// was killed) are the processes it orphaned reaped (Linux): those that carry this server's environment marker AND still belong to the server's
/// session, i.e. did not detach into a session of their own (<see cref="AdapterProcessReaper"/>).</para>
/// </summary>
public sealed class ServerProcessTransport : IDapTransport, IKillableTransport
{
    internal const string MarkerVariable = "CSCODE_DAP_ADAPTER";
    private const int TailChars = 16 * 1024;

    private readonly Process _process;
    private readonly IDapTransport _connection;
    private readonly PrivateSocket? _socket;
    private readonly string _marker;
    private readonly int? _sessionId;
    private readonly ILogger _logger;
    private readonly OutputTail _stderr;
    private readonly OutputTail _stdout;
    private readonly Task _pumps;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;
    private int _disposed;
    private int _killedByUs;

    private ServerProcessTransport(Process process, IDapTransport connection, DebugAdapterDescriptor endpoint, PrivateSocket? socket, string marker, int? sessionId,
        OutputTail stdout, OutputTail stderr, Task pumps, ILogger logger, string name)
    {
        _process = process;
        _connection = connection;
        _socket = socket;
        Endpoint = endpoint;
        _marker = marker;
        _sessionId = sessionId;
        _stdout = stdout;
        _stderr = stderr;
        _pumps = pumps;
        _logger = logger;
        ProcessId = process.Id;
        Description = $"{name} (pid {process.Id}) {connection.Description}";
        process.Exited += OnExited;
        if (process.HasExited) _completion.TrySetResult();
        _ = connection.Completion.ContinueWith(_ => _completion.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Stream Input => _connection.Input;
    public Stream Output => _connection.Output;
    public Task Completion => _completion.Task;
    public int? ProcessId { get; }
    public string Description { get; }
    /// <summary>Where the server listens (<see cref="DebugAdapterNamedPipe"/>: its private socket / pipe; <see cref="DebugAdapterServer"/>: TCP).</summary>
    public DebugAdapterDescriptor Endpoint { get; }
    /// <summary>The private directory of the server's socket (null for TCP and named pipes); deleted when the transport closes.</summary>
    public string? SocketDirectory => _socket?.Directory;
    /// <summary>The marker this server and everything it spawned carry in their environment (tests use it to find leftovers).</summary>
    public string Marker => _marker;
    /// <summary>The server's session id (Linux <c>/proc/&lt;pid&gt;/stat</c>), read when it started; null elsewhere.</summary>
    public int? ServerSessionId => _sessionId;
    public string StandardErrorTail => _stderr.Text;

    /// <summary>
    /// Starts the server, waits (bounded) for its listening line, and connects. On any failure the process tree is killed (and the private
    /// socket directory deleted) before throwing.
    /// </summary>
    public static async ValueTask<ServerProcessTransport> StartAsync(DebugAdapterServerProcess descriptor, ILogger logger, CancellationToken ct)
    {
        var pattern = new Regex(descriptor.ListeningPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        PrivateSocket? socket = null;
        if (descriptor.PrivateSocket)
        {
            try { socket = PrivateSocket.Create(descriptor.SocketPrefix, descriptor.SocketRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Could not create a private socket for the {descriptor.DisplayName}: {ex.Message}", ex);
            }
        }
        try
        {
            return await StartCoreAsync(descriptor, pattern, socket, logger, ct).ConfigureAwait(false);
        }
        catch
        {
            socket?.Delete(); // the server is dead by now (StartCoreAsync killed it)
            throw;
        }
    }

    private static async ValueTask<ServerProcessTransport> StartCoreAsync(DebugAdapterServerProcess descriptor, Regex pattern, PrivateSocket? socket, ILogger logger, CancellationToken ct)
    {
        var marker = Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // [nice -n 10] command args… — no shell; nice execs the command, so Process.Id is the server's own pid and everything it spawns
        // (the debuggees) inherits the lower priority. A post-start setpriority would race the server's first children.
        var nice = descriptor.BelowNormalPriority && !OperatingSystem.IsWindows() ? FindNice() : null;
        var launch = new List<string>();
        if (nice is not null) launch.AddRange([nice, "-n", "10"]);
        launch.Add(descriptor.Command);
        psi.FileName = launch[0];
        foreach (var a in launch.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var a in descriptor.Args) psi.ArgumentList.Add(socket is not null && a == DebugAdapterServerProcess.SocketPathArgument ? socket.Path : a);
        if (descriptor.Cwd is { Length: > 0 } cwd && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (descriptor.Env is not null)
            foreach (var (k, v) in descriptor.Env) psi.Environment[k] = v;
        psi.Environment[MarkerVariable] = marker;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {descriptor.DisplayName} '{descriptor.Command}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start {descriptor.DisplayName} '{descriptor.Command}': {ex.Message}", ex);
        }
        if (descriptor.BelowNormalPriority && nice is null)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* best effort */ }
        }
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        var sessionId = ReadSessionId(process);

        var stdout = new OutputTail(TailChars);
        var stderr = new OutputTail(TailChars);
        var listening = new TaskCompletionSource<(string Host, int Port)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdout, line =>
            {
                if (listening.Task.IsCompleted) return;
                Match m;
                try { m = pattern.Match(line); }
                catch (RegexMatchTimeoutException) { return; }
                if (!m.Success) return;
                if (socket is not null) listening.TrySetResult(("", 0)); // listening on the path we gave it
                else if (int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
                    listening.TrySetResult((m.Groups["host"] is { Success: true, Value.Length: > 0 } h ? h.Value : descriptor.DefaultHost, port));
            }),
            PumpAsync(process.StandardError, stderr, null));
        // Fail fast when the server exits before it listens (bad path, wrong Node version…), with its own words.
        _ = pumps.ContinueWith(_ => listening.TrySetException(new InvalidOperationException(
            $"The {descriptor.DisplayName} exited before it was listening{Tail(stderr, stdout)}")), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = listening.Task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var started = Stopwatch.GetTimestamp();
        try
        {
            (string Host, int Port) endpoint;
            try { endpoint = await listening.Task.WaitAsync(descriptor.StartTimeout, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"The {descriptor.DisplayName} did not report {(socket is null ? "a listening port" : "that it listens on its socket")} within {descriptor.StartTimeout.TotalSeconds:0.#}s{Tail(stderr, stdout)}");
            }
            var remaining = descriptor.StartTimeout - Stopwatch.GetElapsedTime(started);
            var connectTimeout = remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            IDapTransport connection;
            DebugAdapterDescriptor where;
            if (socket is not null)
            {
                connection = await PipeTransport.ConnectAsync(socket.Path, connectTimeout, ct).ConfigureAwait(false);
                where = new DebugAdapterNamedPipe(socket.Path);
            }
            else
            {
                connection = await TcpTransport.ConnectAsync(endpoint.Host, endpoint.Port, connectTimeout, ct).ConfigureAwait(false);
                where = new DebugAdapterServer(endpoint.Host, endpoint.Port);
            }
            logger.LogDebug("Started {Name} (pid {Pid}) listening at {Where}", descriptor.DisplayName, process.Id, connection.Description);
            return new ServerProcessTransport(process, connection, where, socket, marker, sessionId, stdout, stderr, pumps, logger, descriptor.DisplayName);
        }
        catch
        {
            var crashed = process.HasExited;
            KillTree(process);
            // A server that died while starting may have orphaned something already; one we gave up on is killed with its tree.
            if (crashed) await ReapOrphansAsync(marker, sessionId).ConfigureAwait(false);
            await WaitForExitAsync(process).ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

    // Read right after the start, while the server is certainly alive (its pid cannot have been reused yet).
    private static int? ReadSessionId(Process process) => AdapterProcessReaper.SessionOf(process.Id);

    private static string Tail(OutputTail stderr, OutputTail stdout)
    {
        var text = (stderr.Text is { Length: > 0 } e ? e : stdout.Text).Trim();
        if (text.Length > 2_000) text = text[^2_000..];
        return text.Length > 0 ? $": {text}" : ".";
    }

    private static string? FindNice()
    {
        foreach (var p in new[] { "/usr/bin/nice", "/bin/nice", "/opt/homebrew/bin/nice", "/run/current-system/sw/bin/nice" })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Reads a stream line by line into a bounded tail (the server must never block on a full pipe); <paramref name="onLine"/> sees every line.</summary>
    private static async Task PumpAsync(StreamReader reader, OutputTail tail, Action<string>? onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                tail.Append(line);
                onLine?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* process gone */ }
    }

    private void OnExited(object? sender, EventArgs e) => _completion.TrySetResult();

    /// <summary>
    /// Closes the connection and ends the server: it never exits by itself, so there is no grace period to wait out. A server that had already
    /// died on its own (crash, external kill) orphaned its debuggees: those are reaped (see the class remarks). The private socket's directory
    /// is deleted in every case (the server cannot delete it once it was killed).
    /// </summary>
    public async ValueTask CloseAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        var crashed = HasExited(_process) && Volatile.Read(ref _killedByUs) == 0;
        await _connection.CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        Interlocked.Exchange(ref _killedByUs, 1);
        KillTree(_process);
        if (crashed) await ReapOrphansAsync(_marker, _sessionId).ConfigureAwait(false);
        await WaitForExitAsync(_process).ConfigureAwait(false);
        _socket?.Delete();
        try { await _pumps.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        _completion.TrySetResult();
    }

    /// <summary>
    /// Kills the server's process tree right now, without waiting (a container disposed synchronously, a test host about to exit): nothing may
    /// depend on an async shutdown that might never run. The debuggees the server launched are in its tree. <see cref="CloseAsync"/> still runs later.
    /// </summary>
    public void KillNow()
    {
        Interlocked.Exchange(ref _killedByUs, 1);
        KillTree(_process);
        _socket?.Delete(); // unlinking a socket its (dying) server still holds is fine; nothing can connect to it any more
    }

    /// <summary>Exited, or a zombie the runtime has not reaped yet (the session can learn of a crash from the closed socket first).</summary>
    private static bool HasExited(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            return OperatingSystem.IsLinux() && !AdapterProcessReaper.IsAlive(process.Id);
        }
        catch (InvalidOperationException) { return true; }
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* already gone */ }
    }

    private static async Task WaitForExitAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
    }

    /// <summary>
    /// After the server died on its own: kills the processes it orphaned. They were re-parented away from its tree but still carry its marker; a
    /// process in a session of its own (setsid, Node's <c>detached: true</c>) was detached on purpose and is left alone.
    /// </summary>
    private static async Task ReapOrphansAsync(string marker, int? sessionId)
    {
        var reaped = AdapterProcessReaper.KillMarked(MarkerVariable, marker, sessionId);
        await AdapterProcessReaper.WaitUntilGoneAsync(reaped, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CloseAsync(TimeSpan.Zero).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Closing the adapter server"); }
        _socket?.Delete();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _process.Exited -= OnExited;
        _process.Dispose();
    }

    /// <summary>Last characters of a stream, bounded.</summary>
    private sealed class OutputTail(int capacity)
    {
        private readonly StringBuilder _text = new();

        public void Append(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > capacity) _text.Remove(0, _text.Length - capacity);
            }
        }

        public string Text { get { lock (_text) return _text.ToString(); } }
    }
}

/// <summary>Finds and kills processes carrying a marker in their environment (Linux /proc). Only ever kills processes this app spawned.</summary>
internal static class AdapterProcessReaper
{
    /// <summary>Kills the live processes carrying <c>variable=marker</c>; with <paramref name="sessionId"/>, only those still in that session.</summary>
    public static List<int> KillMarked(string variable, string marker, int? sessionId = null)
    {
        var killed = new List<int>();
        if (!OperatingSystem.IsLinux()) return killed;
        foreach (var pid in FindMarked(variable, marker))
        {
            if (pid == Environment.ProcessId) continue;
            if (sessionId is { } sid && SessionOf(pid) != sid) continue; // detached into a session of its own: on purpose
            killed.Add(pid);
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited meanwhile */ }
        }
        return killed;
    }

    /// <summary>Pids whose environment contains <c>variable=marker</c> (Linux; empty elsewhere).</summary>
    public static List<int> FindMarked(string variable, string marker)
    {
        var result = new List<int>();
        if (!OperatingSystem.IsLinux()) return result;
        var needle = Encoding.UTF8.GetBytes($"{variable}={marker}\0");
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); }
        catch (IOException) { return result; }
        foreach (var dir in dirs)
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                var env = File.ReadAllBytes($"/proc/{pid}/environ");
                if (env.AsSpan().IndexOf(needle) >= 0 && IsAlive(pid)) result.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours / gone */ }
        }
        return result;
    }

    public static async Task WaitUntilGoneAsync(IReadOnlyCollection<int> pids, TimeSpan timeout)
    {
        if (pids.Count == 0 || !OperatingSystem.IsLinux()) return;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (pids.Any(IsAlive) && Stopwatch.GetTimestamp() < deadline) await Task.Delay(10).ConfigureAwait(false);
    }

    /// <summary>The session id of a process (field 6 of Linux <c>/proc/&lt;pid&gt;/stat</c>); null when it is gone or elsewhere.</summary>
    public static int? SessionOf(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields.Length > 3 && int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? sid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>A zombie waiting for its reaper counts as gone.</summary>
    public static bool IsAlive(int pid)
    {
        try { return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
