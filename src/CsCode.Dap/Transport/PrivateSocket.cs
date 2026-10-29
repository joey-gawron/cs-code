using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CsCode.Dap.Transport;

/// <summary>
/// A private endpoint for an adapter SERVER, so no other local process (another user on a shared machine included) can connect to it and have
/// the adapter launch programs as this user — which a TCP port on 127.0.0.1 allows. Unix: a Unix domain socket <c>dap.sock</c> in a fresh
/// directory that only this user can enter (mode 0700), under <c>$XDG_RUNTIME_DIR</c> when set (itself private by spec), else the temp
/// directory. Windows: a named pipe <c>\\.\pipe\&lt;prefix&gt;-&lt;random&gt;</c> (no directory).
/// <para>The directory's name carries this process's id and start time (<c>&lt;prefix&gt;-&lt;pid&gt;-&lt;start&gt;-&lt;random&gt;</c>), so the
/// directories of a CsCode that was killed before it could delete them are recognised and removed the next time one is created — never those
/// of a CsCode that still runs (its pid is alive with the same start time).</para>
/// </summary>
internal sealed class PrivateSocket
{
    public const string SocketFileName = "dap.sock";
    /// <summary>sun_path holds 108 bytes on Linux and 104 on macOS, the terminating NUL included.</summary>
    internal const int MaxSocketPathBytes = 103;

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private int _deleted;

    private PrivateSocket(string path, string? directory)
    {
        Path = path;
        Directory = directory;
    }

    /// <summary>The socket (or named pipe) path the server listens on and every client connects to.</summary>
    public string Path { get; }

    /// <summary>The private directory holding the socket (null for a named pipe).</summary>
    public string? Directory { get; }

    /// <summary>The directory sockets are created in when the caller names none: $XDG_RUNTIME_DIR when it exists, else the temp directory.</summary>
    public static string DefaultRoot(Func<string, string?>? environment = null)
    {
        var runtime = (environment ?? Environment.GetEnvironmentVariable)("XDG_RUNTIME_DIR");
        return runtime is { Length: > 0 } && System.IO.Path.IsPathRooted(runtime) && System.IO.Directory.Exists(runtime) ? runtime : System.IO.Path.GetTempPath();
    }

    /// <summary>
    /// Creates a fresh private endpoint (removing stale directories of killed CsCode processes first). Throws <see cref="IOException"/> when
    /// no private directory can be made (it existed already, is not owned by this user, or the path is too long for a socket).
    /// </summary>
    public static PrivateSocket Create(string prefix, string? root)
    {
        var random = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        if (OperatingSystem.IsWindows()) return new PrivateSocket($@"\\.\pipe\{prefix}-{random}", null);

        var baseDir = root is { Length: > 0 } ? root : DefaultRoot();
        var name = $"{prefix}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Stamp(Environment.ProcessId) ?? "0"}-{random}";
        if (Encoding.UTF8.GetByteCount(System.IO.Path.Combine(baseDir, name, SocketFileName)) > MaxSocketPathBytes && System.IO.Directory.Exists("/tmp"))
            baseDir = "/tmp"; // a long TMPDIR: sun_path is short
        var dir = System.IO.Path.Combine(baseDir, name);
        var socket = System.IO.Path.Combine(dir, SocketFileName);
        if (Encoding.UTF8.GetByteCount(socket) > MaxSocketPathBytes)
            throw new IOException($"The socket path '{socket}' is too long for a Unix domain socket.");

        RemoveStale(baseDir, prefix);
        // The name is unguessable, so nobody can have prepared it; still, never use a directory that existed before (CreateDirectory would
        // silently accept one), and make sure it ends up 0700 and ours: SetUnixFileMode fails on a directory another user owns.
        if (System.IO.Directory.Exists(dir) || File.Exists(dir)) throw new IOException($"'{dir}' already exists.");
        System.IO.Directory.CreateDirectory(dir, OwnerOnly);
        try
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) throw new IOException($"'{dir}' is a symbolic link.");
            File.SetUnixFileMode(dir, OwnerOnly);
            if (File.GetUnixFileMode(dir) != OwnerOnly) throw new IOException($"'{dir}' could not be made private (mode 0700).");
            if (System.IO.Directory.EnumerateFileSystemEntries(dir).Any()) throw new IOException($"'{dir}' is not empty.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(dir);
            throw new IOException(ex.Message, ex);
        }
        return new PrivateSocket(socket, dir);
    }

    /// <summary>Removes the directory and the socket in it (the server is gone or going). Idempotent; never throws.</summary>
    public void Delete()
    {
        if (Directory is null || Interlocked.Exchange(ref _deleted, 1) != 0) return;
        TryDelete(Directory);
    }

    private static void TryDelete(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* gone, or not ours */ }
    }

    /// <summary>
    /// Deletes the private socket directories under <paramref name="root"/> whose creator is gone: a pid that no longer runs, or runs with
    /// another start time (the pid was reused). Only real directories named like ours with mode 0700 are touched (a link, or another
    /// user's look-alike, is left alone: deleting it fails anyway in a sticky temp directory).
    /// </summary>
    internal static List<string> RemoveStale(string root, string prefix)
    {
        var removed = new List<string>();
        if (OperatingSystem.IsWindows()) return removed;
        IEnumerable<string> candidates;
        try { candidates = System.IO.Directory.EnumerateDirectories(root, prefix + "-*").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return removed; }
        foreach (var dir in candidates)
        {
            var parts = System.IO.Path.GetFileName(dir)[(prefix.Length + 1)..].Split('-');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || parts[2].Length == 0) continue;
            if (Stamp(pid) is { } live && (live == parts[1] || parts[1] == "0")) continue; // its CsCode still runs
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null || File.GetUnixFileMode(dir) != OwnerOnly) continue;
                info.Delete(recursive: true);
                removed.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours, or gone meanwhile */ }
        }
        return removed;
    }

    /// <summary>
    /// When a process started, as text (Linux: field 22 of <c>/proc/&lt;pid&gt;/stat</c>, clock ticks since boot; elsewhere "0" while it
    /// runs), or null when no such process runs.
    /// </summary>
    internal static string? Stamp(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                return fields.Length > 19 && fields[0] != "Z" ? fields[19] : null; // field 22 overall; a zombie has ended
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { return null; }
        }
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited ? null : "0";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
