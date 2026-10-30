using System.Buffers;
using System.Diagnostics;
using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Pty.Unix;

namespace CsCode.Terminal.Sessions;

/// <summary>
/// One terminal: a PTY host, the emulator and the terminal's own worker thread. The host's reader thread copies output into a bounded queue
/// (256 KB: when the worker falls behind, the reader waits and the program is throttled by its full PTY — the backpressure a terminal
/// gives); the worker parses, mutates the screen and builds snapshots. The UI never touches the emulator: it asks for a frame
/// (<see cref="RequestFrame"/>), the worker builds at most one <see cref="TerminalSnapshot"/> per request and fires <see cref="FrameReady"/>,
/// so a flood of output costs the UI one snapshot per rendered frame and a terminal nobody looks at (a hidden tool window, a hidden session,
/// an inactive terminal) costs the UI nothing. Keystrokes go straight to the host's bounded writer queue (no worker hop).
/// </summary>
public sealed class TerminalSession : ITerminal, IDisposable
{
    /// <summary>Output waiting for the worker before the PTY reader is made to wait.</summary>
    public const long MaxQueuedBytes = 256 * 1024;

    private const int ChunkBytes = 64 * 1024;
    private static readonly TimeSpan ProcessNamePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SynchronizedOutputLimit = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Queue<Work> _queue = new();
    private readonly Func<IPtyHost> _hostFactory;
    private readonly Action? _discardPrepared;
    private readonly Thread _worker;
    private readonly Emitter<TerminalSession> _frameReady = new();
    private readonly Emitter<TerminalSession> _titleChanged = new();
    private readonly Emitter<TerminalExitedEventArgs> _exited = new();
    private readonly Emitter<TerminalSession> _bell = new();
    private readonly Emitter<string> _clipboardWrite = new();
    private IPtyHost? _host;
    private long _queuedBytes;
    private bool _stopping;
    private volatile bool _disposed;

    // published state (written on the worker, read anywhere)
    private TerminalSnapshot? _latest;
    private TerminalModes _modes = TerminalModes.Initial;
    private int _frameRequested = 1;
    private int _nudgePosted;
    private long _publishedChange = -1;
    private long _sequence;
    private volatile string? _oscTitle;
    private volatile string? _processName;
    private volatile string? _userTitle;
    private volatile string? _cwd;
    private volatile bool _hasExited;
    private int _exitCode = -1;
    private int _processId;
    private long _lastNamePoll;
    private long _syncSince;
    private Timer? _syncTimer;

    private readonly struct Work(byte[]? data, int length, Action? action)
    {
        public readonly byte[]? Data = data;
        public readonly int Length = length;
        public readonly Action? Action = action;
    }

