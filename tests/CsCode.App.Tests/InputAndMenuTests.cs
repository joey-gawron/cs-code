using System.Text.Json;
using Avalonia.Input;
using CsCode.App.Input;
using CsCode.App.Menus;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using CsCode.Ui;
using Xunit;

namespace CsCode.App.Tests;

public class KeyMapperTests
{
    [Theory]
    [InlineData(Key.A, KeyModifiers.Control, "ctrl+a")]
    [InlineData(Key.F5, KeyModifiers.None, "f5")]
    [InlineData(Key.F5, KeyModifiers.Shift | KeyModifiers.Control, "ctrl+shift+f5")]
    [InlineData(Key.OemComma, KeyModifiers.Control, "ctrl+,")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Alt, "alt+[")]
    [InlineData(Key.D0, KeyModifiers.None, "0")]
    [InlineData(Key.OemTilde, KeyModifiers.Control, "ctrl+`")]
    [InlineData(Key.Escape, KeyModifiers.None, "escape")]
    [InlineData(Key.Back, KeyModifiers.Control, "ctrl+backspace")]
    public void Keys_map_to_vscode_names(Key key, KeyModifiers mods, string expected)
    {
        Assert.True(AvaloniaKeyMapper.TryMap(key, mods, out var s));
        Assert.Equal(expected, s.ToString());
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.None)]
    public void Modifier_only_presses_are_not_strokes(Key key) => Assert.False(AvaloniaKeyMapper.TryMap(key, KeyModifiers.Control, out _));

    [Fact]
    public void The_mapped_stroke_matches_what_the_keybinding_parser_produces()
    {
        AvaloniaKeyMapper.TryMap(Key.K, KeyModifiers.Control, out var mapped);
        Assert.Equal(KeyStrokeParser.ParseStroke("ctrl+k"), mapped);
        AvaloniaKeyMapper.TryMap(Key.OemPeriod, KeyModifiers.Control, out var dot);
        Assert.Equal(KeyStrokeParser.ParseStroke("ctrl+."), dot);
    }
}

public class KeyboardPipelineTests
{
    private sealed class Rig
    {
        public ManualUiDispatcher Ui { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public SimpleContextKeys Keys { get; } = new();
        public FakeKeybindingService Bindings { get; }
        public TestCommandService Commands { get; } = new();
        public StatusBarService Status { get; }
        public RecordingWindowService Window { get; } = new();
        public KeyboardPipeline Pipeline { get; }
        public List<(string Id, object?[] Args)> Executed { get; } = new();

        public Rig()
        {
            Bindings = new FakeKeybindingService(Keys);
            Status = new StatusBarService(Ui);
            Pipeline = new KeyboardPipeline(Bindings, Commands, Ui, Status, Window, Time, context: Keys);
        }

        public void Cmd(string id) => Commands.Register(id, inv => { Executed.Add((id, inv.Args.ToArray())); return ValueTask.FromResult<object?>(null); });
        public KeyDispatch Press(string s) => Pipeline.Handle(KeyStrokeParser.ParseStroke(s));
        public string? StatusText(string id) { Ui.RunPending(); return Status.Items.FirstOrDefault(i => i.Id == id)?.Text; }
        public async Task Settle() { for (var i = 0; i < 20 && Executed.Count == 0; i++) await Task.Delay(5); }
    }

    [Fact]
    public async Task A_matching_binding_runs_its_command_with_args_and_consumes_the_key()
    {
        var r = new Rig();
        r.Cmd("workbench.action.showCommands");
        r.Bindings.Add("workbench.action.showCommands", "ctrl+shift+p", args: JsonDocument.Parse("{\"x\":1}").RootElement);
        Assert.Equal(KeyDispatch.Handled, r.Press("ctrl+shift+p"));
        await r.Settle();
        var (id, args) = Assert.Single(r.Executed);
        Assert.Equal("workbench.action.showCommands", id);
        Assert.Equal(1, ((JsonElement)args[0]!).GetProperty("x").GetInt32());
    }

