using CsCode.Core.Languages;

namespace CsCode.Build.CMake;

/// <summary>
/// Points clangd at the configured build directory: re-registers clangd's definition with <c>--compile-commands-dir=&lt;build dir&gt;</c>
/// (the language-server manager restarts a running clangd when its launch changes, and only then), and drops it again when no project is
/// configured. The definition it starts from is the one in effect before the first change (the built-in one with the user's
/// <c>cscode.languageServers.clangd</c> overrides): a user override of <c>args</c> is applied on top again by the manager and so wins, which
/// the docs say. The new registration is added before the old one is removed, so a change of directory restarts clangd once.
/// </summary>
internal sealed class ClangdCompileCommands(ILanguageServerManager? servers, Func<bool> enabled) : IDisposable
{
    public const string ServerId = "clangd";
    public const string Argument = "--compile-commands-dir=";

    private readonly object _gate = new();
    private LanguageServerDefinition? _base;
    private IDisposable? _registration;
    private string? _current;

    /// <summary>The directory clangd was last pointed at (null: none).</summary>
    public string? Current { get { lock (_gate) return _current; } }

    /// <summary>Points clangd at <paramref name="buildDirectory"/> (null: back to its own lookup). Cheap when nothing changes; any thread.</summary>
    public void Update(string? buildDirectory)
    {
        if (servers is null) return;
        if (!enabled()) buildDirectory = null;
        IDisposable? old;
        lock (_gate)
        {
            if (string.Equals(_current, buildDirectory, StringComparison.Ordinal)) return;
            _current = buildDirectory;
            old = _registration;
            _registration = null;
            if (buildDirectory is not null)
            {
                _base ??= servers.Definitions.FirstOrDefault(d => d.Id == ServerId);
                if (_base is { } b)
                    _registration = servers.AddDefinition(b with { Args = [.. WithDirectory(b.Args, buildDirectory)] });
            }
        }
        old?.Dispose();
    }

    internal static IEnumerable<string> WithDirectory(IReadOnlyList<string> args, string dir) =>
        args.Where(a => !a.StartsWith(Argument, StringComparison.Ordinal)).Append(Argument + dir);

    public void Dispose()
    {
        IDisposable? old;
        lock (_gate)
        {
            old = _registration;
            _registration = null;
            _current = null;
        }
        old?.Dispose();
    }
}
