using CsCode.App.Docking;
using CsCode.App.Startup;
using CsCode.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;

namespace CsCode.App.Background;

/// <summary>A hot-exit backup read back for a restore, with what happened to its file on disk meanwhile.</summary>
public sealed record PreparedBackup(BackupInfo Info, BackupDiskState DiskState, string Text);

/// <summary>A session loaded off the UI thread, ready to apply: the saved document (null for a new or unreadable session) and its backups.</summary>
public sealed record PreparedSession(string Name, bool IsUnnamed, SessionDocument? Document, SessionLoadStatus Status, string? Message, IReadOnlyList<PreparedBackup> Backups)
{
    /// <summary>The document's dock layout, parsed on the lane (never on the UI thread).</summary>
    public LayoutSnapshot? Layout { get; init; }
}

/// <summary>
/// The background side of a CsCode process started with <c>--host</c>, created in <c>Main</c> before Avalonia initialises: it takes the session's
/// lock, starts loading the session on its own lane, and opens the IPC endpoint at once (so the launcher's <c>hello</c> is answered while the
/// window is still being built). Requests that need the window wait until <see cref="AttachAsync"/> handed it to the
/// <see cref="SessionWindowController"/>. <c>hello</c> is answered from fields alone, never through the UI thread: a busy (or wedged) UI cannot
/// make the launcher wait longer than its handshake timeout.
/// </summary>
public sealed class BackgroundHost : IIpcHandler, IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<SessionWindowController> _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CrashHandler _crash;
    private FileLock? _sessionLock;
    private IpcServer? _server;
    private volatile SessionWindowController? _controller;
    private Task<PreparedSession>? _prepared;
    private string? _session;
    private bool _unnamed, _claimed;
    private volatile string _state = ProcessStates.Starting;
    private long _lastShownMs;
    private int _disposed;
    private volatile string? _startupFailure;
    private volatile Action? _quitAfterFailure;

    private BackgroundHost(CommandLineOptions options, RuntimeDirectory runtime, SessionStore store, CrashHandler crash)
    {
        Options = options;
        Runtime = runtime;
        Store = store;
        _crash = crash;
    }

    public CommandLineOptions Options { get; }
    public RuntimeDirectory Runtime { get; }
    public SessionStore Store { get; }

    /// <summary>Session autosave, backups and store reads run here (never on the UI thread, never on the shared pool).</summary>
    public SerialLane Lane { get; } = new("CsCode session lane");

    public bool IsHeadlessTest => Options.HeadlessTest;
    public string State => _state;
    public string? SessionName { get { lock (_gate) return _session; } }
    public bool IsUnnamed { get { lock (_gate) return _unnamed; } }
    public bool IsUnclaimedSpare { get { lock (_gate) return Options.Spare && !_claimed; } }
    public DateTimeOffset? LastShown => Interlocked.Read(ref _lastShownMs) is var ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    /// <summary>Answers hello's "debugging" (set by the controller once the services exist).</summary>
    internal Func<bool>? IsDebugging { get; set; }

    private volatile bool _needsPrompt;

    /// <summary>Hello's <c>needsPrompt</c>: quitting now would ask the user about unsaved changes (kept current by the controller on the UI thread).</summary>
    internal bool NeedsPrompt { get => _needsPrompt; set => _needsPrompt = value; }

    /// <summary>
    /// Takes the session (or prepares to be the spare) and opens the endpoint. Returns null with <paramref name="refusedExitCode"/> set when this
    /// process must not run (3: another process owns the session; 2: invalid name), or null without it when background mode is unavailable here
    /// (the window then runs as a plain in-process window).
    /// </summary>
    public static BackgroundHost? Start(CommandLineOptions options, CrashHandler crash, out int? refusedExitCode)
    {
        SessionTrace.Mark("host: main");
        ProcessMemory.DetachFromTerminal();
        return Start(options, crash, RuntimeDirectory.Resolve(), new SessionStore(new AppPaths().UserDataDir), out refusedExitCode);
    }

    /// <summary>The same with the runtime directory and store given (tests: a private directory, and no detaching of the test process).</summary>
    internal static BackgroundHost? Start(CommandLineOptions options, CrashHandler crash, RuntimeDirectory runtime, SessionStore store, out int? refusedExitCode)
    {
        refusedExitCode = null;
        if (runtime.Ensure() is { } problem)
        {
            Console.Error.WriteLine("cscode: background mode unavailable: " + problem);
            return null;
        }
        var host = new BackgroundHost(options, runtime, store, crash);
        if (!options.Spare)
        {
            var name = options.Session;
            if (name is not null && SessionNames.Validate(name) is { } invalid)
            {
                Console.Error.WriteLine($"cscode: invalid session name '{name}': {invalid}");
                refusedExitCode = 2;
                host.Dispose();
                return null;
            }
            if (name is null)
            {
                name = host.TakeNewUnnamedSession();
            }
            else if (!host.TryTakeSession(name, options.Unnamed))
            {
                Console.Error.WriteLine($"cscode: session '{name}' is owned by another CsCode process");
                refusedExitCode = 3;
                host.Dispose();
                return null;
            }
            host._prepared = host.PrepareAsync(name, host.IsUnnamed, options.SeedFrom);
        }
        crash.Reported += host.OnCrashReported;
        try { host.StartServer(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("cscode: cannot open the IPC endpoint: " + ex.Message);
            host.Dispose();
            return null;
        }
        return host;
    }

    private void StartServer()
    {
        var server = new IpcServer(Runtime.EndpointOf(Environment.ProcessId), this, new IpcServerOptions { AllowTestVerbs = Options.HeadlessTest });
        server.Start();
        _server = server;
    }

    private string TakeNewUnnamedSession()
    {
        var taken = Store.List().Select(s => s.Name).ToList();
        while (true)
        {
            var name = SessionNames.NextUnnamed(taken);
            if (TryTakeSession(name, unnamed: true)) return name;
            taken.Add(name);
        }
    }

    /// <summary>Takes the lock of <paramref name="name"/> and makes it this process's session (releasing the previous one). False when another process holds it.</summary>
    internal bool TryTakeSession(string name, bool unnamed)
    {
        var acquired = FileLock.TryAcquire(Store.LockPathOf(name));
        if (acquired is null) return false;
        FileLock? previous;
        lock (_gate)
        {
            previous = _sessionLock;
            _sessionLock = acquired;
            _session = name;
            _unnamed = unnamed;
        }
        previous?.Dispose();
        return true;
    }

    internal void SetState(string state) => _state = state;

    /// <summary>
    /// Start-up failed (the app session shows its crash window): the process stays only until that window is closed or it is asked to quit. From
    /// now on every request that needs the window fails at once with the reason (the launcher prints it instead of waiting 30 s for a show that
    /// can never come), <c>quit</c> ends the process through <paramref name="quit"/>, and the session's lock is released, so the next launch of
    /// the session starts a fresh process rather than taking this one for a wedged owner.
    /// </summary>
    internal void StartupFailed(string summary, string? logPath, Action quit)
    {
        _quitAfterFailure = quit;
        _startupFailure = "CsCode could not start: " + summary + (logPath is null ? "" : $" (log: {logPath})");
        _state = ProcessStates.Quitting;
        _attached.TrySetException(new StartupFailedException(_startupFailure));
        ReleaseSessionLock();
        SessionTrace.Mark("host: start-up failed");
    }

    /// <summary>Lets go of the session (start-up failed, or the quit committed and its last write is done); hello keeps naming it, as quitting.</summary>
    internal void ReleaseSessionLock()
    {
        FileLock? held;
        lock (_gate)
        {
            held = _sessionLock;
            _sessionLock = null;
        }
        held?.Dispose();
    }

    /// <summary>The answer to a request that needed the window of a process whose start-up failed: quit ends it, anything else gets the reason.</summary>
    private IpcMessage AfterStartupFailure(IpcMessage request, StartupFailedException failure)
    {
        if (request.Type != IpcVerbs.Quit) return IpcMessage.Failure(failure.Message);
        _quitAfterFailure?.Invoke();
        return IpcMessage.Reply();
    }

    private sealed class StartupFailedException(string message) : Exception(message);

    internal void MarkShown()
    {
        Interlocked.Exchange(ref _lastShownMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _state = ProcessStates.Shown;
        if (SessionName is { } name) _ = Lane.RunAsync(() => Store.WriteLastActive(name));
    }

    /// <summary>Loads a session (or a copy of <paramref name="seedFrom"/>) and its backups on the lane.</summary>
    internal Task<PreparedSession> PrepareAsync(string name, bool unnamed, string? seedFrom = null) => Lane.RunAsync(() =>
    {
        var source = seedFrom ?? name;
        var loaded = Store.Load(source);
        var doc = loaded.Session is { } d ? d with { Name = name, IsUnnamed = unnamed || (seedFrom is null && d.IsUnnamed) } : null;
        var backups = new List<PreparedBackup>();
        foreach (var info in Store.Backups.List(source))
        {
            if (Store.Backups.ReadText(source, info.Id) is { } text) backups.Add(new PreparedBackup(info, HotExitBackupStore.Check(info), text));
        }
        return new PreparedSession(name, doc?.IsUnnamed ?? unnamed, doc, loaded.Status, loaded.Message, backups) { Layout = LayoutSerializer.Deserialize(doc?.Layout) };
    });

    /// <summary>Called by the app session on the UI thread once the main window exists (not shown yet). Shows it unless this is the spare or <c>--hidden</c>.</summary>
    internal async Task AttachAsync(MainWindow window, IServiceProvider services)
    {
        SessionTrace.Mark("host: window built");
        var controller = new SessionWindowController(this, window, services);
        _controller = controller;
        _attached.TrySetResult(controller);
        if (_prepared is null)
        {
            // The warm spare: everything is built, nothing is shown. Warm up the first frame off screen, give back the start-up garbage, and wait
            // for a claim (a claim may arrive at any point of this; it then simply runs after the current step).
            _state = ProcessStates.Spare;
            await controller.WarmUpSpareAsync().ConfigureAwait(true);
            _ = Task.Run(() => ProcessMemory.TrimNow());
            _ = controller.QuitIfDuplicateSpareAsync();
            return;
        }
        var prepared = await _prepared.ConfigureAwait(true);
        await controller.ActivateAsync(prepared, hidden: Options.Hidden).ConfigureAwait(true);
    }

    public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case IpcVerbs.Hello:
                return new IpcMessage
                {
                    Type = IpcVerbs.Hello, Ok = true, ProtocolVersion = IpcMessage.ProtocolVersionCurrent, Pid = Environment.ProcessId, Session = SessionName,
                    State = _state, LastShownUnixMs = LastShown?.ToUnixTimeMilliseconds(), Debugging = SafeIsDebugging(), WorkingSetBytes = Environment.WorkingSet,
                    NeedsPrompt = _needsPrompt ? true : null,
                };
            case IpcVerbs.ListSessions:
                var list = await Lane.RunAsync(Store.List).ConfigureAwait(false);
                return IpcMessage.Reply() with { Sessions = list };
            case IpcVerbs.Claim:
                return await ClaimAsync(request, ct).ConfigureAwait(false);
        }
        SessionWindowController controller;
        try { controller = await _attached.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (StartupFailedException ex) { return AfterStartupFailure(request, ex); }
        return await controller.HandleAsync(request, ct).ConfigureAwait(false);
    }

    private bool SafeIsDebugging()
    {
        try { return IsDebugging?.Invoke() ?? false; }
        catch (Exception) { return false; } // a hello never fails because of a service
    }

    /// <summary>The spare takes a session: its lock (here, off the UI thread), its saved state (lane), then the window restores it (UI).</summary>
    private async Task<IpcMessage> ClaimAsync(IpcMessage request, CancellationToken ct)
    {
        var name = request.Session;
        if (SessionNames.Validate(name) is { } invalid) return IpcMessage.Failure(invalid);
        if (_startupFailure is { } failed) return IpcMessage.Failure(failed);
        lock (_gate)
        {
            if (!Options.Spare || _claimed) return IpcMessage.Failure($"this process is not a free spare (state {_state})");
            _claimed = true;
        }
        if (!TryTakeSession(name!, request.Unnamed == true))
        {
            lock (_gate) _claimed = false;
            return IpcMessage.Failure($"session '{name}' is owned by another CsCode process");
        }
        _state = ProcessStates.Starting;
        SessionTrace.Mark("host: claim " + name);
        var prepared = await PrepareAsync(name!, request.Unnamed == true).ConfigureAwait(false);
        SessionTrace.Mark("host: claim prepared");
        SessionWindowController controller;
        try { controller = await _attached.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (StartupFailedException ex)
        {
            ReleaseSessionLock(); // taken by this claim after the failure: the launcher starts a fresh process for the session instead
            return IpcMessage.Failure(ex.Message);
        }
        await controller.ActivateOnUiAsync(prepared, hidden: request.Hidden == true).ConfigureAwait(false);
        SessionTrace.Mark("host: claim done");
        return IpcMessage.Reply();
    }

    private void OnCrashReported(string summary, string path, bool fatal)
    {
        // A fatal crash: write what the last autosave captured but could not write yet (synchronously: the process is going down).
        if (fatal) _controller?.FlushOnCrash();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _crash.Reported -= OnCrashReported;
        _server?.Dispose();
        FileLock? held;
        lock (_gate)
        {
            held = _sessionLock;
            _sessionLock = null;
        }
        held?.Dispose();
        Lane.Dispose();
    }
}