    [Fact]
    public void Keys_without_a_binding_pass_through_so_typing_is_untouched()
    {
        var r = new Rig();
        r.Bindings.Add("a.b", "ctrl+b");
        Assert.Equal(KeyDispatch.PassThrough, r.Press("a"));
        Assert.Equal(KeyDispatch.PassThrough, r.Press("shift+a"));
        Assert.Equal(KeyDispatch.PassThrough, r.Press("enter"));
        Assert.Equal(0, r.Pipeline.ExecutedCount);
    }

    [Fact]
    public async Task A_chord_shows_the_pending_message_then_runs_on_the_second_key_and_clears_the_message()
    {
        var r = new Rig();
        r.Cmd("editor.action.addCommentLine");
        r.Bindings.Add("editor.action.addCommentLine", "ctrl+k ctrl+c");
        Assert.Equal(KeyDispatch.Handled, r.Press("ctrl+k"));
        Assert.Equal("(Ctrl+K) was pressed. Waiting for second key of chord…", r.StatusText("keys.pending"));
        Assert.True(r.Pipeline.IsChordPending);
        Assert.Equal(KeyDispatch.Handled, r.Press("ctrl+c"));
        await r.Settle();
        Assert.Single(r.Executed);
        Assert.Null(r.StatusText("keys.pending"));
        Assert.False(r.Pipeline.IsChordPending);
    }

    [Fact]
    public void A_dead_chord_is_swallowed_with_an_explanation()
    {
        var r = new Rig();
        r.Cmd("x.y");
        r.Bindings.Add("x.y", "ctrl+k ctrl+c");
        r.Press("ctrl+k");
        Assert.Equal(KeyDispatch.Handled, r.Press("z"));
        Assert.Contains("Ctrl+K, Z", r.StatusText("keys.dead"));
        r.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(r.StatusText("keys.dead"));
    }

    [Fact]
    public void Escape_abandons_a_pending_chord_and_a_timeout_does_too()
    {
        var r = new Rig();
        r.Cmd("x.y");
        r.Bindings.Add("x.y", "ctrl+k ctrl+c");
        r.Press("ctrl+k");
        Assert.Equal(KeyDispatch.Handled, r.Press("escape"));
        Assert.False(r.Pipeline.IsChordPending);
        Assert.Null(r.StatusText("keys.pending"));

        r.Press("ctrl+k");
        Assert.True(r.Pipeline.IsChordPending);
        r.Time.Advance(KeyboardPipeline.DefaultChordTimeout + TimeSpan.FromMilliseconds(1));
        r.Ui.RunPending();
        Assert.False(r.Pipeline.IsChordPending);
        Assert.Null(r.StatusText("keys.pending"));
        Assert.Equal(0, r.Time.ActiveTimers);
    }

    [Fact]
    public void Escape_without_a_pending_chord_passes_through_to_the_focused_control()
    {
        var r = new Rig();
        Assert.Equal(KeyDispatch.PassThrough, r.Press("escape"));
    }

    [Fact]
    public async Task When_clauses_gate_bindings_on_the_tracked_context()
    {
        var r = new Rig();
        r.Cmd("editor.action.formatDocument");
        r.Bindings.Add("editor.action.formatDocument", "ctrl+k ctrl+d", when: "editorTextFocus && !editorReadonly");
        Assert.Equal(KeyDispatch.PassThrough, r.Press("ctrl+k")); // no editor focus: not even a chord prefix
        r.Keys.Set("editorTextFocus", true);
        Assert.Equal(KeyDispatch.Handled, r.Press("ctrl+k"));
        Assert.Equal(KeyDispatch.Handled, r.Press("ctrl+d"));
        await r.Settle();
        Assert.Single(r.Executed);

        r.Keys.Set("editorReadonly", true);
        Assert.Equal(KeyDispatch.PassThrough, r.Press("ctrl+k"));
    }

