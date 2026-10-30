using System.Runtime.CompilerServices;
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
/// kill and the events. Terminals live in VS Code's <see cref="TerminalGroup"/>s (one entry of the terminal list each, several panes when
/// split); <see cref="Terminals"/> lists them in group order. The UI is a subscriber: <see cref="PresentationRequested"/> asks the Terminal
/// window to show (it also goes through <see cref="IViewActivator"/> when one is registered). Disposing the service kills every terminal's
/// process group. Terminals are not restored after a restart (docs/TERMINAL.md).
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
    private readonly ConditionalWeakTable<TerminalSession, BackendLauncher> _backends = new();
    private readonly Emitter<ITerminal> _created = new();
    private readonly Emitter<TerminalExitedEventArgs> _exited = new();
    private readonly Emitter<ITerminal> _removed = new();
    private readonly Emitter<ITerminal> _titleChanged = new();
    private readonly Emitter<ITerminal?> _activeChanged = new();
    private readonly Emitter<TerminalPresentation> _presentation = new();
    private readonly Emitter<object?> _groupsChanged = new();
    private TerminalSession[] _terminals = [];
    private TerminalGroup[] _groups = [];
    private TerminalSession? _active;
    private int _nextId;
    private int _nextGroupId;
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

    /// <summary>
    /// Where terminals run when not on this machine (the Remote module's provider: a remote workspace's terminals run on its server). Asked for every
    /// new terminal; null, or a null answer, means a local PTY. Set by the container (resolved lazily, on the first terminal).
    /// </summary>
    public Func<ITerminalBackendProvider?>? Backends { get; set; }

    /// <summary>Theme colours for OSC 10/11/12/4 answers (set by the panel from the colour theme; any thread).</summary>
    public ITerminalPalette Palette { get; set; } = DefaultTerminalPalette.Instance;

    /// <summary>Default grid until a view measures itself.</summary>
    public (int Columns, int Rows) DefaultSize { get; set; } = (80, 24);

    /// <summary>Every terminal, in group order (a split pane right after the one it was split from).</summary>
    public IReadOnlyList<ITerminal> Terminals => Volatile.Read(ref _terminals);
    public IReadOnlyList<TerminalSession> Sessions => Volatile.Read(ref _terminals);
    public ITerminal? ActiveTerminal => Volatile.Read(ref _active);
    public TerminalSession? ActiveSession => Volatile.Read(ref _active);

    /// <summary>The terminal list's entries (VS Code's groups), in order.</summary>
    public IReadOnlyList<TerminalGroup> Groups => Volatile.Read(ref _groups);

    /// <summary>The group of the active terminal.</summary>
    public TerminalGroup? ActiveGroup => ActiveSession is { } a ? GroupOf(a) : null;

    public TerminalGroup? GroupOf(ITerminal terminal)
    {
        if (terminal is not TerminalSession session) return null;
        foreach (var g in Groups)
            if (g.Contains(session)) return g;
        return null;
    }

    /// <summary>Groups were added, removed, split or joined (fires on any thread; <see cref="Groups"/> is current).</summary>
    public IEventSource<object?> GroupsChanged => _groupsChanged;

    public IEventSource<ITerminal> Created => _created;
    public IEventSource<TerminalExitedEventArgs> Exited => _exited;
    public IEventSource<ITerminal> Removed => _removed;
    public IEventSource<ITerminal> TitleChanged => _titleChanged;
    public IEventSource<ITerminal?> ActiveChanged => _activeChanged;
    /// <summary>The Terminal window should come forward (fires on any thread).</summary>
    public IEventSource<TerminalPresentation> PresentationRequested => _presentation;

    public async ValueTask<ITerminal> CreateAsync(TerminalCreateOptions? options = null, CancellationToken ct = default) =>
        await CreateSessionAsync(options, null, null, ct).ConfigureAwait(false);

    /// <summary>
    /// VS Code's Split Terminal: a new terminal in <paramref name="parent"/>'s group, right of it, made the active one. Without
    /// <paramref name="options"/> it runs the parent's profile (a split of a task's or debuggee's terminal runs the default profile) in the
    /// directory <c>terminal.integrated.splitCwd</c> names (default "inherited": the parent shell's current directory).
    /// </summary>
    public async ValueTask<TerminalSession> SplitAsync(TerminalSession parent, TerminalCreateOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        options ??= await Task.Run(() => SplitOptions(parent), ct).ConfigureAwait(false);
        return await CreateSessionAsync(options, parent, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates (or, with <see cref="TerminalCreateOptions.ReuseWhenIdle"/>, restarts) a terminal; with <paramref name="parent"/> as a split pane
    /// of the parent's group. <paramref name="beforeStart"/> runs on the new (or reused) session before its program is started (an output tap
    /// must not miss the program's first bytes).
    /// </summary>
    internal async ValueTask<TerminalSession> CreateSessionAsync(TerminalCreateOptions? options, TerminalSession? parent, Action<TerminalSession>? beforeStart,
        CancellationToken ct = default)
    {
        options ??= new TerminalCreateOptions();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Resolution reads settings and the file system and spawning takes a fork/exec: never on the caller's (UI) thread.
        var session = await Task.Run(() => CreateCore(options, parent, beforeStart), ct).ConfigureAwait(false);
        if (options.Show) await ShowAsync(session, options.PreserveFocus, ct).ConfigureAwait(false);
        else if (ActiveTerminal is null || (parent is not null && ReferenceEquals(ActiveSession, parent))) SetActive(session);
        return session;
    }

    private TerminalSession CreateCore(TerminalCreateOptions options, TerminalSession? parent, Action<TerminalSession>? beforeStart)
    {
        var (columns, rows) = options.InitialSize ?? DefaultSize;
        if (options.ReuseWhenIdle && options.IsTransient && options.Name is { } name && FindIdle(name, options) is { } idle)
        {
            if (Resolve(options, idle.Emulator.Columns, idle.Emulator.Rows, idle) is { } again)
            {
                beforeStart?.Invoke(idle);
                idle.Restart(again);
                Track(idle);
            }
            return idle;
        }
        int id;
        lock (_gate) id = ++_nextId;
        var backend = Backends?.Invoke() is { } provider && provider.TryCreate(options) is { } first ? new BackendLauncher(provider, options, first) : null;
        var session = new TerminalSession(id, options, backend is null ? _hostFactory : backend.CreateHost, EmulatorOptions(), columns, rows);
        if (backend is not null) _backends.Add(session, backend);
        var subs = new DisposableStore();
        subs.Add(session.TitleChanged.Subscribe(s => _titleChanged.Fire(s)));
        subs.Add(session.Exited.Subscribe(e => _exited.Fire(e)));
        subs.Add(session.CloseRequested.Subscribe(s => _ = KillAsync(s)));
        lock (_gate)
        {
            if (_disposed) { session.Dispose(); throw new ObjectDisposedException(nameof(TerminalService)); }
            _perTerminal[session] = subs;
            var group = parent is null ? null : _groups.FirstOrDefault(g => g.Contains(parent));
            if (group is not null) group.Insert(group.IndexOf(parent!) + 1, session);
            else _groups = [.. _groups, new TerminalGroup(++_nextGroupId, session)];
            _terminals = Flatten();
        }
        _groupsChanged.Fire(null);
        beforeStart?.Invoke(session);
        if (Resolve(options, columns, rows, session) is { } info) session.Start(info);
        Track(session);
        _created.Fire(session);
        return session;
    }

    /// <summary>
    /// An exited transient terminal named <paramref name="name"/> that runs where the program of <paramref name="options"/> must run: an idle local
    /// terminal never runs a remote workspace's program, nor an idle remote one a local program.
    /// </summary>
    private TerminalSession? FindIdle(string name, TerminalCreateOptions options)
    {
        var idle = Sessions.Where(s => s.IsTransient && s.HasExited && s.Options.Name == name).ToList();
        if (idle.Count == 0) return null;
        var elsewhere = WouldRunElsewhere(options);
        return idle.FirstOrDefault(s => RunsElsewhere(s) == elsewhere);
    }

    private TerminalSession[] Flatten() => _groups.SelectMany(g => g.Terminals).ToArray();

    /// <summary>
    /// Runs another program in an exited terminal (its buffer stays), as <paramref name="options"/> describe it: a task terminal reused for
    /// the next task. Resolution and spawning run off the caller's thread; <paramref name="beforeStart"/> runs right before the spawn.
    /// </summary>
    /// <returns>False when the program cannot be found (the reason is written into the terminal, which stays exited).</returns>
    internal async ValueTask<bool> RestartAsync(TerminalSession session, TerminalCreateOptions options, Action<TerminalSession>? beforeStart, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await Task.Run(() =>
        {
            PtyStartInfo info;
            // A terminal that runs elsewhere (a remote workspace's) runs the next program there too, never on this machine; a local one never runs a
            // program that must run elsewhere (the caller picks a terminal of the right kind: this is the guarantee, not the choice).
            try
            {
                info = _backends.TryGetValue(session, out var backend)
                    ? backend.Start(session.Emulator.Columns, session.Emulator.Rows, options)
                    : WouldRunElsewhere(options)
                        ? throw new PtySpawnException("This terminal runs on this machine, and the program must run on the remote host.")
                        : Resolver.Resolve(options, session.Emulator.Columns, session.Emulator.Rows);
            }
            catch (Exception ex) when (ex is PtySpawnException or IOException or UnauthorizedAccessException)
            {
                session.WriteLocal($"\e[31m{ex.Message}\e[0m\r\n");
                return false;
            }
            beforeStart?.Invoke(session);
            session.Restart(info);
            Track(session);
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="session"/> runs its programs elsewhere (on a remote workspace's server), not on this machine.</summary>
    internal bool RunsElsewhere(TerminalSession session) => _backends.TryGetValue(session, out _);

    /// <summary>
    /// Whether a new terminal for <paramref name="options"/> would run elsewhere: the backend provider's answer (no I/O, but the provider is
    /// resolved on first use: call this off the UI thread). A program that must run elsewhere but cannot now (a server's task while its host is
    /// not connected: the provider refuses it) counts as elsewhere — it fails to start there, it never runs here.
    /// </summary>
    internal bool WouldRunElsewhere(TerminalCreateOptions options)
    {
        if (Backends?.Invoke() is not { } provider) return false;
        try
        {
            using var probe = provider.TryCreate(options);
            return probe is not null;
        }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>How a split of <paramref name="parent"/> starts (reads /proc and settings: off the UI thread).</summary>
    internal TerminalCreateOptions SplitOptions(TerminalSession parent)
    {
        var p = parent.Options;
        if (p.IsTransient) return new TerminalCreateOptions { Show = false };
        var mode = _config?.Get<string>(TerminalSettingKeys.SplitCwd) ?? "inherited";
        var cwd = mode switch
        {
            "workspaceRoot" => null, // the default directory
            "initial" => parent.StartDirectory,
            _ => parent.ProcessDirectory() ?? parent.CurrentDirectory,
        };
        return new TerminalCreateOptions { ProfileName = p.ProfileName, ShellPath = p.ShellPath, ShellArgs = p.ShellArgs, Env = p.Env, Cwd = cwd, Show = false };
    }

    /// <summary>VS Code's Unsplit Terminal: the terminal leaves its split group for a group of its own, right after it.</summary>
    public void Unsplit(TerminalSession session)
    {
        lock (_gate)
        {
            var index = Array.FindIndex(_groups, g => g.Contains(session));
            if (index < 0 || !_groups[index].IsSplit) return;
            _groups[index].Remove(session);
            var list = _groups.ToList();
            list.Insert(index + 1, new TerminalGroup(++_nextGroupId, session));
            _groups = list.ToArray();
            _terminals = Flatten();
        }
        _groupsChanged.Fire(null);
        _activeChanged.Fire(ActiveSession);
    }

    /// <summary>VS Code's Join Terminals: <paramref name="sessions"/> move into <paramref name="target"/>'s group as panes on its right.</summary>
    public void Join(TerminalSession target, IEnumerable<TerminalSession> sessions)
    {
        lock (_gate)
        {
            var group = _groups.FirstOrDefault(g => g.Contains(target));
            if (group is null) return;
            var changed = false;
            foreach (var s in sessions)
            {
                var from = _groups.FirstOrDefault(g => g.Contains(s));
                if (from is null || ReferenceEquals(from, group)) continue;
                from.Remove(s);
                group.Insert(group.Terminals.Count, s);
                changed = true;
            }
            if (!changed) return;
            _groups = _groups.Where(g => g.Terminals.Count > 0).ToArray();
            _terminals = Flatten();
        }
        _groupsChanged.Fire(null);
        _activeChanged.Fire(ActiveSession);
    }

    private PtyStartInfo? Resolve(TerminalCreateOptions options, int columns, int rows, TerminalSession session)
    {
        try { return _backends.TryGetValue(session, out var backend) ? backend.Start(columns, rows, options) : Resolver.Resolve(options, columns, rows); }
        catch (Exception ex) when (ex is PtySpawnException or IOException or UnauthorizedAccessException)
        {
            // Shown in the terminal itself: a program that cannot be found makes a terminal that exited at once, with the reason.
            session.Fail(ex.Message);
            return null;
        }
    }

    private void Track(TerminalSession session)
    {
        if (_memory is null || session.ProcessId is not { } pid || _backends.TryGetValue(session, out _)) return; // a remote pid is not ours to account
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

    /// <summary>Makes <paramref name="terminal"/> the active one, which also makes its group the shown one and it that group's focused pane.</summary>
    public void SetActive(ITerminal terminal)
    {
        if (terminal is not TerminalSession session) throw new ArgumentException("Not a terminal of this service.", nameof(terminal));
        lock (_gate)
        {
            if (ReferenceEquals(_active, session) || !_terminals.Contains(session)) return;
            _active = session;
            _groups.FirstOrDefault(g => g.Contains(session))?.SetActive(session);
        }
        _activeChanged.Fire(session);
    }

    public async ValueTask SendTextAsync(string text, bool addNewLine = true, CancellationToken ct = default)
    {
        var target = ActiveTerminal is { HasExited: false } active ? active : await CreateAsync(new TerminalCreateOptions(), ct).ConfigureAwait(false);
        await target.SendTextAsync(text, addNewLine, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kills the terminal's process and closes its pane. In a split group the pane's neighbour (left, else right) becomes the active terminal;
    /// the last pane of a group closes the group and the neighbouring group (the previous one, else the next) is shown, as in VS Code.
    /// </summary>
    public ValueTask KillAsync(ITerminal terminal, CancellationToken ct = default)
    {
        if (terminal is not TerminalSession session) return ValueTask.CompletedTask;
        TerminalSession? nextActive = null;
        var activeMoved = false;
        bool removed;
        DisposableStore? subs;
        lock (_gate)
        {
            var groupIndex = Array.FindIndex(_groups, g => g.Contains(session));
            removed = groupIndex >= 0;
            if (removed)
            {
                var group = _groups[groupIndex];
                group.Remove(session);
                if (group.Terminals.Count == 0) _groups = _groups.Where(g => !ReferenceEquals(g, group)).ToArray();
                _terminals = Flatten();
                if (ReferenceEquals(_active, session))
                {
                    _active = group.ActiveTerminal
                        ?? (_groups.Length == 0 ? null : _groups[Math.Clamp(groupIndex - 1, 0, _groups.Length - 1)].ActiveTerminal);
                    nextActive = _active;
                    activeMoved = true;
                }
            }
            _perTerminal.Remove(session, out subs);
        }
        subs?.Dispose();
        session.Dispose(); // kills the process group (SIGHUP, SIGKILL after the grace period) and reaps it
        if (removed)
        {
            _groupsChanged.Fire(null);
            _removed.Fire(session);
            if (activeMoved) _activeChanged.Fire(nextActive);
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
            _groups = [];
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
        _groupsChanged.Clear();
    }
}
