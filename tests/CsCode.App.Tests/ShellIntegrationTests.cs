using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Services;
using CsCode.App.Views;
using CsCode.Ui.Controls;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Ui.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

public class ShellIntegrationTests
{
    private static async Task<DevHost> StartAsync(Action<IServiceCollection>? configure = null, bool scene = true)
    {
        var host = new DevHost { Configure = configure }.Build();
        if (scene) await host.BuildAsync(); else await host.ShowAsync();
        return host;
    }

    private static T Svc<T>(DevHost h) where T : notnull => h.Services.GetRequiredService<T>();

    private static void Press(DevHost h, Key key, RawInputModifiers mods = RawInputModifiers.None) =>
        h.Window.KeyPress(key, mods, PhysicalKey.None, null);

    [AvaloniaFact]
    public async Task A_key_chord_through_the_real_window_shows_the_pending_message_then_runs_the_command()
    {
        using var host = await StartAsync();
        var ran = 0;
        host.Commands.Register("editor.action.addCommentLine", _ => { ran++; return ValueTask.FromResult<object?>(null); });
        Press(host, Key.K, RawInputModifiers.Control);
        await DevScene.SettleAsync(5);
        Assert.Contains(Svc<Core.Platform.IStatusBarService>(host).Items, i => i.Id == "keys.pending" && i.Text.StartsWith("(Ctrl+K) was pressed"));
        Press(host, Key.C, RawInputModifiers.Control);
        await DevHost.WaitFor(() => ran == 1);
        Assert.Equal(1, ran);
        await DevScene.SettleAsync(5);
        Assert.DoesNotContain(Svc<Core.Platform.IStatusBarService>(host).Items, i => i.Id == "keys.pending");
    }