    [Fact]
    public async Task A_key_bound_to_a_command_no_module_registered_reaches_the_focused_control()
    {
        // The presets bind ids of modules that may not be composed (e.g. F3 -> editor.action.nextMatchFindAction): the editor must still get F3.
        var r = new Rig();
        r.Bindings.Add("does.not.exist", "f3");
        Assert.Equal(KeyDispatch.PassThrough, r.Press("f3"));
        await Task.Delay(20);
        Assert.Empty(r.Window.Messages); // no "command not available" toast for every press either
        Assert.Equal(0, r.Pipeline.ExecutedCount);
    }

    [Fact]
    public async Task An_unregistered_binding_falls_back_to_a_registered_binding_of_the_same_keys()
    {
        var r = new Rig();
        r.Cmd("real.cmd");
        r.Bindings.Add("real.cmd", "f8", when: "editorTextFocus");
        r.Bindings.Add("does.not.exist", "f8"); // higher precedence (added later), but nobody registered it
        Assert.Equal(KeyDispatch.PassThrough, r.Press("f8")); // the fallback's `when` does not hold
        r.Keys.Set("editorTextFocus", true);
        Assert.Equal(KeyDispatch.Handled, r.Press("f8"));
        await r.Settle();
        Assert.Equal("real.cmd", Assert.Single(r.Executed).Id);
    }

    [Fact]
    public void A_chord_prefix_that_only_leads_to_unregistered_commands_is_not_a_chord()
    {
        var r = new Rig();
        r.Bindings.Add("does.not.exist", "ctrl+k ctrl+c");
        Assert.Equal(KeyDispatch.PassThrough, r.Press("ctrl+k"));
        Assert.False(r.Pipeline.IsChordPending);
        Assert.Null(r.StatusText("keys.pending"));
        Assert.Equal(KeyDispatch.PassThrough, r.Press("ctrl+c")); // copy still works
    }

    [Fact]
    public async Task Pressing_keys_never_blocks_on_a_slow_command()
    {
        var r = new Rig();
        var gate = new TaskCompletionSource();
        r.Commands.Register("slow", async _ => { await gate.Task; return null; });
        r.Bindings.Add("slow", "f8");
        r.Press("f8"); // warm-up (JIT); the command is still pending on the gate afterwards
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(KeyDispatch.Handled, r.Press("f8"));
        Assert.True(sw.ElapsedMilliseconds < 100, $"key handling waited for the command ({sw.ElapsedMilliseconds} ms)");
        gate.SetResult();
    }
}

public class ContextKeyTrackerTests
{
    [Fact]
    public async Task Active_editor_state_is_published_as_context_keys()
    {
        using var rig = new WellRig();
        var ui = new ManualUiDispatcher();
        var keys = new SimpleContextKeys();
        using var tracker = new ContextKeyTracker(keys, rig.Well, ui);
        Assert.Equal(false, keys.Get("hasOpenEditors"));
        Assert.True(keys.Get("isLinux") is true || keys.Get("isWindows") is true || keys.Get("isMac") is true);

        var tab = await rig.Open("Program.cs");
        Assert.Equal(true, keys.Get("hasOpenEditors"));
        Assert.Equal(".cs", keys.Get("resourceExtname"));
        Assert.Equal("Program.cs", keys.Get("resourceFilename"));
        Assert.Equal("file", keys.Get("resourceScheme"));
        Assert.Equal("plaintext", keys.Get("editorLangId"));
        Assert.Equal(false, keys.Get("activeEditorIsDirty"));

        rig.Editors.Doc(tab.Uri).SetDirty(true);
        Assert.Equal(true, keys.Get("activeEditorIsDirty"));

        rig.Dialogs.Answers.Enqueue("Don't Save"); // the tab is dirty
        await rig.Well.CloseAllAsync();
        Assert.Equal(false, keys.Get("hasOpenEditors"));
        Assert.Equal("", keys.Get("resourceExtname"));
    }

