using System.Security.Cryptography;
using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Platform;

public enum HostOs { Linux, Windows, MacOs }

/// <summary>
/// Per-OS well-known directories. Pure: computing a path never touches the disk (consumers create what they write).
/// <list type="bullet">
/// <item>Linux (XDG): config <c>~/.config/cscode</c> (settings.json, keybindings.json), data <c>~/.local/share/cscode</c>
/// (<c>themes/</c>, <c>grammars/</c>, <c>snippets/</c>, tools, global/workspace storage), cache <c>~/.cache/cscode</c>,
/// logs <c>~/.local/state/cscode/logs</c>.</item>
/// <item>Windows: <c>%APPDATA%\CsCode</c> for config+data, <c>%LOCALAPPDATA%\CsCode\{Cache,Logs}</c>.</item>
/// <item>macOS: <c>~/Library/Application Support/CsCode</c> for config+data, <c>~/Library/{Caches,Logs}/CsCode</c>.</item>
/// <item><c>CSCODE_HOME</c> overrides everything: <c>$CSCODE_HOME/{config,data,cache,logs}</c> (portable installs, tests).</item>
/// </list>
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string HomeEnvironmentVariable = "CSCODE_HOME";

    private readonly HostOs _os;

    /// <param name="getEnv">Environment lookup (defaults to the process environment); injectable so tests never mutate process state.</param>
    /// <param name="os">Target OS (defaults to the current one); injectable so the Windows/macOS layouts are testable on Linux.</param>
    /// <param name="userHome">The user's home directory (defaults to the OS profile).</param>
    public AppPaths(Func<string, string?>? getEnv = null, HostOs? os = null, string? userHome = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        _os = os ?? (OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOs : HostOs.Linux);
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Path.GetTempPath();
        var sep = _os == HostOs.Windows ? '\\' : '/';
        string Join(string a, params string[] rest) => string.Join(sep, [a.TrimEnd('/', '\\'), .. rest]);

        string? Env(string name) => getEnv(name) is { Length: > 0 } v ? v : null;
        string? Absolute(string? v) => v is not null && (v.StartsWith('/') || (v.Length > 2 && v[1] == ':')) ? v : null; // XDG: relative values are invalid

        if (Env(HomeEnvironmentVariable) is { } portable)
        {
            ConfigDir = Join(portable, "config");
            UserDataDir = Join(portable, "data");
            CacheDir = Join(portable, "cache");
            LogsDir = Join(portable, "logs");
        }
        else if (_os == HostOs.Windows)
        {
            var appData = Env("APPDATA") ?? Join(home, "AppData", "Roaming");
            var local = Env("LOCALAPPDATA") ?? Join(home, "AppData", "Local");
            ConfigDir = UserDataDir = Join(appData, "CsCode");
            CacheDir = Join(local, "CsCode", "Cache");
            LogsDir = Join(local, "CsCode", "Logs");
        }
        else if (_os == HostOs.MacOs)
        {
            var support = Join(home, "Library", "Application Support", "CsCode");
            ConfigDir = UserDataDir = support;
            CacheDir = Join(home, "Library", "Caches", "CsCode");
            LogsDir = Join(home, "Library", "Logs", "CsCode");
        }
        else
        {
            ConfigDir = Join(Absolute(Env("XDG_CONFIG_HOME")) ?? Join(home, ".config"), "cscode");
            UserDataDir = Join(Absolute(Env("XDG_DATA_HOME")) ?? Join(home, ".local", "share"), "cscode");
            CacheDir = Join(Absolute(Env("XDG_CACHE_HOME")) ?? Join(home, ".cache"), "cscode");
            LogsDir = Join(Absolute(Env("XDG_STATE_HOME")) ?? Join(home, ".local", "state"), "cscode", "logs");
        }

        GlobalStorageDir = Join(UserDataDir, "globalStorage");
        ToolsDir = Join(UserDataDir, "tools");
        // CsCode has no extension system; the member exists only because IAppPaths (Core) still declares it. Never created or read.
        ExtensionsDir = Join(UserDataDir, "extensions");
        WorkspaceStorageRoot = Join(UserDataDir, "workspaceStorage");
        InstallDir = AppContext.BaseDirectory;
    }

    /// <summary>Directory holding <c>settings.json</c> and <c>keybindings.json</c> (equals <see cref="UserDataDir"/> on Windows/macOS).</summary>
    public string ConfigDir { get; }

    public string UserDataDir { get; }
    public string ExtensionsDir { get; }
    public string LogsDir { get; }
    public string CacheDir { get; }
    public string GlobalStorageDir { get; }
    public string ToolsDir { get; }
    public string InstallDir { get; }
    private string WorkspaceStorageRoot { get; }

    /// <summary>Per-workspace storage directory, named by a stable hash of the folder URI (never <c>string.GetHashCode</c>, which is randomised per process).</summary>
    public string? WorkspaceStorageDir(Uri? workspaceFolder)
    {
        if (workspaceFolder is null) return null;
        var sep = _os == HostOs.Windows ? "\\" : "/";
        return WorkspaceStorageRoot + sep + StableHash(workspaceFolder, _os != HostOs.Linux);
    }

    /// <summary>First 32 hex chars of SHA-256 over the normalised URI (no trailing slash; case-folded on case-insensitive file systems).</summary>
    public static string StableHash(Uri folder, bool caseInsensitive)
    {
        var text = folder.IsAbsoluteUri ? folder.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped) : folder.OriginalString;
        text = text.TrimEnd('/');
        if (caseInsensitive) text = text.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>The directory that holds settings.json/keybindings.json for any <see cref="IAppPaths"/> (falls back to UserDataDir for foreign implementations).</summary>
    internal static string ConfigDirOf(IAppPaths paths) => paths is AppPaths ap ? ap.ConfigDir : paths.UserDataDir;
}
