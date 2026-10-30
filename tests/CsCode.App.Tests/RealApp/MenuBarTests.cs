using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Core.Text;
using CsCode.Panels.Terminal.Views;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The menu bar in the REAL composition: the nested submenus (View &gt; Tool Windows, Toolbars and Appearance; File &gt; Open Recent) and what
/// View &gt; Tool Windows does with the window and the keyboard. Tests that show the Terminal window (which starts a terminal) run a sandboxed
/// bash: no rc file or config of the user's is read.
/// </summary>
public class MenuBarTests
{
    private static RealAppHost Create(TempDirectory sandbox) => RealAppHost.Create(h =>
    {
        h.Files.WriteFile("a.txt", "alpha beta\n");
        foreach (var d in new[] { "home", "config", "data" }) Directory.CreateDirectory(sandbox.Combine(d));
        var settings = new Dictionary<string, object?>
        {
            ["cscode.languageServers"] = new[] { "typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua" }
                .ToDictionary(s => s, _ => (object)new { enabled = false }),
            ["workbench.startupEditor"] = "none",
            ["terminal.integrated.profiles.linux"] = new Dictionary<string, object>
            {
                ["sandboxed"] = new
                {
                    path = File.Exists("/bin/bash") ? "/bin/bash" : "bash",
                    args = new[] { "--noprofile", "--norc" },
                    env = new Dictionary<string, string?>
                    {
                        ["HOME"] = sandbox.Combine("home"), ["XDG_CONFIG_HOME"] = sandbox.Combine("config"), ["XDG_DATA_HOME"] = sandbox.Combine("data"),
                        ["ZDOTDIR"] = sandbox.Combine("home"), ["PS1"] = "$ ", ["BASH_ENV"] = null, ["ENV"] = null, ["PROMPT_COMMAND"] = null,
                        ["HISTFILE"] = sandbox.Combine("home", ".bash_history"),
                    },
                },
            },
            ["terminal.integrated.defaultProfile.linux"] = "sandboxed",
            ["terminal.integrated.cwd"] = sandbox.Combine("home"),
            ["terminal.integrated.shellIntegration.enabled"] = false,
        };
        h.Home.WriteFile("config/settings.json", JsonSerializer.Serialize(settings));
    });

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
    // (Output behind Error List) was hidden by the click meant to show it; Visual Studio's View menu only ever shows and activates. And it
    // focuses: the window shown alone left the keyboard in the editor, so what the user typed next went into the file. And not on chrome (found
    // in review): the first element in tab order put it on Locals' column splitter (→ resized the columns) and on Clear Console (Enter cleared
    // the console), where Visual Studio puts it in the window's list or input.
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Every_View_Tool_Windows_item_shows_activates_and_focuses_its_window_and_never_hides_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sandboxed shell is bash (Linux/macOS)");
        using var sandbox = new TempDirectory("cscode-menubar-sandbox-");
        await using var host = Create(sandbox);
        await host.StartAsync([host.Files.Path]);
        var shell = host.Get<ShellController>();
        var editor = await shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        var view = shell.Menus.Roots.Single(r => r.Header == "_View");
        shell.Menus.Refresh();
        var items = view.Children.Single(c => c.Header == "_Tool Windows").Children;
        Assert.True(items.Count >= 5, $"only {items.Count} tool windows listed");