    [Theory]
    [InlineData(FocusArea.Editor, "editorTextFocus")]
    [InlineData(FocusArea.Explorer, "explorerViewletFocus")]
    [InlineData(FocusArea.Explorer, "listFocus")]
    [InlineData(FocusArea.Panel, "panelFocus")]
    [InlineData(FocusArea.Input, "inputFocus")]
    [InlineData(FocusArea.Terminal, "terminalFocus")]
    public void Focus_areas_set_exactly_their_context_keys(FocusArea area, string key)
    {
        using var rig = new WellRig();
        var keys = new SimpleContextKeys();
        using var tracker = new ContextKeyTracker(keys, rig.Well, new ManualUiDispatcher());
        tracker.SetFocus(area);
        Assert.Equal(true, keys.Get(key));
        tracker.SetFocus(FocusArea.None);
        Assert.Equal(false, keys.Get(key));
    }

    [Fact]
    public void Debug_state_maps_to_debugState_and_inDebugMode()
    {
        using var rig = new WellRig();
        var ui = new ManualUiDispatcher();
        var keys = new SimpleContextKeys();
        var debug = new FakeDebugService();
        using var tracker = new ContextKeyTracker(keys, rig.Well, ui, debug: debug);
        Assert.Equal("inactive", keys.Get("debugState"));
        debug.SetState(DebugState.Stopped);
        ui.RunPending();
        Assert.Equal("stopped", keys.Get("debugState"));
        Assert.Equal(true, keys.Get("inDebugMode"));
        debug.SetState(DebugState.Inactive);
        ui.RunPending();
        Assert.Equal(false, keys.Get("inDebugMode"));
    }

    [Fact]
    public async Task Workspace_folder_count_and_quick_input_visibility_are_tracked()
    {
        using var rig = new WellRig();
        var ui = new ManualUiDispatcher();
        var keys = new SimpleContextKeys();
        var ws = new FakeWorkspaceService();
        var quick = new CsCode.App.QuickInput.QuickInputViewModel(ui);
        using var tracker = new ContextKeyTracker(keys, rig.Well, ui, quick, ws);
        Assert.Equal(0, keys.Get("workspaceFolderCount"));
        await ws.OpenFolderAsync(new Uri("file:///w/"));
        ui.RunPending();
        Assert.Equal(1, keys.Get("workspaceFolderCount"));
        _ = quick.InputAsync(null);
        Assert.Equal(true, keys.Get("quickInputVisible"));
        quick.Cancel();
        Assert.Equal(false, keys.Get("quickInputVisible"));
    }

    [Fact]
    public async Task The_tracker_does_not_keep_closed_tabs_alive()
    {
        var rig = new WellRig();
        var keys = new SimpleContextKeys();
        using var tracker = new ContextKeyTracker(keys, rig.Well, new ManualUiDispatcher());
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var tab = await rig.Open("leak.cs");
            var doc = tab.Doc; // a closed tab releases its document, so capture it first
            await rig.Well.CloseAsync(tab);
            await Task.Delay(30);
            return [tab, doc];
        });
        rig.Dispose();
    }
}

public class MenuModelTests
{
    private sealed class Rig : IDisposable
    {
        public ManualUiDispatcher Ui { get; } = new();
        public SimpleContextKeys Keys { get; } = new();
        public TestCommandService Commands { get; } = new();
        public FakeKeybindingService Bindings { get; }
        public FakeWorkspaceService Workspace { get; } = new();
        public List<IToolWindow> Tools { get; } = new();
        public HashSet<string> Visible { get; } = new();
        public MenuModel Menu { get; }

        public Rig()
        {
            Bindings = new FakeKeybindingService(Keys);
            foreach (var id in new[] { KnownCommands.NewUntitledFile, KnownCommands.Save, KnownCommands.OpenFolder, KnownCommands.CommandPalette, KnownCommands.Quit, "workbench.action.closeFolder" })
                Commands.Register(id, _ => ValueTask.FromResult<object?>(null), new CommandMetadata(id));
            Menu = new MenuModel(ShellMenus.Build(), Commands, Keys, Ui, Bindings, Workspace, () => Tools, id => Visible.Contains(id));
        }

        public void Dispose() => Menu.Dispose();
        public MenuItemVm Root(string name) => Menu.Roots.Single(r => r.Header.Replace("_", "") == name);
    }

