using System.Security.Cryptography;
using System.Text;

namespace CsCode.Sessions.Ipc;

/// <summary>What <see cref="RuntimeDirectory.Probe"/> found at a path.</summary>
public enum DirectoryOwner
{
    Missing,
    /// <summary>A real directory (not a symbolic link) owned by this user.</summary>
    ThisUser,
    /// <summary>A file, a symbolic link, or another user's directory.</summary>
    Other,
}

/// <summary>
/// Where this user's CsCode processes meet: their IPC endpoints (one per process) and the tray lock. The session locks and the launcher lock are
/// not here: they live with the data they protect (<see cref="SessionStore.LockPathOf"/>), so two launches that resolve different runtime
/// directories (a shell without <c>XDG_RUNTIME_DIR</c>, <c>su -</c>, <c>env -i</c>) still cannot both own one session.
/// <list type="bullet">
/// <item>Linux: <c>$XDG_RUNTIME_DIR/cscode</c> (per-user tmpfs, cleared at logout); without it <c>/run/user/&lt;uid&gt;/cscode</c> when that directory
/// exists (the same tmpfs: a shell that lost the variable still meets the desktop's processes); else <c>/tmp/cscode-&lt;uid&gt;</c>.</item>
/// <item>macOS: <c>$TMPDIR/cscode-&lt;uid&gt;</c> (TMPDIR is per user there).</item>
/// <item>Windows: <c>%LOCALAPPDATA%\CsCode\run</c>; endpoints are named pipes <c>cscode-&lt;hash&gt;-&lt;pid&gt;</c> (current-user-only ACL).</item>
/// <item><c>CSCODE_HOME</c> set (portable installs, every test): <c>$CSCODE_HOME/run</c>, so a test never talks to the user's real CsCode. Unix
/// socket paths are limited to ~108 bytes, so when that path is too long the directory moves to the per-user base above with a hash of
/// <c>CSCODE_HOME</c> in its name (<c>cscode-h&lt;hash&gt;</c>) — still private to that CSCODE_HOME.</item>
/// </list>
/// The directory is created with mode 0700 and rejected when it is a symbolic link, belongs to another user or is not private (another user could
/// otherwise plant endpoints in it); every connection is additionally checked with the peer's credentials (<see cref="IpcServer"/>). Under the
/// shared <c>/tmp</c>, a name another user took first is skipped for the next one (<c>/tmp/cscode-&lt;uid&gt;-1</c>, ...).
/// </summary>
public sealed class RuntimeDirectory
{
    /// <summary>Longest endpoint file name used in the directory (<c>&lt;pid&gt;.sock</c>, pid ≤ 4194304 on Linux) plus the separator.</summary>
    private const int MaxEndpointName = 16;

    /// <summary>sun_path is 108 bytes on Linux and 104 on macOS, including the terminating NUL.</summary>
    private const int MaxSocketPath = 103;

    /// <summary>How many names under the shared <c>/tmp</c> are tried when another user took the first ones.</summary>
    private const int SharedCandidates = 8;

    public RuntimeDirectory(string path, string pipePrefix)
    {
        Path = path;
        PipePrefix = pipePrefix;
    }

    public string Path { get; }

    /// <summary>Windows: prefix of this user's (and this CSCODE_HOME's) pipe names.</summary>
    public string PipePrefix { get; }

    public string TrayLockPath => System.IO.Path.Combine(Path, "tray.lock");

    /// <summary>
    /// Where builds before the session locks moved to the data directory locked session <paramref name="name"/> (<c>&lt;runtime&gt;/locks/&lt;name&gt;.lock</c>).
    /// Still honoured and taken by <see cref="SessionLock"/>: a process of such a build that is still running after an upgrade (quitting, or
    /// wedged) keeps its session, and it sees a session this build owns as taken.
    /// </summary>
    public string LegacySessionLockPath(string name) => System.IO.Path.Combine(Path, "locks", name + ".lock");

    /// <summary>The endpoint of process <paramref name="pid"/>: a socket path, or a pipe name on Windows.</summary>
    public string EndpointOf(int pid) => OperatingSystem.IsWindows()
        ? PipePrefix + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : System.IO.Path.Combine(Path, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".sock");

