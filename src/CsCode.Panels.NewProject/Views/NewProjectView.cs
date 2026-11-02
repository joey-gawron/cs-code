using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CsCode.Core.Projects;
using CsCode.Panels.NewProject.ViewModels;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.Panels.NewProject.Views;

/// <summary>
/// The New Project page, in the layout of Visual Studio 2022's "Create a new project" and IntelliJ's New Project: the generators on the left,
/// Name, Location (with Browse...), the "Project will be created in …" line, the generator's options and "Create Git repository" on the right,
/// Create and Cancel at the bottom. Keyboard: Tab moves through the page, the arrows choose a generator, Alt+N / Alt+L / Alt+G reach Name,
/// Location and the Git box, Enter creates, Esc cancels a running creation (or closes the page). Disposed by the shell with its tab, which
/// disposes the view model (a running creation is cancelled).
/// </summary>
public sealed class NewProjectView : UserControl, IDisposable, IToolWindowFocus
{
    private static readonly IValueConverter StatusBrush = new FuncValueConverter<bool, IBrush>(error => ThemeBrushes.Get(error ? "errorForeground" : "descriptionForeground"));
    private static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(v => v is string s ? s.Length > 0 : v is not null);

    private const int LabelWidth = 130;

    private readonly NewProjectViewModel _vm;
    private readonly TextBox _name;
    private bool _disposed;

