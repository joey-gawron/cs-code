namespace CsCode.Terminal.Pty;

/// <summary>What to run on a pseudo-terminal. Everything is final: the session resolved the profile, the path and the environment.</summary>
/// <param name="FileName">An absolute path to the executable (resolved against the child's PATH by the caller).</param>
/// <param name="Arguments">argv[1..] (argv[0] is the file name).</param>
/// <param name="WorkingDirectory">An existing directory.</param>
/// <param name="Environment">The child's complete environment.</param>
public sealed record PtyStartInfo(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns,
    int Rows)
{
    /// <summary>argv[0]; default: <see cref="FileName"/>. A login shell gets "-bash" style names only when the profile asks for it.</summary>
    public string? Argv0 { get; init; }
}

/// <summary>Receives a PTY's output and exit. Called on the host's reader thread: implementations copy what they need and return quickly.</summary>
public interface IPtyListener
{
    /// <summary>Bytes the program wrote. <paramref name="data"/> is valid only during the call. Blocking here throttles the program (backpressure).</summary>
    void OnOutput(ReadOnlySpan<byte> data);

    /// <summary>The process exited (after its last output was delivered). Exit code, or 128 + signal for a killed process.</summary>
    void OnExited(int exitCode);
}

/// <summary>The program could not be started (no such file, not executable, no PTY available).</summary>
public sealed class PtySpawnException(string message, int errno = 0, Exception? inner = null) : Exception(message, inner)
{
    public int Errno { get; } = errno;
}

/// <summary>
/// A process on a pseudo-terminal: the Unix host (posix_openpt + posix_spawn), Windows' ConPTY, and later a remote host (the remote server
/// runs a local one and forwards it). Threading: every member may be called from any thread and none of them blocks — writes are queued for
/// the host's writer thread (a full PTY input buffer, a program that stopped reading, must never block the UI or the caller), output arrives on
/// the host's reader thread through <see cref="IPtyListener"/>.
/// </summary>
public interface IPtyHost : IDisposable
{
    /// <summary>Spawns the program. Throws <see cref="PtySpawnException"/> when it cannot be started. Call once.</summary>
    void Start(PtyStartInfo info, IPtyListener listener);

    /// <summary>The child's process id (0 before <see cref="Start"/>).</summary>
    int ProcessId { get; }

    bool HasExited { get; }

    /// <summary>Exit code (128 + signal when killed by a signal), once exited.</summary>
    int? ExitCode { get; }

    /// <summary>
    /// Queues bytes for the program. Returns false (nothing queued) when the bounded write queue is full: the program has not been reading
    /// and <see cref="MaxPendingWriteBytes"/> are already waiting. <paramref name="urgent"/> writes (an interrupt, Ctrl+C) go ahead of
    /// queued text, so they reach a program even while a big paste waits for it.
    /// </summary>
    bool Write(ReadOnlySpan<byte> data, bool urgent = false);

    /// <summary>Bytes queued and not yet accepted by the PTY.</summary>
    long PendingWriteBytes { get; }

    /// <summary>Bound of the write queue.</summary>
    long MaxPendingWriteBytes { get; }

    /// <summary>Sets the window size (the kernel sends SIGWINCH to the foreground process group).</summary>
    void Resize(int columns, int rows);

    /// <summary>
    /// Ends the program: SIGHUP to its process group (and the terminal is hung up), then SIGKILL to everything still in its session after
    /// <paramref name="grace"/> (default 2 s). Returns at once.
    /// </summary>
    void Kill(TimeSpan? grace = null);

    /// <summary>The name of the terminal's foreground process ("bash", "vim", "python3"), or null when unknown. Reads /proc: call it off the UI thread.</summary>
    string? GetForegroundProcessName();
}

/// <summary>Creates the platform's PTY host.</summary>
public static class PtyHosts
{
    public static IPtyHost Create() =>
        OperatingSystem.IsWindows() ? new Windows.ConPtyHost() : new Unix.UnixPtyHost();

    /// <summary>Whether this platform can host a PTY at all (Unix: <c>/dev/ptmx</c>; Windows 10 1809+: ConPTY).</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() ? OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();
}