    /// <param name="getEnv">Environment lookup (defaults to the process environment); injectable so tests never mutate process state.</param>
    /// <param name="uid">The effective uid (Unix); defaults to the process's.</param>
    /// <param name="probe">What is at a candidate path (<see cref="Probe"/>): <c>/run/user/&lt;uid&gt;</c> must be this user's directory, a name under
    /// <c>/tmp</c> free or this user's. Injectable for tests.</param>
    public static RuntimeDirectory Resolve(Func<string, string?>? getEnv = null, uint? uid = null, Func<string, DirectoryOwner>? probe = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        string? Env(string name) => getEnv(name) is { Length: > 0 } v ? v : null;
        var effectiveUid = uid ?? UnixNative.EffectiveUid();
        var user = effectiveUid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Environment.UserName;
        var home = Env("CSCODE_HOME");

        if (OperatingSystem.IsWindows())
        {
            var local = Env("LOCALAPPDATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            var dir = home is null ? System.IO.Path.Combine(local, "CsCode", "run") : System.IO.Path.Combine(home, "run");
            return new RuntimeDirectory(dir, $"cscode-{Hash(Environment.UserName + "|" + (home ?? ""))}-");
        }

        probe ??= Probe;

        // Shared: under /tmp, where another user may have taken the name first.
        (string Path, bool Shared) PerUserBase()
        {
            if (OperatingSystem.IsMacOS()) return (System.IO.Path.Combine(Env("TMPDIR") ?? "/tmp", "cscode-" + user), Env("TMPDIR") is null);
            if (Env("XDG_RUNTIME_DIR") is { } xdg && xdg.StartsWith('/')) return (System.IO.Path.Combine(xdg, "cscode"), false);
            // Only logind creates /run/user/<uid> (root owns /run/user): when it is this user's directory, it is their private tmpfs.
            if (OperatingSystem.IsLinux() && effectiveUid is { } id && probe($"/run/user/{id}") == DirectoryOwner.ThisUser) return ($"/run/user/{id}/cscode", false);
            return ("/tmp/cscode-" + user, true);
        }

        string Pick((string Path, bool Shared) candidate)
        {
            if (!candidate.Shared) return candidate.Path;
            for (var i = 0; i < SharedCandidates; i++)
            {
                var name = i == 0 ? candidate.Path : candidate.Path + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (probe(name) is DirectoryOwner.Missing or DirectoryOwner.ThisUser) return name;
            }
            return candidate.Path; // all taken: Ensure says why, and background mode is unavailable
        }

        if (home is null) return new RuntimeDirectory(Pick(PerUserBase()), "");
        var direct = System.IO.Path.Combine(home, "run");
        if (direct.Length + MaxEndpointName <= MaxSocketPath) return new RuntimeDirectory(direct, "");
        var (perUser, shared) = PerUserBase();
        return new RuntimeDirectory(Pick((perUser + "-h" + Hash(home), shared)), "");
    }

    /// <summary>
    /// What is at <paramref name="path"/>: nothing, a real directory (not a symbolic link) this user owns, or anything else. The owner test is a chmod
    /// to the directory's own mode: it changes nothing and succeeds only for the owner.
    /// </summary>
    public static DirectoryOwner Probe(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null) return DirectoryOwner.Other;
            if (!info.Exists) return File.Exists(path) ? DirectoryOwner.Other : DirectoryOwner.Missing;
            if (OperatingSystem.IsWindows()) return DirectoryOwner.ThisUser;
            File.SetUnixFileMode(path, File.GetUnixFileMode(path));
            return DirectoryOwner.ThisUser;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DirectoryOwner.Other; }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>
    /// Creates the directory (mode 0700); returns null when it is usable, otherwise why not: it cannot be created, it is a symbolic link, or it
    /// belongs to another user (the chmod that makes it private fails for anyone but its owner, whatever its mode already is).
    /// </summary>
    public string? Ensure()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path);
                return null;
            }
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var parent = System.IO.Path.GetDirectoryName(Path);
            if (parent is not null && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
            var info = new DirectoryInfo(Path);
            if (info.LinkTarget is not null) return $"{Path} is a symbolic link, not a private directory";
            if (!info.Exists) Directory.CreateDirectory(Path, Private);
            try { File.SetUnixFileMode(Path, Private); }
            catch (UnauthorizedAccessException) { return $"{Path} belongs to another user"; }
            if (File.GetUnixFileMode(Path) != Private) return $"{Path} is not private to this user (mode is not 0700)";
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
