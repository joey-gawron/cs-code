using Avalonia;
using CsCode.App.Commands;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Menus;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Services;
using CsCode.App.Startup;
using CsCode.App.ToolWindows.FindResults;
using CsCode.App.Views;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App;

/// <summary>
/// Wires the shell together once the window exists: built-in themes and the theme applier, the palette, every shell command, the keyboard pipeline,
/// context keys, menus, status items, tool-window auto-show, layout persistence and the deferred (after first frame) startup work.
/// Holds no UI; the window asks it for the objects it renders. Disposing it tears down every subscription it created.
/// </summary>
public sealed class ShellController : IDisposable
{
    public const string UiStallSetting = "cscode.diagnostics.uiStallLogMs";

    /// <summary>
    /// The tool windows of the default (and reset) layout, like Visual Studio's: Explorer on the right, Output and Error List at the bottom. Every
    /// other tool window starts hidden (Find Results, Find in Files, Modules...) unless its <see cref="IToolWindow.AutoShowWhen"/> holds, so a
    /// module that contributes a window never changes the default layout by accident.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultLayoutTools = ["explorer", "output", "errorlist"];

    private readonly IServiceProvider _sp;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _disposables = new();
    private readonly TimeProvider _time;
    private ITimer? _saveTimer;
    private UiStallWatchdog? _watchdog;
    private Func<UiStallWatchdog>? _startWatchdog;
    private readonly HashSet<string> _autoShown = new(StringComparer.Ordinal);
    private bool _started, _restoring, _disposed;

