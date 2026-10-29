using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// dropdown on the toolbar and at the top of the Settings page lists every installed theme grouped with swatches, previews while arrowing without
/// persisting, reverts on Escape, applies and persists on Enter or a click, follows every other way of changing the theme, and can be hidden
/// through View &gt; Toolbars.
/// </summary>
public class ThemeDropdownTests
{
    private static ThemePicker Toolbar(RealAppHost host) =>
        host.Window.GetVisualDescendants().OfType<ToolbarView>().Single().ThemePicker ?? throw new InvalidOperationException("no theme dropdown");

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
    public async Task The_toolbar_dropdown_lists_every_installed_theme_grouped_dark_light_high_contrast_with_a_swatch()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = Toolbar(host);
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
        var picker = Toolbar(host);
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
        var picker = Toolbar(host);
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
        var picker = Toolbar(host);
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
        var picker = Toolbar(host);
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
    public async Task The_dropdowns_follow_the_palette_command_and_an_edit_of_settings_json()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var themes = host.Get<IColorThemeService>();
        var picker = Toolbar(host);
        await host.RunAsync(SearchPanelsHost.OpenSettings);
        var settings = host.Window.GetVisualDescendants().OfType<SettingsView>().Single();
        var settingsPicker = settings.ThemePicker!;
        Assert.True(settingsPicker.IsEffectivelyVisible);
        Assert.Equal(VsThemes.DarkId, settingsPicker.SelectedThemeId);

        // The palette's Color Theme command: preview (both dropdowns show it), then choose Solarized Light.
        var quick = host.Get<QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.SelectTheme);
        quick.Text = "Solarized Light";
        await DevScene.SettleAsync(10);
        quick.Accept();
        await running;
        Assert.True(await RealAppHost.WaitFor(() => picker.SelectedThemeId == "vscode.solarized-light" && settingsPicker.SelectedThemeId == "vscode.solarized-light"));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == "Solarized Light"));

        // settings.json edited by hand (or by another window): the platform reloads it, the theme service follows, so do the dropdowns.
        var path = host.Get<IConfigurationService>().UserSettingsPath;
        var text = File.ReadAllText(path).Replace("\"Solarized Light\"", "\"Dracula\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        Assert.True(await RealAppHost.WaitFor(() => themes.Current.Id == "textmatesharp.dracula", 15_000), "the theme follows settings.json");
        Assert.True(await RealAppHost.WaitFor(() => picker.SelectedThemeId == "textmatesharp.dracula" && settingsPicker.SelectedThemeId == "textmatesharp.dracula"));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == "textmatesharp.dracula"));

        // And the Settings page's dropdown drives the toolbar's.
        settingsPicker.SelectedItem = settingsPicker.Choices.Single(c => c.Id == VsThemes.LightId); // closed box: applies directly
        Assert.True(await RealAppHost.WaitFor(() => picker.SelectedThemeId == VsThemes.LightId && themes.Current.Id == VsThemes.LightId));
        Assert.True(await RealAppHost.WaitFor(() => SettingInFile(host) == "Visual Studio Light"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Toolbars_hides_and_shows_the_color_theme_group_and_persists_it()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var toolbar = host.Window.GetVisualDescendants().OfType<ToolbarView>().Single();
        var config = host.Get<IConfigurationService>();
        host.Shell.Menus.Refresh();
        var toolbars = host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children.Single(c => c.Header == "Tool_bars");
        Assert.Equal(["_Standard", "_Debug", "_Color Theme"], toolbars.Children.Select(c => c.Header));
        var entry = toolbars.Children.Single(c => c.Header == "_Color Theme");
        Assert.True(entry.IsChecked);
        Assert.True(toolbar.IsGroupVisible(ToolbarView.ColorThemeGroup));

        await ((IAsyncRelayCommand)entry.Command!).ExecuteAsync(null);
        Assert.True(await RealAppHost.WaitFor(() => !toolbar.IsGroupVisible(ToolbarView.ColorThemeGroup)));
        Assert.False(toolbar.ThemePicker!.IsEffectivelyVisible);
        Assert.False(config.Get("cscode.toolbars.colorTheme", true));
        Assert.Contains("\"cscode.toolbars.colorTheme\": false", File.ReadAllText(config.UserSettingsPath));
        host.Shell.Menus.Refresh();
        Assert.False(toolbars.Children.Single(c => c.Header == "_Color Theme").IsChecked);
        Assert.True(toolbar.IsGroupVisible(ToolbarView.StandardGroup)); // the other groups are untouched

        await host.RunAsync(ShellMenus.ToggleToolbar, ToolbarView.ColorThemeGroup);
        Assert.True(await RealAppHost.WaitFor(() => toolbar.IsGroupVisible(ToolbarView.ColorThemeGroup)));
        host.Shell.Menus.Refresh();
        Assert.True(toolbars.Children.Single(c => c.Header == "_Color Theme").IsChecked);

        // Hiding the Standard group too leaves the Debug group first in line (its leading separator hidden).
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
        Assert.Equal("vscode.abyss", Toolbar(host).SelectedThemeId);
        // The chrome shows the completed Abyss: its title bar colour on the toolbar (borrowed), not the VS 2022 grey.
        var toolbarBrush = (Avalonia.Media.ISolidColorBrush)host.Shell.Theme!.GetBrush("toolbar.background")!;
        Assert.Equal(Avalonia.Media.Color.Parse("#10192c"), toolbarBrush.Color);
    }
}
