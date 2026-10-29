using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.Core.Platform;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>The Start window shown when no folder is open: recent folders and files, Open Folder, New File and help links (VS2022 "Start window" inspired).</summary>
public sealed class WelcomeView : UserControl
{
    public WelcomeView(ICommandService commands, IWorkspaceService? workspace, IStateStorage? state)
    {
        Control Link(string text, string? icon, Func<Task> action, string? subtitle = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3) };
            if (icon is not null) row.Children.Add(ViewHelpers.Glyph(icon, 16, "textLink.foreground"));
            var label = new TextBlock { Text = text };
            label.Classes.Add("link");
            row.Children.Add(label);
            if (subtitle is not null) row.Children.Add(ViewHelpers.Label(subtitle, "descriptionForeground", 11));
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.Background = Brushes.Transparent;
            row.PointerPressed += async (_, e) => { e.Handled = true; await action().ConfigureAwait(true); };
            return row;
        }
        Task Run(string id, params object?[] args) => commands.Has(id) ? commands.ExecuteAsync(id, args).AsTask() : Task.CompletedTask;

        var start = new StackPanel { Spacing = 2, Width = 360 };
        start.Children.Add(ViewHelpers.Label("Start", "foreground", 16, FontWeight.SemiBold));
        start.Children.Add(Link("Open a folder", IconKey.FolderOpened, () => Run(KnownCommands.OpenFolder)));
        start.Children.Add(Link("Open a file", IconKey.File, () => Run(KnownCommands.OpenFile)));
        start.Children.Add(Link("New file", IconKey.NewFile, () => Run(KnownCommands.NewUntitledFile)));
        start.Children.Add(Link("Command palette", "terminal", () => Run(KnownCommands.CommandPalette)));

        var recent = new StackPanel { Spacing = 2, Width = 420 };
        recent.Children.Add(ViewHelpers.Label("Recent", "foreground", 16, FontWeight.SemiBold));
        var folders = workspace?.RecentFolders.Take(8).ToArray() ?? [];
        if (folders.Length == 0) recent.Children.Add(ViewHelpers.Label("No recent folders", "descriptionForeground"));
        foreach (var f in folders)
        {
            var path = f.IsFile ? f.LocalPath : f.ToString();
            recent.Children.Add(Link(Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path, IconKey.Folder, () => Run(ShellMenus.OpenRecent, f.ToString()), Path.GetDirectoryName(path.TrimEnd('/', '\\'))));
        }
        var files = state?.Get<List<string>>(PaletteSource.RecentFilesKey) ?? [];
        foreach (var s in files.Take(5))
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) && u.IsFile)
                recent.Children.Add(Link(Path.GetFileName(u.LocalPath), IconKey.File, () => Run(KnownCommands.OpenFile, u.ToString()), Path.GetDirectoryName(u.LocalPath)));

        var help = new StackPanel { Spacing = 2, Margin = new Thickness(0, 24, 0, 0) };
        help.Children.Add(ViewHelpers.Label("Help", "foreground", 16, FontWeight.SemiBold));
        help.Children.Add(Link("Keyboard shortcuts reference", IconKey.Key, () => Run(ShellMenus.KeyboardReference)));
        help.Children.Add(Link("About CsCode", "info", () => Run(ShellMenus.About)));
        start.Children.Add(help);

        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 28), Children =
        {
            new TextBlock { Text = "CsCode", FontSize = 32, FontWeight = FontWeight.Light, Foreground = ThemeBrushes.Get("foreground") },
            ViewHelpers.Label("A classic coding IDE", "descriptionForeground", 13),
        } };

        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 60 };
        columns.Children.Add(start);
        columns.Children.Add(recent);
        Content = new ScrollViewer
        {
            Background = ThemeBrushes.Get("welcomePage.background"),
            Content = new StackPanel { Margin = new Thickness(56, 40), Children = { title, columns } },
        };
    }
}

/// <summary>Keyboard shortcut reference page, generated from <see cref="IKeybindingService.All"/> with a filter box.</summary>
public sealed class KeyboardReferenceView : UserControl
{
    private sealed record Row(string Command, string Title, string Keys, string? When, string Source);

    public KeyboardReferenceView(IKeybindingService? bindings, ICommandService commands)
    {
        var titles = commands.All.Where(c => c.Title is not null).ToDictionary(c => c.Id, c => c.Title!);
        var rows = (bindings?.All ?? [])
            .Where(b => !b.IsNegative)
            .Select(b => new Row(b.Command, titles.GetValueOrDefault(b.Command, b.Command), string.Join(", ", b.Chord.Select(s => s.ToString())), b.When, b.Source.ToString()))
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();

        var list = new ListBox { ItemsSource = rows, ItemTemplate = new FuncDataTemplate<Row>((r, _) => RowFor(r), supportsRecycling: true) };
        var filter = new FilterBox { Watermark = "Search keybindings", Margin = new Thickness(12, 8), Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        filter.GetObservable(FilterBox.TextProperty).Subscribe(new TextObserver(t =>
            list.ItemsSource = string.IsNullOrWhiteSpace(t) ? rows : rows.Where(r => r.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Keys.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Command.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList()));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,*,90"), Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background") };
        foreach (var (text, col) in new[] { ("Command", 0), ("Keybinding", 1), ("When", 2), ("Source", 3) })
        {
            var t = ViewHelpers.Label(text, "sideBarSectionHeader.foreground", 11, FontWeight.SemiBold);
            t.Margin = new Thickness(8, 0);
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = ThemeBrushes.Get("editor.background") };
        root.Children.Add(filter);
        Grid.SetRow(header, 1);
        root.Children.Add(header);
        Grid.SetRow(list, 2);
        root.Children.Add(list);
        Content = root;
    }

    private static Control RowFor(Row r)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,*,90"), Height = 22 };
        void Cell(string? text, int col, string color = "foreground")
        {
            var t = ViewHelpers.Label(text ?? "", color);
            t.Margin = new Thickness(8, 0);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Cell(r.Title, 0);
        Cell(r.Keys, 1, "textLink.foreground");
        Cell(r.When, 2, "descriptionForeground");
        Cell(r.Source, 3, "descriptionForeground");
        return grid;
    }

    private sealed class TextObserver(Action<string> onNext) : IObserver<string>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(string value) => onNext(value);
    }
}