    public ShellController(IServiceProvider sp, IUiDispatcher ui)
    {
        _sp = sp;
        _ui = ui;
        _time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    public ThemeApplier? Theme { get; private set; }
    public PaletteController Palette { get; private set; } = null!;
    /// <summary>Saved sessions (Welcome page, Quick Launch's <c>session:</c> mode).</summary>
    public SessionCatalog Sessions { get; private set; } = null!;
    public KeyboardPipeline Keyboard { get; private set; } = null!;
    public ContextKeyTracker ContextKeys { get; private set; } = null!;
    public MenuModel Menus { get; private set; } = null!;
    public LayoutPersistence Layout { get; private set; } = null!;
    public ShellStatusItems StatusItems { get; private set; } = null!;
    /// <summary>The Vim adapter (null when the composition has no Vim module or no real editor): sees keys before the key bindings.</summary>
    public Vim.VimIntegration? Vim { get; private set; }
    public IShellKeyInterceptor? KeyInterceptor => Vim;
    /// <summary>General macros (null when the composition has no macro module or no real editor): the keyboard pipeline's tap.</summary>
    public Macros.MacroIntegration? Macros { get; private set; }
    public ClipboardRouter Clipboard { get; private set; } = null!;
    public ShellContextMenus ContextMenus { get; private set; } = null!;
    public ShellDockFactory Factory => _sp.GetRequiredService<ShellDockFactory>();
    public DocumentWell Well => _sp.GetRequiredService<DocumentWell>();
    public ToolWindowManager Tools => _sp.GetRequiredService<ToolWindowManager>();

    /// <summary>
    /// Makes the editor's theme service hand out complete themes: every theme (the 21 embedded ones, user theme files) is completed with the VS 2022
    /// chrome mapping (<see cref="ThemeCompletion"/>), so the shell, the editor and the theme dropdown all see the same colours. Idempotent and safe on
    /// any thread: <see cref="AppStartup"/> calls it on the start-up pool thread (the current theme is completed there, JIT included), and
    /// <see cref="Initialize"/> again for compositions built on the UI thread (tests). Other <see cref="IColorThemeService"/> implementations are
    /// completed by <see cref="ThemeApplier"/> when they are applied.
    /// </summary>
    public static void PrepareThemes(IColorThemeService themes)
    {
        if (themes is not CsCode.Editor.Theming.ColorThemeService service || service.HasCompletion) return;
        service.SetCompletion(ThemeCompletion.Complete);
        _ = service.Current;
    }

    /// <summary>Synchronous wiring that must exist before the first frame (themes, commands, keys, menus). Cheap: no I/O.</summary>
    public void Initialize(IWindowChrome chrome)
    {
        _ui.AssertUiThread();
        var themes = _sp.GetRequiredService<IColorThemeService>();
        var config = _sp.GetService<IConfigurationService>();
        PrepareThemes(themes);
        VsThemes.RegisterAll(themes, null);
        if (Application.Current is { } app) Theme = new ThemeApplier(app, themes, _ui, config);
        _disposables.Add(Disposable.Create(() => Theme?.Dispose()));
        // The dropdown lists every theme: complete them all now, on a pool thread, rather than on the UI thread when the list first opens.
        _ = Task.Run(() => themes.Available.Count);

        var commands = _sp.GetRequiredService<ICommandService>();
        var keys = _sp.GetRequiredService<IContextKeyService>();
        var window = _sp.GetRequiredService<IWindowService>();
        var quick = _sp.GetRequiredService<QuickInputViewModel>();
        // Command ownership is decided by registration order (the command table is a stack: the newest registration runs), so the order of
        // the next three lines is part of the contract (docs/SHELL.md, "Command ownership"): the editor factory registers its standalone
        // fallbacks first, Dap then owns the debug commands, the panels (built by enumerating the tool windows) register theirs, and only then
        // does the shell register its own commands, skipping ids a panel already owns.
        var well = Well;
        var debug = _sp.GetService<IDebugService>();
        var specs = _sp.GetServices<IToolWindow>().ToArray();
        var nav = _sp.GetRequiredService<NavigationService>();
        var workspace = _sp.GetService<IWorkspaceService>();
        var state = _sp.GetService<IStateStorage>();

        // Saved sessions for the Welcome page and Quick Launch's session: mode (opened through File > Sessions, which session processes register).
        Sessions = new SessionCatalog(commands, _sp.GetService<IAppPaths>()?.UserDataDir, () => (chrome as MainWindow)?.SessionName,
            App.Background is { } background ? list => background.Lane.RunAsync(list) : null);
        Palette = new PaletteController(quick, new PaletteContext(commands, nav, well, keys, _sp.GetService<IKeybindingService>(), workspace, state,
            _sp.GetService<ILanguageFeatureRegistry>(), _sp.GetService<IMemoryMonitor>(), Sessions), _ui, window);
        _disposables.Add(Palette);

        ContextKeys = new ContextKeyTracker(keys, well, _ui, quick, workspace, debug);
        _disposables.Add(ContextKeys);

        if (_sp.GetService<ActiveSelectionProvider>() is { } selection) _disposables.Add(selection.Track(well));

        Layout = new LayoutPersistence(Factory, Tools, well, _ui, state, Tools.Tools.Select(t => t.Spec.Id).Except(DefaultLayoutTools));

        _disposables.Add(ShellCommands.Register(new ShellCommandContext(
            commands, well, Tools, Palette, window, _sp.GetRequiredService<IModalDialogs>(), nav, workspace, config, themes, _sp.GetService<IKeybindingService>(),
            _sp.GetRequiredService<FindResultsViewModel>(), _sp.GetRequiredService<NotificationCenter>(), chrome,
            ResetLayoutAsync, RefreshExplorerAsync, specs,
            () => new WelcomeView(commands, workspace, state, Sessions), () => new KeyboardReferenceView(_sp.GetService<IKeybindingService>(), commands),
            _sp.GetService<ILanguageService>(), _sp.GetService<ShellViewActivator>(), _ui)));

        // Remote editing: Connect to Host / Open Remote Folder / disconnect... commands and the remote indicator in the status bar.
        if (_sp.GetService<CsCode.Remote.Shell.RemoteShellContributions>() is { } remote) _disposables.Add(remote.Start());

        Keyboard = new KeyboardPipeline(_sp.GetRequiredService<IKeybindingService>(), commands, _ui, _sp.GetRequiredService<IStatusBarService>(), window, context: keys);
        _disposables.Add(Keyboard);

        // Vim (docs/VIM.md, "Integration"): one engine per editor view; keys reach it before the key bindings (MainWindow).
        Vim = global::CsCode.App.Vim.VimIntegration.TryCreate(_sp);
        if (Vim is { } vim)
        {
            vim.Start(() => Keyboard.IsChordPending);
            _disposables.Add(vim);
        }

        // General macros (docs/MACROS.md): record and replay through the keyboard pipeline's tap and the editor views.
        Macros = global::CsCode.App.Macros.MacroIntegration.TryCreate(_sp);
        if (Macros is { } macros)
        {
            macros.Start(Keyboard, Vim);
            _disposables.Add(macros);
        }

        Menus = new MenuModel(ShellMenus.Build(), commands, keys, _ui, _sp.GetService<IKeybindingService>(), workspace, () => specs, id => Tools.IsVisible(id));
        _disposables.Add(Menus);
        // Edit > Cut/Copy/Paste follow keyboard focus; right-click menus (editor, tabs, tool windows) are built from contributions.
        Clipboard = new ClipboardRouter(commands, keys, well, () => _sp.GetService<WindowHolder>()?.Window);
        _disposables.Add(Clipboard);
        Menus.Refreshing = Clipboard.UpdateKeys;
        ContextMenus = new ShellContextMenus(well, Tools, Factory, new Ui.Menus.MenuServices(commands, keys, _sp.GetService<IKeybindingService>(), _sp.GetService<Core.Contributions.IContributionRegistry>()),
            window, workspace);
        _disposables.Add(ContextMenus);
        _disposables.Add(Tools.VisibilityChanged.Subscribe(_ => Menus.ScheduleRefresh()));

        StatusItems = new ShellStatusItems(_sp.GetRequiredService<IStatusBarService>(), _ui, nav, _sp.GetRequiredService<NotificationCenter>(), config,
            _sp.GetService<ILanguageService>(), _sp.GetService<IDiagnosticService>(), debug, _sp.GetService<IMemoryMonitor>(), _sp.GetService<ILanguageServerManager>());
        _disposables.Add(StatusItems);

        // Tool windows that declare AutoShowWhen (debug panes) follow their context key.
        _disposables.Add(keys.Changed.Subscribe(_ => _ui.PostCoalesced("tools.autoshow", EvaluateAutoShow)));

        // The shell's own settings are declared so they show up in schema-driven settings UIs.
        if (config is not null)
            _disposables.Add(config.RegisterProperties(
            [
                new ConfigurationProperty(DocumentWell.LiveViewsSetting, System.Text.Json.JsonSerializer.SerializeToElement(DocumentWell.DefaultLiveViews), "How many editor views stay alive besides the active tab of each group; older ones are disposed and recreated on demand."),
                new ConfigurationProperty(NavigationService.ConfirmMultiFileSetting, System.Text.Json.JsonSerializer.SerializeToElement(true), "Ask for confirmation before applying a workspace edit (rename, refactor) that spans several files."),
                new ConfigurationProperty(UiStallSetting, System.Text.Json.JsonSerializer.SerializeToElement(0), "Log a diagnostic when the UI thread has not pumped for this many milliseconds (0 = off; Debug builds default to 100)."),
                .. ToolbarView.Groups.Select(g => new ConfigurationProperty(g.SettingKey, System.Text.Json.JsonSerializer.SerializeToElement(true),
                    $"Show the {g.Title} toolbar (View > Toolbars).")),
            ]));

        // UI stall watchdog: Debug builds, or whenever the setting is > 0.
        var stallMs = config?.Get(UiStallSetting, 0) ?? 0;
#if DEBUG
        if (stallMs <= 0) stallMs = 100;
#endif
        if (stallMs > 0 && _sp.GetService<IOutputService>() is { } outputService)
        {
            var channel = outputService.GetOrCreate("CsCode UI Diagnostics");
            _startWatchdog = () =>
            {
                var dog = new UiStallWatchdog(_ui, channel.AppendLine, stallMs);
                dog.Start();
                return dog;
            };
            _watchdog = _startWatchdog();
        }

        // Re-creatable memory held by the design system joins the memory-pressure policy.
        if (_sp.GetService<IMemoryMonitor>() is { } monitor) _disposables.Add(monitor.Register(new IconCacheTrimmable()));
        // The memory monitor (CsCode.Profiling, docs/MEMORY.md) starts with the window, hears the user's input for its idle trim, writes the Memory channel.
        _disposables.Add(ProfilingShell.Attach(_sp));

        // Persist the layout (debounced) whenever tabs or tool windows move.
        _disposables.Add(well.TabsChanged.Subscribe(_ => ScheduleSaveLayout()));
        _disposables.Add(Tools.VisibilityChanged.Subscribe(_ => ScheduleSaveLayout()));
        void OnMoved(object? s, EventArgs e) => ScheduleSaveLayout();
        var factory = Factory; // not resolved again at dispose: the container may be disposed by then
        factory.DockableMoved += OnMoved;
        factory.DockableDocked += OnMoved;
        factory.DockablePinned += OnMoved;
        factory.DockableUnpinned += OnMoved;
        // Floating windows: one opening or going away, or the user moving one by Dock's grip.
        factory.WindowOpened += OnMoved;
        factory.WindowRemoved += OnMoved;
        factory.WindowMoveDragEnd += OnMoved;
        _disposables.Add(Disposable.Create(() =>
        {
            factory.DockableMoved -= OnMoved;
            factory.DockableDocked -= OnMoved;
            factory.DockablePinned -= OnMoved;
            factory.DockableUnpinned -= OnMoved;
            factory.WindowOpened -= OnMoved;
            factory.WindowRemoved -= OnMoved;
            factory.WindowMoveDragEnd -= OnMoved;
        }));

        if (config is not null)
            _disposables.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(SettingKeys.WorkbenchColorTheme)) _ui.Post(() => _ = ApplyConfiguredThemeAsync());
            }));
    }

    /// <summary>
    /// Background mode: replaces the layout restore of <see cref="StartDeferredAsync"/> with the session's own (folders, layout, editors with view
    /// state, hot-exit backups). Set by the session controller before it calls <see cref="StartDeferredAsync"/> on a still hidden window.
    /// </summary>
    public Func<Task>? RestoreOverride { get; set; }

    /// <summary>Runs after the first frame: theme from settings, layout restore, command-line targets, the Welcome page.</summary>
    public async Task StartDeferredAsync()
    {
        if (_started) return;
        _started = true;
        await ApplyConfiguredThemeAsync().ConfigureAwait(true);
        _restoring = true;
        try
        {
            if (RestoreOverride is { } restore) await restore().ConfigureAwait(true);
            else if (Layout.Load() is { } snapshot) await Layout.ApplyAsync(snapshot).ConfigureAwait(true);
            else await ResetLayoutAsync().ConfigureAwait(true);
            Tools.HideInitiallyHidden(t => t.AutoShowWhen is not null && !_sp.GetRequiredService<IContextKeyService>().Evaluate(t.AutoShowWhen));
            await OpenCommandLineTargetsAsync().ConfigureAwait(true);
        }
        finally { _restoring = false; }

        var workspace = _sp.GetService<IWorkspaceService>();
        var startup = _sp.GetService<IConfigurationService>()?.Get(SettingKeys.WorkbenchStartupEditor, "welcomePage") ?? "welcomePage";
        if (Well.Tabs.Count == 0 && (workspace?.Folders.Count ?? 0) == 0 && startup != "none")
            await _sp.GetRequiredService<ICommandService>().ExecuteAsync(ShellMenus.Welcome).ConfigureAwait(true);
        EvaluateAutoShow();
    }

    private async Task ApplyConfiguredThemeAsync()
    {
        if (_disposed) return;
        var themes = _sp.GetRequiredService<IColorThemeService>();
        // workbench.colorTheme holds a label (VS Code's convention; ids are accepted too). The editor's service follows the setting itself; this
        // is for services that do not, and it never ends a picker's preview (the picker commits or reverts).
        var value = _sp.GetService<IConfigurationService>()?.Get<string>(SettingKeys.WorkbenchColorTheme);
        if (value is null || ThemeSelection.Names(themes.Current, value) || themes is IColorThemePreview { PreviewedThemeId: not null }) return;
        if (themes.Available.FirstOrDefault(t => ThemeSelection.Names(t, value)) is { } wanted) await themes.SetCurrentAsync(wanted.Id).ConfigureAwait(true);
    }

    private async Task OpenCommandLineTargetsAsync()
    {
        var options = _sp.GetRequiredService<CommandLineOptions>();
        var workspace = _sp.GetService<IWorkspaceService>();
        var nav = _sp.GetRequiredService<INavigationService>();
        foreach (var remote in options.RemoteTargets) // cscode ssh://[user@]host[:port]/path: connect, then open the folder
            _ = _sp.GetRequiredService<ICommandService>().ExecuteAsync(CsCode.Remote.Shell.RemoteShellContributions.OpenFolder, remote).AsTask();
        foreach (var t in options.Targets)
        {
            try
            {
                if (Directory.Exists(t.Path))
                {
                    if (workspace is not null) await workspace.OpenFolderAsync(new Uri(Path.TrimEndingDirectorySeparator(t.Path) + "/")).ConfigureAwait(true);
                }
                else
                {
                    TextRange? at = t.Line is { } l ? TextRange.At(Math.Max(0, l - 1), Math.Max(0, (t.Column ?? 1) - 1)) : null;
                    await nav.OpenAsync(new Uri(Path.GetFullPath(t.Path)), at).ConfigureAwait(true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
            {
                await _sp.GetRequiredService<IWindowService>().ShowMessageAsync(MessageSeverity.Error, $"Could not open {t.Path}: {ex.Message}").ConfigureAwait(true);
            }
        }
    }

    private void EvaluateAutoShow()
    {
        if (_disposed) return; // a coalesced post can outlive the window during shutdown
        var keys = _sp.GetRequiredService<IContextKeyService>();
        foreach (var tool in _sp.GetServices<IToolWindow>().Where(t => t.AutoShowWhen is not null))
        {
            var want = keys.Evaluate(tool.AutoShowWhen);
            if (want && !Tools.IsVisible(tool.Id)) { Tools.Show(tool.Id); _autoShown.Add(tool.Id); }
            else if (!want && _autoShown.Remove(tool.Id)) Tools.Hide(tool.Id);
        }
    }

    public async Task ResetLayoutAsync() => await Layout.ResetAsync().ConfigureAwait(true);

    private async Task RefreshExplorerAsync()
    {
        Menus.Refresh();
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------ layout persistence

    private void ScheduleSaveLayout()
    {
        if (_restoring) return;
        _saveTimer?.Dispose();
        _saveTimer = _time.CreateTimer(_ => _ui.Post(() => { if (!_disposed) Layout.Save(); }), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Saves the layout immediately (window closing).</summary>
    public void SaveLayoutNow()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
        Layout.Save();
    }

    /// <summary>
    /// Background mode: a hidden window must have no periodic work (≈0 % CPU), so the UI stall watchdog, which polls the dispatcher every 20 ms,
    /// stops while the window is hidden and starts again when it is shown.
    /// </summary>
    public void SetHidden(bool hidden)
    {
        _ui.AssertUiThread();
        if (hidden)
        {
            _watchdog?.Dispose();
            _watchdog = null;
        }
        else if (_watchdog is null && _startWatchdog is { } start && !_disposed) _watchdog = start();
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog?.Dispose();
        _watchdog = null;
        _saveTimer?.Dispose();
        _disposables.Dispose();
    }

    private sealed class IconCacheTrimmable : IMemoryTrimmable
    {
        public string Name => "shell.iconGeometries";
        public long EstimatedBytes => IconCatalog.Names.Count * 600L;
        public long Trim(MemoryTrimLevel level) => level >= MemoryTrimLevel.Moderate ? Dispatcher_Trim() : 0;
        private static long Dispatcher_Trim()
        {
            // Geometries must be released on the UI thread; they re-parse on demand.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IconCatalog.Trim());
            return 0;
        }
    }
}
