using System.Diagnostics;
using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;

namespace CsCode.Build.CMake;

/// <summary>A program to run: file, arguments, working directory and extra environment.</summary>
internal sealed record CMakeCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null)
{
    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

    internal static string Quote(string a) => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '\\') ? "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'" : a;
}

/// <summary>
/// Runs cmake, the build tool and ctest. Output lines (standard output and standard error, in arrival order per stream) are handed over in
/// batches, never one call per line, so a build that prints thousands of lines costs the receiver a few calls per second. Cancelling kills the
/// whole process tree. Tests substitute a scripted fake.
/// </summary>
internal interface ICMakeProcesses
{
    /// <summary>Runs to completion: the exit code, null when <paramref name="ct"/> cancelled it, -1 when it could not start (the reason is a line).</summary>
    Task<int?> RunAsync(CMakeCommand command, Action<IReadOnlyList<string>> onLines, CancellationToken ct);
}

/// <summary>
/// The real runner: a tracked child process at below-normal priority (on Unix through <c>nice -n 10</c>, which the compilers it starts inherit;
/// on Windows the process priority class), whose output is batched every <see cref="BatchInterval"/> or <see cref="BatchLines"/> lines.
/// Children inherit the login shell's environment (<see cref="LoginShellEnvironment"/>).
/// </summary>
internal sealed class CMakeProcesses(IMemoryMonitor? memory = null) : ICMakeProcesses
{
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);
    public const int BatchLines = 500;

    public async Task<int?> RunAsync(CMakeCommand command, Action<IReadOnlyList<string>> onLines, CancellationToken ct)
    {
        await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
        var nice = !OperatingSystem.IsWindows() && File.Exists("/usr/bin/nice");
        var psi = new ProcessStartInfo(nice ? "/usr/bin/nice" : command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (nice)
        {
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("10");
            psi.ArgumentList.Add(command.FileName);
        }
        foreach (var a in command.Arguments) psi.ArgumentList.Add(a);
        InheritedDotnetRoot.Fold(psi.Environment);
        // compilers colour their diagnostics only for a terminal; keep the output plain so the problem matcher sees it as written
        psi.Environment["CLICOLOR_FORCE"] = null;
        if (command.Environment is not null) foreach (var (k, v) in command.Environment) psi.Environment[k] = v;

        var batch = new Batcher(onLines);
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return -1;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            onLines([$"Could not start {command.FileName}: {ex.Message}"]);
            return -1;
        }
        if (OperatingSystem.IsWindows()) TrySetPriority(process);
        using var tracked = TryTrack(process, Path.GetFileNameWithoutExtension(command.FileName));
        process.StandardInput.Close();
        using var ticker = new CancellationTokenSource();
        var tick = TickAsync(batch, ticker.Token);
        var outTask = PumpAsync(process.StandardOutput, batch);
        var errTask = PumpAsync(process.StandardError, batch);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException) { }
            return null;
        }
        finally
        {
            ticker.Cancel();
            try { await tick.ConfigureAwait(false); } catch (OperationCanceledException) { }
            batch.Flush();
        }
    }

    private static void TrySetPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
    }

    private IDisposable? TryTrack(Process process, string name)
    {
        try { return memory?.TrackChildProcess(name, process.Id); }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task TickAsync(Batcher batch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(BatchInterval, ct).ConfigureAwait(false);
            batch.Flush();
        }
    }

    private static async Task PumpAsync(StreamReader reader, Batcher batch)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) batch.Add(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Collects lines from both reader threads; flushed by the ticker, when it is full, and at the end. The sink is called under the lock so
    /// batches arrive in order; it must only enqueue (the service posts them to its lane).
    /// </summary>
    private sealed class Batcher(Action<IReadOnlyList<string>> sink)
    {
        private readonly object _gate = new();
        private List<string> _lines = [];

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
                if (_lines.Count >= BatchLines) Deliver();
            }
        }

        public void Flush()
        {
            lock (_gate) if (_lines.Count > 0) Deliver();
        }

        private void Deliver()
        {
            var lines = _lines;
            _lines = [];
            sink(lines);
        }
    }
}
