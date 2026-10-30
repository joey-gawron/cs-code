using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.ToolWindows.Explorer;
using CsCode.App.ToolWindows.Output;
using CsCode.App.Views;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Menus;
using Dock.Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Clipboard brief B1-B4 and A1 over the REAL composition (<see cref="RealAppHost"/>: every module, the real keymap, headless window): right-clicks
/// at given positions (editor text, selection, tab, tool tab, panel rows, status bar), the items, their order, enabled state and shortcut text,
/// their effect; Edit > Cut/Copy/Paste following focus; menu budgets and retention.
/// </summary>
public class RealAppContextMenuTests
{
    private const string Source = "class Program\n{\n    static void Main()\n    {\n        var answer = 42;\n    }\n}\n";

    private static async Task<RealAppHost> StartWithFileAsync(string name = "Program.cs", string text = Source, Action<IServiceCollection>? configure = null)
    {
        var host = RealAppHost.Create(h => h.Files.WriteFile(name, text));
        await host.StartAsync([], configure);
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.Combine(name)));
        await DevScene.SettleAsync(20);
        return host;
    }

    private static Control EditorView(RealAppHost host) => host.Shell.Well.ActiveEditor!.LiveView!;

    /// <summary>Right-clicks <paramref name="target"/> at <paramref name="local"/> (its coordinates) and returns the menu that opened.</summary>
    private static async Task<ContextMenu> RightClickAsync(RealAppHost host, Visual target, Point local)
    {
        var at = target.TranslatePoint(local, host.Window) ?? local;
        var before = ContextMenus.LastOpened;
        host.Window.MouseDown(at, MouseButton.Right);
        host.Window.MouseUp(at, MouseButton.Right);
        await DevScene.SettleAsync(5);
        var menu = ContextMenus.LastOpened;
        Assert.True(menu is not null && !ReferenceEquals(menu, before), "a context menu opened");
        return menu!;
    }

    private static IReadOnlyList<MenuEntry> Entries(ItemsControl menu) => ContextMenus.ItemsOf(menu).Select(i => ContextMenus.EntryOf(i)!).ToArray();

    private static string[] Headers(ItemsControl menu) =>
        menu.Items.Select(i => i is MenuItem m ? ContextMenus.EntryOf(m)!.AccessibleName : "-").ToArray();

    private static async Task Click(MenuItem item)
    {
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await DevScene.SettleAsync(10);
    }

    private static Point PointOfOffset(Control view, int line, int column)
    {
        var editor = view.GetVisualDescendants().OfType<AvaloniaEdit.Editing.TextArea>().First();
        var tv = editor.TextView;
        var p = tv.GetVisualPosition(new AvaloniaEdit.TextViewPosition(line + 1, column + 1), AvaloniaEdit.Rendering.VisualYPosition.TextMiddle) - tv.ScrollOffset;
        return tv.TranslatePoint(new Point(p.X + 1, p.Y), view) ?? p;
    }

    // ---------------------------------------------------------------- B1: the editor's menu

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Right_clicking_the_editor_opens_Visual_Studios_menu_with_the_keymaps_shortcuts_and_hides_what_cannot_apply()
    {
        await using var host = await StartWithFileAsync();
        var view = EditorView(host);
        var menu = await RightClickAsync(host, view, PointOfOffset(view, 4, 12));

        // No language server is running (RealAppHost disables them): the capability-gated items are hidden, not dead; no debug session: no watch items.
        Assert.Equal(
        [
            "Breakpoint", "Run To Cursor", "-",
            "Snippet", "-",
            "Cut", "Copy", "Copy with Formatting", "Paste", "-",
            "Comment Selection", "Uncomment Selection", "-",
            "Command Palette...",
        ], Headers(menu));
        var byName = Entries(menu).ToDictionary(e => e.AccessibleName);
        Assert.Equal("Ctrl+X", byName["Cut"].Gesture);
        Assert.Equal("Ctrl+C", byName["Copy"].Gesture);
        Assert.Equal("Ctrl+V", byName["Paste"].Gesture);
        Assert.Equal("Ctrl+K, Ctrl+C", byName["Comment Selection"].Gesture);
        Assert.Equal("Ctrl+F10", byName["Run To Cursor"].Gesture);
        Assert.Equal(["Insert Breakpoint", "Conditions..."], byName["Breakpoint"].Children.Select(c => c.AccessibleName));
        Assert.Equal("F9", byName["Breakpoint"].Children[0].Gesture);
        Assert.True(Avalonia.Automation.AutomationProperties.GetName(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Copy")) == "Copy");
        menu.Close();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Language_server_capabilities_add_their_items_and_Rename_enables_after_prepareRename_answers()
    {
        var prepared = new TaskCompletionSource<PrepareRenameResult?>();
        await using var host = await StartWithFileAsync();
        var registry = host.Get<ILanguageFeatureRegistry>();
        using var rename = registry.Register<IRenameProvider>(DocumentSelector.All, new SlowRename(prepared.Task));
        using var definition = registry.Register<IDefinitionProvider>(DocumentSelector.All, new NoDefinitions());
        await DevScene.SettleAsync(10);
        var view = EditorView(host);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var menu = await RightClickAsync(host, view, PointOfOffset(view, 4, 13));
        Assert.Equal(["Rename...", "-", "Go To Definition", "Go To Declaration", "Go To Implementation"], Headers(menu).Take(5));
        var renameItem = ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Rename...");
        Assert.Equal("Ctrl+R, Ctrl+R", ContextMenus.EntryOf(renameItem)!.Gesture);
        Assert.False(renameItem.IsEnabled); // the server has not answered prepareRename yet: the menu did not wait for it
        prepared.SetResult(new PrepareRenameResult(TextRange.FromLines(4, 12, 4, 18), null));
        Assert.True(await RealAppHost.WaitFor(() => renameItem.IsEnabled), "Rename enabled when prepareRename answered");
        menu.Close();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_right_click_outside_the_selection_moves_the_caret_and_inside_keeps_the_selection_for_Cut_and_Copy()
    {
        await using var host = await StartWithFileAsync();
        var view = EditorView(host);
        var controller = host.Shell.Well.ActiveEditor!.Controller!;
        controller.SetSelection(TextRange.FromLines(4, 12, 4, 18)); // "answer"
        await DevScene.SettleAsync(5);
        var menu = await RightClickAsync(host, view, PointOfOffset(view, 4, 14));
        Assert.Equal(TextRange.FromLines(4, 12, 4, 18), controller.Selections[0]);
        await Click(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Copy"));
        Assert.Equal("answer", (await host.Get<IClipboardService>().ReadAsync())?.Text);

        menu = await RightClickAsync(host, view, PointOfOffset(view, 0, 3));
        menu.Close();
        Assert.True(controller.Selections[0].IsEmpty);
        Assert.Equal(0, controller.Caret.Line);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Shift_F10_the_Menu_key_and_the_menu_command_open_the_editor_menu_at_the_caret()
    {
        await using var host = await StartWithFileAsync();
        var controller = host.Shell.Well.ActiveEditor!.Controller!;
        controller.SetSelection(TextRange.At(4, 8));
        controller.Focus();
        await DevScene.SettleAsync(5);
        async Task<ContextMenu> Opened(Func<Task> open)
        {
            var before = ContextMenus.LastOpened;
            await open();
            await DevScene.SettleAsync(5);
            var menu = ContextMenus.LastOpened;
            Assert.True(menu is not null && !ReferenceEquals(menu, before), "a menu opened");
            Assert.Equal(Avalonia.Controls.PlacementMode.AnchorAndGravity, menu!.Placement); // below the caret, not at the pointer
            Assert.Equal(controller.GetCaretBounds(), menu.PlacementRect);
            Assert.Contains("Copy", Headers(menu));
            menu.Close();
            return menu;
        }
        await Opened(() =>
        {
            host.Window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            host.Window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
            return Task.CompletedTask;
        });
        controller.Focus();
        await Opened(() =>
        {
            host.Window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            host.Window.KeyRelease(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            return Task.CompletedTask;
        });
        controller.Focus();
        await Opened(() => host.RunAsync("editor.action.showContextMenu"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Paste_from_the_menu_goes_in_as_one_undo_step_and_converts_line_endings()
    {
        await using var host = await StartWithFileAsync();
        var tab = host.Shell.Well.ActiveEditor!;
        await host.Get<IClipboardService>().WriteAsync(ClipboardText.Plain("x\r\ny"));
        tab.Controller!.SetSelection(TextRange.At(7, 0));
        var menu = await RightClickAsync(host, tab.LiveView!, PointOfOffset(tab.LiveView!, 7, 0));
        var paste = ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Paste");
        Assert.True(await RealAppHost.WaitFor(() => paste.IsEnabled), "Paste enabled once the clipboard answered");
        await Click(paste);
        Assert.True(await RealAppHost.WaitFor(() => tab.Doc.GetText().EndsWith("}\nx\ny", StringComparison.Ordinal)), tab.Doc.GetText());
        tab.Controller.Undo();
        Assert.Equal(Source, tab.Doc.GetText());
    }

    // ---------------------------------------------------------------- B2: tabs, tool windows, panels, status bar

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Document_tabs_have_Visual_Studios_tab_menu_instead_of_Docks()
    {
        await using var host = await StartWithFileAsync();
        await host.Get<INavigationService>().OpenAsync(new Uri(host.Files.WriteFile("second.txt", "2\n")));
        await DevScene.SettleAsync(10);
        var first = host.Shell.Well.EditorTabs.Single(t => t.Title == "Program.cs");
        var item = host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(i => ReferenceEquals(i.DataContext, first));
        var menu = await RightClickAsync(host, item, new Point(10, 8));
        Assert.Equal(
        [
            "Close", "Close All But This", "Close All", "Close To The Right", "-",
            "Pin Tab", "-",
            "Copy Full Path", "Open Containing Folder", "Reveal in Explorer", "-",
            "Float", "New Horizontal Document Group", "New Vertical Document Group",
        ], Headers(menu));
        var entries = Entries(menu).ToDictionary(e => e.AccessibleName);
        Assert.Equal("Ctrl+F4", entries["Close"].Gesture);
        Assert.True(entries["Close To The Right"].IsEnabled); // second.txt is to its right
        Assert.False(entries["Reveal in Explorer"].IsEnabled); // the file is not in an open folder

        await Click(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Copy Full Path"));
        Assert.Equal(host.Files.Combine("Program.cs"), (await host.Get<IClipboardService>().ReadAsync())?.Text);
        await Click(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Close To The Right"));
        Assert.Equal(["Program.cs"], host.Shell.Well.Tabs.Select(t => t.Title));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Tool_window_tabs_have_Float_Dock_Auto_Hide_and_Close()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var output = host.Shell.Tools.Get("output")!;
        var tab = host.Window.GetVisualDescendants().OfType<ToolTabStripItem>().First(i => ReferenceEquals(i.DataContext, output));
        var menu = await RightClickAsync(host, tab, new Point(8, 6));
        Assert.Equal(["Float", "Dock", "Auto Hide", "Close"], Headers(menu));
        var entries = Entries(menu);
        Assert.True(entries[0].IsEnabled);
        Assert.False(entries[1].IsEnabled); // docked already
        await Click(ContextMenus.ItemsOf(menu).Last());
        Assert.False(host.Shell.Tools.IsVisible("output"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Error_List_rows_copy_go_to_and_show_help()
    {
        await using var host = await StartWithFileAsync();
        var uri = new Uri(host.Files.Combine("Program.cs"));
        host.Get<IDiagnosticService>().Set(uri, "test", [new Diagnostic(TextRange.FromLines(4, 12, 4, 18), "The variable 'answer' is assigned but never used", DiagnosticSeverity.Warning, "CS0219", "csharp", CodeDescriptionHref: new Uri("https://example.invalid/CS0219"))]);
        host.Shell.Tools.Show("errorlist");
        await DevScene.SettleAsync(20);
        var view = (ErrorListView)host.Shell.Tools.Get("errorlist")!.LiveView!;
        Assert.True(await RealAppHost.WaitFor(() => view.GetVisualDescendants().OfType<ListBoxItem>().Any(i => i.DataContext is ErrorRow { IsHeader: false })));
        var row = view.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is ErrorRow { IsHeader: false });
        var menu = await RightClickAsync(host, row, new Point(40, 8));
        Assert.Equal(["Copy", "-", "Go To Location", "Show Error Help"], Headers(menu));
        Assert.All(Entries(menu), e => Assert.True(e.IsEnabled));
        await Click(ContextMenus.ItemsOf(menu).First());
        Assert.Equal($"Warning\tCS0219\tThe variable 'answer' is assigned but never used\t{uri.LocalPath}\t5", (await host.Get<IClipboardService>().ReadAsync())?.Text);
        await Click(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Go To Location"));
        Assert.Equal(new TextPosition(4, 18), host.Get<INavigationService>().ActiveCaret);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Output_copies_selected_lines_clears_and_wraps()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var channel = host.Get<IOutputService>().GetOrCreate("Tests");
        channel.AppendLine("first line");
        channel.AppendLine("second line");
        host.Shell.Tools.Show("output");
        await DevScene.SettleAsync(20);
        var view = (OutputView)host.Shell.Tools.Get("output")!.LiveView!;
        var model = (OutputViewModel)view.DataContext!;
        model.SelectedChannel = "Tests";
        Assert.True(await RealAppHost.WaitFor(() => model.Lines.Count >= 2));
        var list = view.GetVisualDescendants().OfType<ListBox>().First();
        list.SelectedItems!.Add(model.Lines[0]);
        list.SelectedItems!.Add(model.Lines[1]);
        var menu = await RightClickAsync(host, list, new Point(30, 8));
        Assert.Equal(["Copy", "-", "Clear All", "Word Wrap"], Headers(menu));
        await Click(ContextMenus.ItemsOf(menu).First());
        Assert.Equal("first line\nsecond line", (await host.Get<IClipboardService>().ReadAsync())?.Text);
        await Click(ContextMenus.ItemsOf(menu).Last());
        Assert.True(view.WordWrap);
        await Click(ContextMenus.ItemsOf(menu).First(i => ContextMenus.EntryOf(i)!.AccessibleName == "Clear All"));
        Assert.True(await RealAppHost.WaitFor(() => model.Lines.Count == 0));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Explorer_nodes_have_file_operations_paths_and_contributed_items()
    {
        await using var host = RealAppHost.Create(h => { h.Files.WriteFile("src/a.cs", "a"); });
        await host.StartAsync([host.Files.Path]);
        host.Shell.Tools.Show("explorer");
        var explorer = (ExplorerView)host.Shell.Tools.Get("explorer")!.LiveView!;
        Assert.True(await RealAppHost.WaitFor(() => explorer.Model.Rows.Any(r => r.Name == "src")));
        var src = explorer.Model.Rows.First(r => r.Name == "src");
        var folderMenu = explorer.MenuFor(src).Select(e => e.IsSeparator ? "-" : e.AccessibleName).ToArray();
        Assert.Equal(
        [
            "New File...", "New Folder...", "-",
            "Reveal in File Manager", "Open in Terminal", "Find in Folder...", "-",
            "Cut", "Copy", "Paste", "-",
            "Copy Path", "Copy Relative Path", "-",
            "Rename", "Duplicate", "Delete",
        ], folderMenu);
        await explorer.Model.ExpandAsync(src);
        var file = explorer.Model.Rows.First(r => r.Name == "a.cs");
        var entries = explorer.MenuFor(file);
        Assert.DoesNotContain(entries, e => e.AccessibleName is "New File..." or "Find in Folder...");
        Assert.Contains(entries, e => e.AccessibleName == "Open in Terminal"); // the integrated terminal opens in the file's folder
        await entries.First(e => e.AccessibleName == "Copy Relative Path").Run!();
        Assert.Equal(Path.Combine("src", "a.cs"), (await host.Get<IClipboardService>().ReadAsync())?.Text);
        Assert.Equal("Ctrl+C", entries.First(e => e.AccessibleName == "Copy").Gesture);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Status_bar_items_offer_their_command_and_Copy()
    {
        await using var host = await StartWithFileAsync();
        var status = host.Window.GetVisualDescendants().OfType<StatusBarView>().Single();
        var position = host.Get<StatusBarService>().Items.First(i => i.Id == "editor.position");
        var entries = status.MenuFor(position);
        Assert.Equal("Copy", entries[^1].AccessibleName);
        await entries[^1].Run!();
        Assert.Equal(StatusBarView.PlainText(position.Text), (await host.Get<IClipboardService>().ReadAsync())?.Text);
        if (position.CommandId is not null) Assert.Equal(position.CommandId, entries[0].CommandId);
    }

    // ---------------------------------------------------------------- A1: Edit > Cut/Copy/Paste follow focus

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Edit_menu_Cut_Copy_Paste_act_on_whatever_has_focus()
    {
        await using var host = await StartWithFileAsync();
        var menus = host.Shell.Menus;
        var copy = menus.Find("editor.action.clipboardCopyAction")!;
        var paste = menus.Find("editor.action.clipboardPasteAction")!;
        var clipboard = host.Get<IClipboardService>();

        // The editor: Visual Studio's whole-line copy from the menu.
        var tab = host.Shell.Well.ActiveEditor!;
        tab.Controller!.SetSelection(TextRange.At(4, 9));
        tab.Controller.Focus();
        await DevScene.SettleAsync(5);
        menus.Refresh();
        Assert.True(copy.IsEnabled);
        Assert.Equal("Ctrl+C", copy.Gesture);
        await ((IAsyncRelayCommand)copy.Command!).ExecuteAsync(null);
        Assert.Equal("        var answer = 42;\n", (await clipboard.ReadAsync())?.Text);

        // A list with a selection (the Error List): Copy copies its row; Paste is off there.
        var uri = new Uri(host.Files.Combine("Program.cs"));
        host.Get<IDiagnosticService>().Set(uri, "test", [new Diagnostic(TextRange.At(0, 0), "message", DiagnosticSeverity.Error, "X1")]);
        host.Shell.Tools.Show("errorlist");
        await DevScene.SettleAsync(20);
        var errors = (ErrorListView)host.Shell.Tools.Get("errorlist")!.LiveView!;
        var list = errors.GetVisualDescendants().OfType<ListBox>().First();
        Assert.True(await RealAppHost.WaitFor(() => list.ItemCount > 0));
        list.SelectedIndex = 0;
        await DevScene.SettleAsync(5);
        Assert.True((list.ContainerFromIndex(0) as Control)?.Focus() == true, "the row took focus");
        await DevScene.SettleAsync(5);
        menus.Refresh();
        Assert.True(copy.IsEnabled);
        Assert.False(paste.IsEnabled);
        await ((IAsyncRelayCommand)copy.Command!).ExecuteAsync(null);
        Assert.StartsWith("Error\tX1\tmessage", (await clipboard.ReadAsync())?.Text);

        // A text box (the palette's): its own selection.
        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        var box = host.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsFocused || t.IsKeyboardFocusWithin);
        box.Text = "typed text";
        box.SelectAll();
        await DevScene.SettleAsync(5);
        menus.Refresh();
        Assert.True(copy.IsEnabled);
        await ((IAsyncRelayCommand)copy.Command!).ExecuteAsync(null);
        await DevScene.SettleAsync(10);
        Assert.Equal("typed text", await host.Window.Clipboard!.TryGetTextAsync());
        await host.ClosePaletteAsync(running);
    }

    // ---------------------------------------------------------------- B4: budgets and retention

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Menus_open_well_under_50_ms_and_100_open_close_cycles_retain_nothing()
    {
        await using var host = await StartWithFileAsync();
        var view = EditorView(host);
        var tab = host.Shell.Well.ActiveEditor!;
        var item = host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(i => ReferenceEquals(i.DataContext, tab));
        var menus = host.Shell.ContextMenus;
        // Warm-up (first use JITs the builder).
        menus.OpenEditorMenu(tab, view)?.Close();
        menus.OpenTabMenu(tab, item).Close();
        await DevScene.SettleAsync(5);

        // Timing on a machine shared with other build agents: best of up to 3 runs of 100 openings (as ResponsivenessTests do); every opening of
        // the best run must be under budget. Retention is checked over all of them.
        var weak = new List<WeakReference>();
        var best = double.MaxValue;
        var report = "";
        for (var attempt = 0; attempt < 3 && best >= 50; attempt++)
        {
            var times = new List<double>();
            await OpenCloseAsync(menus, tab, view, item, weak, times.Add);
            times.Sort();
            if (times[^1] < best)
            {
                best = times[^1];
                report = $"p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)(times.Count * 0.95)]:F1} ms, max {times[^1]:F1} ms";
            }
        }
        for (var attempt = 0; attempt < 40 && weak.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await DevScene.SettleAsync(2);
        }
        Console.WriteLine("context menu open: " + report);
        Assert.True(best < 50, "menu opening over budget: " + report);
        Assert.True(weak.Count(w => w.IsAlive) <= 1, $"{weak.Count(w => w.IsAlive)} of {weak.Count} closed menus are still reachable");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task OpenCloseAsync(Menus.ShellContextMenus menus, EditorTab tab, Control view, Control item, List<WeakReference> weak, Action<double> time)
    {
        for (var i = 0; i < 100; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var menu = i % 2 == 0 ? menus.OpenEditorMenu(tab, view)! : menus.OpenTabMenu(tab, item);
            sw.Stop();
            time(sw.Elapsed.TotalMilliseconds);
            Dispatcher.UIThread.RunJobs();
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            weak.Add(new WeakReference(menu));
        }
        await DevScene.SettleAsync(5);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Text_boxes_take_all_the_standard_clipboard_keys_and_keep_their_Cut_Copy_Paste_menu()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        var box = host.Window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsFocused || t.IsKeyboardFocusWithin);
        Assert.NotNull(box.ContextFlyout ?? (object?)box.ContextMenu); // Fluent's Cut / Copy / Paste flyout (the shell's window handler leaves it alone)
        box.Text = "hello";
        box.SelectAll();
        host.Window.KeyPress(Key.Insert, RawInputModifiers.Control, PhysicalKey.Insert, null);
        await DevScene.SettleAsync(5);
        Assert.Equal("hello", await host.Window.Clipboard!.TryGetTextAsync());
        host.Window.KeyPress(Key.Delete, RawInputModifiers.Shift, PhysicalKey.Delete, null);
        await DevScene.SettleAsync(5);
        Assert.Equal("", box.Text);
        host.Window.KeyPress(Key.Insert, RawInputModifiers.Shift, PhysicalKey.Insert, null);
        Assert.True(await RealAppHost.WaitFor(() => box.Text == "hello"), box.Text);
        await host.ClosePaletteAsync(running);
    }

    // ---------------------------------------------------------------- screenshots (written to .scratch/screenshots, git-ignored)

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Screenshots_of_the_editor_menu_and_the_tab_menu_in_the_dark_theme()
    {
        await using var host = await StartWithFileAsync();
        var tab = host.Shell.Well.ActiveEditor!;
        tab.Controller!.SetSelection(TextRange.FromLines(4, 12, 4, 18));
        await DevScene.SettleAsync(5);
        foreach (var (name, open) in new (string, Func<Task<ContextMenu>>)[]
        {
            ("context-menu-editor", () => RightClickAsync(host, tab.LiveView!, PointOfOffset(tab.LiveView!, 4, 14))),
            ("context-menu-tab", () => RightClickAsync(host, host.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().First(i => ReferenceEquals(i.DataContext, tab)), new Point(10, 8))),
        })
        {
            var menu = await open();
            await DevScene.SettleAsync(10);
            var root = TopLevel.GetTopLevel(menu) ?? host.Window;
            var frame = root.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var path = Path.Combine(DevHost.ScreenshotDirectory(), name + ".png");
            frame!.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Assert.True(new FileInfo(path).Length > 2_000, name + " has content");
            menu.Close();
            await DevScene.SettleAsync(5);
        }
    }

    // ---------------------------------------------------------------- A5: middle-click in the real composition

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Selecting_sets_PRIMARY_and_a_middle_click_pastes_it_in_the_real_editor()
    {
        var primary = new InMemoryPrimarySelection();
        await using var host = await StartWithFileAsync(configure: s =>
            s.AddSingleton(sp => new ClipboardService(sp.GetRequiredService<Ui.Threading.IUiDispatcher>(), AvaloniaPlatformClipboard.Of(() => sp.GetRequiredService<WindowHolder>().Window), () => primary)));
        Assert.True(OperatingSystem.IsLinux()); // editor.selectionClipboard is on by default on Linux
        await DevScene.SettleAsync(10);
        var tab = host.Shell.Well.ActiveEditor!;
        tab.Controller!.SetSelection(TextRange.FromLines(0, 0, 0, 5)); // "class"
        await DevScene.SettleAsync(5);
        Assert.Equal("class", primary.Provider?.Invoke());
        var view = tab.LiveView!;
        var at = view.TranslatePoint(PointOfOffset(view, 7, 0), host.Window)!.Value;
        host.Window.MouseDown(at, MouseButton.Middle);
        host.Window.MouseUp(at, MouseButton.Middle);
        Assert.True(await RealAppHost.WaitFor(() => tab.Doc.GetText().EndsWith("}\nclass", StringComparison.Ordinal)), tab.Doc.GetText());
    }

    private sealed class SlowRename(Task<PrepareRenameResult?> prepared) : IRenameProvider
    {
        public async ValueTask<PrepareRenameResult?> PrepareRenameAsync(ITextDocument document, TextPosition position, CancellationToken ct) => await prepared.WaitAsync(ct);
        public ValueTask<WorkspaceEdit?> ProvideRenameAsync(ITextDocument document, TextPosition position, string newName, CancellationToken ct) => ValueTask.FromResult<WorkspaceEdit?>(null);
    }

    private sealed class NoDefinitions : IDefinitionProvider
    {
        public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<LocationLink>?>(null);
    }
}
