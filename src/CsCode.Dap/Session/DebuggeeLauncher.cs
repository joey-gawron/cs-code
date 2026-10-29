using System.Diagnostics;
using System.Text;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Session;

/// <summary>
/// Answers the adapter's <c>runInTerminal</c> reverse request. There is no terminal UI yet, so the debuggee is spawned here (kind
/// <c>integrated</c>/<c>external</c> alike) with its stdout/stderr wired to the Debug Console; stdin is closed (EOF). Every spawned
/// process (tree) is killed when the session ends, so a debuggee can never outlive its session.
/// </summary>
internal sealed class DebuggeeLauncher : IDisposable
{
    private readonly DebugLane _lane;
    private readonly ILogger _logger;
    private readonly Action<string, string> _output; // (category, text) — called on the lane
    private readonly List<Process> _processes = new();
    private readonly object _gate = new();
    private bool _disposed;

    public DebuggeeLauncher(DebugLane lane, ILogger logger, Action<string, string> output)
    {
        _lane = lane;
        _logger = logger;
        _output = output;
    }

    public IReadOnlyList<int> LiveProcessIds
    {
        get
        {
            lock (_gate) return [.. _processes.Where(p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } }).Select(p => p.Id)];
        }
    }

    public async Task<DapRunInTerminalResponse> LaunchAsync(DapRunInTerminalArguments a)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DebuggeeLauncher));
        if (a.Args.Count == 0) throw new ArgumentException("runInTerminal without a command line");
        var psi = new ProcessStartInfo(a.Args[0])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (var i = 1; i < a.Args.Count; i++) psi.ArgumentList.Add(a.Args[i]);
        if (!string.IsNullOrEmpty(a.Cwd) && Directory.Exists(a.Cwd)) psi.WorkingDirectory = a.Cwd;
        if (a.Env is not null)
            foreach (var (k, v) in a.Env)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }

        // fork/exec can take milliseconds: keep it off the lane. We resume on the lane afterwards (context captured).
        var process = await Task.Run(() => Process.Start(psi)) ?? throw new InvalidOperationException($"Could not start '{a.Args[0]}'");
        lock (_gate)
        {
            if (_disposed) { SafeKill(process); process.Dispose(); throw new ObjectDisposedException(nameof(DebuggeeLauncher)); }
            _processes.Add(process);
        }
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        var stdout = new BoundedTextPump(_lane, text => _output("stdout", text));
        var stderr = new BoundedTextPump(_lane, text => _output("stderr", text));
        _ = PumpAsync(process, process.StandardOutput, stdout);
        _ = PumpAsync(process, process.StandardError, stderr);
        _ = WatchExitAsync(process);
        return new DapRunInTerminalResponse { ProcessId = process.Id, ShellProcessId = process.Id };
    }

    private async Task PumpAsync(Process process, StreamReader reader, BoundedTextPump pump)
    {
        var buf = new char[4096];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                if (n <= 0) break;
                pump.Append(buf.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug("Debuggee pipe closed: {Message}", ex.Message);
        }
        finally { pump.Complete(); }
    }

    private async Task WatchExitAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
        lock (_gate)
        {
            if (_processes.Remove(process)) process.Dispose();
        }
    }

    private static void SafeKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Kills every spawned process (tree). The launcher stays usable (a restarted session spawns again).</summary>
    public void KillAll()
    {
        Process[] all;
        lock (_gate)
        {
            all = [.. _processes];
            _processes.Clear();
        }
        foreach (var p in all)
        {
            SafeKill(p); // SIGKILL to the whole tree; the runtime reaps the child asynchronously (never block the lane waiting for it)
            p.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        KillAll();
    }
}
