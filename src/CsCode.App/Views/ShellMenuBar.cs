using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Controls;

using Avalonia.Layout;
using Avalonia.Media;
using CsCode.App.Menus;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>
/// The File ... Help menu bar. Items are (re)generated from the <see cref="MenuModel"/> every time a menu opens, so enablement, check marks,
/// shortcut labels, recent folders and the tool-window list are always current without per-item subscriptions. Alt mnemonics work through
/// <see cref="AccessText"/>; arrow-key navigation is Avalonia's built-in menu behaviour.
/// </summary>
public sealed class ShellMenuBar : Menu
{
    private readonly MenuModel _model;

    // Control themes are looked up by exact type: without this the subclass would have no template and render nothing.
    protected override Type StyleKeyOverride => typeof(Menu);

    public ShellMenuBar(MenuModel model)
    {
        _model = model;
        Background = Brushes.Transparent;
        foreach (var root in model.Roots)
        {
            // Top-level items sit on the title bar, not on the popup surface: transparent background and the title-bar foreground.
            var top = new MenuItem { Header = new AccessText { Text = root.Header }, Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.Get("titleBar.activeForeground") };
            top.Resources["MenuFlyoutItemBackground"] = Brushes.Transparent;
            Populate(top, root);
            var captured = root;
            top.SubmenuOpened += (_, e) =>
            {
                // SubmenuOpened bubbles: a nested submenu opening (View > Tool Windows) reaches this handler too, and rebuilding the menu
                // then would detach the very item whose submenu is opening (nested submenus never showed).
                if (e.Source != top) return;
                _model.Refresh();
                Populate(top, captured);
            };
            Items.Add(top);
        }
    }

    private static void Populate(MenuItem target, MenuItemVm vm)
    {
        target.Items.Clear();
        foreach (var child in vm.Children)
        {
            if (child.IsSeparator) { target.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = HeaderFor(child), IsEnabled = child.IsEnabled, Command = child.Command };
            if (child.IconKey is { } icon && IconCatalog.Contains(icon)) item.Icon = new Icon(icon, 14);
            if (child.IsChecked)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }
            if (child.HasChildren) Populate(item, child);
            target.Items.Add(item);
        }
    }

    private static Control HeaderFor(MenuItemVm vm)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 220 };
        grid.Children.Add(new AccessText { Text = vm.Header, VerticalAlignment = VerticalAlignment.Center });
        if (vm.Gesture is { Length: > 0 } g)
        {
            var gesture = new TextBlock { Text = g, Margin = new Thickness(32, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(gesture, 1);
            grid.Children.Add(gesture);
        }
        return grid;
    }
}
