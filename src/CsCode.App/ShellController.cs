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
    private readonly IServiceProvider _sp;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _disposables = new();
    private readonly TimeProvider _time;
    private ITimer? _saveTimer;
    private readonly HashSet<string> _autoShown = new(StringComparer.Ordinal);
    private bool _started, _restoring;

    public ShellController(IServiceProvider sp, IUiDispatcher ui)
    {
        _sp = sp;
        _ui = ui;
        _time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    public ThemeApplier? Theme { get; private set; }
    public PaletteController Palette { get; private set; } = null!;
    public KeyboardPipeline Keyboard { get; private set; } = null!;
    public ContextKeyTracker ContextKeys { get; private set; } = null!;
    public MenuModel Menus { get; private set; } = null!;
    public LayoutPersistence Layout { get; private set; } = null!;
    public ShellStatusItems StatusItems { get; private set; } = null!;
    public ShellDockFactory Factory => _sp.GetRequiredService<ShellDockFactory>();
    public DocumentWell Well => _sp.GetRequiredService<DocumentWell>();
    public ToolWindowManager Tools => _sp.GetRequiredService<ToolWindowManager>();

    /// <summary>Synchronous wiring that must exist before the first frame (themes, commands, keys, menus). Cheap: no I/O.</summary>
    public void Initialize(IWindowChrome chrome)
    {
        _ui.AssertUiThread();
        var themes = _sp.GetRequiredService<IColorThemeService>();
        var config = _sp.GetService<IConfigurationService>();
        VsThemes.RegisterAll(themes, null);
        if (Application.Current is { } app) Theme = new ThemeApplier(app, themes, _ui, config);
        _disposables.Add(Disposable.Create(() => Theme?.Dispose()));

        var commands = _sp.GetRequiredService<ICommandService>();
        var keys = _sp.GetRequiredService<IContextKeyService>();
        var window = _sp.GetRequiredService<IWindowService>();
        var quick = _sp.GetRequiredService<QuickInputViewModel>();
        var well = Well;
        var nav = _sp.GetRequiredService<NavigationService>();
        var workspace = _sp.GetService<IWorkspaceService>();
        var state = _sp.GetService<IStateStorage>();
        var debug = _sp.GetService<IDebugService>();

        Palette = new PaletteController(quick, new PaletteContext(commands, nav, well, keys, _sp.GetService<IKeybindingService>(), workspace, state,
            _sp.GetService<ILanguageFeatureRegistry>(), _sp.GetService<IMemoryMonitor>()), _ui, window);
        _disposables.Add(Palette);

        ContextKeys = new ContextKeyTracker(keys, well, _ui, quick, workspace, debug);
        _disposables.Add(ContextKeys);

        Layout = new LayoutPersistence(Factory, Tools, well, _ui, state, ["findresults"]);

        var specs = _sp.GetServices<IToolWindow>().ToArray();
        _disposables.Add(ShellCommands.Register(new ShellCommandContext(
            commands, well, Tools, Palette, window, _sp.GetRequiredService<IModalDialogs>(), nav, workspace, config, themes, _sp.GetService<IKeybindingService>(),
            _sp.GetRequiredService<FindResultsViewModel>(), _sp.GetRequiredService<NotificationCenter>(), chrome,
            ResetLayoutAsync, RefreshExplorerAsync, specs,
            () => new WelcomeView(commands, workspace, state), () => new KeyboardReferenceView(_sp.GetService<IKeybindingService>(), commands))));

        Keyboard = new KeyboardPipeline(_sp.GetRequiredService<IKeybindingService>(), commands, _ui, _sp.GetRequiredService<IStatusBarService>(), window);
        _disposables.Add(Keyboard);

        Menus = new MenuModel(ShellMenus.Build(), commands, keys, _ui, _sp.GetService<IKeybindingService>(), workspace, () => specs, id => Tools.IsVisible(id));
        _disposables.Add(Menus);
        _disposables.Add(Tools.VisibilityChanged.Subscribe(_ => Menus.ScheduleRefresh()));

        StatusItems = new ShellStatusItems(_sp.GetRequiredService<IStatusBarService>(), _ui, nav, _sp.GetRequiredService<NotificationCenter>(), config,
            _sp.GetService<ILanguageService>(), _sp.GetService<IDiagnosticService>(), debug, _sp.GetService<IMemoryMonitor>(), _sp.GetService<ILanguageServerManager>());
        _disposables.Add(StatusItems);

        // Tool windows that declare AutoShowWhen (debug panes) follow their context key.
        _disposables.Add(keys.Changed.Subscribe(_ => _ui.PostCoalesced("tools.autoshow", EvaluateAutoShow)));

        // Re-creatable memory held by the design system joins the memory-pressure policy.
        if (_sp.GetService<IMemoryMonitor>() is { } monitor) _disposables.Add(monitor.Register(new IconCacheTrimmable()));

        // Persist the layout (debounced) whenever tabs or tool windows move.
        _disposables.Add(well.TabsChanged.Subscribe(_ => ScheduleSaveLayout()));
        _disposables.Add(Tools.VisibilityChanged.Subscribe(_ => ScheduleSaveLayout()));
        void OnMoved(object? s, EventArgs e) => ScheduleSaveLayout();
        Factory.DockableMoved += OnMoved;
        Factory.DockableDocked += OnMoved;
        Factory.DockablePinned += OnMoved;
        Factory.DockableUnpinned += OnMoved;
        _disposables.Add(Disposable.Create(() =>
        {
            Factory.DockableMoved -= OnMoved;
            Factory.DockableDocked -= OnMoved;
            Factory.DockablePinned -= OnMoved;
            Factory.DockableUnpinned -= OnMoved;
        }));

        if (config is not null)
            _disposables.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(SettingKeys.WorkbenchColorTheme)) _ui.Post(() => _ = ApplyConfiguredThemeAsync());
            }));
    }

    /// <summary>Runs after the first frame: theme from settings, layout restore, command-line targets, the Welcome page.</summary>
    public async Task StartDeferredAsync()
    {
        if (_started) return;
        _started = true;
        await ApplyConfiguredThemeAsync().ConfigureAwait(true);
        _restoring = true;
        try
        {
            var snapshot = Layout.Load();
            if (snapshot is not null) await Layout.ApplyAsync(snapshot).ConfigureAwait(true);
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
        var themes = _sp.GetRequiredService<IColorThemeService>();
        var id = _sp.GetService<IConfigurationService>()?.Get<string>(SettingKeys.WorkbenchColorTheme);
        if (id is not null && themes.Current.Id != id && themes.Available.Any(t => t.Id == id)) await themes.SetCurrentAsync(id).ConfigureAwait(true);
    }

    private async Task OpenCommandLineTargetsAsync()
    {
        var options = _sp.GetRequiredService<CommandLineOptions>();
        var workspace = _sp.GetService<IWorkspaceService>();
        var nav = _sp.GetRequiredService<INavigationService>();
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
        _saveTimer = _time.CreateTimer(_ => _ui.Post(() => Layout.Save()), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Saves the layout immediately (window closing).</summary>
    public void SaveLayoutNow()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
        Layout.Save();
    }

    public void Dispose()
    {
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
