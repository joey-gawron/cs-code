using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;
using CsCode.Panels.Search;
using CsCode.Panels.Search.Views;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// A1/A2 on the REAL composition (<see cref="RealAppHost"/>: every module, <c>CSCODE_HOME</c> in a temp directory, headless): the Color Theme
/// dropdown at the top of the Settings page (<i>Environment › Appearance</i>; the user removed the toolbar's on 2026-10-29) lists every installed
/// theme grouped with swatches, previews while arrowing without persisting, reverts on Escape, applies and persists on Enter or a click, and follows
/// every other way of changing the theme. The main window has no theme dropdown, and View &gt; Toolbars no Color Theme entry.
/// </summary>
public class ThemeDropdownTests
{
    /// <summary>Opens the Settings page (Tools &gt; Options) and returns its Color Theme dropdown.</summary>
    internal static async Task<ThemePicker> SettingsPickerAsync(RealAppHost host)
    {
        await host.RunAsync(SearchPanelsHost.OpenSettings);
        Assert.True(await RealAppHost.WaitFor(() => host.Window.GetVisualDescendants().OfType<SettingsView>().Any()), "the Settings page did not open");
        await DevScene.SettleAsync(5);
        return host.Window.GetVisualDescendants().OfType<SettingsView>().Single().ThemePicker ?? throw new InvalidOperationException("no theme dropdown");
    }

    private static string? SettingInFile(RealAppHost host)
    {
        var path = host.Get<IConfigurationService>().UserSettingsPath;
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.TryGetProperty(SettingKeys.WorkbenchColorTheme, out var v) ? v.GetString() : null;
    }

    private static void Press(RealAppHost host, Key key)
    {
        host.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
    }