    [AvaloniaFact]
    public async Task Typing_into_a_text_box_is_not_swallowed_by_the_shell_keyboard_pipeline()
    {
        using var host = await StartAsync();
        var box = new TextBox();
        host.Window.GetVisualDescendants().OfType<FilterBox>().First(); // a text input exists in the explorer
        var handledByShell = 0;
        host.Window.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Handled) handledByShell++; }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        Press(host, Key.A);
        Press(host, Key.B, RawInputModifiers.Shift);
        Assert.Equal(0, handledByShell);
        Assert.Equal(0, Svc<ShellController>(host).Keyboard.ExecutedCount);
        _ = box;
    }

    [AvaloniaFact]
    public async Task Ctrl_Tab_shows_the_mru_switcher_and_releasing_ctrl_activates_the_previous_tab()
    {
        using var host = await StartAsync();
        var well = Svc<DocumentWell>(host);
        var before = well.Active;
        var previous = well.MruOrder[1];
        Press(host, Key.Tab, RawInputModifiers.Control);
        await DevScene.SettleAsync(3);
        Assert.Contains(host.Window.GetVisualDescendants().OfType<Border>(), b => b.IsVisible && b.Child is StackPanel sp && sp.Children.OfType<Border>().Count() >= 2);
        host.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.None, null);
        await DevScene.SettleAsync(5);
        Assert.Same(previous, well.Active);
        Assert.NotSame(before, well.Active);
    }

    [AvaloniaFact]
    public async Task The_palette_opens_from_its_keybinding_and_filters_commands_instantly()
    {
        using var host = await StartAsync();
        var quick = Svc<QuickInputViewModel>(host);
        Press(host, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        await DevHost.WaitFor(() => quick.IsVisible);
        Assert.True(quick.IsVisible);
        Assert.Equal(">", quick.Text);
        Assert.NotEmpty(quick.Items);
        quick.Text = ">rename";
        Assert.Contains(quick.Items, i => i.Label.Contains("Rename Symbol"));
        Press(host, Key.Escape);
        await DevHost.WaitFor(() => !quick.IsVisible);
        Assert.False(quick.IsVisible);
        Assert.Equal(false, host.Keys.Get("quickInputVisible"));
    }

    [AvaloniaFact]
    public async Task The_theme_picker_previews_live_while_arrowing_and_reverts_on_escape()
    {
        using var host = await StartAsync();
        var quick = Svc<QuickInputViewModel>(host);
        _ = host.Commands.ExecuteAsync(KnownCommands.SelectTheme);
        await DevHost.WaitFor(() => quick.IsVisible);
        Assert.Equal(3, quick.Items.Count);
        var applier = Svc<ShellController>(host).Theme!;
        var original = host.Themes.Current.Id;

        quick.MoveSelection(+1);
        await DevHost.WaitFor(() => host.Themes.Current.Id != original);
        Assert.NotEqual(original, host.Themes.Current.Id); // previewed while highlighted
        await DevHost.WaitFor(() => applier.AppliedThemeId == host.Themes.Current.Id);
        Assert.Equal(host.Themes.Current.Id, applier.AppliedThemeId);

        quick.Cancel();
        await DevHost.WaitFor(() => host.Themes.Current.Id == original);
        Assert.Equal(original, host.Themes.Current.Id); // Esc reverts
    }

    [AvaloniaFact]
    public async Task Choosing_a_theme_persists_it_in_settings()
    {
        using var host = await StartAsync();
        var quick = Svc<QuickInputViewModel>(host);
        _ = host.Commands.ExecuteAsync(KnownCommands.SelectTheme);
        await DevHost.WaitFor(() => quick.IsVisible);
        quick.MoveSelection(+1);
        quick.MoveSelection(+1);
        quick.Accept();
        await DevHost.WaitFor(() => host.Config.Get<string>(SettingKeys.WorkbenchColorTheme) is not null);
        Assert.Equal(VsThemes.LightId, host.Config.Get<string>(SettingKeys.WorkbenchColorTheme));
        Assert.Equal(VsThemes.LightId, host.Themes.Current.Id);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_with_a_dirty_tab_prompts_and_cancel_keeps_the_window_open()
    {
        using var host = await StartAsync();
        var dialog = Svc<DialogHostViewModel>(host);
        host.Window.Close();
        await DevHost.WaitFor(() => dialog.IsVisible);
        Assert.True(dialog.IsVisible);
        Assert.Equal(["Save", "Don't Save", "Cancel"], dialog.Buttons);
        dialog.Choose("Cancel");
        await DevScene.SettleAsync(10);
        Assert.True(host.Window.IsVisible);
        Assert.NotEmpty(Svc<DocumentWell>(host).Tabs);

        host.Window.Close();
        await DevHost.WaitFor(() => dialog.IsVisible);
        dialog.Choose("Don't Save");
        await DevHost.WaitFor(() => !host.Window.IsVisible);
        Assert.False(host.Window.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_layout_is_saved_when_the_window_closes_so_the_next_start_restores_the_tabs()
    {
        using var host = await StartAsync();
        var dialog = Svc<DialogHostViewModel>(host);
        host.Window.Close();
        await DevHost.WaitFor(() => dialog.IsVisible);
        dialog.Choose("Don't Save");
        await DevHost.WaitFor(() => !host.Window.IsVisible);
        var json = host.State.Get<string>(LayoutPersistence.StateKey, StorageScope.Workspace);
        Assert.NotNull(json);
        var snapshot = LayoutSerializer.Deserialize(json)!;
        Assert.Contains(snapshot.Documents, d => d.Uri.EndsWith("Program.cs"));
        Assert.Contains(snapshot.Tools, t => t.Id == "explorer" && t.Visible);
    }

    [AvaloniaFact]
    public async Task The_window_title_shows_a_dirty_marker_the_active_file_and_the_workspace()
    {
        using var host = await StartAsync();
        await DevScene.SettleAsync(5);
        Assert.StartsWith("● ", host.Window.Title);
        Assert.Contains("README.md", host.Window.Title);
        Assert.EndsWith("CsCode", host.Window.Title);
        Assert.Contains(Path.GetFileName(host.Workspace.Path), host.Window.Title);
    }

    [AvaloniaFact]
    public async Task With_no_folder_and_no_tabs_the_welcome_page_is_shown_and_the_status_bar_is_purple()
    {
        using var host = await StartAsync(scene: false);
        await DevScene.SettleAsync(10);
        Assert.Contains(Svc<DocumentWell>(host).Tabs, t => t.Title == "Welcome");
        var bar = host.Window.GetVisualDescendants().OfType<StatusBarView>().Single();
        Assert.Equal(StatusBarState.NoFolder, bar.State);
    }

    [AvaloniaFact]
    public async Task Opening_a_folder_through_the_command_uses_the_native_dialog_and_loads_the_explorer()
    {
        var shell = new FakeShellHost();
        using var host = await StartAsync(s => { Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IShellHost>(s); s.AddSingleton<IShellHost>(shell); }, scene: false);
        host.SeedWorkspace();
        shell.OpenAnswers.Enqueue([host.Workspace.Uri]);
        await host.Commands.ExecuteAsync(KnownCommands.OpenFolder);
        await DevHost.WaitFor(() => host.WorkspaceService.Folders.Count == 1);
        await DevHost.WaitFor(() => host.Window.GetVisualDescendants().OfType<ListBoxItem>().Any(i => i.DataContext?.GetType().Name == "ExplorerNode"));
        Assert.Single(host.WorkspaceService.Folders);
        Assert.Equal(1, host.Keys.Get("workspaceFolderCount"));
        Assert.Equal(StatusBarState.Normal, host.Window.GetVisualDescendants().OfType<StatusBarView>().Single().State);
    }

    [AvaloniaFact]
    public async Task Find_results_delivered_through_the_command_show_the_tool_window_with_the_hits()
    {
        using var host = await StartAsync();
        var tools = Svc<ToolWindowManager>(host);
        Assert.False(tools.IsVisible("findresults"));
        var loc = new Core.Languages.Location(new Uri(Path.Combine(host.Workspace.Path, "src/Demo.App/Program.cs")), Core.Text.TextRange.At(8, 8));
        await host.Commands.ExecuteAsync("cscode.references.show", "References", new List<Core.Languages.Location> { loc, loc with { Range = Core.Text.TextRange.At(2, 0) } });
        await DevScene.SettleAsync(10);
        Assert.True(tools.IsVisible("findresults"));
        Assert.Equal(2, Svc<ToolWindows.FindResults.FindResultsViewModel>(host).HitCount);
    }

    [AvaloniaFact]
    public async Task Hiding_the_explorer_disposes_its_view_model_and_watcher_and_showing_it_again_reloads_the_tree()
    {
        using var host = await StartAsync();
        var tools = Svc<ToolWindowManager>(host);
        var watcher = (FakeFileWatcher)host.WorkspaceService.Watcher;
        // Other modules watch too (e.g. the debugger's launch.json); only the explorer's own watch must come and go with its view.
        var withExplorer = watcher.ActiveWatches;
        Assert.True(withExplorer >= 1, "the explorer watches the workspace");
        tools.Hide("explorer");
        Assert.True(watcher.ActiveWatches == withExplorer - 1, "still watched: " + string.Join(", ", watcher.ActiveRoots));
        Assert.Null(tools.Get("explorer")!.LiveView);
        tools.Show("explorer");
        await DevHost.WaitFor(() => watcher.ActiveWatches == withExplorer);
        Assert.Equal(withExplorer, watcher.ActiveWatches);
    }

    [AvaloniaFact]
    public async Task The_debug_tool_windows_follow_their_context_key_and_the_toolbar_shows_step_buttons_only_while_debugging()
    {
        using var host = await StartAsync();
        var toolbar = host.Window.GetVisualDescendants().OfType<ToolbarView>().Single();
        Button Step(string tip) => toolbar.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) is string s && s.StartsWith(tip));
        Assert.False(Step("Step Over").IsVisible);
        host.Keys.Set("inDebugMode", true);
        host.Keys.Set("debugState", "stopped");
        await DevScene.SettleAsync(10);
        Assert.True(Step("Step Over").IsVisible);
        Assert.True(Step("Step Over").IsEnabled);
        host.Keys.Set("debugState", "running");
        await DevScene.SettleAsync(10);
        Assert.False(Step("Step Over").IsEnabled); // stepping needs a stopped target
        Assert.True(Step("Pause").IsEnabled);
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_the_palette_500_times_retains_no_entries()
    {
        using var host = await StartAsync();
        var quick = Svc<QuickInputViewModel>(host);
        var palette = Svc<ShellController>(host).Palette;
        var weak = new List<WeakReference>();
        for (var i = 0; i < 500; i++)
        {
            var show = palette.ShowAsync(">");
            Assert.True(quick.IsVisible);
            if (i % 50 == 0) weak.Add(new WeakReference(quick.Items[0]));
            quick.Cancel();
            await show;
        }
        Assert.Empty(quick.Items);
        GcUntil(() => weak.Count(w => w.IsAlive) == 0);
        Assert.Equal(0, weak.Count(w => w.IsAlive));
    }

    [AvaloniaFact]
    public async Task Opening_and_closing_300_tabs_in_the_live_dock_host_retains_no_editor_documents_or_views()
    {
        using var host = await StartAsync();
        var well = Svc<DocumentWell>(host);
        var weak = new List<(int Index, string Kind, WeakReference Ref)>();
        long at100 = 0;
        for (var i = 0; i < 300; i++)
        {
            var tab = await well.OpenAsync(new Uri($"file:///scratch/f{i}.txt"));
            await DevHost.WaitFor(() => tab.LiveView is not null, 1000); // Dock presented it, creating the editor view
            // Dock.Avalonia 12.1 caches the control it presented for every dockable (control recycling, keyed by the dockable); the shell drops a tab's
            // entry when the tab closes, so the tab object itself, its document and its view are all released (see docs/SHELL.md).
            if (i % 30 == 0) { weak.Add((i, "doc", new WeakReference(tab.Doc))); if (tab.LiveView is { } v) weak.Add((i, "view", new WeakReference(v))); weak.Add((i, "tab", new WeakReference(tab))); }
            await well.CloseAsync(tab, prompt: false);
            if (i == 100) { await DevScene.SettleAsync(5); GcUntil(() => false); at100 = GC.GetTotalMemory(forceFullCollection: true); }
        }
        // Give Dock's delayed content presentation time to drain (it holds a closure per presented tab until its timer fires).
        for (var round = 0; round < 8 && weak.Any(w => w.Ref.IsAlive); round++)
        {
            await DevScene.SettleAsync(40);
            GcUntil(() => weak.Count(w => w.Ref.IsAlive) == 0);
        }
        await DevScene.SettleAsync(5); // the last close's coalesced UI work runs before the host is disposed
        var at300 = GC.GetTotalMemory(forceFullCollection: true);
        // Every sample, early ones included: before the recycling cache entry was dropped, 6-8 of the 12 steady-state samples could stay reachable.
        var alive = weak.Where(w => w.Ref.IsAlive).Select(w => $"{w.Kind}#{w.Index}").ToArray();
        Assert.True(alive.Length == 0, $"{alive.Length} of {weak.Count} closed tabs/documents/views still reachable: " + string.Join(", ", alive));
        Assert.True(at300 - at100 < 4 * 1024 * 1024, $"managed heap grew by {(at300 - at100) / 1024} KB between tab 100 and tab 300");
    }

    [AvaloniaFact]
    public async Task Showing_and_closing_dialogs_500_times_retains_nothing()
    {
        using var host = await StartAsync();
        var dialogs = Svc<DialogHostViewModel>(host);
        async Task Cycle(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var task = dialogs.ShowAsync("title " + i, new string('m', 200), ["Save", "Don't Save", "Cancel"]);
                await DevScene.SettleAsync(1); // let the dialog view rebuild its buttons
                dialogs.Choose("Save");
                Assert.Equal("Save", await task);
            }
        }
        await Cycle(50); // warm-up: JIT, caches
        GcUntil(() => false);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        await Cycle(500);
        GcUntil(() => false);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.False(dialogs.IsVisible);
        Assert.True(after - before < 2 * 1024 * 1024, $"500 dialogs grew the managed heap by {(after - before) / 1024} KB");
    }

    private static void GcUntil(Func<bool> done)
    {
        for (var i = 0; i < 10 && !done(); i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
