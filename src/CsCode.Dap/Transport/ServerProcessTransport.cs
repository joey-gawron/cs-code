using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Transport;

/// <summary>
/// An adapter that runs as a SERVER process (js-debug's <c>dapDebugServer.js</c>): it is started, prints the port it listens on, and the client
/// connects over TCP. The process belongs to the session that started it — closing that session's transport kills it with its whole tree
/// (debuggees it spawned included) — while child sessions (<c>startDebugging</c>) open their own connections to the same server.
/// </summary>
/// <param name="ListeningPattern">Regex matched against each stdout line until it matches; group <c>port</c> (required) and <c>host</c> (optional).</param>
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
}

/// <summary>
/// TCP connection to an adapter server this transport started (<see cref="DebugAdapterServerProcess"/>). Owns the server process: on close the
/// socket is closed, then the server's process tree is killed (a server never exits by itself) and processes that were re-parented away from the
/// tree but still carry this server's environment marker (Linux) are reaped too, so no adapter or debuggee outlives its session.
/// </summary>
public sealed class ServerProcessTransport : IDapTransport
{
    internal const string MarkerVariable = "CSCODE_DAP_ADAPTER";
    private const int TailChars = 16 * 1024;

    private readonly Process _process;
    private readonly TcpTransport _tcp;
    private readonly string _marker;
    private readonly ILogger _logger;
    private readonly OutputTail _stderr;
    private readonly OutputTail _stdout;
    private readonly Task _pumps;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;
    private int _disposed;

    private ServerProcessTransport(Process process, TcpTransport tcp, string marker, OutputTail stdout, OutputTail stderr, Task pumps, ILogger logger, string name)
    {
        _process = process;
        _tcp = tcp;
        _marker = marker;
        _stdout = stdout;
        _stderr = stderr;
        _pumps = pumps;
        _logger = logger;
        ProcessId = process.Id;
        Description = $"{name} (pid {process.Id}) {tcp.Description}";
        process.Exited += OnExited;
        if (process.HasExited) _completion.TrySetResult();
        _ = tcp.Completion.ContinueWith(_ => _completion.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Stream Input => _tcp.Input;
    public Stream Output => _tcp.Output;
    public Task Completion => _completion.Task;
    public int? ProcessId { get; }
    public string Description { get; }
    public string Host => _tcp.Host;
    public int Port => _tcp.Port;
    /// <summary>The marker this server and everything it spawned carry in their environment (tests use it to find leftovers).</summary>
    public string Marker => _marker;
    public string StandardErrorTail => _stderr.Text is { Length: > 0 } err ? err : _stdout.Text;

    /// <summary>Starts the server, waits (bounded) for its listening line, and connects. On any failure the process tree is killed before throwing.</summary>
    public static async ValueTask<ServerProcessTransport> StartAsync(DebugAdapterServerProcess descriptor, ILogger logger, CancellationToken ct)
    {
        var pattern = new Regex(descriptor.ListeningPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
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
        foreach (var a in descriptor.Args) psi.ArgumentList.Add(a);
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
                if (m.Success && int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
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
                throw new TimeoutException($"The {descriptor.DisplayName} did not report a listening port within {descriptor.StartTimeout.TotalSeconds:0.#}s{Tail(stderr, stdout)}");
            }
            var remaining = descriptor.StartTimeout - Stopwatch.GetElapsedTime(started);
            var tcp = await TcpTransport.ConnectAsync(endpoint.Host, endpoint.Port, remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            logger.LogDebug("Started {Name} (pid {Pid}) listening at {Host}:{Port}", descriptor.DisplayName, process.Id, endpoint.Host, endpoint.Port);
            return new ServerProcessTransport(process, tcp, marker, stdout, stderr, pumps, logger, descriptor.DisplayName);
        }
        catch
        {
            await KillAsync(process, marker).ConfigureAwait(false);
            process.Dispose();
            throw;
        }
    }

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

    /// <summary>Closes the connection and ends the server: it never exits by itself, so there is no grace period to wait out.</summary>
    public async ValueTask CloseAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        await _tcp.CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await KillAsync(_process, _marker).ConfigureAwait(false);
        try { await _pumps.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
        _completion.TrySetResult();
    }

    private static async Task KillAsync(Process process, string marker)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* already gone */ }
        // Descendants the server's tree no longer contains (a debuggee's watchdog re-parented to init when its parent died) still carry the marker.
        var reaped = AdapterProcessReaper.KillMarked(MarkerVariable, marker);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
        await AdapterProcessReaper.WaitUntilGoneAsync(reaped, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CloseAsync(TimeSpan.Zero).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Closing the adapter server"); }
        await _tcp.DisposeAsync().ConfigureAwait(false);
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
    public static List<int> KillMarked(string variable, string marker)
    {
        var killed = new List<int>();
        if (!OperatingSystem.IsLinux()) return killed;
        foreach (var pid in FindMarked(variable, marker))
        {
            if (pid == Environment.ProcessId) continue;
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

    /// <summary>A zombie waiting for its reaper counts as gone.</summary>
    public static bool IsAlive(int pid)
    {
        try { return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
