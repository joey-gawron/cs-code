using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Ui.Threading;

namespace CsCode.Ui.Theming;

/// <summary>
/// Publishes the current <see cref="ColorTheme"/> as application resources: one <see cref="SolidColorBrush"/> per workbench colour id
/// under <c>vsc:&lt;id&gt;</c>, the <c>ui:*</c> tokens, and the Dock.Avalonia brush keys. Brushes are created once and their
/// <see cref="SolidColorBrush.Color"/> is mutated in place, so a theme switch repaints every open window without recreating
/// anything (and without churning allocations). Preparation (fallback-chain resolution of ~250 ids) runs off the UI thread; the UI
/// thread only applies a small immutable array of colours.
/// </summary>
public sealed class ThemeApplier : IDisposable
{
    // Dock.Avalonia brush resource key -> workbench colour id it follows.
    private static readonly (string ResourceKey, string ColorId)[] DockBridge =
    [
        ("DockApplicationAccentForegroundBrush", "button.foreground"),
        ("DockBorderStrongBrush", "panel.border"), ("DockBorderSubtleBrush", "panel.border"), ("DockSeparatorBrush", "panel.border"),
        ("DockChromeButtonDangerHoverBrush", "inputValidation.errorBorder"), ("DockChromeButtonForegroundBrush", "icon.foreground"),
        ("DockChromeButtonHoverBackgroundBrush", "toolbar.hoverBackground"), ("DockChromeButtonPressedBackgroundBrush", "toolbar.activeBackground"),
        ("DockDocumentContentBorderBrush", "editorGroup.border"),
        ("DockDocumentTabClosePointerOverForegroundBrush", "tab.activeForeground"), ("DockDocumentTabCloseSelectedForegroundBrush", "tab.activeForeground"),
        ("DockDocumentTabPointerOverForegroundBrush", "tab.hoverForeground"), ("DockDocumentTabSelectedForegroundBrush", "tab.activeForeground"),
        ("DockDocumentTabStripBackgroundBrush", "editorGroupHeader.tabsBackground"), ("DockDocumentTabStripMaskBrush", "editorGroupHeader.tabsBackground"),
        ("DockSelectorOverlayBackdropBrush", "widget.shadow"),
        ("DockSplitterDragBrush", "focusBorder"), ("DockSplitterHoverBrush", "sash.hoverBorder"), ("DockSplitterIdleBrush", "editorGroupHeader.tabsBackground"),
        ("DockSurfaceEditorBrush", "editor.background"), ("DockSurfaceHeaderActiveBrush", "tab.activeBackground"), ("DockSurfaceHeaderBrush", "sideBarSectionHeader.background"),
        ("DockSurfacePanelBrush", "panel.background"), ("DockSurfaceSidebarBrush", "sideBar.background"), ("DockSurfaceWorkbenchBrush", "editorGroupHeader.tabsBackground"),
        ("DockTabActiveBackgroundBrush", "tab.activeBackground"), ("DockTabActiveForegroundBrush", "tab.activeForeground"),
        ("DockTabBackgroundBrush", "tab.inactiveBackground"), ("DockTabCloseHoverBackgroundBrush", "toolbar.hoverBackground"), ("DockTabForegroundBrush", "tab.inactiveForeground"),
        ("DockTabHoverBackgroundBrush", "tab.hoverBackground"), ("DockTabSelectedForegroundBrush", "tab.activeForeground"), ("DockTargetIndicatorBrush", "focusBorder"),
        ("DockThemeAccentBrush", "focusBorder"), ("DockThemeBackgroundBrush", "sideBar.background"), ("DockThemeBorderLowBrush", "panel.border"),
        ("DockThemeControlBackgroundBrush", "input.background"), ("DockThemeForegroundBrush", "foreground"), ("DockToolChromeIconBrush", "icon.foreground"),
        ("DockWindowChromeBackgroundBrush", "titleBar.activeBackground"), ("DockWindowChromeBorderBrush", "panel.border"),
        ("DockWindowChromeForegroundBrush", "titleBar.activeForeground"), ("DockWindowChromeTitleBarBackgroundBrush", "titleBar.activeBackground"),
        ("DockApplicationAccentBrushHigh", "statusBar.background"), ("DockApplicationAccentBrushMed", "toolbar.hoverBackground"),
        ("DockApplicationAccentBrushLow", "list.hoverBackground"),
    ];

    private readonly Application _app;
    private readonly IColorThemeService _themes;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private int _version;
    private string? _appliedThemeId;