    private sealed class FakeTool(string id, string title, string? toggle, ToolWindowLocation loc = ToolWindowLocation.Right) : IToolWindow
    {
        public string Id => id;
        public string Title => title;
        public string? IconKey => "files";
        public ToolWindowLocation DefaultLocation => loc;
        public string? ToggleCommandId => toggle;
        public string? AutoShowWhen => null;
        public Avalonia.Controls.Control CreateView(IServiceProvider services) => throw new NotSupportedException();
    }

    [Fact]
    public void A_refresh_still_queued_when_the_menus_are_disposed_does_not_run()
    {
        var rig = new Rig();
        rig.Tools.Add(new FakeTool("explorer", "Explorer", "workbench.view.explorer"));
        var refreshes = rig.Menu.RefreshCount;
        rig.Keys.Set("anything", true); // schedules a coalesced refresh
        rig.Menu.Dispose();             // the window closed (and the provider behind isToolVisible is gone) before it ran
        rig.Visible.Clear();
        rig.Ui.RunPending();
        Assert.Equal(refreshes, rig.Menu.RefreshCount);
    }

    [Fact]
    public void The_menu_bar_has_exactly_the_nine_visual_studio_menus_in_order()
    {
        using var rig = new Rig();
        Assert.Equal(["File", "Edit", "View", "Go", "Debug", "Terminal", "Tools", "Window", "Help"], rig.Menu.Roots.Select(r => r.Header.Replace("_", "")));
    }