    public NewProjectView(NewProjectViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;

        var title = new TextBlock { Text = "Create a new project", FontSize = 22, FontWeight = FontWeight.Light, Foreground = ThemeBrushes.Get("foreground"), Margin = new Thickness(0, 0, 0, 16) };

        // ---- left: the generators
        var list = new ListBox
        {
            Width = 280,
            ItemsSource = viewModel.Generators,
            ItemTemplate = new FuncDataTemplate<GeneratorItem>((item, _) => GeneratorRow(), supportsRecycling: false),
            Background = ThemeBrushes.Get("sideBar.background"),
            BorderBrush = ThemeBrushes.Get("panel.border"),
            BorderThickness = new Thickness(1),
            [AutomationProperties.NameProperty] = "Project type",
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.IsEnabledProperty, new Binding(nameof(GeneratorItem.IsSelectable))),
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 6)),
            },
        });
        list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(NewProjectViewModel.SelectedGenerator)) { Mode = BindingMode.TwoWay });

        // ---- right: name, location, preview, options, git
        _name = new TextBox { MinHeight = 26, Padding = new Thickness(6, 3), [AutomationProperties.NameProperty] = "Project name" };
        _name.Bind(TextBox.TextProperty, new Binding(nameof(NewProjectViewModel.Name)) { Mode = BindingMode.TwoWay });
        var location = new TextBox { MinHeight = 26, Padding = new Thickness(6, 3), [AutomationProperties.NameProperty] = "Location" };
        location.Bind(TextBox.TextProperty, new Binding(nameof(NewProjectViewModel.Location)) { Mode = BindingMode.TwoWay });
        var browse = new Button { Content = "_Browse...", MinHeight = 26, Padding = new Thickness(12, 3), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, e) => { e.Handled = true; await _vm.BrowseAsync().ConfigureAwait(true); };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 4 };
        Place(form, FieldLabel("_Name:", _name), 0, 0);
        Place(form, _name, 0, 1, span: 2);
        Place(form, Message(nameof(NewProjectViewModel.NameError), "errorForeground"), 1, 1, span: 2);
        Place(form, FieldLabel("_Location:", location), 2, 0);
        Place(form, location, 2, 1);
        Place(form, browse, 2, 2);
        Place(form, Message(nameof(NewProjectViewModel.LocationError), "errorForeground"), 3, 1, span: 2);
        var preview = Message(nameof(NewProjectViewModel.Preview), "descriptionForeground");
        preview.Margin = new Thickness(0, 2, 0, 0);
        Place(form, preview, 4, 1, span: 2);

        var options = new ItemsControl { ItemsSource = viewModel.Options };
        options.DataTemplates.Add(new FuncDataTemplate<ChoiceOptionItem>((_, _) => ChoiceRow(), supportsRecycling: false));
        options.DataTemplates.Add(new FuncDataTemplate<ToggleOptionItem>((_, _) => ToggleRow(), supportsRecycling: false));
        options.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 });
        var optionsHeader = SectionHeader("Options");
        optionsHeader.Bind(IsVisibleProperty, new Binding(nameof(NewProjectViewModel.HasOptions)));

        var git = new CheckBox { Content = "Create _Git repository", Margin = new Thickness(0, 10, 0, 0) };
        git.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(NewProjectViewModel.CreateGitRepository)) { Mode = BindingMode.TwoWay });
        var tool = Message(nameof(NewProjectViewModel.ToolSummary), "descriptionForeground");
        tool.FontSize = 11;

        var right = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(24, 0, 8, 0),
            Children = { form, optionsHeader, options, git, tool },
        };

        // ---- bottom: status, progress, Create and Cancel
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(NewProjectViewModel.Status)));
        status.Bind(TextBlock.ForegroundProperty, new Binding(nameof(NewProjectViewModel.StatusIsError)) { Converter = StatusBrush });
        var progress = new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        progress.Bind(IsVisibleProperty, new Binding(nameof(NewProjectViewModel.IsBusy)));
        var create = new Button { Content = "_Create", MinWidth = 96, MinHeight = 28, HorizontalContentAlignment = HorizontalAlignment.Center };
        create.Classes.Add("accent");
        create.Bind(IsEnabledProperty, new Binding(nameof(NewProjectViewModel.CanCreate)));
        create.Click += async (_, e) => { e.Handled = true; await _vm.CreateAsync().ConfigureAwait(true); };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, MinHeight = 28, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, e) => { e.Handled = true; _vm.Cancel(); };
        var bottom = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { progress, create, cancel } };
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(status);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,*,Auto"), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        Place(body, title, 0, 0, span: 2);
        Place(body, list, 1, 0);
        Place(body, new ScrollViewer { Content = right, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 1, 1);
        Place(body, bottom, 2, 0, span: 2);
        Content = new Border { Background = ThemeBrushes.Get("welcomePage.background"), Padding = new Thickness(40, 28), Child = body };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => FocusPreferred(), DispatcherPriority.Loaded);
    }

    /// <summary>Enter creates (unless a button or an open drop-down takes it), Esc cancels a creation or closes the page.</summary>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.Cancel();
        }
        else if (e.Key == Key.Enter && e.Source is not Button && !(e.Source is ComboBox { IsDropDownOpen: true }))
        {
            e.Handled = true;
            await _vm.CreateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The keyboard starts in the Name box, its text selected, so typing names the project.</summary>
    public bool FocusPreferred()
    {
        if (_disposed || !_name.IsEffectivelyVisible) return false;
        _name.SelectAll();
        return _name.Focus();
    }

    private static Control GeneratorRow()
    {
        var icon = new Icon { Size = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0), Foreground = ThemeBrushes.Get("icon.foreground") };
        icon.Bind(Icon.KindProperty, new Binding(nameof(GeneratorItem.IconKey)));
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = ThemeBrushes.Get("foreground") };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(GeneratorItem.DisplayName)));
        var detail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("descriptionForeground") };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(GeneratorItem.Detail)));
        detail.Bind(ToolTip.TipProperty, new Binding(nameof(GeneratorItem.InstallHint)));
        var text = new StackPanel { Spacing = 1, Children = { name, detail } };
        var row = new DockPanel { Children = { icon, text } };
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        row.Bind(OpacityProperty, new Binding(nameof(GeneratorItem.IsAvailable)) { Converter = new FuncValueConverter<bool, double>(a => a ? 1.0 : 0.7) });
        return row;
    }

    private static Control ChoiceRow()
    {
        var combo = new ComboBox { MinWidth = 260, MinHeight = 26, Padding = new Thickness(6, 2) };
        combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(ChoiceOptionItem.Choices)));
        combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(ChoiceOptionItem.Selected)) { Mode = BindingMode.TwoWay });
        combo.Bind(AutomationProperties.NameProperty, new Binding(nameof(OptionItem.Label)));
        combo.ItemTemplate = new FuncDataTemplate<ProjectOptionChoice>((choice, _) => new TextBlock { Text = choice?.Label }, supportsRecycling: true);
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.Get("foreground") };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(OptionItem.Label)) { StringFormat = "{0}:" });
        var description = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("descriptionForeground") };
        description.Bind(TextBlock.TextProperty, new Binding(nameof(ChoiceOptionItem.SelectedDescription)));
        description.Bind(IsVisibleProperty, new Binding(nameof(ChoiceOptionItem.SelectedDescription)) { Converter = NotNull });
        // The same first column as Name and Location, so every field starts at one edge.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 2 };
        Place(grid, label, 0, 0);
        combo.HorizontalAlignment = HorizontalAlignment.Left;
        Place(grid, combo, 0, 1);
        Place(grid, description, 1, 1);
        return grid;
    }

    private static Control ToggleRow()
    {
        var check = new CheckBox();
        check.Bind(ContentControl.ContentProperty, new Binding(nameof(OptionItem.Label)));
        check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleOptionItem.IsChecked)) { Mode = BindingMode.TwoWay });
        check.Bind(ToolTip.TipProperty, new Binding(nameof(OptionItem.Description)));
        var description = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground") };
        description.Bind(TextBlock.TextProperty, new Binding(nameof(OptionItem.Description)));
        description.Bind(IsVisibleProperty, new Binding(nameof(OptionItem.Description)) { Converter = NotNull });
        return new StackPanel { Spacing = 1, Children = { check, description } };
    }

    private static Label FieldLabel(string text, Control target) =>
        new() { Content = text, Target = target, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Foreground = ThemeBrushes.Get("foreground") };

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = ThemeBrushes.Get("foreground"),
        Margin = new Thickness(0, 10, 0, 0),
    };

    /// <summary>A line bound to <paramref name="path"/> (hidden while empty), in the colour <paramref name="colorId"/>.</summary>
    private static TextBlock Message(string path, string colorId)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ThemeBrushes.Get(colorId) };
        block.Bind(TextBlock.TextProperty, new Binding(path));
        block.Bind(IsVisibleProperty, new Binding(path) { Converter = NotNull });
        return block;
    }

    private static void Place(Grid grid, Control control, int row, int column, int span = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        if (span > 1) Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.Dispose();
        DataContext = null;
    }
}
