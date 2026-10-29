using System.Diagnostics;
using System.Text;

namespace CsCode.Remote.Transport;

/// <summary>How to start a child process for a transport (never through a shell: program + argument list).</summary>
public sealed record ProcessLaunch(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Variables to set (value) or remove (null) in the child's environment.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Start through <c>nice -n 10</c> (Linux/macOS) so the whole child tree runs below normal priority.</summary>
    public bool BelowNormalPriority { get; init; }

    public override string ToString() => FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
}

/// <summary>
/// A running child process seen as a duplex byte stream: <see cref="Input"/> is its stdout, <see cref="Output"/> its stdin. Stderr is drained on its own
/// thread into a bounded ring (<see cref="StderrTail"/>) and handed line by line to <see cref="StderrLine"/>. Disposing kills the whole process tree
/// (never anything else) and waits briefly for it to exit.
/// </summary>
public sealed class ProcessChannel : IAsyncDisposable
{
    private const int StderrLines = 64;

    private readonly Process _process;
    private readonly Queue<string> _stderr = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private ProcessChannel(Process process)
    {
        _process = process;
        Input = process.StandardOutput.BaseStream;
        Output = process.StandardInput.BaseStream;
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public int ProcessId => _process.Id;

    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Called on the stderr drain thread for every stderr line (ssh's own errors, server start-up failures).</summary>
    public Action<string>? StderrLine { get; set; }

    public string StderrTail { get { lock (_stderr) return string.Join('\n', _stderr); } }

    /// <summary>Starts the process. Blocking (process creation): call it from a background thread.</summary>
    public static ProcessChannel Start(ProcessLaunch launch, Action<string>? stderrLine = null)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = launch.WorkingDirectory ?? "",
        };
        // nice at exec time, not a later setpriority: on Linux priority is per thread, and threads/children created before a late change would escape it
        var nice = File.Exists("/usr/bin/nice") ? "/usr/bin/nice" : File.Exists("/bin/nice") ? "/bin/nice" : null;
        var useNice = launch.BelowNormalPriority && !OperatingSystem.IsWindows() && nice is not null;
        if (useNice)
        {
            psi.FileName = nice!;
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add(launch.FileName);
        }
        else psi.FileName = launch.FileName;
        foreach (var a in launch.Arguments) psi.ArgumentList.Add(a);
        if (launch.Environment is { } env)
            foreach (var (k, v) in env)
            {
                if (v is null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var channel = new ProcessChannel(process) { StderrLine = stderrLine };
        process.Exited += (_, _) => channel.OnExited();
        process.Start();
        if (launch.BelowNormalPriority && !useNice)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
        }
        var drain = new Thread(channel.DrainStderr) { IsBackground = true, Name = "CsCode.Remote-stderr", Priority = ThreadPriority.BelowNormal };
        drain.Start();
        if (process.HasExited) channel.OnExited();
        return channel;
    }

    private void OnExited()
    {
        int code;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        _exited.TrySetResult(code);
    }

    private void DrainStderr()
    {
        try
        {
            var reader = _process.StandardError;
            while (reader.ReadLine() is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Enqueue(line.Length > 2000 ? line[..2000] : line);
                    while (_stderr.Count > StderrLines) _stderr.Dequeue();
                }
                try { StderrLine?.Invoke(line); } catch (Exception) { /* a logging callback must not end the drain */ }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Closes stdin (an orderly "goodbye" for servers that exit at EOF) without killing anything.</summary>
    public void CloseOutput()
    {
        try { Output.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>Waits up to <paramref name="grace"/> for the process to exit on its own, then kills its tree.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (!HasExited) await Task.WhenAny(Exited, Task.Delay(grace)).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CloseOutput();
        if (!HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        try { Input.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _process.Dispose();
    }
}