        foreach (var item in items)
        {
            var id = (string)item.Node.Argument!;
            for (var click = 1; click <= 2; click++) // the second click must not hide it
            {
                editor.Controller!.Focus();
                await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command!).ExecuteAsync(null);
                Settle();
                Assert.Null(shell.Menus.LastError);
                var tab = shell.Tools.Get(id)!;
                Assert.True(shell.Tools.IsVisible(id), $"View > Tool Windows > {item.Header} (click {click}) left it hidden");
                Assert.True(tab.Owner is not Dock.Model.Core.IDock well || ReferenceEquals(well.ActiveDockable, tab),
                    $"View > Tool Windows > {item.Header} (click {click}) did not bring it to the front");
                // In the view, or on the host around it when nothing in the view can take the keyboard (the Call Stack outside a debug session).
                Assert.True(await RealAppHost.WaitFor(() => (tab.LiveView?.Parent as Control)?.IsKeyboardFocusWithin == true, 20_000),
                    $"View > Tool Windows > {item.Header} (click {click}) left the keyboard on {Focused(host)}");
                // A ToggleButton (a CheckBox too) is a Button.
                Assert.False(host.Window.FocusManager?.GetFocusedElement() is Button or GridSplitter or ComboBox,
                    $"View > Tool Windows > {item.Header} (click {click}) put the keyboard on {Focused(host)}, where Enter presses it, the arrows drag it or switch it");
            }
        }
        Assert.Single(host.Get<ITerminalService>().Terminals); // the Terminal's view and its focus command did not each start one

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

    // Before, the Terminal item ran the Terminal's toggle, which created a terminal when there was none and focused it; the window shown alone
    // left the keyboard in the editor, so the command typed next went into the file.
    [AvaloniaFact(Timeout = 120_000)]
    public async Task View_Tool_Windows_Terminal_puts_the_keyboard_in_a_terminal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sandboxed shell is bash (Linux/macOS)");
        using var sandbox = new TempDirectory("cscode-menubar-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Terminal");

        Assert.True(await RealAppHost.WaitFor(() => host.Window.GetVisualDescendants().OfType<TerminalControl>().Any(c => c.IsKeyboardFocusWithin), 20_000),
            $"the keyboard stayed on {Focused(host)}");
        Assert.True(host.Get<IContextKeyService>().Get("terminalFocus") is true);
        Assert.Single(host.Get<ITerminalService>().Terminals);
        await host.RunAsync("workbench.action.terminal.kill");
    }

    // Before, the Find in Files item ran workbench.action.findInFiles: the query seeded from the word at the caret and the query box focused.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_Find_in_Files_seeds_the_query_from_the_editor_and_focuses_the_query_box()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n")).StartAsync();
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        editor.Controller!.SetSelection(TextRange.At(0, 8), reveal: true); // in "beta"
        editor.Controller.Focus();

        await ClickToolWindowItemAsync(host, "Find in Files");

        TextBox? Query() => host.Shell.Tools.Get("search")?.LiveView?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "QueryBox");
        Assert.True(await RealAppHost.WaitFor(() => Query()?.IsFocused == true), $"the keyboard stayed on {Focused(host)}");
        Assert.Equal("beta", Query()!.Text);
    }

    // Found in review: the Debug Console's first element in tab order is its Clear Console button, so the View menu left the keyboard there; the
    // next Enter cleared the console (and the history kept for late subscribers) and an expression typed went nowhere. Visual Studio's
    // Immediate window takes the keyboard in its input.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_Debug_Console_puts_the_keyboard_in_its_input_box()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n")).StartAsync();
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Debug Console");

        TextBox? Input() => host.Shell.Tools.Get("debug.console")?.LiveView?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "Input");
        Assert.True(await RealAppHost.WaitFor(() => Input()?.IsFocused == true), $"the keyboard is on {Focused(host)}");
        Assert.False(editor.Controller.IsFocused);
    }

    // A tool with no focus logic of its own (no ShowCommandId) gets the keyboard where its view says: the Explorer's selected row, where the
    // arrows, Enter and F2 work, not the New File button that leads its tab order.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_Explorer_puts_the_keyboard_on_a_file_row()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n"));
        await host.StartAsync([host.Files.Path]);
        var explorer = host.Shell.Tools.Get("explorer")!;
        ListBoxItem? Row(string name) => explorer.LiveView?.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(r => r.IsEffectivelyVisible && r.DataContext is ToolWindows.Explorer.ExplorerNode { Name: var n } && n == name);
        Assert.True(await RealAppHost.WaitFor(() => Row("a.txt") is not null), "no a.txt row in the Explorer");
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Explorer");

        Assert.True(await RealAppHost.WaitFor(() => explorer.LiveView?.IsKeyboardFocusWithin == true), $"the keyboard stayed on {Focused(host)}");
        Assert.IsType<ListBoxItem>(host.Window.FocusManager?.GetFocusedElement());
        Assert.Equal(CsCode.App.Input.FocusArea.Explorer, host.Shell.ContextKeys.Focus);
        Assert.False(editor.Controller.IsFocused);
    }

    // Found in review: reopening a closed Explorer, the item's main use, left the keyboard nowhere. Dock lays the new view out a pass later, by
    // when the folder's root row is there and takes the keyboard; then the root's children arrive as one bulk change (a Reset), which recycles
    // every row, the focused one too, and Avalonia moves the keyboard to nothing: the arrows, Enter and typing went nowhere, where before the
    // item it was at least in the editor. When Dock was quicker than the folder's first load, there was no row yet and the keyboard went to the
    // filter box instead.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_Explorer_reopened_keeps_the_keyboard_on_a_row_while_its_folder_loads()
    {
        await using var host = RealAppHost.Create(h => { h.Files.WriteFile("a.txt", "alpha beta\n"); h.Files.WriteFile("b.txt", "gamma\n"); });
        await host.StartAsync([host.Files.Path]);
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        host.Shell.Tools.Hide("explorer");
        await DevScene.SettleAsync(10);
        Assert.Null(host.Shell.Tools.Get("explorer")!.LiveView); // closed: the item creates its view and loads the folder again
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Explorer");

        var explorer = host.Shell.Tools.Get("explorer")!;
        Assert.True(await RealAppHost.WaitFor(() => explorer.LiveView?.GetVisualDescendants().OfType<ListBoxItem>()
            .Any(r => r.IsEffectivelyVisible && r.DataContext is ToolWindows.Explorer.ExplorerNode { Name: "b.txt" }) == true), "the folder's files never showed");
        await DevScene.SettleAsync(10);
        var focused = host.Window.FocusManager?.GetFocusedElement();
        Assert.True(focused is ListBoxItem row && explorer.LiveView!.IsVisualAncestorOf(row), $"the keyboard is on {Focused(host)}");
        Assert.False(editor.Controller.IsFocused);
    }

    // Found in review: with no line (an empty channel, or after Clear Output) the Output window has no row to take the keyboard, and tab order
    // put it on the channel picker, where Down switched the channel and a letter searched the channels. Visual Studio puts it in the window.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_Output_with_no_line_puts_the_keyboard_in_the_window_not_on_the_channel_picker()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n")).StartAsync();
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        host.Get<IOutputService>().GetOrCreate("Tests").AppendLine("x"); // a channel nothing else writes to, so it stays empty once cleared
        host.Shell.Tools.Show("output");
        var output = host.Shell.Tools.Get("output")!;
        Assert.True(await RealAppHost.WaitFor(() => output.LiveView is { } v && v.IsAttachedToVisualTree()));
        var model = (ToolWindows.Output.OutputViewModel)output.LiveView!.DataContext!;
        model.SelectedChannel = "Tests";
        await DevScene.SettleAsync(5);
        model.Clear();
        Assert.True(await RealAppHost.WaitFor(() => model.Lines.Count == 0 && !output.LiveView.GetVisualDescendants().OfType<ListBoxItem>().Any(r => r.IsEffectivelyVisible)));
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Output");

        Assert.True(await RealAppHost.WaitFor(() => (output.LiveView.Parent as Control)?.IsKeyboardFocusWithin == true), $"the keyboard is on {Focused(host)}");
        Assert.False(host.Window.FocusManager?.GetFocusedElement() is ComboBox, $"the keyboard is on {Focused(host)}, where Down switches the channel");
        host.Window.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
        await DevScene.SettleAsync(5);
        Assert.Equal("Tests", model.SelectedChannel);
    }

    // Found in review: a tool with neither a ShowCommandId nor IToolWindowFocus got the keyboard on its first element in tab order, which in a
    // window with a toolbar or a column header is a button (Enter presses it) or a splitter (the arrows drag it). The shell's fallback skips
    // them for the window's content, and with nothing else focuses the window itself.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_puts_the_keyboard_of_a_tool_without_focus_logic_past_its_toolbar_and_column_splitter()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n")).StartAsync([], s =>
        {
            s.AddSingleton<CsCode.Ui.IToolWindow>(new ChromeFirstTool("test.content", "Chrome Then Content", withContent: true));
            s.AddSingleton<CsCode.Ui.IToolWindow>(new ChromeFirstTool("test.chrome", "Chrome Only", withContent: false));
        });
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));

        editor.Controller!.Focus();
        await ClickToolWindowItemAsync(host, "Chrome Then Content");
        var content = host.Shell.Tools.Get("test.content")!;
        Assert.True(await RealAppHost.WaitFor(() => content.LiveView?.IsKeyboardFocusWithin == true), $"the keyboard is on {Focused(host)}");
        Assert.Equal("Content", (host.Window.FocusManager?.GetFocusedElement() as Control)?.Name);

        editor.Controller.Focus();
        await ClickToolWindowItemAsync(host, "Chrome Only");
        var chrome = host.Shell.Tools.Get("test.chrome")!;
        Assert.True(await RealAppHost.WaitFor(() => (chrome.LiveView?.Parent as Control)?.IsKeyboardFocusWithin == true), $"the keyboard is on {Focused(host)}");
        Assert.Same(chrome.LiveView!.Parent, host.Window.FocusManager?.GetFocusedElement()); // the window itself, not one of its buttons
    }

    // Found in review: an Explorer just created has no row until its folder loads, after its first frame; when Dock laid the view out first, the
    // shell asked it at once, it found no row, and tab order put the keyboard in its filter box. A view whose content is pending is asked once it
    // has come.
    [AvaloniaFact(Timeout = 60_000)]
    public async Task View_Tool_Windows_asks_a_view_whose_content_is_pending_once_it_has_come_instead_of_falling_back_to_tab_order()
    {
        await using var host = await RealAppHost.Create(h => h.Files.WriteFile("a.txt", "alpha beta\n")).StartAsync([], s =>
            s.AddSingleton<CsCode.Ui.IToolWindow>(new PendingContentTool()));
        var editor = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => editor.Controller is not null && editor.LiveView!.IsAttachedToVisualTree()));
        editor.Controller!.Focus();

        await ClickToolWindowItemAsync(host, "Pending Content");

        var view = Assert.IsType<PendingContentView>(host.Shell.Tools.Get("test.pending")!.LiveView);
        Assert.True(await RealAppHost.WaitFor(() => view.IsAttachedToVisualTree() && view.IsEffectivelyVisible));
        await DevScene.SettleAsync(5);
        Assert.False(view.Filter.IsFocused, "the keyboard went to the filter box while the content was on its way");
        view.Content.IsVisible = true; // the content comes
        Assert.True(await RealAppHost.WaitFor(() => view.Content.IsFocused), $"the keyboard is on {Focused(host)}");
    }

    private sealed class PendingContentTool : CsCode.Ui.IToolWindow
    {
        public string Id => "test.pending";
        public string Title => "Pending Content";
        public string? IconKey => "files";
        public CsCode.Ui.ToolWindowLocation DefaultLocation => CsCode.Ui.ToolWindowLocation.Bottom;
        public string? ToggleCommandId => "test.toggle.pending";
        public string? AutoShowWhen => null;
        public Control CreateView(IServiceProvider services) => new PendingContentView();
    }

    /// <summary>As the Explorer: a filter box (first in tab order), then the content it prefers, which comes after the view is shown.</summary>
    private sealed class PendingContentView : StackPanel, CsCode.Ui.IToolWindowFocus
    {
        public TextBox Filter { get; } = new() { Name = "Filter" };
        public TextBox Content { get; } = new() { Name = "Content", IsVisible = false };
        public PendingContentView() { Children.Add(Filter); Children.Add(Content); }
        public bool FocusPreferred() => Content.IsVisible && Content.Focus();
        public bool ContentPending => !Content.IsVisible;
    }

    /// <summary>
    /// A tool window whose tab order starts with chrome, as the debug windows' does: a toolbar (a button and a toggle) and a column header with a
    /// splitter, then (<paramref name="withContent"/>) a text box named Content.
    /// </summary>
    private sealed class ChromeFirstTool(string id, string title, bool withContent) : CsCode.Ui.IToolWindow
    {
        public string Id => id;
        public string Title => title;
        public string? IconKey => "files";
        public CsCode.Ui.ToolWindowLocation DefaultLocation => CsCode.Ui.ToolWindowLocation.Bottom;
        public string? ToggleCommandId => "test.toggle." + id; // View > Tool Windows lists only tools that have one
        public string? AutoShowWhen => null;

        public Control CreateView(IServiceProvider services)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("100,Auto,*") };
            var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(splitter, 1);
            header.Children.Add(splitter);
            var view = new StackPanel();
            view.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new Button { Content = "Clear" }, new ToggleButton { Content = "Lock" } } });
            view.Children.Add(header);
            if (withContent) view.Children.Add(new TextBox { Name = "Content" });
            return view;
        }
    }

    /// <summary>Clicks View &gt; Tool Windows &gt; <paramref name="title"/> in the menu bar as the user does: the item's click, then the menu closes.</summary>
    private static async Task ClickToolWindowItemAsync(RealAppHost host, string title)
    {
        var bar = host.Window.GetVisualDescendants().OfType<ShellMenuBar>().Single();
        var top = bar.Items.OfType<MenuItem>().Single(i => Header(i) == "_View");
        top.IsSubMenuOpen = true;
        Settle();
        var tools = top.Items.OfType<MenuItem>().Single(i => Header(i) == "_Tool Windows");
        tools.IsSubMenuOpen = true;
        Settle();
        var item = tools.Items.OfType<MenuItem>().Single(i => Header(i) == title);
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        bar.Close();
        await DevScene.SettleAsync(10);
    }

    private static string Focused(RealAppHost host) => host.Window.FocusManager?.GetFocusedElement()?.ToString() ?? "nothing";

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
