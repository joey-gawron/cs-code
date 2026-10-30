namespace CsCode.Sessions.Ipc;

/// <summary>
/// An exclusive advisory lock held for as long as this object lives (and released by the kernel when the process dies, kill -9 included):
/// .NET opens a file with <see cref="FileShare.None"/> using <c>flock(LOCK_EX|LOCK_NB)</c> on Unix and a share-deny-all handle on Windows.
/// Used for the per-session lock (one process owns a session) and the launcher lock (concurrent launches are serialised).
/// The file's content is never read (another process cannot open it while it is locked); the owning process is found over IPC.
/// <para>A lock that another process holds and a lock that cannot be taken at all (its directory or file cannot be created or opened: a read-only
/// or foreign data directory) are told apart: waiting only helps with the first.</para>
/// </summary>
public sealed class FileLock : IDisposable
{
    private FileStream? _stream;

    private FileLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }
    public bool IsHeld => _stream is not null;

    /// <summary>Takes the lock now, or returns null when another process (or another handle in this process) holds it, or it cannot be taken.</summary>
    public static FileLock? TryAcquire(string path) => TryAcquire(path, out _);

    /// <summary>
    /// Takes the lock now. Null when it is held elsewhere (<paramref name="problem"/> null), or when it cannot be taken at all (<paramref name="problem"/>
    /// says why: the directory or the file cannot be created or opened).
    /// </summary>
    public static FileLock? TryAcquire(string path, out string? problem)
    {
        problem = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            return new FileLock(path, stream);
        }
        catch (IOException ex) when (IsHeldByAnother(ex)) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"cannot take the lock {path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The lock is taken by someone else (not "cannot be used"): <c>flock</c> answered EWOULDBLOCK, which .NET reports as an IOException carrying
    /// the raw errno (11 on Linux, 35 on macOS and the BSDs); Windows reports a sharing or lock violation.
    /// </summary>
    private static bool IsHeldByAnother(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 32 or 33
        : ex.HResult == (OperatingSystem.IsLinux() ? 11 : 35);

    /// <summary>Takes the lock, retrying until <paramref name="timeout"/>; null when it stayed held.</summary>
    public static async Task<FileLock?> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryAcquire(path) is { } held) return held;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Blocking variant of <see cref="AcquireAsync"/> for the launcher process.</summary>
    public static FileLock? Acquire(string path, TimeSpan timeout) => Acquire(path, timeout, out _);

    /// <summary>
    /// Blocking: takes the lock, retrying until <paramref name="timeout"/> while another process holds it. Returns at once, with
    /// <paramref name="problem"/> set, when the lock cannot be taken at all (waiting would not help).
    /// </summary>
    public static FileLock? Acquire(string path, TimeSpan timeout, out string? problem)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryAcquire(path, out problem) is { } held) return held;
            if (problem is not null || DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// True when somebody holds the lock right now (probes by taking it briefly). A lock file that exists but cannot be opened counts as held: a
    /// session is never taken for free when that cannot be told.
    /// </summary>
    public static bool IsHeldElsewhere(string path)
    {
        if (!File.Exists(path)) return false;
        using var probe = TryAcquire(path);
        return probe is null;
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }
}
