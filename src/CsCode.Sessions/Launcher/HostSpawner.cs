using System.Diagnostics;

namespace CsCode.Sessions.Launcher;

/// <summary>A CsCode process the launcher started.</summary>
public interface ISpawnedHost
{
    int Pid { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
}

/// <summary>Starts CsCode processes (session hosts, the warm spare). Replaced by tests.</summary>
public interface IHostSpawner
{
    ISpawnedHost Spawn(IReadOnlyList<string> args);
}

/// <summary>
/// Starts this same executable (<see cref="Environment.ProcessPath"/>; through <c>dotnet cscode.dll</c> when the launcher itself runs that way) with
/// the host arguments. The child gets no terminal: stdin/stdout/stderr are pipes the launcher closes at once (the host logs to its log file and
/// detaches from the terminal's session itself), and its working directory is the user's home so it never pins the launcher's directory.
/// </summary>
public sealed class ProcessHostSpawner : IHostSpawner
{
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefix;
    private readonly IReadOnlyList<string> _suffix;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <param name="suffix">Arguments appended to every host command line (e.g. <c>--headless-test</c>, inherited from the launcher).</param>
    public ProcessHostSpawner(string fileName, IReadOnlyList<string>? prefix = null, IReadOnlyList<string>? suffix = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _fileName = fileName;
        _prefix = prefix ?? [];
        _suffix = suffix ?? [];
        _environment = environment;
    }

    /// <summary>The spawner for the current executable.</summary>
    public static ProcessHostSpawner ForCurrentProcess(IReadOnlyList<string>? suffix = null)
    {
        var processPath = Environment.ProcessPath ?? "cscode";
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet cscode.dll ...: start the same assembly the same way.
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entry)) return new ProcessHostSpawner(processPath, [entry], suffix);
        }
        return new ProcessHostSpawner(processPath, [], suffix);
    }

    public ISpawnedHost Spawn(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home && Directory.Exists(home) ? home : Path.GetTempPath(),
        };
        foreach (var a in _prefix) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var a in _suffix) psi.ArgumentList.Add(a);
        if (_environment is not null)
            foreach (var (k, v) in _environment) psi.Environment[k] = v;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + _fileName);
        // The host writes nothing we need; closing our ends means its console writes are dropped instead of filling a pipe.
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
        return new SpawnedProcess(process);
    }

    private sealed class SpawnedProcess(Process process) : ISpawnedHost
    {
        public int Pid { get; } = process.Id;
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }
        public int? ExitCode => HasExited ? process.ExitCode : null;
    }
}
