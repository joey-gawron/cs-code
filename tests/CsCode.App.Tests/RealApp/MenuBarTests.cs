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
