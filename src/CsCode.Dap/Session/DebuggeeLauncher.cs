using System.Diagnostics;
using System.Text;
using CsCode.Core.Terminal;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Session;

/// <summary>
/// Answers the adapter's <c>runInTerminal</c> reverse request. With the integrated terminal composed (<see cref="ITerminalService"/>), kind
/// <c>integrated</c> runs the debuggee in a terminal tab named after the launch configuration, started without a shell in between (the
/// process id returned is the debuggee's), and reused for the next run once its program has exited, as VS Code reuses its debug terminal.
/// Otherwise (no terminal, or kind <c>external</c>: CsCode opens no external terminal window) the debuggee is spawned here with its
/// stdout/stderr wired to the Debug Console; stdin is closed (EOF). Every spawned process (tree) and every terminal debuggee is killed when the
/// session ends, so a debuggee can never outlive its session (the terminal stays, with the program's output and its exit code).
/// </summary>
internal sealed class DebuggeeLauncher : IDisposable
{
    private readonly DebugLane _lane;
    private readonly ILogger _logger;
    private readonly Action<string, string> _output; // (category, text) — called on the lane
    private readonly List<Process> _processes = new();
    /// <summary>The terminals running this session's debuggees, with the debuggee's pid: a terminal is reused for the next run (another session's).</summary>
    private readonly List<(ITerminal Terminal, int Pid)> _terminals = new();
    private readonly Func<ITerminalService?>? _terminalService;
    private readonly object _gate = new();
    private bool _disposed;

    public DebuggeeLauncher(DebugLane lane, ILogger logger, Action<string, string> output, Func<ITerminalService?>? terminals = null)
    {
        _lane = lane;
        _logger = logger;
        _output = output;
        _terminalService = terminals;
    }

    public IReadOnlyList<int> LiveProcessIds
    {
        get
        {
            lock (_gate) return [.. _processes.Where(p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } }).Select(p => p.Id)];
        }
    }

    public async Task<DapRunInTerminalResponse> LaunchAsync(DapRunInTerminalArguments a, string? sessionName = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DebuggeeLauncher));
        if (a.Args.Count == 0) throw new ArgumentException("runInTerminal without a command line");
        if (!string.Equals(a.Kind, "external", StringComparison.OrdinalIgnoreCase) && _terminalService?.Invoke() is { } terminals)
            return await LaunchInTerminalAsync(terminals, a, sessionName, ct);
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
        DotnetRootFold.Apply(psi.Environment, a.Env?.Keys);

        // fork/exec can take milliseconds: keep it off the lane. We resume on the lane afterwards (context captured).
        var process = await Task.Run(() => Process.Start(psi)) ?? throw new InvalidOperationException($"Could not start '{a.Args[0]}'");
        lock (_gate)
        {
            if (_disposed) { SafeKill(process); process.Dispose(); throw new ObjectDisposedException(nameof(DebuggeeLauncher)); }
            _processes.Add(process);
        }
        var pid = process.Id; // read FIRST: a short-lived debuggee may be reaped (and its Process object disposed) any moment now
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        var stdout = new BoundedTextPump(_lane, text => _output("stdout", text));
        var stderr = new BoundedTextPump(_lane, text => _output("stderr", text));
        var pumps = Task.WhenAll(PumpAsync(process, process.StandardOutput, stdout), PumpAsync(process, process.StandardError, stderr));
        _ = WatchExitAsync(process, pumps);
        return new DapRunInTerminalResponse { ProcessId = pid, ShellProcessId = pid };
    }

    private async Task<DapRunInTerminalResponse> LaunchInTerminalAsync(ITerminalService terminals, DapRunInTerminalArguments a, string? sessionName, CancellationToken ct)
    {
        var terminal = await terminals.CreateAsync(new TerminalCreateOptions
        {
            Name = string.IsNullOrWhiteSpace(a.Title) ? sessionName ?? "Debug" : a.Title,
            ShellPath = a.Args[0],
            ShellArgs = a.Args.Skip(1).ToArray(),
            Cwd = string.IsNullOrEmpty(a.Cwd) ? null : a.Cwd,
            // The adapter's env only, as a terminal's caller variables: a local terminal folds CsCode's DOTNET_ROOT_<ARCH> in what it inherits,
            // before the user's terminal.integrated.env and inheritEnv apply (TerminalLaunchResolver); a remote workspace's terminal starts from
            // the remote host's environment, where this machine's means nothing.
            Env = a.Env,
            IsTransient = true,
            ReuseWhenIdle = true,
            Show = true,
            PreserveFocus = true, // the editor keeps the keyboard: the user is about to hit a breakpoint there
        }, ct).ConfigureAwait(false);
        if (terminal.ProcessId is not { } pid || terminal.HasExited)
            throw new InvalidOperationException($"Could not start '{a.Args[0]}' in the terminal.");
        lock (_gate)
        {
            if (_disposed) { terminal.KillProcess(); throw new ObjectDisposedException(nameof(DebuggeeLauncher)); }
            _terminals.RemoveAll(t => t.Terminal.HasExited || t.Terminal.ProcessId != t.Pid);
            _terminals.Add((terminal, pid));
        }
        _logger.LogDebug("Debuggee {Program} runs in terminal {Terminal} (pid {Pid})", a.Args[0], terminal.Id, pid);
        return new DapRunInTerminalResponse { ProcessId = pid };
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

    /// <summary>Disposes the Process only after its output has been fully drained (never truncate a fast program's output).</summary>
    private async Task WatchExitAsync(Process process, Task pumps)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); // a grandchild holding the pipes open must not pin us
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException) { }
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
        (ITerminal Terminal, int Pid)[] terminals;
        lock (_gate)
        {
            all = [.. _processes];
            _processes.Clear();
            terminals = [.. _terminals];
            _terminals.Clear();
        }
        // The terminal stays (its buffer and exit code); only OUR debuggee goes. Once it ended, the terminal may already run the next run's
        // debuggee (it is reused when idle, and this session can end late — js-debug's parent session ends after its program's): never kill that.
        foreach (var (terminal, pid) in terminals)
            if (!terminal.HasExited && terminal.ProcessId == pid) terminal.KillProcess();
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
