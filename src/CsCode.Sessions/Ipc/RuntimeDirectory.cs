using System.Security.Cryptography;
using System.Text;

namespace CsCode.Sessions.Ipc;

/// <summary>
/// Where this user's CsCode processes meet: their IPC endpoints (one per process), the session lock files and the launcher lock.
/// <list type="bullet">
/// <item>Linux: <c>$XDG_RUNTIME_DIR/cscode</c> (per-user tmpfs, cleared at logout), else <c>/tmp/cscode-&lt;uid&gt;</c>.</item>
/// <item>macOS: <c>$TMPDIR/cscode-&lt;uid&gt;</c> (TMPDIR is per user there).</item>
/// <item>Windows: <c>%LOCALAPPDATA%\CsCode\run</c> for the lock files; endpoints are named pipes <c>cscode-&lt;hash&gt;-&lt;pid&gt;</c> (current-user-only ACL).</item>
/// <item><c>CSCODE_HOME</c> set (portable installs, every test): <c>$CSCODE_HOME/run</c>, so a test never talks to (or locks against) the user's real
/// CsCode. Unix socket paths are limited to ~108 bytes, so when that path is too long the directory moves to the per-user base above with a hash of
/// <c>CSCODE_HOME</c> in its name (<c>cscode-h&lt;hash&gt;</c>) — still private to that CSCODE_HOME.</item>
/// </list>
/// The directory is created with mode 0700 and rejected when it is not (another user could otherwise plant endpoints in it); every connection is
/// additionally checked with the peer's credentials (<see cref="IpcServer"/>).
/// </summary>
public sealed class RuntimeDirectory
{
    /// <summary>Longest endpoint file name used in the directory (<c>&lt;pid&gt;.sock</c>, pid ≤ 4194304 on Linux) plus the separator.</summary>
    private const int MaxEndpointName = 16;

    /// <summary>sun_path is 108 bytes on Linux and 104 on macOS, including the terminating NUL.</summary>
    private const int MaxSocketPath = 103;

    public RuntimeDirectory(string path, string pipePrefix)
    {
        Path = path;
        PipePrefix = pipePrefix;
    }

    public string Path { get; }

    /// <summary>Windows: prefix of this user's (and this CSCODE_HOME's) pipe names.</summary>
    public string PipePrefix { get; }

    public string LocksDir => System.IO.Path.Combine(Path, "locks");
    public string LauncherLockPath => System.IO.Path.Combine(Path, "launcher.lock");
    public string TrayLockPath => System.IO.Path.Combine(Path, "tray.lock");
    public string SessionLockPath(string session) => System.IO.Path.Combine(LocksDir, session + ".lock");

    /// <summary>The endpoint of process <paramref name="pid"/>: a socket path, or a pipe name on Windows.</summary>
    public string EndpointOf(int pid) => OperatingSystem.IsWindows()
        ? PipePrefix + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : System.IO.Path.Combine(Path, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".sock");

    /// <param name="getEnv">Environment lookup (defaults to the process environment); injectable so tests never mutate process state.</param>
    /// <param name="uid">The effective uid (Unix); defaults to the process's.</param>
    public static RuntimeDirectory Resolve(Func<string, string?>? getEnv = null, uint? uid = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        string? Env(string name) => getEnv(name) is { Length: > 0 } v ? v : null;
        var user = (uid ?? UnixNative.EffectiveUid())?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Environment.UserName;
        var home = Env("CSCODE_HOME");

        if (OperatingSystem.IsWindows())
        {
            var local = Env("LOCALAPPDATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            var dir = home is null ? System.IO.Path.Combine(local, "CsCode", "run") : System.IO.Path.Combine(home, "run");
            return new RuntimeDirectory(dir, $"cscode-{Hash(Environment.UserName + "|" + (home ?? ""))}-");
        }

        string PerUserBase()
        {
            if (OperatingSystem.IsMacOS()) return System.IO.Path.Combine(Env("TMPDIR") ?? "/tmp", "cscode-" + user);
            if (Env("XDG_RUNTIME_DIR") is { } xdg && xdg.StartsWith('/')) return System.IO.Path.Combine(xdg, "cscode");
            return "/tmp/cscode-" + user;
        }

        if (home is null) return new RuntimeDirectory(PerUserBase(), "");
        var direct = System.IO.Path.Combine(home, "run");
        if (direct.Length + MaxEndpointName <= MaxSocketPath) return new RuntimeDirectory(direct, "");
        var shortened = PerUserBase() + "-h" + Hash(home);
        return new RuntimeDirectory(shortened, "");
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>
    /// Creates the directory (mode 0700) and its <c>locks</c> subdirectory; returns null when it is usable, otherwise why not (it exists with
    /// looser permissions that cannot be tightened, i.e. it belongs to someone else, or it cannot be created).
    /// </summary>
    public string? Ensure()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(LocksDir);
                return null;
            }
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var parent = System.IO.Path.GetDirectoryName(Path);
            if (parent is not null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
            if (!Directory.Exists(Path)) Directory.CreateDirectory(Path, Private);
            if (File.GetUnixFileMode(Path) != Private)
            {
                try { File.SetUnixFileMode(Path, Private); }
                catch (UnauthorizedAccessException) { return $"{Path} is not private to this user and cannot be made so"; }
                if (File.GetUnixFileMode(Path) != Private) return $"{Path} is not private to this user (mode is not 0700)";
            }
            if (!Directory.Exists(LocksDir)) Directory.CreateDirectory(LocksDir, Private);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cannot create {Path}: {ex.Message}";
        }
    }

    /// <summary>Pids that have an endpoint in this directory (live or stale).</summary>
    public IReadOnlyList<int> ListEndpointPids()
    {
        var pids = new List<int>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
                {
                    var name = System.IO.Path.GetFileName(pipe);
                    if (name.StartsWith(PipePrefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(PipePrefix.Length), out var p)) pids.Add(p);
                }
                return pids;
            }
            if (!Directory.Exists(Path)) return pids;
            foreach (var file in Directory.EnumerateFiles(Path, "*.sock"))
                if (int.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), out var pid) && pid > 0) pids.Add(pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return pids;
    }
}
