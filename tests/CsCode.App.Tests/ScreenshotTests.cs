using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Platform;
using CsCode.Ui.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>Renders the full shell in every built-in theme and in the interesting states. PNGs land in .scratch/screenshots (git-ignored).</summary>
public class ScreenshotTests
{
    [AvaloniaTheory]
    [InlineData(VsThemes.DarkId, "dark")]
    [InlineData(VsThemes.BlueId, "blue")]
    [InlineData(VsThemes.LightId, "light")]
    public async Task Main_window_in_each_theme(string themeId, string name)
    {
        using var host = new DevHost().Build();
        await host.Themes.SetCurrentAsync(themeId);
        await host.BuildAsync();
        var path = host.Capture($"main-{name}");
        Assert.True(new FileInfo(path).Length > 20_000, "the rendered frame looks empty");
    }

    [AvaloniaFact]
    public async Task Command_palette_open()
    {
        using var host = new DevHost().Build();
        await host.BuildAsync();
        _ = host.Commands.ExecuteAsync(KnownCommands.CommandPalette);
        await DevScene.SettleAsync();
        host.Capture("palette-commands");
    }

    [AvaloniaFact]
    public async Task File_menu_open()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "the in-window menu bar; macOS shows the menus in the system menu bar");
        using var host = new DevHost().Build();
        await host.BuildAsync();
        var menu = host.Window.GetVisualDescendants().OfType<Views.ShellMenuBar>().Single();
        ((MenuItem)menu.Items[0]!).IsSubMenuOpen = true;
        await DevScene.SettleAsync();
        host.Capture("menu-file");
    }

    [AvaloniaFact]
    public async Task Debugging_state_turns_the_status_bar_orange_and_shows_step_buttons()
    {
        using var host = new DevHost().Build();
        await host.BuildAsync(debugging: true);
        host.Capture("main-debugging");
        var bar = host.Window.GetVisualDescendants().OfType<Views.StatusBarView>().Single();
        Assert.Equal(Views.StatusBarState.Debugging, bar.State);
    }

    [AvaloniaFact]
    public async Task Notifications_and_dialog()
    {
        using var host = new DevHost().Build();
        await host.BuildAsync();
        var window = host.Services.GetRequiredService<IWindowService>();
        _ = window.ShowMessageAsync(MessageSeverity.Error, "The language server for C# crashed and was restarted.", [new MessageAction("Show Log"), new MessageAction("Dismiss")]);
        _ = window.ShowMessageAsync(MessageSeverity.Warning, "Project file changed on disk.");
        _ = window.ShowMessageAsync(MessageSeverity.Info, "Indexing workspace...");
        await DevScene.SettleAsync();
        host.Capture("notifications");
        _ = host.Services.GetRequiredService<Notifications.IModalDialogs>().ShowAsync("Save changes", "Do you want to save the changes you made to Program.cs?\n\nYour changes will be lost if you don't save them.", ["Save", "Don't Save", "Cancel"]);
        await DevScene.SettleAsync();
        host.Capture("dialog");
    }

    [AvaloniaFact]
    public async Task Welcome_page_without_a_folder()
    {
        using var host = new DevHost().Build();
        await host.ShowAsync();
        await DevScene.SettleAsync();
        host.Capture("welcome");
    }
}
