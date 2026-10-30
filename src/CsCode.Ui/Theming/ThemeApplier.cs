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
                if (e.Affects(SettingKeys.EditorFontFamily) || e.Affects(SettingKeys.EditorFontLigatures)) _ui.Post(() => SetCodeFont(config));
            }));
        ApplyNow(themes.Current);
    }

    /// <summary>Id of the theme fully applied to the resources, Fluent's palette and variant included (null before the first apply).</summary>
    public string? AppliedThemeId => _appliedThemeId;

    /// <summary>Number of theme applications (brush repaints) performed (tests use this to prove coalescing under rapid switching).</summary>
    public int ApplyCount { get; private set; }

    /// <summary>Applies <paramref name="theme"/> synchronously on the calling (UI) thread. Used at startup, before the first frame.</summary>
    public void ApplyNow(ColorTheme theme)
    {
        _ui.AssertUiThread();
        Apply(Prepare(theme), sliced: false);
    }

    private void OnThemeChanged(ColorTheme theme)
    {
        var version = Interlocked.Increment(ref _version);
        // Heavy part (fallback resolution) off the UI thread. Preparations may finish out of order, so the newest one wins a single slot
        // and the coalesced UI post applies whatever the slot holds (a stale preparation can never overwrite a newer one).
        _ = Task.Run(() =>
        {
            var prepared = Prepare(theme);
            lock (_slotGate)
            {
                if (version <= _slotVersion) return; // a newer theme was already prepared
                _slot = prepared;
                _slotVersion = version;
            }
            _ui.PostCoalesced("theme.apply", () =>
            {
                Prepared? next;
                lock (_slotGate) { next = _slot; _slot = null; }
                if (next is not null) Apply(next, sliced: true);
            });
        });
    }

    private readonly object _slotGate = new();
    private Prepared? _slot;
    private int _slotVersion;

    private sealed record Prepared(string ThemeId, ThemeKind Kind, Color[] Colors);

    private Prepared Prepare(ColorTheme theme)
    {
        // Whatever the service hands over (a user's theme file, a test double's theme) is completed for the VS 2022 chrome first, so no published
        // id is ever unthemed and chrome text meets WCAG AA. The editor's service already completes its themes: then this is a lookup.
        var full = Completions.GetValue(theme, ThemeCompletion.Complete);
        var ids = WorkbenchColors.All;
        var colors = new Color[ids.Count];
        for (var i = 0; i < ids.Count; i++) colors[i] = ToColor(Resolve(full, ids[i], 0));
        return new Prepared(theme.Id, theme.Kind, colors);
    }

    // Per theme instance (reference identity), dropped with the theme: bounded by the number of themes, never by the number of switches.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ColorTheme, ColorTheme> Completions = new();

    private Rgba Resolve(ColorTheme theme, string id, int depth)
    {
        if (theme.TryGetColor(id, out var c)) return c;
        if (depth < 4 && WorkbenchColors.ShellFallbacks.TryGetValue(id, out var fallback)) return Resolve(theme, fallback, depth + 1);
        try { return _themes.GetColor(id); }
        catch (Exception) { return new Rgba(255, 0, 255); } // loud magenta: a theme service that throws must be visible, not silent
    }

    private static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Per-phase milliseconds of the most recent apply (brushes, dock keys, fluent palette, variant, selection), for budget diagnostics.</summary>
    public string LastTimings { get; private set; } = "";

    private int _applyVersion;

    /// <summary>
    /// Brushes first (in place: everything bound to a <c>vsc:</c> brush repaints), then Avalonia Fluent's own resources. Each step of the second part
    /// (the palette, the light/dark variant, the text selection colour) makes every control re-resolve its Fluent resources, a few milliseconds on a
    /// full window. A runtime switch (<paramref name="sliced"/>) gives each its own dispatcher callback, so input and rendering interleave and no
    /// callback approaches the latency budget; a newer theme's apply supersedes the remaining steps of an older one. The startup apply runs them
    /// in one go, before the first frame.
    /// </summary>
    private void Apply(Prepared p, bool sliced)
    {
        _ui.AssertUiThread();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
        var brushesMs = Ms(t0);
        // Every assignment into the application resource dictionary makes ALL controls re-resolve their dynamic resources, so the structural
        // entries (Dock keys, tokens, bridge keys) are published exactly once: the brushes behind them are mutated in place afterwards.
        t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_structureApplied)
        {
            foreach (var (key, id) in DockBridge) resources[key] = _brushes[id];
            // Visual Studio draws no drag grip in tool captions and marks the active document tab with a TOP accent bar (MainWindow styles),
            // not with the bottom indicator line Dock draws by default.
            resources["DockChromeGripBrush"] = Brushes.Transparent;
            resources["DockTabActiveIndicatorBrush"] = Brushes.Transparent;
            resources["DockApplicationAccentBrushIndicator"] = Brushes.Transparent;
            FluentBridge.PublishStructure(_app, _brushes);
            _structureApplied = true;
        }
        var structureMs = Ms(t0);
        var variant = p.Kind is ThemeKind.Light or ThemeKind.HighContrastLight ? ThemeVariant.Light : ThemeVariant.Dark;
        var version = ++_applyVersion;
        ApplyCount++;
        double paletteMs = 0, variantMs = 0;

        // The target variant's palette before the variant: a variant switch then re-resolves once, with the final palette.
        void Palette()
        {
            if (version != _applyVersion) return; // superseded by a newer theme
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            FluentBridge.UpdatePalette(_app, id => _brushes[id].Color, variant);
            paletteMs = Ms(t);
            if (sliced) _ui.PostCoalesced("theme.apply.variant", Variant); else Variant();
        }
        void Variant()
        {
            if (version != _applyVersion) return;
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_app.RequestedThemeVariant != variant) _app.RequestedThemeVariant = variant;
            variantMs = Ms(t);
            if (sliced) _ui.PostCoalesced("theme.apply.selection", Selection); else Selection();
        }
        void Selection()
        {
            if (version != _applyVersion) return;
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            FluentBridge.UpdateSelectionHighlight(_app, _brushes["selection.background"].Color);
            _appliedThemeId = p.ThemeId;
            LastTimings = $"brushes={brushesMs:F1} structure={structureMs:F1} palette={paletteMs:F1} variant={variantMs:F1} selection={Ms(t):F1} ms";
        }

        if (!sliced) { Palette(); return; }
        // A burst of switches (arrowing through a theme list: a preview every ThemePreviewer.MinInterval) repaints every vsc: brush at once but defers Fluent's steps
        // until the burst settles, so the expensive re-resolutions run once per burst instead of once per preview. A lone switch runs them now.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var burst = _lastSlicedApply != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastSlicedApply, now) < BurstWindow;
        _lastSlicedApply = now;
        _settleTimer?.Dispose();
        _settleTimer = null;
        if (!burst) _ui.PostCoalesced("theme.apply.palette", Palette);
        else _settleTimer = TimeProvider.System.CreateTimer(_ => _ui.PostCoalesced("theme.apply.palette", Palette), null, BurstWindow, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Two switches closer than this are a burst (see <see cref="Apply"/>). Longer than the pickers' preview interval
    /// (<see cref="ThemePreviewer.MinInterval"/>, 250 ms) with room for timer jitter: at 200 ms, previews arrowed through at that interval were
    /// never a burst, so every preview ran Fluent's palette step (4-8 ms on a quiet machine, 10-30 ms under load) and the preview budget was missed
    /// (T4).
    /// </summary>
    public static readonly TimeSpan BurstWindow = ThemePreviewer.MinInterval + TimeSpan.FromMilliseconds(150);

    private long _lastSlicedApply;
    private ITimer? _settleTimer;

    private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private bool _structureApplied;

    /// <summary>The live brush for a colour id (null before the first apply or for an unknown id). Lets code-behind use theme colours.</summary>
    public IBrush? GetBrush(string colorId) => _brushes.GetValueOrDefault(colorId);

    private void SetTokens(IConfigurationService? config)
    {
        var r = _app.Resources;
        r["ui:FontFamily"] = new FontFamily(FontFamilies.Normalize(OperatingSystem.IsMacOS() ? FontFamilies.MacUi : "Segoe UI, Inter, Noto Sans, sans-serif"));
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
        if (string.IsNullOrWhiteSpace(family))
            family = FontFamilies.DefaultCode;
        _app.Resources["ui:CodeFontFamily"] = new FontFamily(FontFamilies.Normalize(family));
        _app.Resources["ui:CodeFontFeatures"] = CodeFontFeatures(FontFamilies.LigatureFeatures(config?.GetRaw(SettingKeys.EditorFontLigatures)));
    }

    /// <summary><paramref name="features"/> (<see cref="FontFamilies.LigatureFeatures"/>) as the collection a text control takes; ill-formed ones are skipped.</summary>
    public static FontFeatureCollection CodeFontFeatures(IReadOnlyList<string> features)
    {
        var collection = new FontFeatureCollection();
        foreach (var f in features)
        {
            try { collection.Add(FontFeature.Parse(f)); }
            catch (Exception e) when (e is FormatException or ArgumentException) { }
        }
        return collection;
    }

    public void Dispose()
    {
        _subs.Dispose();
        _settleTimer?.Dispose();
        _applyVersion++; // a step still queued finds itself superseded
    }
}
