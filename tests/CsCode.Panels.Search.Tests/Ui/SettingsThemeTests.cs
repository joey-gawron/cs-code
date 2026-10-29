using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.ViewModels;
using CsCode.Panels.Search.Views;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Search.Tests.Ui;

/// <summary>
/// A1: the Color Theme dropdown at the top of the Settings page (Environment &gt; Appearance, where Visual Studio has it). It is the toolbar's
/// control (<see cref="ThemePicker"/>); the real composition's behaviour (preview, revert, sync with the toolbar) is covered by the App's
/// RealApp ThemeDropdownTests. These tests cover the page: the section exists only with a theme service, the dropdown applies and persists,
/// follows outside changes, and is released with the page.
/// </summary>
public class SettingsThemeTests
{
    private static SearchServices WithThemes(UiFixture f, IColorThemeService? themes) => new()
    {
        Workspace = f.Services.Workspace, Ui = f.Services.Ui, Engine = f.Services.Engine, Replace = f.Services.Replace, Configuration = f.Services.Configuration,
        Documents = f.Services.Documents, Navigation = f.Services.Navigation, Windows = f.Services.Windows, State = f.Services.State,
        Commands = f.Services.Commands, Keybindings = f.Services.Keybindings, ContextKeys = f.Services.ContextKeys, FileSystem = f.Services.FileSystem,
        Themes = themes,
    };

    [AvaloniaFact]
    public async Task The_appearance_section_hosts_the_color_theme_dropdown_which_applies_and_persists_the_label()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        var themes = new StaticThemeService();
        var vm = new SettingsViewModel(WithThemes(f, themes), debounceMs: 20);
        using var view = new SettingsView(vm);
        var window = UiSupport.Host(view, 900, 600);
        try
        {
            var picker = Assert.IsType<ThemePicker>(view.ThemePicker);
            Assert.True(picker.IsEffectivelyVisible);
            Assert.True(view.FindControl<Border>("AppearanceSection")!.IsVisible);
            Assert.Equal(VsThemes.All.Select(t => t.Id).Order(), picker.Choices.Select(c => c.Id).Order());
            Assert.Equal(themes.Current.Id, picker.SelectedThemeId);
            UiSupport.Screenshot(window, "settings-appearance");

            // The closed box applies a choice at once and writes workbench.colorTheme (the label, as VS Code stores it).
            picker.SelectedItem = picker.Choices.Single(c => c.Id == VsThemes.LightId);
            await UiSupport.WaitUntilAsync(() => themes.Current.Id == VsThemes.LightId);
            await UiSupport.WaitUntilAsync(() => f.Configuration.Writes.Any(w => w.Key == SettingKeys.WorkbenchColorTheme));
            Assert.Contains(f.Configuration.Writes, w => w.Key == SettingKeys.WorkbenchColorTheme && Equals(w.Value, VsThemes.Light.Label));

            // A change made elsewhere (the palette, the toolbar's dropdown, settings.json) shows in the page's dropdown.
            await themes.SetCurrentAsync(VsThemes.BlueId);
            await UiSupport.WaitUntilAsync(() => picker.SelectedThemeId == VsThemes.BlueId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Without_a_theme_service_the_section_is_not_shown()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        var vm = new SettingsViewModel(f.Services, debounceMs: 20);
        using var view = new SettingsView(vm);
        var window = UiSupport.Host(view, 900, 600);
        try
        {
            Assert.Null(view.ThemePicker);
            Assert.Null(vm.Themes);
            Assert.False(view.FindControl<Border>("AppearanceSection")!.IsVisible);
            Assert.Empty(view.Descendants<ThemePicker>());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_the_settings_page_releases_its_theme_dropdown()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        var themes = new StaticThemeService(); // outlives the pages, as the real theme service does
        await LeakAssert.NotRetainedAsync(() =>
        {
            var kept = new List<object>();
            for (var i = 0; i < 10; i++)
            {
                var vm = new SettingsViewModel(WithThemes(f, themes), debounceMs: 5);
                var view = new SettingsView(vm);
                var window = UiSupport.Host(view, 600, 400);
                window.Close();
                kept.Add(view.ThemePicker!);
                view.Dispose();
                kept.Add(view);
                kept.Add(vm);
            }
            UiSupport.Pump();
            return Task.FromResult(kept.ToArray());
        });
        // Theme changes after the pages are gone reach no disposed dropdown.
        await themes.SetCurrentAsync(VsThemes.LightId);
        UiSupport.Pump();
    }
}
