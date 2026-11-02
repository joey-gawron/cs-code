using CsCode.Core.Lifecycle;

namespace CsCode.Core.Python;

/// <summary>Where an interpreter was found (the picker shows it; the default choice prefers the first kinds).</summary>
public enum PythonEnvironmentKind
{
    /// <summary>A virtual environment (a <c>pyvenv.cfg</c> next to its <c>bin</c>): the workspace's .venv / venv / env, or <c>VIRTUAL_ENV</c>.</summary>
    Venv,
    Poetry,
    Conda,
    Pyenv,
    Homebrew,
    /// <summary>Any other global interpreter: python.org's framework builds, the OS's, one found on PATH, the Windows launcher's.</summary>
    Global,
    /// <summary>Entered by hand or from <c>python.defaultInterpreterPath</c>.</summary>
    Custom,
}

/// <summary>
/// One Python interpreter. <see cref="Executable"/> is the path to run (for a venv the venv's own <c>bin/python</c>, never the base interpreter
/// it links to); <see cref="Prefix"/> is its <c>sys.prefix</c> (the environment's directory for a venv or conda env), <see cref="Version"/> its
/// <c>sys.version</c> number. Both are null until the interpreter has been asked (a probe that failed or timed out).
/// </summary>
public sealed record PythonEnvironment(string Executable, PythonEnvironmentKind Kind, string? Version, string? Prefix, string? Name = null)
{
    /// <summary>The directory a terminal puts first on PATH: the interpreter's own (<c>.venv/bin</c>, <c>.venv\Scripts</c>).</summary>
    public string BinDirectory => Path.GetDirectoryName(Executable) ?? Executable;

    /// <summary>A virtual environment of some kind: terminals set <c>VIRTUAL_ENV</c> (or <c>CONDA_PREFIX</c>) for it.</summary>
    public bool IsEnvironment => Kind is PythonEnvironmentKind.Venv or PythonEnvironmentKind.Poetry or PythonEnvironmentKind.Conda;

    /// <summary>"3.13.5 ('.venv': venv)", the status bar's and the picker's label (VS Code's wording).</summary>
    public string Label
    {
        get
        {
            var kind = Kind switch
            {
                PythonEnvironmentKind.Venv => "venv",
                PythonEnvironmentKind.Poetry => "poetry",
                PythonEnvironmentKind.Conda => "conda",
                PythonEnvironmentKind.Pyenv => "pyenv",
                PythonEnvironmentKind.Homebrew => "Homebrew",
                PythonEnvironmentKind.Custom => "custom",
                _ => "global",
            };
            var version = Version ?? "?";
            return Name is { Length: > 0 } name ? $"{version} ('{name}': {kind})" : $"{version} ({kind})";
        }
    }
}

/// <summary>
/// Finds Python interpreters and remembers which one each workspace folder uses (language stage 1, docs/PYTHON.md). The choice feeds the Python
/// language servers (<c>python.pythonPath</c>), the debugpy debugger, the pytest / unittest test provider and new terminals.
/// <para>
/// Discovery runs off the caller's thread, each interpreter probe bounded (2 s, then killed), and is cached per workspace until
/// <see cref="DiscoverAsync"/> is asked to refresh. The active interpreter of a folder is, in order: the one picked for it (persisted per
/// folder), <c>python.defaultInterpreterPath</c>, then the best one found (the folder's own virtual environment first).
/// </para>
/// Implemented by CsCode.Platform; local folders only (a remote folder has none).
/// </summary>
public interface IPythonEnvironmentService
{
    /// <summary>The active interpreter for the folder that holds <paramref name="resource"/> (a file or a folder; null = the first folder), if known already. Never blocks.</summary>
    PythonEnvironment? GetActive(Uri? resource);

    /// <summary>The active interpreter, discovering first when nothing is known yet. Null when there is no Python at all.</summary>
    ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default);

    /// <summary>Every interpreter found for the folder of <paramref name="resource"/> (its virtual environments, then the global ones).</summary>
    ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default);

    /// <summary>Picks <paramref name="executable"/> for the folder of <paramref name="resource"/> (persisted); null forgets the pick.</summary>
    ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default);

    /// <summary>Asks an interpreter for its version and prefix (bounded); null when it is not a working Python.</summary>
    ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default);

    /// <summary>Whether <paramref name="module"/> (debugpy, pytest) can be imported by the interpreter (bounded, cached until the next refresh).</summary>
    ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default);

    /// <summary>Fires (with the folder, or null for all) when a folder's active interpreter changed: picked, discovered, or the setting changed.</summary>
    IEventSource<Uri?> ActiveChanged { get; }
}
