using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>The menu bar's nested submenus in the REAL composition: View &gt; Tool Windows, Toolbars and Appearance; File &gt; Open Recent.</summary>
public class MenuBarTests
{
    [AvaloniaTheory(Timeout = 60_000)]
    [InlineData("_View", "_Tool Windows")]
    [InlineData("_View", "Tool_bars")]
    [InlineData("_View", "_Appearance")]
    [InlineData("_File", "Open _Recent")]
    public async Task A_nested_submenu_opens_with_its_items_and_its_menu_stays_as_it_was(string menuHeader, string submenuHeader)
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "x\n"));
        await host.StartAsync([host.Files.Path]);
        var bar = host.Window.GetVisualDescendants().OfType<ShellMenuBar>().Single();
        var top = bar.Items.OfType<MenuItem>().Single(i => Header(i) == menuHeader);

        top.IsSubMenuOpen = true;
        Settle();
        var nested = top.Items.OfType<MenuItem>().Single(i => Header(i) == submenuHeader);
        var siblings = top.Items.Cast<object>().ToArray();
        Assert.True(nested.ItemCount > 0, $"{submenuHeader} has no items");

        nested.IsSubMenuOpen = true;
        Settle();
        // Opening the nested submenu must not rebuild its parent menu: that would drop the item whose submenu is opening.
        Assert.Equal(siblings, top.Items.Cast<object>().ToArray());
        Assert.True(nested.IsSubMenuOpen, $"{submenuHeader} closed as it opened");
        Assert.True(top.IsSubMenuOpen, $"{menuHeader} closed when {submenuHeader} opened");
        Assert.Contains(nested.Items.OfType<MenuItem>(), i => i.IsEnabled);
        top.IsSubMenuOpen = false;
        Settle();
    }

    // From the desktop: the View menu's items "do not function correctly". Each entry ran its tool's toggle, so one open as a background tab
    // (Output behind Error List) was hidden by the click meant to show it; Visual Studio's View menu only ever shows and activates.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Every_View_Tool_Windows_item_shows_and_activates_its_window_and_never_hides_it()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "x\n"));
        await host.StartAsync([host.Files.Path]);
        var shell = host.Get<ShellController>();
        var view = shell.Menus.Roots.Single(r => r.Header == "_View");
        shell.Menus.Refresh();
        var items = view.Children.Single(c => c.Header == "_Tool Windows").Children;
        Assert.True(items.Count >= 5, $"only {items.Count} tool windows listed");

        foreach (var item in items)
        {
            var id = (string)item.Node.Argument!;
            for (var click = 1; click <= 2; click++) // the second click must not hide it
            {
                await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command!).ExecuteAsync(null);
                Settle();
                Assert.Null(shell.Menus.LastError);
                var tab = shell.Tools.Get(id)!;
                Assert.True(shell.Tools.IsVisible(id), $"View > Tool Windows > {item.Header} (click {click}) left it hidden");
                Assert.True(tab.Owner is not Dock.Model.Core.IDock well || ReferenceEquals(well.ActiveDockable, tab),
                    $"View > Tool Windows > {item.Header} (click {click}) did not bring it to the front");
            }
        }

        // the plain items (Command Palette, Go to File, Notifications) run without error
        foreach (var header in new[] { "_Notifications", "_Command Palette...", "Go to _File..." })
        {
            var item = view.Children.Single(c => c.Header == header);
            Assert.True(item.IsEnabled, header + " is disabled");
            item.Command!.Execute(null);
            Settle();
            Assert.Null(shell.Menus.LastError);
            host.Get<CsCode.App.QuickInput.QuickInputViewModel>().Cancel(); // the palette and Go to File open the quick input
            Settle();
        }
    }

    private static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static string? Header(MenuItem item) => item.Header switch
    {
        AccessText a => a.Text,
        Grid g => g.Children.OfType<AccessText>().FirstOrDefault()?.Text,
        var h => h?.ToString(),
    };
}
