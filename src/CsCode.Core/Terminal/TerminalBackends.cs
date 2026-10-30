namespace CsCode.Core.Terminal;

// Additive Core contract (Remote R3, wave 3): terminals that run somewhere other than this machine. Implemented by CsCode.Remote (a PTY on the remote
// server); consumed by CsCode.Terminal, which asks the provider for every new terminal and falls back to a local PTY when it answers null.

/// <summary>Decides where a new terminal runs: a backend when the workspace is remote, else null (a local PTY).</summary>
public interface ITerminalBackendProvider
{
    /// <summary>A backend for this terminal when it must run elsewhere, else null. Cheap and non-blocking (no I/O).</summary>
    ITerminalBackend? TryCreate(TerminalCreateOptions options);
}

/// <summary>Receives a backend's output and exit, in order, on the backend's delivery thread. Blocking here throttles the remote program.</summary>
public interface ITerminalBackendListener
{
    void OnOutput(ReadOnlySpan<byte> data);

    /// <summary>The program exited (after its last output): exit code, 128 + signal for a killed process, -1 when the connection to it was lost for good.</summary>
    void OnExited(int exitCode);
}

/// <summary>What the other side started: its process id there, the program (resolved there) and its working directory there.</summary>
public sealed record TerminalBackendStarted(int ProcessId, string Program, string WorkingDirectory)
{
    /// <summary>
    /// The few variables of the program's environment the terminal itself needs (additive, end pass): the shell-integration ones (<c>CSCODE_NONCE</c>,
    /// <c>CSCODE_INJECTION</c>) when the other side started the shell with CsCode's shell integration. Null: none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// A terminal program running elsewhere, with the shape of a local PTY host. Every member may be called from any thread and returns without waiting,
/// except <see cref="Start"/> (one round trip: the terminal service calls it off the UI thread, as it spawns local programs there).
/// </summary>
public interface ITerminalBackend : IDisposable
{
    /// <summary>
    /// Starts the program; the other side resolves the profile, shell, environment and working directory from <paramref name="options"/> (its own
    /// files and settings). Throws <see cref="IOException"/> or <see cref="InvalidOperationException"/> with the reason when it cannot start.
    /// </summary>
    TerminalBackendStarted Start(TerminalCreateOptions options, int columns, int rows);

    /// <summary>Connects the listener: output produced since <see cref="Start"/> is delivered first.</summary>
    void Attach(ITerminalBackendListener listener);

    int ProcessId { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>Queues input (false: the bounded queue is full). Keeps working across a short loss of the connection (sent on reconnect).</summary>
    bool Write(ReadOnlySpan<byte> data, bool urgent = false);

    long PendingWriteBytes { get; }

    long MaxPendingWriteBytes { get; }

    void Resize(int columns, int rows);

    /// <summary>Ends the program (SIGHUP, then SIGKILL after <paramref name="grace"/> on the other side). Returns at once.</summary>
    void Kill(TimeSpan? grace = null);

    /// <summary>The foreground process's name as last reported by the other side (refreshed in the background), or null.</summary>
    string? GetForegroundProcessName();
}
