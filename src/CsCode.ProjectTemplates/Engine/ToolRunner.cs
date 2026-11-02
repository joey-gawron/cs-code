using System.Diagnostics;
using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;

namespace CsCode.ProjectTemplates.Engine;

/// <summary>A tool to run: program, arguments, working directory and extra environment variables.</summary>
public sealed record ToolRun(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>The command line as the Output channel shows it (arguments with spaces quoted).</summary>
    public override string ToString() => Path.GetFileName(FileName) + (Arguments.Count > 0 ? " " + string.Join(' ', Arguments.Select(Quote)) : "");

    private static string Quote(string a) => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a;
}

/// <summary>How a tool ended: its exit code (-1: it could not be started) and its output, standard error included, capped at <see cref="ToolRunner.MaxCapturedChars"/>.</summary>
public sealed record ToolResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Starts the generators' tools (cargo, dotnet, cmake, python, git, zig). Lines of standard output and standard error are handed to
/// <c>onLine</c> as they come, on the reader threads. Cancelling kills the whole process tree and throws <see cref="OperationCanceledException"/>.
/// Tests replace it with a scripted fake.
/// </summary>
public interface IToolRunner
{
    Task<ToolResult> RunAsync(ToolRun run, Action<string>? onLine, CancellationToken ct);
}

/// <summary>
/// The real runner (System.Diagnostics.Process): the child inherits the login shell's environment (<see cref="LoginShellEnvironment"/>), has no
/// terminal (standard input is closed at once, so a prompt reads end of file), is tracked by the memory monitor while it runs and never outlives
/// a cancel.
/// </summary>
public sealed class ToolRunner(IMemoryMonitor? memory = null) : IToolRunner
{
    /// <summary>The output kept for the caller to parse (the Output channel gets every line); <c>dotnet new list</c> is ~6 KB.</summary>
    public const int MaxCapturedChars = 1 << 20;

    public async Task<ToolResult> RunAsync(ToolRun run, Action<string>? onLine, CancellationToken ct)
    {
        await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(run.FileName)
        {
            WorkingDirectory = run.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in run.Arguments) psi.ArgumentList.Add(a);
        InheritedDotnetRoot.Fold(psi.Environment);
        // The tools' first-run banners and telemetry notices are noise in a generated project's log; colours are escape codes in the Output window.
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = psi.Environment.TryGetValue("DOTNET_CLI_TELEMETRY_OPTOUT", out var optOut) && optOut is { Length: > 0 } ? optOut : "1";
        psi.Environment["CARGO_TERM_COLOR"] = "never";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (run.Environment is not null) foreach (var (k, v) in run.Environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new ToolResult(-1, $"{run.FileName} did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var message = $"Could not start {run.FileName}: {ex.Message}";
            onLine?.Invoke(message);
            return new ToolResult(-1, message);
        }
        using var tracked = TryTrack(process, Path.GetFileNameWithoutExtension(run.FileName));
        process.StandardInput.Close();
        var captured = new StringBuilder();
        void Sink(string line)
        {
            lock (captured)
                if (captured.Length < MaxCapturedChars) captured.Append(line).Append('\n');
            onLine?.Invoke(line);
        }
        var outTask = PumpAsync(process.StandardOutput, Sink);
        var errTask = PumpAsync(process.StandardError, Sink);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            // A grandchild that keeps the pipes open (a build server) must not hold the step: what was read by then is the output.
            await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException) { }
            throw;
        }
        lock (captured) return new ToolResult(process.ExitCode, captured.ToString());
    }

    private IDisposable? TryTrack(Process process, string name)
    {
        try { return memory?.TrackChildProcess("new project: " + name, process.Id); }
        catch (InvalidOperationException) { return null; }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> sink)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) sink(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
