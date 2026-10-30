using CsCode.Core.Terminal;

namespace CsCode.Terminal.Pty;

/// <summary>
/// A terminal whose program runs through an <see cref="ITerminalBackend"/> (a remote workspace's server runs the PTY): the session sees an ordinary
/// <see cref="IPtyHost"/>. The backend was started before (see <see cref="BackendLauncher"/>): <see cref="Start"/> only connects the listener.
/// </summary>
public sealed class BackendPtyHost(ITerminalBackend backend) : IPtyHost
{
    public ITerminalBackend Backend { get; } = backend;

    public void Start(PtyStartInfo info, IPtyListener listener) => Backend.Attach(new Listener(listener));

    public int ProcessId => Backend.ProcessId;
    public bool HasExited => Backend.HasExited;
    public int? ExitCode => Backend.ExitCode;
    public bool Write(ReadOnlySpan<byte> data, bool urgent = false) => Backend.Write(data, urgent);
    public long PendingWriteBytes => Backend.PendingWriteBytes;
    public long MaxPendingWriteBytes => Backend.MaxPendingWriteBytes;
    public void Resize(int columns, int rows) => Backend.Resize(columns, rows);
    public void Kill(TimeSpan? grace = null) => Backend.Kill(grace);
    public string? GetForegroundProcessName() => Backend.GetForegroundProcessName();
    public void Dispose() => Backend.Dispose();

    private sealed class Listener(IPtyListener inner) : ITerminalBackendListener
    {
        public void OnOutput(ReadOnlySpan<byte> data) => inner.OnOutput(data);
        public void OnExited(int exitCode) => inner.OnExited(exitCode);
    }
}

/// <summary>
/// Starts the backend of a terminal that runs elsewhere and hands it to the session's host factory: the start (a round trip) resolves the program
/// and directory on the other side, and the session shows those (tab title, process name). A reused transient terminal starts a fresh backend.
/// </summary>
internal sealed class BackendLauncher(ITerminalBackendProvider provider, TerminalCreateOptions options, ITerminalBackend first)
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();
    private ITerminalBackend? _first = first;
    private ITerminalBackend? _started;

    /// <summary>The session's host factory: the backend <see cref="Start"/> just started.</summary>
    public IPtyHost CreateHost() =>
        new BackendPtyHost(Interlocked.Exchange(ref _started, null) ?? throw new PtySpawnException("The remote terminal was not started."));

    /// <summary>
    /// The terminal was closed before its session took the backend <see cref="Start"/> started (during that round trip): the backend goes, and
    /// with it the program on the other side, which would otherwise run unseen (a persistent server keeps it) with a delivery thread waiting for
    /// a listener that never comes. Called on the thread that called <see cref="Start"/>.
    /// </summary>
    public void Discard() => Interlocked.Exchange(ref _started, null)?.Dispose();

    /// <summary>Starts a backend on the other side (blocking: called off the UI thread) and describes it for the session.</summary>
    /// <param name="next">What to run: the options of the program a reused terminal runs now (the next task's, the next debuggee's); null: the
    /// terminal's first program.</param>
    public PtyStartInfo Start(int columns, int rows, TerminalCreateOptions? next = null)
    {
        var current = next ?? options;
        var backend = Interlocked.Exchange(ref _first, null) ?? provider.TryCreate(current)
                      ?? throw new PtySpawnException("The terminal's remote workspace is no longer connected.");
        TerminalBackendStarted started;
        try { started = backend.Start(current, columns, rows); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            backend.Dispose();
            throw new PtySpawnException(ex.Message, 0, ex);
        }
        Interlocked.Exchange(ref _started, backend)?.Dispose();
        // The shell-integration variables the other side gave the shell: the emulator trusts the command lines reported with that nonce.
        return new PtyStartInfo(started.Program, current.ShellArgs ?? [], started.WorkingDirectory, started.Environment ?? NoEnvironment, columns, rows);
    }
}
