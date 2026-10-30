using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>The Welcome page's Recent list over the REAL composition: clicking an entry opens it (no dialog in between).</summary>
public class WelcomePageTests
{
    private static void Click(Control row, TopLevel root) =>
        row.RaiseEvent(new PointerPressedEventArgs(row, new Pointer(1, PointerType.Mouse, true), root, default, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Clicking_a_recent_file_opens_it()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("notes.txt", "remember me\n")).StartAsync();
        var uri = new Uri(host.Files.Combine("notes.txt"));
        host.Get<IStateStorage>().Set(PaletteSource.RecentFilesKey, new List<string> { uri.ToString() }); // opened through Ctrl+P before
        await host.RunAsync(ShellMenus.Welcome);
        var welcome = host.Window.GetVisualDescendants().OfType<WelcomeView>().Single();
        var link = welcome.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "notes.txt");
        Click((Control)link.GetVisualParent()!, host.Window);

        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.Find(uri) is not null), "the recent file was not opened");
        Assert.Same(host.Shell.Well.Find(uri), host.Shell.Well.ActiveEditor); // (it used to show the Open File dialog instead)
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Open_File_with_a_file_argument_opens_it_without_the_dialog()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("given.txt", "x\n")).StartAsync();
        var uri = new Uri(host.Files.Combine("given.txt"));
        await host.RunAsync(KnownCommands.OpenFile, uri.ToString());
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.ActiveEditor?.Uri == uri));
    }
}
