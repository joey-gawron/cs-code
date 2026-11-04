using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;

namespace CsCode.Platform.Python;

/// <summary>
/// <see cref="IPythonEnvironmentService"/> (docs/PYTHON.md §1). Discovery looks, in this order, at the folder's <c>.venv</c> / <c>venv</c> /
/// <c>env</c> (a <c>pyvenv.cfg</c> marks one), the <c>python3</c> of its Nix flake's development shell (<c>nix print-dev-env</c>, only when
/// <c>nix</c> exists and a flake.nix is in the folder or above it in its repository), <c>VIRTUAL_ENV</c>, <c>CONDA_PREFIX</c>, Poetry's environment
/// (<c>poetry env info -p</c>, only when <c>poetry</c> exists and the folder's pyproject.toml uses it), conda's environments (<c>~/.conda/environments.txt</c>, the base of
/// <c>CONDA_EXE</c>), pyenv's versions, Homebrew's <c>python3*</c>, python.org's framework builds, <c>python3</c> / <c>python</c> on PATH, and
/// <c>py -0p</c> on Windows. The file system part runs on the IO lane; every interpreter is then asked for its version and prefix
/// (<see cref="PythonProcess.ProbeArguments"/>), at most four at a time, each killed after <see cref="ProbeTimeout"/>. Interpreters that do not answer
/// are dropped, and two paths to one interpreter (the same <c>sys.prefix</c>) are listed once. Results are cached per folder until a refresh.
/// <para>
/// Discovery waits for the login shell's environment first (<see cref="LoginShellEnvironment"/>): an app started from the Dock has launchd's PATH.
/// On macOS <c>/usr/bin/python3</c> is used only when the Command Line Tools' (or Xcode's) Python is installed: without them, running it opens
/// the "install the command line developer tools" dialog.
/// </para>
/// </summary>
public sealed class PythonEnvironmentService : IPythonEnvironmentService, IDisposable
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    /// <summary>The bound on <c>nix print-dev-env</c>: about 2 s once Nix has cached the flake's evaluation, longer while it first builds the shell.</summary>
    public static readonly TimeSpan NixTimeout = TimeSpan.FromSeconds(30);
    private const string StateKeyPrefix = "python.interpreter:";
    private const int MaxParallelProbes = 4;
    private const int MaxModuleCacheEntries = 256;

    private readonly IoLane _io;
    private readonly IWorkspaceService? _workspace;
    private readonly IConfigurationService? _config;
    private readonly IStateStorage? _state;
    private readonly ILogger? _log;
    private readonly PythonProcessRunner _run;
    private readonly Func<string, string?> _env;
    private readonly bool _waitForLoginShell;
    private readonly DisposableStore _store = new();
    private readonly Emitter<Uri?> _activeChanged = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<IReadOnlyList<PythonEnvironment>>> _discovered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PythonEnvironment?> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<PythonEnvironment?>> _resolving = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Executable, string Module), bool> _modules = new();
    private readonly CancellationTokenSource _disposed = new();

    public PythonEnvironmentService(IoLane io, IWorkspaceService? workspace = null, IConfigurationService? config = null, IStateStorage? state = null,
        ILogger? log = null, PythonProcessRunner? runner = null, Func<string, string?>? environment = null)
    {
        _io = io;
        _workspace = workspace;
        _config = config;
        _state = state;
        _log = log;
        _run = runner ?? PythonProcess.RunAsync;
        _env = environment ?? Environment.GetEnvironmentVariable;
        _waitForLoginShell = environment is null;
        if (config is not null)
        {
            _store.Add(config.RegisterProperties(PythonSettingKeys.Properties));
            _store.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(PythonSettingKeys.DefaultInterpreterPath)) Invalidate(null);
            }));
        }
        if (workspace is not null) _store.Add(workspace.FoldersChanged.Subscribe(_ =>
        {
            lock (_gate) _discovered.Clear();
            Invalidate(null);
        }));
    }

    public IEventSource<Uri?> ActiveChanged => _activeChanged;

    // ---- folders ----

    /// <summary>The workspace folder holding <paramref name="resource"/> (the deepest one), else the first folder, else the resource's own directory.</summary>
    internal Uri? FolderOf(Uri? resource)
    {
        var folders = _workspace?.Folders ?? [];
        if (resource is null) return folders.Count > 0 ? folders[0] : null;
        if (!resource.IsFile) return null;
        var path = resource.LocalPath;
        var best = folders.Where(f => f.IsFile && PythonProcess.IsUnder(path, f.LocalPath)).OrderByDescending(f => f.LocalPath.Length).FirstOrDefault();
        if (best is not null) return best;
        if (Directory.Exists(path)) return resource;
        return folders.Count > 0 ? folders[0] : Path.GetDirectoryName(path) is { } dir ? new Uri(dir) : null;
    }

    private static string KeyOf(Uri? folder) => folder?.LocalPath ?? "";

    // ---- active ----

    public PythonEnvironment? GetActive(Uri? resource)
    {
        var folder = FolderOf(resource);
        if (_active.TryGetValue(KeyOf(folder), out var env)) return env;
        if (!_disposed.IsCancellationRequested) _ = ResolveActiveAsync(folder); // known later: ActiveChanged fires
        return null;
    }

    public async ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default)
    {
        var folder = FolderOf(resource);
        if (_active.TryGetValue(KeyOf(folder), out var env)) return env;
        return await ResolveActiveAsync(folder).WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<PythonEnvironment?> ResolveActiveAsync(Uri? folder)
    {
        var key = KeyOf(folder);
        return _resolving.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var env = await ComputeActiveAsync(folder, _disposed.Token).ConfigureAwait(false);
                var changed = !_active.TryGetValue(key, out var old) || old != env;
                _active[key] = env;
                if (changed) _activeChanged.Fire(folder);
                return env;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Choosing the Python interpreter for {Folder} failed", folder);
                return null;
            }
            finally
            {
                _resolving.TryRemove(key, out _);
            }
        }));
    }

    private async Task<PythonEnvironment?> ComputeActiveAsync(Uri? folder, CancellationToken ct)
    {
        // 1. the folder's pick
        if (_state?.Get<string>(StateKeyPrefix + KeyOf(folder), StorageScope.Workspace) is { Length: > 0 } picked)
        {
            if (await FindOrProbeAsync(folder, picked, ct).ConfigureAwait(false) is { } env) return env;
            _log?.LogInformation("The Python interpreter picked for {Folder} ({Path}) no longer works; choosing another", folder, picked);
        }
        // 2. python.defaultInterpreterPath (other than its default "python", which means: the best found)
        if (_config?.Get<string>(PythonSettingKeys.DefaultInterpreterPath) is { Length: > 0 } setting && setting != "python" && setting != "python3")
        {
            var path = ExpandPath(setting, folder);
            if (await FindOrProbeAsync(folder, path, ct).ConfigureAwait(false) is { } env) return env;
            _log?.LogWarning("{Setting} ({Path}) is not a working Python interpreter", PythonSettingKeys.DefaultInterpreterPath, path);
        }
        // 3. the best one found
        var found = await DiscoverAsync(folder, refresh: false, ct).ConfigureAwait(false);
        return found.Count > 0 ? found[0] : null;
    }

    private async Task<PythonEnvironment?> FindOrProbeAsync(Uri? folder, string executable, CancellationToken ct)
    {
        var found = await DiscoverAsync(folder, refresh: false, ct).ConfigureAwait(false);
        return found.FirstOrDefault(e => SamePath(e.Executable, executable)) ?? await ProbeAsync(executable, ct).ConfigureAwait(false);
    }

    public async ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default)
    {
        var folder = FolderOf(resource);
        var key = StateKeyPrefix + KeyOf(folder);
        if (executable is null) _state?.Remove(key, StorageScope.Workspace);
        else _state?.Set(key, executable, StorageScope.Workspace);
        _active.TryRemove(KeyOf(folder), out _);
        await ResolveActiveAsync(folder).WaitAsync(ct).ConfigureAwait(false);
        _activeChanged.Fire(folder); // even when the result is the same interpreter: the pick itself changed
    }

    private void Invalidate(Uri? folder)
    {
        if (folder is null) _active.Clear();
        else _active.TryRemove(KeyOf(folder), out _);
        _activeChanged.Fire(folder);
    }

    // ---- discovery ----

    public async ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default)
    {
        var folder = resource is null || _workspace?.Folders.Contains(resource) == true ? resource ?? FolderOf(null) : FolderOf(resource);
        var key = KeyOf(folder);
        Task<IReadOnlyList<PythonEnvironment>> task;
        lock (_gate)
        {
            if (refresh || !_discovered.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled)
            {
                if (refresh) _modules.Clear();
                task = DiscoverCoreAsync(folder, _disposed.Token);
                _discovered[key] = task;
            }
        }
        var result = await task.WaitAsync(ct).ConfigureAwait(false);
        if (refresh) Invalidate(folder);
        return result;
    }

    /// <summary>
    /// A candidate interpreter before it was asked: where it came from decides its kind, its name and its <see cref="Rank"/> for the default choice
    /// (0 the folder's own virtual environment, 1 its Nix development shell's, 2 <c>VIRTUAL_ENV</c>, 3 <c>CONDA_PREFIX</c>, 4 Poetry's, 5 the first
    /// <c>python3</c> on PATH, 6 the rest).
    /// </summary>
    internal sealed record Candidate(string Executable, PythonEnvironmentKind Kind, string? Name, int Rank = Candidate.Other)
    {
        public const int Other = 6;
    }

    private async Task<IReadOnlyList<PythonEnvironment>> DiscoverCoreAsync(Uri? folder, CancellationToken ct)
    {
        if (_waitForLoginShell) await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
        var byProcess = Task.Run(() => FindByProcessAsync(folder, ct), ct); // nix takes seconds: it runs while the file system is searched
        var candidates = await _io.RunAsync(token => FindCandidates(folder, token), ct).ConfigureAwait(false);
        candidates.AddRange(await byProcess.ConfigureAwait(false));
        using var gate = new SemaphoreSlim(MaxParallelProbes);
        var probes = candidates.Select(async c =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return (Candidate: c, Env: await ProbeAsync(c, ct).ConfigureAwait(false)); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var list = new List<(PythonEnvironment Env, Candidate Candidate, int Index)>();
        // two paths to one interpreter (the same prefix): the one with the better rank stays (PATH's /usr/local/bin/python3 over the framework's own path)
        foreach (var (r, i) in results.Select((r, i) => (r, i)).OrderBy(x => x.r.Candidate.Rank).ThenBy(x => x.i))
            if (r.Env is { } env && seen.Add(env.Prefix ?? env.Executable)) list.Add((env, r.Candidate, i));
        _log?.LogInformation("Found {Count} Python interpreter(s) for {Folder} ({Candidates} candidates)", list.Count, folder, candidates.Count);
        // The default choice first (by rank, then as found); the rest by kind, newest version first.
        return list
            .OrderBy(x => x.Candidate.Rank)
            .ThenBy(x => x.Candidate.Rank < Candidate.Other ? x.Index : 0)
            .ThenBy(x => (int)x.Env.Kind)
            .ThenByDescending(x => ParseVersion(x.Env.Version))
            .ThenBy(x => x.Index)
            .Select(x => x.Env)
            .ToList();
    }

    private static Version ParseVersion(string? v) => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);

    /// <summary>The file-system part of discovery (IO lane): existing executables only, nothing run.</summary>
    internal List<Candidate> FindCandidates(Uri? folder, CancellationToken ct)
    {
        var list = new List<Candidate>();
        var firstOnPath = true;
        void Add(string? exe, PythonEnvironmentKind kind, string? name, int rank = Candidate.Other)
        {
            if (exe is not null && File.Exists(exe) && !list.Any(c => SamePath(c.Executable, exe))) list.Add(new Candidate(exe, kind, name, rank));
        }
        void AddEnvironment(string? dir, PythonEnvironmentKind kind, string? name = null, int rank = Candidate.Other)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            Add(PythonProcess.EnvironmentExecutable(dir), kind, name ?? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, '/')), rank);
        }

        if (folder is { IsFile: true })
            foreach (var name in new[] { ".venv", "venv", "env" })
            {
                var dir = Path.Combine(folder.LocalPath, name);
                if (File.Exists(Path.Combine(dir, "pyvenv.cfg"))) AddEnvironment(dir, PythonEnvironmentKind.Venv, name, rank: 0);
            }
        AddEnvironment(_env("VIRTUAL_ENV"), PythonEnvironmentKind.Venv, rank: 2);
        if (_env("CONDA_PREFIX") is { Length: > 0 } condaPrefix)
            AddEnvironment(condaPrefix, PythonEnvironmentKind.Conda, _env("CONDA_DEFAULT_ENV") is { Length: > 0 } condaName ? condaName : null, rank: 3);
        ct.ThrowIfCancellationRequested();

        var home = _env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // conda: the environments it registered, and the installation CONDA_EXE belongs to
        var condaEnvs = Path.Combine(home, ".conda", "environments.txt");
        if (File.Exists(condaEnvs))
            foreach (var line in SafeReadLines(condaEnvs).Take(200))
                AddEnvironment(line.Trim(), PythonEnvironmentKind.Conda);
        if (_env("CONDA_EXE") is { Length: > 0 } condaExe && Path.GetDirectoryName(Path.GetDirectoryName(condaExe)) is { } condaBase)
        {
            AddEnvironment(condaBase, PythonEnvironmentKind.Conda, "base");
            foreach (var dir in SafeDirectories(Path.Combine(condaBase, "envs")).Take(200)) AddEnvironment(dir, PythonEnvironmentKind.Conda);
        }
        // pyenv
        var pyenvRoot = _env("PYENV_ROOT") is { Length: > 0 } pr ? pr : Path.Combine(home, ".pyenv");
        foreach (var dir in SafeDirectories(Path.Combine(pyenvRoot, "versions")).Take(200))
            Add(PythonProcess.EnvironmentExecutable(dir), PythonEnvironmentKind.Pyenv, Path.GetFileName(dir));
        ct.ThrowIfCancellationRequested();

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            foreach (var brew in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin" })
                foreach (var exe in SafeFiles(brew, "python3*").Where(IsVersionedPython).OrderBy(f => f, StringComparer.Ordinal))
                    if (IsHomebrew(exe)) Add(exe, PythonEnvironmentKind.Homebrew, null);
        }
        if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in SafeDirectories("/Library/Frameworks/Python.framework/Versions").Where(d => Path.GetFileName(d) != "Current"))
                Add(Path.Combine(dir, "bin", "python3"), PythonEnvironmentKind.Global, null);
        }
        // PATH: the order the user's shell would use
        foreach (var dir in (_env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : ["python3", "python"])
            {
                var exe = Path.Combine(dir, name);
                if (OperatingSystem.IsWindows() && exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) continue; // the Store's install stub
                if (!UsableSystemPython(exe)) continue;
                if (!File.Exists(exe)) continue;
                // the first python3 on PATH is what a terminal would run: the default when nothing better is there (even when found above already)
                if (firstOnPath && (name.StartsWith("python3", StringComparison.Ordinal) || OperatingSystem.IsWindows()))
                {
                    firstOnPath = false;
                    var known = list.FindIndex(c => SamePath(c.Executable, exe));
                    if (known >= 0) list[known] = list[known] with { Rank = Math.Min(list[known].Rank, 5) };
                    else Add(exe, IsHomebrew(exe) ? PythonEnvironmentKind.Homebrew : PythonEnvironmentKind.Global, null, rank: 5);
                    continue;
                }
                Add(exe, IsHomebrew(exe) ? PythonEnvironmentKind.Homebrew : PythonEnvironmentKind.Global, null);
            }
        if (!OperatingSystem.IsWindows() && UsableSystemPython("/usr/bin/python3")) Add("/usr/bin/python3", PythonEnvironmentKind.Global, null);
        return list;
    }

    /// <summary>The parts of discovery that need a program to answer: Poetry's environment, the Nix development shell's and the Windows launcher's list.</summary>
    private async Task<List<Candidate>> FindByProcessAsync(Uri? folder, CancellationToken ct)
    {
        var list = new List<Candidate>();
        if (folder is { IsFile: true } && FindOnPath("poetry") is { } poetry && UsesPoetry(folder.LocalPath))
        {
            var r = await _run(poetry, ["env", "info", "-p"], folder.LocalPath, ProbeTimeout, ct).ConfigureAwait(false);
            if (r.ExitCode == 0 && r.Output.Trim() is { Length: > 0 } dir && Directory.Exists(dir))
                list.Add(new Candidate(PythonProcess.EnvironmentExecutable(dir), PythonEnvironmentKind.Poetry, Path.GetFileName(dir), Rank: 4));
        }
        if (folder is { IsFile: true } && !OperatingSystem.IsWindows() && FlakeDirectory(folder.LocalPath) is { } flake && FindOnPath("nix") is { } nix)
        {
            var r = await _run(nix, PythonProcess.DevShellArguments(flake), flake, NixTimeout, ct).ConfigureAwait(false);
            // the first python3 on the shell's PATH: what `python3` runs inside `nix develop`
            if (r.ExitCode == 0 && PythonProcess.ParseDevShellPath(r.Output).Select(d => Path.Combine(d, "python3")).FirstOrDefault(File.Exists) is { } python)
                list.Add(new Candidate(python, PythonEnvironmentKind.Nix, Path.GetFileName(flake), Rank: 1));
            else
                _log?.LogInformation("No Python from the Nix development shell of {Flake} (nix print-dev-env: {Exit})", flake,
                    r.ExitCode is { } exit ? "exit " + exit : "timed out");
        }
        if (OperatingSystem.IsWindows() && FindOnPath("py.exe") is { } py)
        {
            var r = await _run(py, ["-0p"], null, ProbeTimeout, ct).ConfigureAwait(false);
            foreach (var line in r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var at = line.IndexOf(":\\", StringComparison.Ordinal);
                if (at > 0 && line[(at - 1)..] is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    list.Add(new Candidate(path, PythonEnvironmentKind.Global, null));
            }
        }
        return list;
    }

    /// <summary>
    /// The flake whose development shell is the folder's: its own flake.nix, else the nearest one above it up to its git repository's root (where
    /// <c>nix develop</c> stops looking). Outside a repository only the folder's own counts, never one in a parent directory.
    /// </summary>
    internal static string? FlakeDirectory(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        string? nearest = null;
        for (var dir = folder; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            nearest ??= File.Exists(Path.Combine(dir, "flake.nix")) ? dir : null;
            if (Path.Exists(Path.Combine(dir, ".git"))) return nearest;
        }
        return File.Exists(Path.Combine(folder, "flake.nix")) ? folder : null;
    }

    private static bool UsesPoetry(string folder)
    {
        var pyproject = Path.Combine(folder, "pyproject.toml");
        try
        {
            return File.Exists(pyproject) && File.ReadLines(pyproject).Take(2000).Any(l => l.TrimStart().StartsWith("[tool.poetry", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>python3, python3.13 (not python3-config, python3.13-intel64…).</summary>
    private static bool IsVersionedPython(string path)
    {
        var name = Path.GetFileName(path);
        return name == "python3" || (name.StartsWith("python3.", StringComparison.Ordinal) && name[8..].All(char.IsAsciiDigit) && name.Length > 8);
    }

    private static bool IsHomebrew(string exe)
    {
        if (exe.StartsWith("/opt/homebrew/", StringComparison.Ordinal) || exe.StartsWith("/home/linuxbrew/", StringComparison.Ordinal)) return true;
        try
        {
            return new FileInfo(exe).ResolveLinkTarget(returnFinalTarget: true)?.FullName.Contains("/Cellar/", StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>macOS's /usr/bin/python3 is a shim: without the Command Line Tools (or Xcode) it asks to install them instead of running.</summary>
    private static bool UsableSystemPython(string exe)
    {
        if (!OperatingSystem.IsMacOS() || exe != "/usr/bin/python3") return true;
        return File.Exists("/Library/Developer/CommandLineTools/usr/bin/python3") || File.Exists("/Applications/Xcode.app/Contents/Developer/usr/bin/python3");
    }

    private string? FindOnPath(string name)
    {
        foreach (var dir in (_env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- probing ----

    public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) =>
        new(ProbeAsync(new Candidate(executable, PythonEnvironmentKind.Custom, null), ct));

    private async Task<PythonEnvironment?> ProbeAsync(Candidate c, CancellationToken ct)
    {
        if (!File.Exists(c.Executable)) return null;
        var r = await _run(c.Executable, PythonProcess.ProbeArguments, null, ProbeTimeout, ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || PythonProcess.ParseProbe(r.Output) is not { } info)
        {
            _log?.LogDebug("{Python} did not answer the probe (exit {Exit})", c.Executable, r.ExitCode);
            return null;
        }
        var pyenvRoot = _env("PYENV_ROOT") is { Length: > 0 } pr ? pr : Path.Combine(_env("HOME") ?? "", ".pyenv");
        var kind = c.Kind == PythonEnvironmentKind.Custom ? PythonProcess.Classify(c.Executable, info.Prefix, info.BasePrefix, pyenvRoot) : c.Kind;
        var name = c.Name;
        if (name is null && kind is PythonEnvironmentKind.Venv or PythonEnvironmentKind.Conda or PythonEnvironmentKind.Poetry) name = Path.GetFileName(info.Prefix.TrimEnd('/', '\\'));
        return new PythonEnvironment(c.Executable, kind, info.Version, info.Prefix, name);
    }

    public async ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default)
    {
        if (_modules.TryGetValue((executable, module), out var known)) return known;
        var r = await _run(executable, PythonProcess.ModuleArguments(module), null, ProbeTimeout, ct).ConfigureAwait(false);
        var has = r.ExitCode == 0;
        if (r.ExitCode is not null) // a timeout is not an answer: ask again next time
        {
            if (_modules.Count >= MaxModuleCacheEntries) _modules.Clear();
            _modules[(executable, module)] = has;
        }
        return has;
    }

    // ---- helpers ----

    private string ExpandPath(string value, Uri? folder)
    {
        var path = value.Replace("${workspaceFolder}", folder?.LocalPath ?? "", StringComparison.Ordinal);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)) path = (_env("HOME") ?? "") + path[1..];
        if (!Path.IsPathRooted(path) && FindOnPath(path) is { } onPath) return onPath;
        return path;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeReadLines(string file)
    {
        try { return File.ReadAllLines(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _store.Dispose();
        _disposed.Dispose();
    }
}
