using System.Diagnostics;
using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;

namespace CsCode.Testing.Engine;

/// <summary>A process to start: program, arguments, working directory and extra environment variables.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment = null)
{
    public override string ToString() => FileName + (Arguments.Count > 0 ? " " + string.Join(' ', Arguments.Select(Quote)) : "");

    private static string Quote(string a) => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a;
}

/// <summary>
/// Starts the test tools (dotnet, cargo, node, the test executables). Lines of standard output and standard error are handed over as they come,
/// on the reader threads; cancelling kills the whole process tree. Tests replace it with a scripted fake.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs to completion; the exit code, or null when <paramref name="ct"/> cancelled it (the tree is killed first).</summary>
    Task<int?> RunAsync(ProcessSpec spec, Action<string> stdout, Action<string> stderr, CancellationToken ct);

    /// <summary>Starts the process and returns at once (for a debugger to attach to); the task completes when it exits.</summary>
    Task<int?> StartAsync(ProcessSpec spec, Action<string> stdout, Action<string> stderr, CancellationToken ct) => RunAsync(spec, stdout, stderr, ct);
}

/// <summary>The real runner (System.Diagnostics.Process). Children inherit the login shell's environment (<see cref="LoginShellEnvironment"/>).</summary>
public sealed class ProcessRunner(IMemoryMonitor? memory = null) : IProcessRunner
{
    public async Task<int?> RunAsync(ProcessSpec spec, Action<string> stdout, Action<string> stderr, CancellationToken ct)
    {
        await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // a runner that waits for a key (xUnit's -wait) reads end-of-file, never the user's terminal
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        InheritedDotnetRoot.Fold(psi.Environment);
        if (spec.Environment is not null) foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return -1;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            stderr($"Could not start {spec.FileName}: {ex.Message}");
            return -1;
        }
        using var tracked = TryTrack(process);
        process.StandardInput.Close();
        var outTask = PumpAsync(process.StandardOutput, stdout);
        var errTask = PumpAsync(process.StandardError, stderr);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException) { }
            return null;
        }
    }

    private IDisposable? TryTrack(Process process)
    {
        try { return memory?.TrackChildProcess("test runner", process.Id); }
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

    /// <summary>The executable <paramref name="name"/> on PATH (with Windows' extensions), else null.</summary>
    public static string? FindOnPath(string name)
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>
    /// The dotnet host: <c>DOTNET_HOST_PATH</c> (set when CsCode runs under the SDK), then <c>DOTNET_ROOT</c>, then PATH, then the usual install
    /// directories (~/.dotnet, /usr/local/share/dotnet, /usr/share/dotnet, /usr/lib/dotnet). Plain "dotnet" when none exists (the start then
    /// fails with a message).
    /// </summary>
    public static string FindDotnet()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        foreach (var variable in new[] { InheritedDotnetRoot.ArchRootVariable, InheritedDotnetRoot.RootVariable })
            if (System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root && File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
        if (FindOnPath("dotnet") is { } onPath) return onPath;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, ".dotnet"), "/usr/local/share/dotnet", "/usr/share/dotnet", "/usr/lib/dotnet" })
            if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        return "dotnet";
    }

    /// <summary>cargo on PATH, else ~/.cargo/bin/cargo, else plain "cargo".</summary>
    public static string FindCargo()
    {
        if (FindOnPath("cargo") is { } onPath) return onPath;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(home, ".cargo", "bin", OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");
        return File.Exists(candidate) ? candidate : "cargo";
    }
}