    /// <param name="discardPrepared">
    /// Ends a program started for the next <see cref="Start"/> before it (a remote terminal's: the server started it when the launch was
    /// resolved) when the session turns out to be closed and <paramref name="hostFactory"/> will not take it.
    /// </param>
    internal TerminalSession(int id, TerminalCreateOptions options, Func<IPtyHost> hostFactory, EmulatorOptions emulatorOptions, int columns, int rows,
        Action? discardPrepared = null)
    {
        Id = id;
        Options = options;
        _hostFactory = hostFactory;
        _discardPrepared = discardPrepared;
        Emulator = new VtEmulator(columns, rows, emulatorOptions)
        {
            Respond = bytes => _host?.Write(bytes),
            TitleChanged = title => { _oscTitle = title.Length == 0 ? null : title; _titleChanged.Fire(this); },
            CurrentDirectoryChanged = dir => _cwd = dir,
            Bell = () => _bell.Fire(this),
            ClipboardWrite = text => _clipboardWrite.Fire(text),
            CommandRecorded = OnCommandRecorded,
        };
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"cscode-terminal-{id}" };
        _worker.Start();
    }

    public int Id { get; }
    public TerminalCreateOptions Options { get; }

    /// <summary>The emulator: touched only on this session's worker (use <see cref="Post"/> / <see cref="InvokeAsync{T}"/>).</summary>
    internal VtEmulator Emulator { get; }

    public string Title => _userTitle ?? _name ?? Options.Name ?? _oscTitle ?? _processName ?? "Terminal";

    private volatile string? _name;

    /// <summary>A new creator's name (a task terminal reused for another task is named after that task).</summary>
    internal void SetName(string name)
    {
        _name = name;
        _titleChanged.Fire(this);
    }

    /// <summary>
    /// The program's raw output as the PTY delivers it (reader thread, before the emulator sees it): a task's problem matchers read it.
    /// Must return at once.
    /// </summary>
    internal Action<ReadOnlySpan<byte>>? OutputTap { get; set; }

    /// <summary>What is written below the output when the program exits (null: VS Code's "[The terminal process exited with code N.]").</summary>
    internal Func<int, string>? ExitMessage { get; set; }

    /// <summary>VS Code's task terminals: once the program exited, any key closes the terminal ("press any key to close it").</summary>
    internal bool CloseOnInputAfterExit { get; set; }

    private readonly Emitter<TerminalSession> _closeRequested = new();

    /// <summary>A key was pressed in an exited terminal that closes on input (<see cref="CloseOnInputAfterExit"/>).</summary>
    internal IEventSource<TerminalSession> CloseRequested => _closeRequested;

    /// <summary>Writes text into the terminal as if the program had (CsCode's own messages: a task's command line). Any thread.</summary>
    internal void WriteLocal(string text) => Post(() => Emulator.Feed(text));
    public int? ProcessId => _processId > 0 ? _processId : null;
    public bool IsTransient => Options.IsTransient;
    public bool HasExited => _hasExited;
    /// <summary>The exit code once exited (-1: the program could not be started).</summary>
    public int? ExitCode => _hasExited ? Volatile.Read(ref _exitCode) : null;
    public string? CurrentDirectory => _cwd;
    public bool IsDisposed => _disposed;

    /// <summary>The directory the program was started in.</summary>
    public string? StartDirectory { get; private set; }

    /// <summary>CsCode started the shell with its shell-integration script (the marks still depend on the shell running it).</summary>
    public bool ShellIntegrationInjected { get; private set; }

    /// <summary>The last command the shell reported finished (shell integration), with a short tail of its output; null before the first.</summary>
    public TerminalCommandSummary? LastCommand => Volatile.Read(ref _lastCommand);

    private TerminalCommandSummary? _lastCommand;
    private readonly Emitter<TerminalCommandSummary> _commandFinished = new();

    /// <summary>A command finished (shell integration; fires on the worker thread): screen readers announce it.</summary>
    public IEventSource<TerminalCommandSummary> CommandFinished => _commandFinished;

    /// <summary>On the worker, right after the emulator recorded a finished command: a bounded summary for the UI.</summary>
    private void OnCommandRecorded(TerminalCommand command)
    {
        var summary = new TerminalCommandSummary(command, Emulator.CommandOutput(command, TerminalCommandSummary.MaxOutputTail, maxRows: 200) ?? "");
        Volatile.Write(ref _lastCommand, summary);
        _commandFinished.Fire(summary);
    }

    /// <summary>The whole output of the last command (bounded by the scrollback), read on the worker; null without shell integration.</summary>
    public Task<string?> GetLastCommandOutputAsync() => InvokeAsync(e => e.CommandOutput());

    /// <summary>The scrollback and screen as text for screen readers (VS Code's accessible buffer), read on the worker.</summary>
    public Task<TerminalAccessibleBuffer> GetAccessibleBufferAsync() => InvokeAsync(e => e.AccessibleBuffer());

    /// <summary>
    /// The program's working directory as the kernel knows it (<c>/proc/&lt;pid&gt;/cwd</c>, macOS: libproc): right even for a shell without
    /// shell integration that never reported a <c>cd</c>. Null when unknown (exited, no process table). Blocking read: call off the UI thread.
    /// </summary>
    public string? ProcessDirectory()
    {
        if (ProcessId is not { } pid || _hasExited || !ProcessTable.IsAvailable) return null;
        return ProcessTable.WorkingDirectory(pid) is { } dir && Directory.Exists(dir) ? dir : null;
    }

    /// <summary>The program's window title (OSC 0/2), if it set one.</summary>
    public string? ProgramTitle => _oscTitle;

    /// <summary>The rename the user gave the tab (null: automatic).</summary>
    public string? UserTitle
    {
        get => _userTitle;
        set { _userTitle = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); _titleChanged.Fire(this); }
    }

    /// <summary>Fires on the worker thread when a frame the UI asked for is ready (<see cref="LatestSnapshot"/>).</summary>
    public IEventSource<TerminalSession> FrameReady => _frameReady;
    public IEventSource<TerminalSession> TitleChanged => _titleChanged;
    public IEventSource<TerminalExitedEventArgs> Exited => _exited;
    public IEventSource<TerminalSession> BellRang => _bell;
    /// <summary>OSC 52 writes (only when allowed in the settings).</summary>
    public IEventSource<string> ClipboardWriteRequested => _clipboardWrite;

    /// <summary>The newest frame (immutable), or null before the first one.</summary>
    public TerminalSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    /// <summary>The modes input encoding needs, current as of the last parsed output.</summary>
    public TerminalModes Modes => Volatile.Read(ref _modes);

    // ------------------------------------------------------------------ process

    /// <summary>
    /// Spawns the program (on the calling thread: callers run it off the UI thread). Failures are written into the terminal and reported as an
    /// exit. A session disposed meanwhile (its terminal closed while it was being created or reused) starts nothing: a program spawned then
    /// would run unseen and never be killed. A remote one, which the server started while the launch was resolved, is ended.
    /// </summary>
    internal void Start(PtyStartInfo info)
    {
        if (_disposed)
        {
            _discardPrepared?.Invoke();
            return;
        }
        // The view may have measured the terminal before the program could start (a split pane's layout during a remote round trip): a local
        // PTY opens at that size, so the program's first query already sees it. (A remote one was opened when the launch was resolved: the
        // size is passed on below; resizing a PTY to the size it has is a no-op in the kernel, no SIGWINCH.)
        var applied = ((long)info.Columns << 32) | (uint)info.Rows;
        if (PendingSize() is { } pending) info = info with { Columns = pending.Columns, Rows = pending.Rows };
        var host = _hostFactory();
        // shell integration: the emulator trusts command lines reported with the nonce the shell was given (queued before any output)
        var nonce = info.Environment.GetValueOrDefault(ShellIntegrationInjector.NonceVariable);
        ShellIntegrationInjected = info.Environment.ContainsKey(ShellIntegrationInjector.InjectionVariable);
        Post(() => Emulator.ShellIntegrationNonce = nonce);
        var listener = new Listener(this);
        try
        {
            host.Start(info, listener);
        }
        catch (PtySpawnException ex)
        {
            listener.Detach();
            host.Dispose();
            Fail(ex.Message);
            return;
        }
        var installed = false;
        lock (_gate)
        {
            if (!_disposed)
            {
                _host = host;
                _listener = listener;
                installed = true;
            }
        }
        if (!installed)
        {
            // disposed while the program was being spawned: Dispose did not see this host, so it goes here
            listener.Detach();
            host.Dispose();
            return;
        }
        Volatile.Write(ref _processId, host.ProcessId);
        _processName = Path.GetFileName(info.FileName);
        _startName = host.GetForegroundProcessName() ?? _processName;
        _cwd = info.WorkingDirectory;
        StartDirectory = info.WorkingDirectory;
        // A Resize that came in while the host was being spawned found no host to pass the size on to: give it the newest size now (a
        // later Resize to the same size returns early). Repeated until stable, so a Resize racing with this one cannot leave an older size.
        while (Volatile.Read(ref _pendingSize) is var size and >= 0 && size != applied)
        {
            host.Resize((int)(size >> 32), (int)(size & 0xffffffff));
            applied = size;
        }
    }

    /// <summary>The newest size <see cref="Resize"/> was given, or null before the first.</summary>
    private (int Columns, int Rows)? PendingSize()
    {
        var size = Volatile.Read(ref _pendingSize);
        return size < 0 ? null : ((int)(size >> 32), (int)(size & 0xffffffff));
    }

    private Listener? _listener;

    /// <summary>
    /// The host's view of this session. Detached when the session lets go of the host, so a disposed host's reader thread (alive until it has
    /// reaped the program, which may ignore the hang-up for a while) neither reaches nor keeps alive a closed terminal.
    /// </summary>
    private sealed class Listener(TerminalSession session) : IPtyListener
    {
        private volatile TerminalSession? _session = session;

        public void Detach() => _session = null;

        public void OnOutput(ReadOnlySpan<byte> data) => _session?.OnOutput(data);

        public void OnExited(int exitCode) => _session?.OnExited(exitCode);
    }

    /// <summary>The program could not be started: the reason is written into the terminal, which then counts as exited (code -1).</summary>
    internal void Fail(string message)
    {
        Post(() => Emulator.Feed($"\e[31m{message}\e[0m\r\n"));
        OnExited(-1);
    }

    /// <summary>Runs another program in this exited terminal (its buffer stays): a transient terminal reused for the next debug run.</summary>
    internal void Restart(PtyStartInfo info)
    {
        if (_disposed)
        {
            _discardPrepared?.Invoke(); // closed while it was being claimed for the next run: nothing starts (Start says so too)
            return;
        }
        if (!_hasExited) throw new InvalidOperationException("The terminal is still running.");
        IPtyHost? old;
        lock (_gate)
        {
            old = _host;
            _host = null;
            _listener?.Detach();
            _listener = null;
        }
        old?.Dispose();
        Post(Emulator.PrepareForNextProgram); // whatever the last program left on: primary screen, modes, visible cursor; a fresh line
        Volatile.Write(ref _exitCode, -1);
        _hasExited = false;
        Start(info);
    }

    public ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        // VS Code's sendText: line breaks become Enter (CR); a new line is added unless the text already ends with one.
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (addNewLine && !normalized.EndsWith('\r')) normalized += "\r";
        Input(Encoding.UTF8.GetBytes(normalized));
        return ValueTask.CompletedTask;
    }

    /// <summary>Keystrokes and pastes for the program: queued on the host's writer (never blocks); typing scrolls the view to the bottom.</summary>
    public bool Input(ReadOnlySpan<byte> bytes, bool urgent = false)
    {
        if (_hasExited && CloseOnInputAfterExit && !_disposed && !bytes.IsEmpty)
        {
            // a finished task's terminal: the key closes it
            CloseOnInputAfterExit = false;
            _closeRequested.Fire(this);
            return true;
        }
        return Report(bytes, urgent);
    }

    /// <summary>
    /// Bytes the terminal reports on its own (mouse and focus reports, the wheel's cursor keys): like <see cref="Input"/>, but never the key
    /// that closes a finished task's terminal (moving the mouse over it must not close it). <paramref name="scrollToBottom"/>: false for a
    /// report that is not the user's input, which leaves a scrolled-back view where it is (focus reports, as xterm.js sends them).
    /// </summary>
    public bool Report(ReadOnlySpan<byte> bytes, bool urgent = false, bool scrollToBottom = true)
    {
        if (_disposed || _host is not { } host || _hasExited) return false;
        var ok = host.Write(bytes, urgent);
        if (scrollToBottom) Post(ScrollToBottomIfScrolled);
        return ok;
    }

    private void ScrollToBottomIfScrolled()
    {
        if (!Emulator.FollowOutput) Emulator.ScrollViewportToBottom();
    }

    /// <summary>Kills the program (SIGHUP to its group, SIGKILL to its session after the grace period); the terminal stays.</summary>
    public void KillProcess(TimeSpan? grace) => _host?.Kill(grace);

    public void KillProcess() => KillProcess(TimeSpan.FromSeconds(1));

    /// <summary>Name of the foreground process (/proc): call off the UI thread.</summary>
    public string? ForegroundProcessName() => _host?.GetForegroundProcessName();

    /// <summary>True when the shell runs something in the foreground (VS Code's "has child processes" for confirmOnKill). Reads /proc.</summary>
    public bool HasForegroundChild()
    {
        if (_host is not { } host || _hasExited) return false;
        var fg = host.GetForegroundProcessName();
        return fg is not null && _startName is not null && fg != _startName;
    }

    private volatile string? _startName;

    // ------------------------------------------------------------------ UI requests (any thread)

    /// <summary>The UI drew <see cref="LatestSnapshot"/> and can take another frame.</summary>
    public void RequestFrame()
    {
        Volatile.Write(ref _frameRequested, 1);
        if (Interlocked.CompareExchange(ref _nudgePosted, 1, 0) == 0) Post(() => { Volatile.Write(ref _nudgePosted, 0); Publish(); });
    }

    /// <summary>
    /// Resizes the grid (on the worker, coalesced: only the newest size is applied) and the PTY. The grid change is queued first and the
    /// PTY's size set right away (a non-blocking ioctl): what the program draws after its SIGWINCH reaches the worker after the new grid.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 4096);
        rows = Math.Clamp(rows, 1, 4096);
        if (Interlocked.Exchange(ref _pendingSize, ((long)columns << 32) | (uint)rows) == ((long)columns << 32 | (uint)rows)) return;
        Post(ApplyResize);
        if (!_disposed) _host?.Resize(columns, rows);
    }

    private long _pendingSize = -1;

    private void ApplyResize()
    {
        var size = Volatile.Read(ref _pendingSize);
        if (size < 0) return;
        var columns = (int)(size >> 32);
        var rows = (int)(size & 0xffffffff);
        if (columns == Emulator.Columns && rows == Emulator.Rows) return;
        Emulator.Resize(columns, rows);
    }

    /// <summary>Queues work for the worker (the emulator may be used inside). Never blocks.</summary>
    public void Post(Action action)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _queue.Enqueue(new Work(null, 0, action));
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Runs <paramref name="func"/> on the worker and returns its result (selection text, search): never call it and wait on the UI thread.</summary>
    public Task<T> InvokeAsync<T>(Func<VtEmulator, T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopping) { tcs.SetCanceled(); return tcs.Task; }
            _queue.Enqueue(new Work(null, 0, () =>
            {
                try { tcs.SetResult(func(Emulator)); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
            Monitor.Pulse(_gate);
        }
        return tcs.Task;
    }

    // ------------------------------------------------------------------ PTY callbacks (reader thread)

    private void OnOutput(ReadOnlySpan<byte> data)
    {
        OutputTap?.Invoke(data);
        while (!data.IsEmpty)
        {
            var n = Math.Min(data.Length, ChunkBytes);
            var buffer = ArrayPool<byte>.Shared.Rent(n);
            data[..n].CopyTo(buffer);
            data = data[n..];
            lock (_gate)
            {
                // backpressure: the reader (and so the program, once the PTY buffer is full) waits for the worker
                while (_queuedBytes > MaxQueuedBytes && !_stopping) Monitor.Wait(_gate, 100);
                if (_stopping) { ArrayPool<byte>.Shared.Return(buffer); return; }
                _queue.Enqueue(new Work(buffer, n, null));
                _queuedBytes += n;
                Monitor.Pulse(_gate);
            }
        }
    }

    private void OnExited(int exitCode)
    {
        Post(() =>
        {
            if (_hasExited) return;
            Volatile.Write(ref _exitCode, exitCode);
            _hasExited = true;
            // a synchronized update the program never ended (it was killed while drawing) ends with it
            if (Emulator.SynchronizedOutput) EndSynchronizedOutput();
            // VS Code's wording; the buffer stays so the user can read what happened.
            Emulator.Feed(ExitMessage?.Invoke(exitCode) ?? $"\e[0m\r\n[The terminal process exited with code {exitCode}.]");
            Publish();
            _exited.Fire(new TerminalExitedEventArgs(this, exitCode));
            _titleChanged.Fire(this);
        });
    }

    // ------------------------------------------------------------------ worker

    private void WorkerLoop()
    {
        var lastPublish = Stopwatch.GetTimestamp();
        while (true)
        {
            Work work;
            bool more;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stopping) Monitor.Wait(_gate);
                if (_queue.Count == 0) break; // stopping and drained
                work = _queue.Dequeue();
                if (work.Data is not null)
                {
                    _queuedBytes -= work.Length;
                    Monitor.PulseAll(_gate);
                }
                more = _queue.Count > 0;
            }
            try
            {
                if (work.Data is not null)
                {
                    Emulator.Feed(work.Data.AsSpan(0, work.Length));
                    ArrayPool<byte>.Shared.Return(work.Data);
                }
                else work.Action?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The emulator never throws on input; an action may (a test's, a closed host). The terminal keeps working.
                Debug.WriteLine($"terminal {Id}: {ex}");
            }
            // Publish when the queue ran dry, and during a flood at least every 8 ms (a frame never waits for the flood to end).
            if (!more || Stopwatch.GetElapsedTime(lastPublish).TotalMilliseconds >= 8)
            {
                Publish();
                lastPublish = Stopwatch.GetTimestamp();
            }
        }
        lock (_gate)
        {
            while (_queue.Count > 0)
                if (_queue.Dequeue().Data is { } buffer) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Publish()
    {
        var e = Emulator;
        if (!ReferenceEquals(_modes, e.Modes)) Volatile.Write(ref _modes, e.Modes);
        PollProcessName();
        if (e.ChangeCount == _publishedChange || Volatile.Read(ref _frameRequested) == 0) return;
        if (e.SynchronizedOutput)
        {
            // DECSET 2026: the program draws a frame in pieces; show it when it says so, or after a second at the latest. An update the
            // program does not end within the second is ended here (as the 2026 spec and other terminals do): output after it is drawn as
            // it comes, not held for a second at a time until a reset.
            if (_syncSince == 0) _syncSince = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_syncSince) < SynchronizedOutputLimit)
            {
                _syncTimer ??= new Timer(_ => Post(SynchronizedOutputExpired), null, SynchronizedOutputLimit, Timeout.InfiniteTimeSpan);
                return;
            }
            EndSynchronizedOutput();
        }
        else if (_syncTimer is not null) EndSynchronizedOutput(); // ended by the program: a timer armed for it must not end a later update
        _syncSince = 0;
        var snapshot = e.CreateSnapshot(++_sequence);
        _publishedChange = e.ChangeCount;
        Volatile.Write(ref _latest, snapshot);
        Volatile.Write(ref _frameRequested, 0);
        _frameReady.Fire(this);
    }

    /// <summary>On the worker: the synchronized update's second is over (the timer fired; it may fire a hair early by the stopwatch's count).</summary>
    private void SynchronizedOutputExpired()
    {
        if (Emulator.SynchronizedOutput && _syncSince != 0) EndSynchronizedOutput();
        else
        {
            _syncTimer?.Dispose();
            _syncTimer = null;
        }
        Publish();
    }

    /// <summary>On the worker: the synchronized update ends (timed out, or the program exited) and its timer goes.</summary>
    private void EndSynchronizedOutput()
    {
        Emulator.EndSynchronizedOutput();
        _syncTimer?.Dispose();
        _syncTimer = null;
        _syncSince = 0;
    }

    private void PollProcessName()
    {
        if (_host is not { } host || _hasExited || Options.Name is not null) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastNamePoll != 0 && Stopwatch.GetElapsedTime(_lastNamePoll) < ProcessNamePollInterval) return;
        _lastNamePoll = now;
        var name = host.GetForegroundProcessName();
        if (name is not null && name != _processName)
        {
            _processName = name;
            if (_oscTitle is null && _userTitle is null) _titleChanged.Fire(this);
        }
    }

    public void Dispose()
    {
        IPtyHost? host;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            host = _host; // a host Start installs after this point is disposed by Start itself
            _listener?.Detach();
            Monitor.PulseAll(_gate);
        }
        _syncTimer?.Dispose();
        host?.Dispose();
        _frameReady.Clear();
        _titleChanged.Clear();
        _exited.Clear();
        _bell.Clear();
        _clipboardWrite.Clear();
        _commandFinished.Clear();
        _closeRequested.Clear();
        OutputTap = null;
    }

    /// <summary>Waits (tests) until the worker thread ended after <see cref="Dispose"/>.</summary>
    internal bool JoinWorker(TimeSpan timeout) => _worker.Join(timeout);

    public override string ToString() => $"{Id}: {Title}";
}

/// <summary>A finished command for the UI: the command (line, exit code, rows) and the end of its output (at most <see cref="MaxOutputTail"/> characters).</summary>
public sealed record TerminalCommandSummary(TerminalCommand Command, string OutputTail)
{
    public const int MaxOutputTail = 4096;
}
