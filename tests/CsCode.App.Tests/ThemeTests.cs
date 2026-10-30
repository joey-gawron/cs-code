using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Theming;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.App.Tests;

public class BuiltInThemeTests
{
    [Fact]
    public void Three_vs2022_themes_exist_with_the_right_kinds_and_dark_is_the_default()
    {
        Assert.Equal([VsThemes.DarkId, VsThemes.BlueId, VsThemes.LightId], VsThemes.All.Select(t => t.Id));
        Assert.Equal(ThemeKind.Dark, VsThemes.Dark.Kind);
        Assert.Equal(ThemeKind.Light, VsThemes.Blue.Kind);
        Assert.Equal(ThemeKind.Light, VsThemes.Light.Kind);
        Assert.Equal(VsThemes.DarkId, VsThemes.DefaultId);
    }

    [Theory]
    [InlineData(VsThemes.DarkId)]
    [InlineData(VsThemes.BlueId)]
    [InlineData(VsThemes.LightId)]
    public void Every_published_workbench_colour_is_defined_without_relying_on_a_fallback_chain(string id)
    {
        var theme = VsThemes.All.Single(t => t.Id == id);
        var missing = WorkbenchColors.All.Where(k => !theme.Colors.ContainsKey(k)).ToArray();
        Assert.True(missing.Length == 0, $"{id} lacks: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_documented_palette_values_of_visual_studio_dark_are_exact()
    {
        var c = VsThemes.Dark.Colors;
        Assert.Equal("#1E1E1E", c["editor.background"].ToHex());
        Assert.Equal("#252526", c["sideBar.background"].ToHex());
        Assert.Equal("#2D2D30", c["titleBar.activeBackground"].ToHex());
        Assert.Equal("#3F3F46", c["panel.border"].ToHex());
        Assert.Equal("#007ACC", c["focusBorder"].ToHex());
        Assert.Equal("#264F78", c["editor.selectionBackground"].ToHex());
        Assert.Equal("#007ACC", c["statusBar.background"].ToHex());
        // #CA5100 (the VS orange) gives white text 4.48:1, just under WCAG AA: one step darker reaches 4.54:1 and looks the same.
        Assert.Equal("#C95000", c["statusBar.debuggingBackground"].ToHex());
        Assert.Equal("#68217A", c["statusBar.noFolderBackground"].ToHex());
        Assert.Equal("#007ACC", c["tab.activeBorderTop"].ToHex());
        Assert.Equal("#1E1E1E", c["tab.activeBackground"].ToHex());
        Assert.Equal("#2D2D30", c["tab.inactiveBackground"].ToHex());
        Assert.Equal("#333337", c["menu.background"].ToHex());
        Assert.Equal("#F1F1F1", c["foreground"].ToHex());
        Assert.Equal("#A0A0A0", c["descriptionForeground"].ToHex()); // #999999 failed AA on menus and inputs (#333337): 4.42:1
        Assert.Equal("#3E3E40", c["list.hoverBackground"].ToHex());
        Assert.Equal("#333337", c["input.background"].ToHex());
    }

    [Fact]
    public void Blue_and_light_follow_their_documented_palettes()
    {
        Assert.Equal("#293955", VsThemes.Blue.Colors["titleBar.activeBackground"].ToHex());
        Assert.Equal("#CFD6E5", VsThemes.Blue.Colors["toolbar.background"].ToHex());
        Assert.Equal("#F5F5F5", VsThemes.Light.Colors["sideBar.background"].ToHex());
        Assert.Equal("#FFFFFF", VsThemes.Light.Colors["editor.background"].ToHex());
        Assert.Equal("#CCCEDB", VsThemes.Light.Colors["panel.border"].ToHex());
        Assert.Equal("#007ACC", VsThemes.Light.Colors["focusBorder"].ToHex());
    }

    [Theory]
    [InlineData(VsThemes.DarkId, "#569CD6", "#4EC9B0", "#D69D85", "#57A64A", "#B5CEA8", "#DCDCAA")]
    [InlineData(VsThemes.LightId, "#0000FF", "#2B91AF", "#A31515", "#008000", "#098658", "#74531F")]
    public void Token_colours_use_the_vs_c_sharp_palette(string id, string keyword, string type, string str, string comment, string number, string function)
    {
        var theme = VsThemes.All.Single(t => t.Id == id);
        string Fg(string scope) => theme.TokenColors.First(r => r.Scopes.Contains(scope)).Foreground!;
        Assert.Equal(keyword, Fg("keyword"));
        Assert.Equal(type, Fg("entity.name.type"));
        Assert.Equal(str, Fg("string"));
        Assert.Equal(comment, Fg("comment"));
        Assert.Equal(number, Fg("constant.numeric"));
        Assert.Equal(function, Fg("entity.name.function"));
    }

    [Fact]
    public void The_catalogue_has_no_duplicates_and_the_shell_fallbacks_point_at_published_ids()
    {
        Assert.Equal(WorkbenchColors.All.Count, WorkbenchColors.All.Distinct().Count());
        Assert.All(WorkbenchColors.ShellFallbacks, kv => Assert.Contains(kv.Value, WorkbenchColors.All));
        Assert.True(WorkbenchColors.All.Count >= 200, $"{WorkbenchColors.All.Count} ids");
    }

    [Fact]
    public void Shell_docs_list_every_published_key()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "SHELL.md"));
        var missing = WorkbenchColors.All.Where(k => !doc.Contains("`" + k + "`", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, "docs/SHELL.md must document these vsc: keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_icon_catalog_ships_well_over_150_icons_and_all_shell_keys_exist()
    {
        Assert.True(IconCatalog.Names.Count >= 150, $"{IconCatalog.Names.Count} icons");
        foreach (var f in typeof(IconKey).GetFields().Where(f => f.IsLiteral))
            Assert.True(IconCatalog.Contains((string)f.GetRawConstantValue()!), $"IconKey.{f.Name} = {f.GetRawConstantValue()} is not in the catalog");
    }
}

public class ThemeApplierTests
{
    private static FakeColorThemeService Themes()
    {
        var svc = new FakeColorThemeService();
        VsThemes.RegisterAll(svc);
        return svc;
    }

    [AvaloniaFact]
    public async Task Every_workbench_colour_becomes_a_live_brush_under_its_vsc_key()
    {
        var svc = Themes();
        var app = Application.Current!;
        using var applier = new ThemeApplier(app, svc, new ImmediateUiDispatcher());
        foreach (var id in WorkbenchColors.All)
        {
            Assert.True(app.Resources.TryGetResource(WorkbenchColors.BrushKey(id), null, out var value), $"vsc:{id} missing");
            Assert.IsAssignableFrom<IBrush>(value);
        }
        Assert.Equal(Color.Parse("#1E1E1E"), ((SolidColorBrush)applier.GetBrush("editor.background")!).Color);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Switching_theme_at_runtime_mutates_the_same_brush_objects_in_place()
    {
        var svc = Themes();
        var app = Application.Current!;
        var ui = new ManualUiDispatcher();
        using var applier = new ThemeApplier(app, svc, ui);
        var brush = (SolidColorBrush)applier.GetBrush("statusBar.background")!;
        var before = brush.Color;
        var editorBg = (SolidColorBrush)applier.GetBrush("editor.background")!;

        await svc.SetCurrentAsync(VsThemes.LightId);
        var deadline = Environment.TickCount64 + 5000;
        while (applier.AppliedThemeId != VsThemes.LightId && Environment.TickCount64 < deadline) { ui.RunPending(); await Task.Delay(5); }

        Assert.Equal(VsThemes.LightId, applier.AppliedThemeId);
        Assert.Same(brush, applier.GetBrush("statusBar.background")); // same object: every control bound to it repaints, nothing is re-resolved
        Assert.Equal(Color.Parse("#FFFFFF"), editorBg.Color);
        Assert.Equal(before, brush.Color); // the VS status bar blue is the same in Dark and Light
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

        await svc.SetCurrentAsync(VsThemes.DarkId);
        deadline = Environment.TickCount64 + 5000;
        while (applier.AppliedThemeId != VsThemes.DarkId && Environment.TickCount64 < deadline) { ui.RunPending(); await Task.Delay(5); }
        Assert.Equal(Color.Parse("#1E1E1E"), editorBg.Color);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task Rapid_theme_switching_applies_only_the_final_theme()
    {
        var svc = Themes();
        var ui = new ManualUiDispatcher();
        using var applier = new ThemeApplier(Application.Current!, svc, ui);
        var applied = applier.ApplyCount;
        for (var i = 0; i < 30; i++) await svc.SetCurrentAsync(i % 2 == 0 ? VsThemes.BlueId : VsThemes.LightId);
        await svc.SetCurrentAsync(VsThemes.BlueId);
        var deadline = Environment.TickCount64 + 5000;
        while (applier.AppliedThemeId != VsThemes.BlueId && Environment.TickCount64 < deadline) { ui.RunPending(); await Task.Delay(5); }
        await Task.Delay(100);
        ui.RunPending();
        Assert.Equal(VsThemes.BlueId, applier.AppliedThemeId);
        Assert.Equal(Color.Parse("#293955"), ((SolidColorBrush)applier.GetBrush("titleBar.activeBackground")!).Color);
        Assert.True(applier.ApplyCount - applied < 31, "stale preparations must be dropped, not all applied");
    }

    [AvaloniaFact]
    public void Tokens_dock_keys_and_the_fluent_palette_are_published()
    {
        var svc = Themes();
        var app = Application.Current!;
        using var applier = new ThemeApplier(app, svc, new ImmediateUiDispatcher());
        foreach (var key in new[] { "ui:FontFamily", "ui:FontSize", "ui:CornerRadius", "ui:CornerRadiusNone", "ui:CodeFontFamily", "DockTabForegroundBrush", "DockDocumentTabStripBackgroundBrush", "DockThemeBackgroundBrush" })
            Assert.True(app.Resources.TryGetResource(key, null, out _), key + " missing");
        app.Resources.TryGetResource("ui:FontSize", null, out var size);
        Assert.Equal(12.0, size);
        app.Resources.TryGetResource("ui:CornerRadius", null, out var radius);
        Assert.Equal(new CornerRadius(2), radius);
    }

    /// <summary>
    /// The code panels (Output, Find Results, the debug console...) shape the code font with ui:CodeFontFeatures, from editor.fontLigatures:
    /// off by default, so the bundled Fira Code draws != and => as their characters there too, and live on when the setting asks.
    /// </summary>
    [AvaloniaFact]
    public void Code_panels_draw_Fira_Code_without_ligatures_unless_editor_fontLigatures_turns_them_on()
    {
        const string code = "a != b => c === d <= e";
        var config = new CsCode.TestDoubles.TestConfigurationService();
        var app = Application.Current!;
        using var applier = new ThemeApplier(app, Themes(), new ImmediateUiDispatcher(), config);

        int Substituted()
        {
            var block = new Avalonia.Controls.TextBlock { Text = code, FontFamily = ThemeBrushes.CodeFont, FontFeatures = ThemeBrushes.CodeFontFeatures };
            block.Measure(Size.Infinity);
            var run = block.TextLayout.TextLines[0].TextRuns.OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().Single();
            var face = run.ShapedBuffer.GlyphTypeface;
            Assert.Equal("Fira Code", face.FamilyName);
            var glyphs = run.ShapedBuffer.ToList();
            Assert.Equal(code.Length, glyphs.Count);
            return Enumerable.Range(0, code.Length).Count(i => face.CharacterToGlyphMap.TryGetGlyph(code[i], out var plain) && plain != glyphs[i].GlyphIndex);
        }

        Assert.Equal(["-liga", "-calt"], ThemeBrushes.CodeFontFeatures!.Select(f => f.ToString()));
        Assert.Equal(0, Substituted());
        config.Set("editor.fontLigatures", true);
        Assert.Empty(ThemeBrushes.CodeFontFeatures!);
        Assert.True(Substituted() >= 8);
        config.Set("editor.fontLigatures", "'ss01', 'cv01' 2"); // VS Code's CSS feature list
        Assert.Equal(["ss01", "cv01=2"], ThemeBrushes.CodeFontFeatures!.Select(f => f.ToString()));
    }

    [AvaloniaFact]
    public void Missing_ids_fall_back_through_the_shell_chain_then_the_core_chain()
    {
        var svc = new FakeColorThemeService();
        // A user theme that defines almost nothing.
        svc.Register(new ColorTheme("user", "User", ThemeKind.Dark,
            new Dictionary<string, Rgba> { ["editor.background"] = new Rgba(1, 2, 3), ["titleBar.activeBackground"] = new Rgba(10, 20, 30), ["foreground"] = new Rgba(250, 250, 250) }, []));
        using var applier = new ThemeApplier(Application.Current!, svc, new ImmediateUiDispatcher());
        // toolbar.background is shell-specific: it follows the title bar when the theme omits it.
        Assert.Equal(Color.FromRgb(10, 20, 30), ((SolidColorBrush)applier.GetBrush("toolbar.background")!).Color);
        // tab.inactiveBackground is a VS Code id: it goes through the Core chain (here: editorGroupHeader.tabsBackground, undefined => transparent).
        Assert.NotNull(applier.GetBrush("tab.inactiveBackground"));
    }

    [AvaloniaFact]
    public void A_theme_from_a_service_that_does_not_complete_is_completed_by_the_applier_so_no_icon_colour_is_transparent()
    {
        var svc = new FakeColorThemeService();
        // A user theme (or a VS Code theme) that knows nothing of the shell's icons.
        svc.Register(new ColorTheme("user", "User", ThemeKind.Dark,
            new Dictionary<string, Rgba> { ["editor.background"] = new Rgba(1, 2, 3), ["editor.foreground"] = new Rgba(220, 220, 220) }, []));
        using var applier = new ThemeApplier(Application.Current!, svc, new ImmediateUiDispatcher());
        foreach (var id in new[] { "debugIcon.startForeground", "problemsErrorIcon.foreground", "problemsWarningIcon.foreground", "problemsInfoIcon.foreground" })
        {
            var expected = VsThemes.Dark.Colors[id];
            Assert.Equal(Color.FromArgb(expected.A, expected.R, expected.G, expected.B), ((SolidColorBrush)applier.GetBrush(id)!).Color);
        }
        Assert.Equal(Color.FromRgb(1, 2, 3), ((SolidColorBrush)applier.GetBrush("editor.background")!).Color); // the theme's own colours stay
    }

    private static async Task WaitForPostAsync(ManualUiDispatcher ui)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (ui.PendingCount == 0 && Environment.TickCount64 < deadline) await Task.Delay(2); // the preparation runs off the UI thread
        Assert.True(ui.PendingCount > 0, "the prepared theme was never posted");
    }

    [AvaloniaFact]
    public async Task A_runtime_switch_repaints_the_brushes_in_one_callback_and_retargets_fluent_in_three_more()
    {
        var svc = Themes();
        var app = Application.Current!;
        var ui = new ManualUiDispatcher();
        using var applier = new ThemeApplier(app, svc, ui);
        var editorBg = (SolidColorBrush)applier.GetBrush("editor.background")!;

        await svc.SetCurrentAsync(VsThemes.LightId);
        await WaitForPostAsync(ui);
        Assert.Equal(1, ui.RunOneTurn());
        Assert.Equal(Color.Parse("#FFFFFF"), editorBg.Color);            // every vsc: brush repainted at once
        Assert.Equal(VsThemes.DarkId, applier.AppliedThemeId);           // Fluent's palette, variant and text selection follow, one callback each
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        var turns = 0;
        while (applier.AppliedThemeId != VsThemes.LightId && turns < 10) turns += ui.RunOneTurn();
        Assert.Equal(3, turns);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public async Task A_burst_of_switches_repaints_the_brushes_each_time_and_retargets_fluent_once_it_settles()
    {
        var svc = Themes();
        var app = Application.Current!;
        var ui = new ManualUiDispatcher();
        using var applier = new ThemeApplier(app, svc, ui);
        var titleBar = (SolidColorBrush)applier.GetBrush("titleBar.activeBackground")!;

        await svc.SetCurrentAsync(VsThemes.LightId);
        await WaitForPostAsync(ui);
        var first = System.Diagnostics.Stopwatch.GetTimestamp();
        ui.RunPending();
        Assert.Equal(VsThemes.LightId, applier.AppliedThemeId);          // a lone switch is applied completely at once

        await svc.SetCurrentAsync(VsThemes.BlueId);
        await WaitForPostAsync(ui);
        ui.RunPending();
        var gap = System.Diagnostics.Stopwatch.GetElapsedTime(first);    // at least the time between the two applies
        Assert.Equal(Color.Parse("#293955"), titleBar.Color);             // Blue's brushes are on screen
        // (On a machine so loaded that the second switch came later than the window, it was a lone switch and is applied completely.)
        if (gap < ThemeApplier.BurstWindow)
            Assert.Equal(VsThemes.LightId, applier.AppliedThemeId);      // a burst: Fluent waits until the switching stops
        var deadline = Environment.TickCount64 + 5000;
        while (applier.AppliedThemeId != VsThemes.BlueId && Environment.TickCount64 < deadline) { ui.RunPending(); await Task.Delay(5); }
        Assert.Equal(VsThemes.BlueId, applier.AppliedThemeId);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
    }

    /// <summary>
    /// T4: the pickers preview at most once per <see cref="ThemePreviewer.MinInterval"/>, so previews arrowed through at that pace must count as a
    /// burst (Fluent's palette step once, when the arrowing stops); with a burst window shorter than the interval every preview paid for it.
    /// </summary>
    [AvaloniaFact]
    public async Task Previews_at_the_pickers_interval_are_a_burst_and_fluent_waits_until_they_stop()
    {
        Assert.True(ThemeApplier.BurstWindow > ThemePreviewer.MinInterval + TimeSpan.FromMilliseconds(100));
        var svc = Themes();
        var ui = new ManualUiDispatcher();
        using var applier = new ThemeApplier(Application.Current!, svc, ui);
        await svc.SetCurrentAsync(VsThemes.LightId);
        await WaitForPostAsync(ui);
        var first = System.Diagnostics.Stopwatch.GetTimestamp();
        ui.RunPending();
        Assert.Equal(VsThemes.LightId, applier.AppliedThemeId);
        await Task.Delay(ThemePreviewer.MinInterval);                     // the next preview, as a held arrow key gives it
        await svc.SetCurrentAsync(VsThemes.BlueId);
        await WaitForPostAsync(ui);
        ui.RunPending();
        var gap = System.Diagnostics.Stopwatch.GetElapsedTime(first);
        if (gap < ThemeApplier.BurstWindow) Assert.Equal(VsThemes.LightId, applier.AppliedThemeId); // deferred (unless the machine stalled past the window)
        var deadline = Environment.TickCount64 + 5000;
        while (applier.AppliedThemeId != VsThemes.BlueId && Environment.TickCount64 < deadline) { ui.RunPending(); await Task.Delay(5); }
        Assert.Equal(VsThemes.BlueId, applier.AppliedThemeId);
    }

    [AvaloniaFact]
    public async Task Disposing_the_applier_cancels_the_steps_still_queued()
    {
        var svc = Themes();
        var ui = new ManualUiDispatcher();
        var applier = new ThemeApplier(Application.Current!, svc, ui);
        await svc.SetCurrentAsync(VsThemes.LightId);
        await WaitForPostAsync(ui);
        ui.RunOneTurn();                                                  // brushes applied, Fluent's steps queued
        applier.Dispose();
        ui.RunPending();
        Assert.Equal(VsThemes.DarkId, applier.AppliedThemeId);           // the queued steps found themselves superseded
    }

    [AvaloniaFact]
    public void Disposed_appliers_stop_listening_and_are_released()
    {
        var svc = Themes();
        LeakAssert_NotRetained(() =>
        {
            var applier = new ThemeApplier(Application.Current!, svc, new ImmediateUiDispatcher());
            applier.Dispose();
            return [applier];
        });
    }

    private static void LeakAssert_NotRetained(Func<object[]> f) => CsCode.TestDoubles.LeakAssert.NotRetained(f);

    [AvaloniaFact]
    public async Task Switching_the_theme_200_times_leaves_the_resource_table_and_brush_set_unchanged()
    {
        var svc = Themes();
        var app = Application.Current!;
        var ui = AvaloniaUiDispatcher.Instance; // the apply step must run on the real UI thread
        using var applier = new ThemeApplier(app, svc, ui);
        var brushes = WorkbenchColors.All.Select(id => applier.GetBrush(id)!).ToArray();
        var countBefore = app.Resources.Count;
        for (var i = 0; i < 200; i++)
        {
            await svc.SetCurrentAsync(VsThemes.All[i % 3].Id);
            var deadline = Environment.TickCount64 + 2000;
            while (applier.AppliedThemeId != VsThemes.All[i % 3].Id && Environment.TickCount64 < deadline) await Task.Delay(1);
        }
        Assert.Equal(countBefore, app.Resources.Count);
        for (var i = 0; i < brushes.Length; i++) Assert.Same(brushes[i], applier.GetBrush(WorkbenchColors.All[i]));
    }
}
