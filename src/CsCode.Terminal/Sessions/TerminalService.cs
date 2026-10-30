using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Pty;

namespace CsCode.Terminal.Sessions;

/// <summary>A request to bring the Terminal window forward (and focus a terminal unless <see cref="PreserveFocus"/>), handled by the panel.</summary>
public sealed record TerminalPresentation(TerminalSession? Terminal, bool PreserveFocus);

/// <summary>
/// <see cref="ITerminalService"/>: the list of terminals, the active one, creation (resolution and spawning run off the caller's thread),
/// kill and the events. The UI is a subscriber: <see cref="PresentationRequested"/> asks the Terminal window to show (it also goes through
/// <see cref="IViewActivator"/> when one is registered). Disposing the service kills every terminal's process group. Terminals are not
/// restored after a restart (docs/TERMINAL.md).
/// </summary>
public sealed class TerminalService : ITerminalService, IDisposable
{
    public const string ViewId = "terminal";

    private readonly object _gate = new();
    private readonly IConfigurationService? _config;
    private readonly IViewActivator? _views;
    private readonly IMemoryMonitor? _memory;
    private readonly Func<IPtyHost> _hostFactory;
    private readonly DisposableStore _subscriptions = new();
    private readonly Dictionary<TerminalSession, DisposableStore> _perTerminal = new();
    private readonly Emitter<ITerminal> _created = new();
    private readonly Emitter<TerminalExitedEventArgs> _exited = new();
    private readonly Emitter<ITerminal> _removed = new();
    private readonly Emitter<ITerminal> _titleChanged = new();
    private readonly Emitter<ITerminal?> _activeChanged = new();
    private readonly Emitter<TerminalPresentation> _presentation = new();
    private TerminalSession[] _terminals = [];
    private TerminalSession? _active;
    private int _nextId;
    private bool _disposed;

    public TerminalService(IConfigurationService? config = null, IWorkspaceService? workspace = null, IViewActivator? views = null,
        IMemoryMonitor? memory = null, Func<IPtyHost>? hostFactory = null, Func<IReadOnlyDictionary<string, string>>? environment = null)
    {
        _config = config;
        _views = views;
        _memory = memory;
        _hostFactory = hostFactory ?? PtyHosts.Create;
        Resolver = new TerminalLaunchResolver(config, workspace, environment);
        if (config is not null) _subscriptions.Add(config.RegisterProperties(TerminalSettingKeys.Properties));
    }

    public TerminalLaunchResolver Resolver { get; }

    /// <summary>Theme colours for OSC 10/11/12/4 answers (set by the panel from the colour theme; any thread).</summary>
    public ITerminalPalette Palette { get; set; } = DefaultTerminalPalette.Instance;

    /// <summary>Default grid until a view measures itself.</summary>
    public (int Columns, int Rows) DefaultSize { get; set; } = (80, 24);

    public IReadOnlyList<ITerminal> Terminals => Volatile.Read(ref _terminals);
    public IReadOnlyList<TerminalSession> Sessions => Volatile.Read(ref _terminals);
    public ITerminal? ActiveTerminal => Volatile.Read(ref _active);
    public TerminalSession? ActiveSession => Volatile.Read(ref _active);

    public IEventSource<ITerminal> Created => _created;
    public IEventSource<TerminalExitedEventArgs> Exited => _exited;
    public IEventSource<ITerminal> Removed => _removed;
    public IEventSource<ITerminal> TitleChanged => _titleChanged;
    public IEventSource<ITerminal?> ActiveChanged => _activeChanged;
    /// <summary>The Terminal window should come forward (fires on any thread).</summary>
    public IEventSource<TerminalPresentation> PresentationRequested => _presentation;

    public async ValueTask<ITerminal> CreateAsync(TerminalCreateOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TerminalCreateOptions();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Resolution reads settings and the file system and spawning takes a fork/exec: never on the caller's (UI) thread.
        var session = await Task.Run(() => CreateCore(options), ct).ConfigureAwait(false);
        if (options.Show) await ShowAsync(session, options.PreserveFocus, ct).ConfigureAwait(false);
        else if (ActiveTerminal is null) SetActive(session);
        return session;
    }