    private static async Task OpenAsync(RealAppHost host, ThemePicker picker)
    {
        picker.Focus();
        picker.IsDropDownOpen = true;
        await DevScene.SettleAsync(10);
        Assert.True(picker.IsDropDownOpen);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_settings_dropdown_lists_every_installed_theme_grouped_dark_light_high_contrast_with_a_swatch()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = await SettingsPickerAsync(host);
        Assert.True(picker.IsEffectivelyVisible);

        var items = ((IEnumerable<ThemeListItem>)picker.ItemsSource!).ToList();
        var choices = items.OfType<ThemeChoiceItem>().ToList();
        Assert.Equal(themes.Available.Count, choices.Count);
        Assert.Equal(24, choices.Count); // 3 Visual Studio 2022 + the 21 embedded in TextMateSharp.Grammars
        Assert.Equal([ThemeSelection.DarkGroup, ThemeSelection.LightGroup, ThemeSelection.HighContrastGroup], items.OfType<ThemeGroupItem>().Select(g => g.Title));
        // Each group's members follow its header and match the group.
        string? group = null;
        foreach (var item in items)
        {
            if (item is ThemeGroupItem g) { group = g.Title; continue; }
            var c = (ThemeChoiceItem)item;
            Assert.Equal(group, ThemeSelection.GroupOf(c.Kind));
            var theme = themes.Available.Single(t => t.Id == c.Id);
            var bg = theme.Colors["editor.background"];
            Assert.Equal(Avalonia.Media.Color.FromArgb(bg.A, bg.R, bg.G, bg.B), ((Avalonia.Media.ISolidColorBrush)c.Background).Color);
            Assert.Equal(3, c.Accents.Count);
        }
        Assert.Contains(choices, c => c.Label == "Monokai");
        Assert.Contains(choices, c => c.Label == "Dark High Contrast" && c.Kind == ThemeKind.HighContrastDark);
        Assert.Equal(themes.Current.Id, picker.SelectedThemeId);
        Assert.Equal(VsThemes.DarkId, picker.SelectedThemeId); // the default, workbench.colorTheme = "Visual Studio Dark"
        // Every theme the dropdown offers is a complete CsCode theme (the shell completed the editor's built-ins).
        Assert.All(themes.Available, t => Assert.True(ThemeCompletion.DefinesAll(t), t.Label));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Arrowing_through_the_open_list_previews_live_and_escape_reverts_without_persisting()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var applier = host.Shell.Theme!;
        var picker = await SettingsPickerAsync(host);
        var before = SettingInFile(host);
        await OpenAsync(host, picker);

        Press(host, Key.Down);
        await DevScene.SettleAsync(10);
        Press(host, Key.Down);
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id != VsThemes.DarkId));
        var previewed = themes.Current.Id;
        Assert.Equal(previewed, ((IColorThemePreview)themes).PreviewedThemeId);
        Assert.True(await RealAppHost.WaitFor(() => applier.AppliedThemeId == previewed), "the chrome repaints with the previewed theme");
        await Task.Delay(300);
        Assert.Equal(before, SettingInFile(host)); // a preview writes nothing

        Press(host, Key.Escape);
        Assert.True(await RealAppHost.WaitFor(() => !picker.IsDropDownOpen && themes.Current.Id == VsThemes.DarkId));
        Assert.Null(((IColorThemePreview)themes).PreviewedThemeId);
        Assert.True(await RealAppHost.WaitFor(() => applier.AppliedThemeId == VsThemes.DarkId));
        Assert.Equal(VsThemes.DarkId, picker.SelectedThemeId);
        await Task.Delay(300);
        Assert.Equal(before, SettingInFile(host));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Enter_applies_the_highlighted_theme_and_persists_its_label()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = await SettingsPickerAsync(host);
        await OpenAsync(host, picker);
        for (var i = 0; i < 3; i++) { Press(host, Key.Down); await DevScene.SettleAsync(3); }
        // The open list moves the keyboard focus (the selection changes on Enter): the highlighted entry is the focused container's.
        var focused = picker.GetRealizedContainers().OfType<ComboBoxItem>().Single(c => c.IsKeyboardFocusWithin);
        var highlightedId = Assert.IsType<ThemeChoiceItem>(picker.ItemFromContainer(focused)).Id;
        Assert.NotEqual(VsThemes.DarkId, highlightedId);
        // Previews are throttled (ThemePreviewer.MinInterval), latest wins: the highlighted entry is shown once the interval has passed.
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == highlightedId), "previewed: " + themes.Current.Id);
        var highlighted = themes.Current;

        Press(host, Key.Enter);
        Assert.True(await RealAppHost.WaitFor(() => !picker.IsDropDownOpen));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == highlighted.Label), "settings.json: " + SettingInFile(host));
        Assert.Equal(highlighted.Id, themes.Current.Id);
        Assert.Null(((IColorThemePreview)themes).PreviewedThemeId);
        Assert.Equal(highlighted.Id, picker.SelectedThemeId);
        Assert.Equal(highlighted.Label, host.Get<IConfigurationService>().Get<string>(SettingKeys.WorkbenchColorTheme));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Clicking_an_entry_applies_and_persists_it()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = await SettingsPickerAsync(host);
        await OpenAsync(host, picker);
        var container = picker.GetRealizedContainers().OfType<ComboBoxItem>().First(c => picker.ItemFromContainer(c) is ThemeChoiceItem { Label: "Monokai" });
        var top = TopLevel.GetTopLevel(container)!;
        var center = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), top)!.Value;
        top.MouseMove(center);
        top.MouseDown(center, MouseButton.Left);
        top.MouseUp(center, MouseButton.Left);
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == "vscode.monokai" && !picker.IsDropDownOpen));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == "Monokai"), "settings.json: " + SettingInFile(host));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Arrow_keys_on_the_closed_focused_box_apply_directly_and_skip_group_headers()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = await SettingsPickerAsync(host);
        picker.Focus();
        // Up from the first dark theme: the "Dark" header above it is not selectable.
        var firstDark = picker.Choices.First();
        await ThemeSelection.CommitAsync(themes, host.Get<IConfigurationService>(), firstDark.Id);
        await DevScene.SettleAsync(10);
        Press(host, Key.Up);
        await DevScene.SettleAsync(10);
        Assert.Equal(firstDark.Id, picker.SelectedThemeId);
        Press(host, Key.Down);
        var second = picker.Choices.Skip(1).First();
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == second.Id));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == second.Label));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_dropdown_follows_the_palette_command_and_an_edit_of_settings_json()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = await SettingsPickerAsync(host);
        Assert.True(picker.IsEffectivelyVisible);
        Assert.Equal(VsThemes.DarkId, picker.SelectedThemeId);

        // The palette's Color Theme command: preview (the dropdown shows it), then choose Solarized Light.
        var quick = host.Get<QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.SelectTheme);
        quick.Text = "Solarized Light";
        await DevScene.SettleAsync(10);
        quick.Accept();
        await running;
        Assert.True(await RealAppHost.WaitFor(() => picker.SelectedThemeId == "vscode.solarized-light"));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == "Solarized Light"));

        // settings.json edited by hand (or by another window): the platform reloads it, the theme service follows, so does the dropdown.
        var path = host.Get<IConfigurationService>().UserSettingsPath;
        var text = File.ReadAllText(path).Replace("\"Solarized Light\"", "\"Dracula\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == "textmatesharp.dracula", 15_000), "the theme follows settings.json");
        Assert.True(await RealAppHost.WaitFor(() => picker.SelectedThemeId == "textmatesharp.dracula"));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == "textmatesharp.dracula"));

        // A Settings page opened later (a fresh dropdown) starts on the current theme and drives the theme like the first one.
        await host.RunAsync(KnownCommands.CloseActiveEditor);
        Assert.True(await RealAppHost.WaitFor(() => !host.Window.GetVisualDescendants().OfType<SettingsView>().Any()));
        var second = await SettingsPickerAsync(host);
        Assert.NotSame(picker, second);
        Assert.Equal("textmatesharp.dracula", second.SelectedThemeId);
        second.SelectedItem = second.Choices.Single(c => c.Id == VsThemes.LightId); // closed box: applies directly
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == VsThemes.LightId));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == "Visual Studio Light"));
    }

    /// <summary>
    /// Switching faster than settings.json is written (the closed box arrowed through, or a loaded machine whose disk lags): the write of the
    /// previous choice reports its setting change after the next choice was made, and that late report must not take the newer choice back.
    /// The lane is held so the write of the newer choice is still pending when the report of the older one reaches the UI thread.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_late_report_of_the_previous_choice_written_to_settings_json_never_takes_back_the_newer_one()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var config = host.Get<IConfigurationService>();
        var older = themes.Available.Single(t => t.Id == "vscode.monokai");
        var newer = themes.Available.Single(t => t.Id == "textmatesharp.dracula");

        // The test body runs on the UI thread: nothing posted to it runs until the next await.
        _ = themes.SetCurrentAsync(older.Id); // persisted on the IO lane, whose setting change posts the shell's follow-up to the UI thread
        var deadline = Environment.TickCount64 + 10_000;
        while (config.Get<string>(SettingKeys.WorkbenchColorTheme) != older.Label && Environment.TickCount64 < deadline) Thread.Sleep(5);
        Assert.Equal(older.Label, config.Get<string>(SettingKeys.WorkbenchColorTheme));
        var lane = host.Get<CsCode.Platform.Io.IoLane>();
        using var release = new ManualResetEventSlim();
        var held = 0;
        var threads = host.Get<CsCode.Platform.PlatformOptions>().IoThreads;
        for (var i = 0; i < threads; i++) lane.Post(() => { Interlocked.Increment(ref held); release.Wait(30_000); });
        try
        {
            while (Volatile.Read(ref held) < threads && Environment.TickCount64 < deadline) Thread.Sleep(5);
            Assert.Equal(threads, Volatile.Read(ref held));
            _ = themes.SetCurrentAsync(newer.Id); // its write waits behind the held lane
            Assert.Equal(newer.Id, themes.Current.Id);
            await DevScene.SettleAsync(20); // the older choice's setting change is handled while settings.json still names it
            Assert.Equal(newer.Id, themes.Current.Id);
        }
        finally { release.Set(); }

        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == newer.Label), "settings.json: " + SettingInFile(host));
        await DevScene.SettleAsync(20);
        Assert.Equal(newer.Label, SettingInFile(host));
        Assert.Equal(newer.Id, themes.Current.Id);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == newer.Id), "applied: " + host.Shell.Theme!.AppliedThemeId);
    }

    /// <summary>The user's Extra (2026-10-29): the Color Theme dropdown is gone from the main window and from View &gt; Toolbars.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_main_window_has_no_theme_dropdown_and_View_Toolbars_lists_only_Standard_and_Debug()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var config = host.Get<IConfigurationService>();
        var toolbar = host.Window.GetVisualDescendants().OfType<ToolbarView>().Single();
        // Nothing outside a Settings page is a theme dropdown (the toolbar, the title bar, the status bar).
        Assert.Empty(host.Window.GetVisualDescendants().OfType<ThemePicker>());
        Assert.Empty(toolbar.GetLogicalDescendants().OfType<ThemePicker>());
        Assert.Equal([ToolbarView.StandardGroup, ToolbarView.DebugGroup], ToolbarView.Groups.Select(g => g.Id));
        host.Shell.Menus.Refresh();
        var toolbars = host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children.Single(c => c.Header == "Tool_bars");
        Assert.Equal(["_Standard", "_Debug"], toolbars.Children.Select(c => c.Header));
        // Tools > Color Theme... (the palette's quick pick) stays.
        Assert.Contains(host.Shell.Menus.Roots.Single(r => r.Header == "T_ools").Children, c => c.CommandId == KnownCommands.SelectTheme);
        Assert.DoesNotContain(config.Properties, p => p.Key == "cscode.toolbars.colorTheme");
        Assert.Contains(config.Properties, p => p.Key == "cscode.toolbars.debug");

        // The old group id is ignored by View > Toolbars' command: nothing is written, nothing appears.
        await host.RunAsync(ShellMenus.ToggleToolbar, "colorTheme");
        Assert.DoesNotContain("cscode.toolbars.colorTheme", File.Exists(config.UserSettingsPath) ? File.ReadAllText(config.UserSettingsPath) : "");
        Assert.Empty(host.Window.GetVisualDescendants().OfType<ThemePicker>());

        // The Settings page has it, and only one.
        var picker = await SettingsPickerAsync(host);
        Assert.True(picker.IsEffectivelyVisible);
        Assert.Single(host.Window.GetVisualDescendants().OfType<ThemePicker>());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Toolbars_hides_and_shows_a_group_and_persists_it()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var toolbar = host.Window.GetVisualDescendants().OfType<ToolbarView>().Single();
        var config = host.Get<IConfigurationService>();
        host.Shell.Menus.Refresh();
        var toolbars = host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children.Single(c => c.Header == "Tool_bars");
        var entry = toolbars.Children.Single(c => c.Header == "_Debug");
        Assert.True(entry.IsChecked);
        Assert.True(toolbar.IsGroupVisible(ToolbarView.DebugGroup));

        await ((IAsyncRelayCommand)entry.Command!).ExecuteAsync(null);
        Assert.True(await RealAppHost.WaitFor(() => !toolbar.IsGroupVisible(ToolbarView.DebugGroup)));
        Assert.False(config.Get("cscode.toolbars.debug", true));
        Assert.Contains("\"cscode.toolbars.debug\": false", File.ReadAllText(config.UserSettingsPath));
        host.Shell.Menus.Refresh();
        Assert.False(toolbars.Children.Single(c => c.Header == "_Debug").IsChecked);
        Assert.True(toolbar.IsGroupVisible(ToolbarView.StandardGroup)); // the other group is untouched

        await host.RunAsync(ShellMenus.ToggleToolbar, ToolbarView.DebugGroup);
        Assert.True(await RealAppHost.WaitFor(() => toolbar.IsGroupVisible(ToolbarView.DebugGroup)));
        host.Shell.Menus.Refresh();
        Assert.True(toolbars.Children.Single(c => c.Header == "_Debug").IsChecked);

        // Hiding the Standard group leaves the Debug group first in line (its leading separator hidden).
        await host.RunAsync(ShellMenus.ToggleToolbar, ToolbarView.StandardGroup);
        Assert.True(await RealAppHost.WaitFor(() => !toolbar.IsGroupVisible(ToolbarView.StandardGroup)));
        Assert.True(toolbar.IsGroupVisible(ToolbarView.DebugGroup));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_theme_chosen_in_a_previous_session_is_restored_at_startup()
    {
        await using var host = RealAppHost.Create(h =>
        {
            var path = h.Home.Combine("config", "settings.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"workbench.startupEditor\": \"none\"", "\"workbench.startupEditor\": \"none\",\n  \"workbench.colorTheme\": \"Abyss\"", StringComparison.Ordinal));
        });
        await host.StartAsync();
        Assert.Equal("vscode.abyss", host.Get<IColorThemeService>().Current.Id);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == "vscode.abyss"));
        Assert.Equal("vscode.abyss", (await SettingsPickerAsync(host)).SelectedThemeId);
        // The chrome shows the completed Abyss: its title bar colour on the toolbar (borrowed), not the VS 2022 grey.
        var toolbarBrush = (Avalonia.Media.ISolidColorBrush)host.Shell.Theme!.GetBrush("toolbar.background")!;
        Assert.Equal(Avalonia.Media.Color.Parse("#10192c"), toolbarBrush.Color);
    }
}