    [Fact]
    public void There_is_no_extension_or_marketplace_entry_anywhere()
    {
        using var rig = new Rig();
        IEnumerable<MenuItemVm> Walk(MenuItemVm m) => new[] { m }.Concat(m.Children.SelectMany(Walk));
        var all = rig.Menu.Roots.SelectMany(Walk).ToArray();
        Assert.DoesNotContain(all, i => i.Header.Contains("xtension", StringComparison.OrdinalIgnoreCase) || (i.CommandId ?? "").Contains("extension", StringComparison.OrdinalIgnoreCase)
            || (i.CommandId ?? "").Contains("marketplace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Items_are_enabled_only_when_their_command_exists_and_their_when_holds()
    {
        using var rig = new Rig();
        Assert.True(rig.Menu.Find(KnownCommands.NewUntitledFile)!.IsEnabled);
        Assert.False(rig.Menu.Find(KnownCommands.Find)!.IsEnabled); // Editor module's command is not registered
        Assert.False(rig.Menu.Find(KnownCommands.Save)!.IsEnabled); // registered, but hasOpenEditors is false
        rig.Keys.Set("hasOpenEditors", true);
        rig.Ui.RunPending();
        Assert.True(rig.Menu.Find(KnownCommands.Save)!.IsEnabled);
        Assert.True(rig.Menu.Find("workbench.action.closeFolder")!.IsEnabled == false); // needs workspaceFolderCount
        rig.Keys.Set("workspaceFolderCount", 1);
        rig.Ui.RunPending();
        Assert.True(rig.Menu.Find("workbench.action.closeFolder")!.IsEnabled);
    }

    [Fact]
    public void A_late_command_registration_enables_its_item()
    {
        using var rig = new Rig();
        Assert.False(rig.Menu.Find(KnownCommands.Find)!.IsEnabled);
        rig.Commands.Register(KnownCommands.Find, _ => ValueTask.FromResult<object?>(null));
        rig.Keys.Set("hasOpenEditors", true);
        rig.Ui.RunPending();
        Assert.True(rig.Menu.Find(KnownCommands.Find)!.IsEnabled);
    }

    [Fact]
    public void Shortcut_labels_come_from_the_keybinding_service_and_follow_changes()
    {
        using var rig = new Rig();
        rig.Bindings.Add(KnownCommands.NewUntitledFile, "ctrl+n");
        rig.Bindings.Add(KnownCommands.Save, "ctrl+k ctrl+s");
        rig.Bindings.RaiseChanged();
        rig.Ui.RunPending();
        Assert.Equal("Ctrl+N", rig.Menu.Find(KnownCommands.NewUntitledFile)!.Gesture);
        Assert.Equal("Ctrl+K, Ctrl+S", rig.Menu.Find(KnownCommands.Save)!.Gesture);
        Assert.Null(rig.Menu.Find(KnownCommands.OpenFolder)!.Gesture);
    }

    [Fact]
    public void Recent_folders_are_listed_and_empty_state_is_a_disabled_placeholder()
    {
        using var rig = new Rig();
        var recent = rig.Root("File").Children.Single(c => c.Header.Contains("Recent"));
        Assert.Equal("(no recent folders)", Assert.Single(recent.Children).Header);
        Assert.False(recent.Children[0].IsEnabled);

        rig.Workspace.Recents.AddRange([new Uri("file:///home/a/proj1/"), new Uri("file:///home/a/proj2/")]);
        rig.Menu.Refresh();
        Assert.Equal(2, recent.Children.Count);
        Assert.Contains("proj1", recent.Children[0].Header);
    }

    [Fact]
    public void The_view_menu_lists_tool_windows_with_check_state_from_visibility()
    {
        using var rig = new Rig();
        rig.Tools.Add(new FakeTool("explorer", "Explorer", "workbench.view.explorer"));
        rig.Tools.Add(new FakeTool("output", "Output", "workbench.action.output.toggleOutput", ToolWindowLocation.Bottom));
        rig.Tools.Add(new FakeTool("hidden", "No Toggle", null));
        rig.Commands.Register("workbench.view.explorer", _ => ValueTask.FromResult<object?>(null));
        rig.Visible.Add("output");
        rig.Menu.Refresh();
        var tools = rig.Root("View").Children.Single(c => c.Header.Contains("Tool Windows"));
        Assert.Equal(["Explorer", "Output"], tools.Children.Select(c => c.Header));
        Assert.False(tools.Children[0].IsChecked);
        Assert.True(tools.Children[1].IsChecked);
        Assert.True(tools.Children[0].IsEnabled);
        Assert.False(tools.Children[1].IsEnabled); // its toggle command is not registered
    }

    [Fact]
    public async Task Invoking_an_item_executes_its_command()
    {
        using var rig = new Rig();
        var ran = 0;
        rig.Commands.Register("custom.cmd", _ => { ran++; return ValueTask.FromResult<object?>(null); });
        var item = new MenuModel([new MenuNode("X", "custom.cmd")], rig.Commands, rig.Keys, rig.Ui).Roots[0];
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command!).ExecuteAsync(null);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Context_key_bursts_cause_one_coalesced_refresh()
    {
        using var rig = new Rig();
        rig.Ui.RunPending();
        var before = rig.Menu.RefreshCount;
        for (var i = 0; i < 200; i++) rig.Keys.Set("k" + i, true);
        rig.Ui.RunPending();
        Assert.Equal(before + 1, rig.Menu.RefreshCount);
    }

    [Fact]
    public void Contributed_menu_items_are_filtered_by_when_and_titled_from_command_metadata()
    {
        var registry = new Core.Contributions.ContributionRegistry();
        registry.Contribute(Core.Contributions.ContributionSources.User, "/u", new[]
        {
            new Core.Contributions.MenuContribution(ContributedMenus.ExplorerContext, "a.cmd", "1_a", "resourceExtname == .cs"),
            new Core.Contributions.MenuContribution(ContributedMenus.ExplorerContext, "b.cmd", "0_b"),
            new Core.Contributions.MenuContribution(ContributedMenus.EditorContext, "c.cmd"),
        });
        var commands = new TestCommandService();
        commands.Register("a.cmd", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("a.cmd", "Do A"));
        var keys = new SimpleContextKeys();
        var hidden = ContributedMenus.Get(registry, commands, keys, ContributedMenus.ExplorerContext);
        Assert.Equal(["b.cmd"], hidden.Select(h => h.Item.Command));
        keys.Set("resourceExtname", ".cs");
        var shown = ContributedMenus.Get(registry, commands, keys, ContributedMenus.ExplorerContext);
        Assert.Equal(["b.cmd", "a.cmd"], shown.Select(h => h.Item.Command));
        Assert.Equal("Do A", shown[1].Title);
    }
}
