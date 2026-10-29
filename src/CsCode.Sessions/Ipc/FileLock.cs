namespace CsCode.Sessions.Ipc;

/// <summary>
/// An exclusive advisory lock held for as long as this object lives (and released by the kernel when the process dies, kill -9 included):
/// .NET opens a file with <see cref="FileShare.None"/> using <c>flock(LOCK_EX|LOCK_NB)</c> on Unix and a share-deny-all handle on Windows.
/// Used for the per-session lock (one process owns a session) and the launcher lock (concurrent launches are serialised).
/// The file's content is never read (another process cannot open it while it is locked); the owning process is found over IPC.
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

    /// <summary>Takes the lock now, or returns null when another process (or another handle in this process) holds it.</summary>
    public static FileLock? TryAcquire(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            return new FileLock(path, stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

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
    public static FileLock? Acquire(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryAcquire(path) is { } held) return held;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(10);
        }
    }

    /// <summary>True when somebody holds the lock right now (probes by taking it briefly).</summary>
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
