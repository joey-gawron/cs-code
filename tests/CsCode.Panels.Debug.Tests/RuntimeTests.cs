using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.ToolWindows;
using CsCode.Panels.Debug.Variables;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>The app-lifetime <see cref="DebugPanelsRuntime"/>: the <c>debug.addToWatch</c> command and what it contributes.</summary>
public class RuntimeTests
{
    /// <summary>Records <see cref="IViewActivator.ShowViewAsync"/> calls; <see cref="OnShow"/> plays the shell (creates/attaches the view).</summary>
    private sealed class TestViewActivator : IViewActivator
    {
        public List<string> Shown { get; } = new();
        public Func<string, bool>? OnShow { get; set; }

        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default)
        {
            Shown.Add(viewId);
            return ValueTask.FromResult(OnShow?.Invoke(viewId) ?? false);
        }
    }

    private static void Hide(Control view)
    {
        (view as IDisposable)?.Dispose();
        view.DataContext = null;
    }

    private static Task AddWatchCommand(Scenario sc) =>
        sc.Provider.GetRequiredService<ICommandService>().ExecuteAsync(DebugPanelsRuntime.AddToWatchCommand).AsTask();

    [AvaloniaFact]
    public async Task Add_watch_asks_with_an_input_box_when_the_watch_windows_view_exists_but_is_not_on_screen()
    {
        // Regression (dbgp-int-3): the shell creates the views of every auto-shown debug window but only one tab of the group is on screen.
        // The command used to start the inline editor of the hidden Watch tab (nothing visible happened) and skipped the input box.
        using var sc = new Scenario();
        var view = sc.View("debug.watch"); // created, but its tab is not the active one: not in any visual tree
        var vm = (WatchViewModel)view.DataContext!;
        sc.Rig.Window.InputResponses.Enqueue("count * 2");

        await AddWatchCommand(sc);
        Assert.Single(sc.Rig.Window.InputPrompts);
        Assert.False(vm.NewRow.IsEditing);
        Assert.Contains(sc.Rig.Debug.Watches, w => w.Expression == "count * 2");
        Hide(view);
    }

    [AvaloniaFact]
    public async Task Add_watch_starts_the_inline_editor_when_the_watch_window_is_on_screen()
    {
        using var sc = new Scenario();
        var view = sc.View("debug.watch");
        var vm = (WatchViewModel)view.DataContext!;
        var window = new Window { Width = 600, Height = 300, Content = view };
        window.Show();

        await AddWatchCommand(sc);
        Assert.Empty(sc.Rig.Window.InputPrompts);
        Assert.True(vm.NewRow.IsEditingExpression);
        window.Content = null;
        window.Close();
        Hide(view);
    }

    [AvaloniaFact]
    public async Task Add_watch_brings_the_watch_window_forward_through_the_view_activator()
    {
        var activator = new TestViewActivator();
        using var sc = new Scenario(configure: s => s.AddSingleton<IViewActivator>(activator));
        Control? view = null;
        var window = new Window { Width = 600, Height = 300 };
        activator.OnShow = id =>
        {
            if (id != "debug.watch") return false;
            view = sc.View(id); // what the shell does: create the view (or select its tab) and put it on screen
            window.Content = view;
            window.Show();
            return true;
        };

        await AddWatchCommand(sc);
        Assert.Equal(["debug.watch"], activator.Shown);
        Assert.Empty(sc.Rig.Window.InputPrompts);
        Assert.True(((WatchViewModel)view!.DataContext!).NewRow.IsEditingExpression);
        window.Content = null;
        window.Close();
        Hide(view);
    }

    [AvaloniaFact]
    public async Task Add_watch_falls_back_to_the_input_box_when_the_view_activator_cannot_show_the_window()
    {
        var activator = new TestViewActivator { OnShow = _ => false };
        using var sc = new Scenario(configure: s => s.AddSingleton<IViewActivator>(activator));
        sc.Rig.Window.InputResponses.Enqueue("x");

        await AddWatchCommand(sc);
        Assert.Equal(["debug.watch"], activator.Shown);
        Assert.Single(sc.Rig.Window.InputPrompts);
        Assert.Equal("x", Assert.Single(sc.Rig.Debug.Watches).Expression);
    }

    [AvaloniaFact]
    public async Task Add_watch_with_an_expression_adds_it_and_brings_the_watch_window_forward_once()
    {
        // As Visual Studio does when Add Watch comes from the editor or from Locals with the expression already chosen.
        var activator = new TestViewActivator { OnShow = _ => true };
        using var sc = new Scenario(configure: s => s.AddSingleton<IViewActivator>(activator));

        await sc.Provider.GetRequiredService<ICommandService>().ExecuteAsync(DebugPanelsRuntime.AddToWatchCommand, "count * 2").AsTask();
        Assert.Equal(["debug.watch"], activator.Shown);
        Assert.Empty(sc.Rig.Window.InputPrompts);
        Assert.Equal("count * 2", Assert.Single(sc.Rig.Debug.Watches).Expression);
    }

    [AvaloniaFact]
    public void Ctrl_alt_w_is_contributed_for_add_watch_while_debugging_and_withdrawn_with_the_runtime()
    {
        // Regression (dbgp-int-6): the shortcut the brief asks for was bound nowhere.
        var registry = new ContributionRegistry();
        var sc = new Scenario(stopped: false, configure: s => s.AddSingleton<IContributionRegistry>(registry));
        _ = sc.Provider.GetRequiredService<DebugPanelsRuntime>();
        var binding = Assert.Single(registry.Of<KeybindingContribution>().Items);
        Assert.Equal(DebugPanelsRuntime.AddToWatchCommand, binding.Value.Command);
        Assert.Equal("ctrl+alt+w", binding.Value.Key);
        Assert.Equal("inDebugMode", binding.Value.When);
        Assert.Equal(ContributionSources.Builtin, binding.SourceId);

        sc.Dispose();
        Assert.Empty(registry.Of<KeybindingContribution>().Items);
    }
}
