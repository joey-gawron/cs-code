using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.Tests.ViewModels;
using CsCode.Panels.Search.ViewModels;
using CsCode.Panels.Search.Views;
using Xunit;

namespace CsCode.Panels.Search.Tests.Ui;

/// <summary>The Settings page's Remote tab in the real view: shown with the host's name while connected, absent otherwise, and it switches the scope.</summary>
public class RemoteSettingsViewTests
{
    private static (UiFixture F, SettingsViewModel Vm, Window Window, SettingsView View) Open(FakeRemoteMachineSettings? remote)
    {
        UiSupport.EnsureTheme();
        var f = new UiFixture();
        f.Harness.Config.RegisterProperties(
        [
            new ConfigurationProperty("editor.tabSize", JsonSerializer.SerializeToElement(4), "The number of spaces a tab is equal to.", JsonDocument.Parse("""{"type":"integer","minimum":1}""").RootElement.Clone()),
            new ConfigurationProperty("cscode.debug.gdbPath", JsonSerializer.SerializeToElement("gdb"), "The gdb this machine runs.", JsonDocument.Parse("""{"type":"string"}""").RootElement.Clone())
                { Scope = ConfigurationScope.Machine },
        ]);
        var s = f.Services;
        var services = new SearchServices
        {
            Workspace = s.Workspace, Ui = s.Ui, Engine = s.Engine, Replace = s.Replace, Configuration = s.Configuration, Documents = s.Documents,
            Navigation = s.Navigation, Windows = s.Windows, State = s.State, Commands = s.Commands, Keybindings = s.Keybindings, FileSystem = s.FileSystem,
            RemoteSettings = remote,
        };
        var vm = new SettingsViewModel(services, debounceMs: 20);
        var view = new SettingsView(vm);
        return (f, vm, UiSupport.Host(view, 980, 600), view);
    }

    private static ToggleButton? RemoteTab(SettingsView view) =>
        view.Descendants<ToggleButton>().SingleOrDefault(t => Equals(t.GetValue(Avalonia.Automation.AutomationProperties.NameProperty), "Remote settings"));

    [AvaloniaFact]
    public async Task The_remote_tab_shows_the_host_and_switches_the_page_to_its_settings()
    {
        using var dir = new CsCode.TestDoubles.TempDirectory("cscode-remote-tab-");
        var remote = new FakeRemoteMachineSettings(dir.Combine("machine", "settings.json"));
        remote.ChangedOnHost("{ \"cscode.debug.gdbPath\": \"/opt/gdb-14/bin/gdb\" }");
        var (f, vm, window, view) = Open(remote);
        using (f)
        {
            var tab = RemoteTab(view);
            Assert.NotNull(tab);
            Assert.True(tab!.IsVisible);
            Assert.Equal("Remote [SSH: box]", tab.Content);
            tab.IsChecked = true;                                            // what a click does
            Assert.Equal(SettingsScope.Remote, vm.SelectedScope);
            await UiSupport.WaitUntilAsync(() => ((StringSettingViewModel)vm.AllItems.Single(i => i.Key == "cscode.debug.gdbPath")).Value == "/opt/gdb-14/bin/gdb");
            UiSupport.Pump();
            UiSupport.Screenshot(window, "settings-remote-tab");

            remote.SetConnected(false);
            await UiSupport.WaitUntilAsync(() => !tab.IsVisible);
            Assert.Equal(SettingsScope.User, vm.SelectedScope);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Without_remote_editing_there_is_no_remote_tab()
    {
        var (f, _, window, view) = Open(remote: null);
        using (f)
        {
            Assert.False(RemoteTab(view)?.IsVisible ?? false);
            window.Close();
        }
    }
}