    private TerminalSession CreateCore(TerminalCreateOptions options)
    {
        var (columns, rows) = options.InitialSize ?? DefaultSize;
        if (options.ReuseWhenIdle && options.IsTransient && options.Name is { } name
            && Sessions.FirstOrDefault(s => s.IsTransient && s.HasExited && s.Options.Name == name) is { } idle)
        {
            if (Resolve(options, idle.Emulator.Columns, idle.Emulator.Rows, idle) is { } again)
            {
                idle.Restart(again);
                Track(idle);
            }
            return idle;
        }
        int id;
        lock (_gate) id = ++_nextId;
        var session = new TerminalSession(id, options, _hostFactory, EmulatorOptions(), columns, rows);
        var subs = new DisposableStore();
        subs.Add(session.TitleChanged.Subscribe(s => _titleChanged.Fire(s)));
        subs.Add(session.Exited.Subscribe(e => _exited.Fire(e)));
        lock (_gate)
        {
            if (_disposed) { session.Dispose(); throw new ObjectDisposedException(nameof(TerminalService)); }
            _perTerminal[session] = subs;
            _terminals = [.. _terminals, session];
        }
        if (Resolve(options, columns, rows, session) is { } info) session.Start(info);
        Track(session);
        _created.Fire(session);
        return session;
    }

    private PtyStartInfo? Resolve(TerminalCreateOptions options, int columns, int rows, TerminalSession session)
    {
        try { return Resolver.Resolve(options, columns, rows); }
        catch (Exception ex) when (ex is PtySpawnException or IOException or UnauthorizedAccessException)
        {
            // Shown in the terminal itself: a program that cannot be found makes a terminal that exited at once, with the reason.
            session.Fail(ex.Message);
            return null;
        }
    }

    private void Track(TerminalSession session)
    {
        if (_memory is null || session.ProcessId is not { } pid) return;
        var tracked = _memory.TrackChildProcess($"terminal {session.Id}", pid);
        lock (_gate)
        {
            if (_perTerminal.TryGetValue(session, out var subs)) subs.Add(tracked);
            else tracked.Dispose();
        }
    }

    internal EmulatorOptions EmulatorOptions() => new()
    {
        ScrollbackLines = Math.Clamp(_config?.Get(TerminalSettingKeys.Scrollback, 1000) ?? 1000, 0, 1_000_000),
        AllowOsc52 = _config?.Get(TerminalSettingKeys.AllowOsc52, false) ?? false,
        Reflow = _config?.Get(TerminalSettingKeys.Reflow, true) ?? true,
        Palette = Palette,
    };

    public async ValueTask ShowAsync(ITerminal? terminal = null, bool preserveFocus = false, CancellationToken ct = default)
    {
        if (terminal is not null) SetActive(terminal);
        _presentation.Fire(new TerminalPresentation(terminal as TerminalSession ?? ActiveSession, preserveFocus));
        if (_views is not null)
        {
            try { await _views.ShowViewAsync(ViewId, ct).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* no window yet (tests, start-up) */ }
        }
    }

    public void SetActive(ITerminal terminal)
    {
        if (terminal is not TerminalSession session) throw new ArgumentException("Not a terminal of this service.", nameof(terminal));
        lock (_gate)
        {
            if (ReferenceEquals(_active, session) || !_terminals.Contains(session)) return;
            _active = session;
        }
        _activeChanged.Fire(session);
    }

    public async ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default)
    {
        var target = ActiveTerminal is { HasExited: false } active ? active : await CreateAsync(new TerminalCreateOptions(), ct).ConfigureAwait(false);
        await target.SendTextAsync(text, addNewLine, ct).ConfigureAwait(false);
    }

    public ValueTask KillAsync(ITerminal terminal, CancellationToken ct = default)
    {
        if (terminal is not TerminalSession session) return ValueTask.CompletedTask;
        TerminalSession? nextActive = null;
        bool removed;
        DisposableStore? subs;
        lock (_gate)
        {
            var index = Array.IndexOf(_terminals, session);
            removed = index >= 0;
            if (removed)
            {
                _terminals = _terminals.Where(t => !ReferenceEquals(t, session)).ToArray();
                if (ReferenceEquals(_active, session))
                {
                    // VS Code activates the neighbour (the previous one, else the next).
                    _active = _terminals.Length == 0 ? null : _terminals[Math.Clamp(index - 1, 0, _terminals.Length - 1)];
                    nextActive = _active;
                }
            }
            _perTerminal.Remove(session, out subs);
        }
        subs?.Dispose();
        session.Dispose(); // kills the process group (SIGHUP, SIGKILL after the grace period) and reaps it
        if (removed)
        {
            _removed.Fire(session);
            if (nextActive is not null || ActiveTerminal is null) _activeChanged.Fire(nextActive);
        }
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        TerminalSession[] all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _terminals;
            _terminals = [];
            _active = null;
        }
        foreach (var s in all) s.Dispose();
        foreach (var subs in _perTerminal.Values) subs.Dispose();
        _perTerminal.Clear();
        _subscriptions.Dispose();
        _created.Clear();
        _exited.Clear();
        _removed.Clear();
        _titleChanged.Clear();
        _activeChanged.Clear();
        _presentation.Clear();
    }
}
