using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Python;

namespace CsCode.Platform.Python;

/// <summary>One finished (or killed) run of a small helper process: its exit code (null when killed or not started) and standard output.</summary>
public sealed record PythonProcessResult(int? ExitCode, string Output);

/// <summary>Runs a short helper process with a deadline (an interpreter probe, <c>poetry env info</c>, <c>nix print-dev-env</c>, <c>py -0p</c>). Tests pass a fake.</summary>
public delegate Task<PythonProcessResult> PythonProcessRunner(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken ct);

/// <summary>The probes the environment service runs, and the real process runner.</summary>
public static class PythonProcess
{
    /// <summary>
    /// Isolated mode (<c>-I</c>: no user site-packages, no PYTHON* variables, the working directory not on sys.path) so a folder's own
    /// <c>json.py</c> can never run; prints one JSON line.
    /// </summary>
    public static readonly string[] ProbeArguments =
        ["-I", "-c", "import json,sys;print(json.dumps({'v':'%d.%d.%d'%sys.version_info[:3],'p':sys.prefix,'b':sys.base_prefix}))"];

    /// <summary>Exit 0 when the module named by the last argument can be imported (found, not imported: nothing of it runs).</summary>
    public static string[] ModuleArguments(string module) =>
        ["-I", "-c", "import importlib.util,sys;sys.exit(0 if importlib.util.find_spec(sys.argv[1]) else 3)", module];

    /// <summary>
    /// The environment of a flake's default development shell, as JSON (what <c>nix develop</c> would start, without running its shellHook). The
    /// features are named so a Nix that has not turned flakes on still answers.
    /// </summary>
    public static string[] DevShellArguments(string flakeDirectory) =>
        ["--extra-experimental-features", "nix-command flakes", "print-dev-env", "--json", flakeDirectory];

    /// <summary>The development shell's PATH (<c>variables.PATH.value</c>) from <see cref="DevShellArguments"/>' output; empty when it has none.</summary>
    public static IReadOnlyList<string> ParseDevShellPath(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("variables", out var variables)
                && variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty("PATH", out var path)
                && path.ValueKind == JsonValueKind.Object && path.TryGetProperty("value", out var value) && value.GetString() is { } dirs
                ? dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Version, sys.prefix and sys.base_prefix from a probe's output; null when it is not one.</summary>
    public static (string Version, string Prefix, string BasePrefix)? ParseProbe(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return r.TryGetProperty("v", out var v) && r.TryGetProperty("p", out var p) && r.TryGetProperty("b", out var b)
                && v.GetString() is { } version && p.GetString() is { } prefix && b.GetString() is { } basePrefix
                ? (version, prefix, basePrefix)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The real runner: standard input closed, output collected (at most 1 MB: <c>nix print-dev-env --json</c> prints about 100 KB, its PATH near
    /// the end), the process tree killed when <paramref name="timeout"/> passes
    /// or <paramref name="ct"/> is cancelled. Never throws for a program that cannot start (exit code null).
    /// </summary>
    public static async Task<PythonProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new PythonProcessResult(null, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new PythonProcessResult(null, "");
        }
        process.StandardInput.Close();
        var output = new StringBuilder();
        var stdout = PumpAsync(process.StandardOutput, output);
        var stderr = PumpAsync(process.StandardError, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            lock (output) return new PythonProcessResult(process.ExitCode, output.ToString());
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            ct.ThrowIfCancellationRequested();
            return new PythonProcessResult(null, "");
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder? sink)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                if (sink is not null) lock (sink) { if (sink.Length < 1024 * 1024) sink.Append(buffer, 0, n); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>The interpreter of a virtual environment or conda environment directory (<c>bin/python</c>, <c>Scripts\python.exe</c> or <c>python.exe</c>).</summary>
    public static string EnvironmentExecutable(string environmentDirectory) => OperatingSystem.IsWindows()
        ? File.Exists(Path.Combine(environmentDirectory, "Scripts", "python.exe")) ? Path.Combine(environmentDirectory, "Scripts", "python.exe") : Path.Combine(environmentDirectory, "python.exe")
        : File.Exists(Path.Combine(environmentDirectory, "bin", "python")) ? Path.Combine(environmentDirectory, "bin", "python") : Path.Combine(environmentDirectory, "bin", "python3");

    /// <summary>The kind of an interpreter from where it lives and what it said (a venv's prefix differs from its base prefix).</summary>
    public static PythonEnvironmentKind Classify(string executable, string prefix, string basePrefix, string? pyenvRoot)
    {
        if (!string.Equals(prefix, basePrefix, StringComparison.Ordinal) || File.Exists(Path.Combine(prefix, "pyvenv.cfg"))) return PythonEnvironmentKind.Venv;
        if (Directory.Exists(Path.Combine(prefix, "conda-meta"))) return PythonEnvironmentKind.Conda;
        if (prefix.StartsWith("/nix/store/", StringComparison.Ordinal)) return PythonEnvironmentKind.Nix;
        if (pyenvRoot is not null && IsUnder(prefix, Path.Combine(pyenvRoot, "versions"))) return PythonEnvironmentKind.Pyenv;
        if (prefix.Contains("/Cellar/", StringComparison.Ordinal) || prefix.StartsWith("/opt/homebrew/", StringComparison.Ordinal)
            || prefix.StartsWith("/home/linuxbrew/", StringComparison.Ordinal) || executable.StartsWith("/opt/homebrew/", StringComparison.Ordinal))
            return PythonEnvironmentKind.Homebrew;
        return PythonEnvironmentKind.Global;
    }

    public static bool IsUnder(string path, string directory)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var dir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(dir, comparison) || path.StartsWith(dir + Path.DirectorySeparatorChar, comparison) || path.StartsWith(dir + '/', comparison);
    }
}