    public ThemeApplier(Application app, IColorThemeService themes, IUiDispatcher ui, IConfigurationService? config = null)
    {
        _app = app;
        _themes = themes;
        _ui = ui;
        SetTokens(config);
        // Subscribed BEFORE the initial apply so a switch racing with construction is never lost.
        _subs.Add(themes.Changed.Subscribe(OnThemeChanged));
        if (config is not null)
            _subs.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(SettingKeys.EditorFontFamily)) _ui.Post(() => SetCodeFont(config));
            }));
        ApplyNow(themes.Current);
    }

    /// <summary>Id of the theme currently applied to the resources (null before the first apply).</summary>
    public string? AppliedThemeId => _appliedThemeId;

    /// <summary>Number of theme applications performed (tests use this to prove coalescing under rapid switching).</summary>
    public int ApplyCount { get; private set; }

    /// <summary>Applies <paramref name="theme"/> synchronously on the calling (UI) thread. Used at startup, before the first frame.</summary>
    public void ApplyNow(ColorTheme theme)
    {
        _ui.AssertUiThread();
        Apply(Prepare(theme));
    }

    private void OnThemeChanged(ColorTheme theme)
    {
        var version = Interlocked.Increment(ref _version);
        // Heavy part (fallback resolution) off the UI thread; stale preparations are dropped by version.
        _ = Task.Run(() =>
        {
            var prepared = Prepare(theme);
            _ui.PostCoalesced("theme.apply", () =>
            {
                if (version == Volatile.Read(ref _version)) Apply(prepared);
            });
        });
    }

    private sealed record Prepared(string ThemeId, ThemeKind Kind, Color[] Colors);

    private Prepared Prepare(ColorTheme theme)
    {
        var ids = WorkbenchColors.All;
        var colors = new Color[ids.Count];
        for (var i = 0; i < ids.Count; i++) colors[i] = ToColor(Resolve(theme, ids[i], 0));
        return new Prepared(theme.Id, theme.Kind, colors);
    }

    private Rgba Resolve(ColorTheme theme, string id, int depth)
    {
        if (theme.TryGetColor(id, out var c)) return c;
        if (depth < 4 && WorkbenchColors.ShellFallbacks.TryGetValue(id, out var fallback)) return Resolve(theme, fallback, depth + 1);
        try { return _themes.GetColor(id); }
        catch (Exception) { return new Rgba(255, 0, 255); } // loud magenta: a theme service that throws must be visible, not silent
    }

    private static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private void Apply(Prepared p)
    {
        _ui.AssertUiThread();
        var ids = WorkbenchColors.All;
        var resources = _app.Resources;
        for (var i = 0; i < ids.Count; i++)
        {
            if (!_brushes.TryGetValue(ids[i], out var brush))
            {
                _brushes[ids[i]] = brush = new SolidColorBrush(p.Colors[i]);
                resources[WorkbenchColors.BrushKey(ids[i])] = brush;
            }
            else if (brush.Color != p.Colors[i])
            {
                brush.Color = p.Colors[i];
            }
        }
        foreach (var (key, id) in DockBridge)
            if (!resources.ContainsKey(key) || !ReferenceEquals(resources[key], _brushes[id]))
                resources[key] = _brushes[id];

        var variant = p.Kind is ThemeKind.Light or ThemeKind.HighContrastLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (_app.RequestedThemeVariant != variant) _app.RequestedThemeVariant = variant;
        // Visual Studio draws no drag grip in tool captions and marks the active document tab with a TOP accent bar (MainWindow styles),
        // not with the bottom indicator line Dock draws by default.
        resources["DockChromeGripBrush"] = Brushes.Transparent;
        resources["DockTabActiveIndicatorBrush"] = Brushes.Transparent;
        resources["DockApplicationAccentBrushIndicator"] = Brushes.Transparent;
        FluentBridge.Apply(_app, _brushes, id => _brushes[id].Color, variant);
        _appliedThemeId = p.ThemeId;
        ApplyCount++;
    }

    /// <summary>The live brush for a colour id (null before the first apply or for an unknown id). Lets code-behind use theme colours.</summary>
    public IBrush? GetBrush(string colorId) => _brushes.GetValueOrDefault(colorId);

    private void SetTokens(IConfigurationService? config)
    {
        var r = _app.Resources;
        r["ui:FontFamily"] = new FontFamily("Segoe UI, Inter, Noto Sans, sans-serif");
        r["ui:FontSize"] = 12.0;
        r["ui:FontSizeSmall"] = 11.0;
        r["ui:FontSizeLarge"] = 14.0;
        r["ui:FontSizeTitle"] = 20.0;
        r["ui:CornerRadius"] = new CornerRadius(2);
        r["ui:CornerRadiusNone"] = new CornerRadius(0);
        r["ui:BorderThickness"] = new Thickness(1);
        r["ui:ControlPadding"] = new Thickness(6, 3);
        r["ui:MenuItemPadding"] = new Thickness(8, 4);
        r["ui:ToolbarHeight"] = 30.0;
        r["ui:TitleBarHeight"] = 30.0;
        r["ui:StatusBarHeight"] = 22.0;
        r["ui:ItemHeight"] = 22.0;
        r["ui:IconSize"] = 16.0;
        SetCodeFont(config);
    }

    private void SetCodeFont(IConfigurationService? config)
    {
        var family = config?.Get<string>(SettingKeys.EditorFontFamily);
        _app.Resources["ui:CodeFontFamily"] = new FontFamily(string.IsNullOrWhiteSpace(family) ? "Cascadia Code, Consolas, DejaVu Sans Mono, Liberation Mono, monospace" : family);
    }

    public void Dispose() => _subs.Dispose();
}
