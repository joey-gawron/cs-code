using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Editor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The shell's side of the editor view contract (docs/EDITOR.md) with the REAL editor in the real window: views are handed back through
/// <see cref="IEditorViewFactory.DisposeView"/>, inactive tabs are destroyed and recreated with their full view state, and a focused editor gives up
/// keyboard focus before it is destroyed (a focused AvaloniaEdit view stays reachable from the focus / input-method machinery).
/// </summary>
public class EditorViewLifecycleTests
{
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_a_focused_editor_tab_moves_focus_off_the_editor_and_releases_its_view()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("focused.cs", "class C\n{\n    void M() { }\n}\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();
        var editors = run.Services.GetRequiredService<IEditorViewFactory>();
        // Another document stays open: as in Visual Studio, the keyboard moves into the document that takes the closed one's place.
        var other = await OpenPresentedAsync(well, env.Workspace.WriteFile("other.cs", "class D { }\n"));

        var view = await OpenFocusAndCloseAsync(well, editors, main, new Uri(file));

        Assert.True(await DevHost.WaitFor(() => other.Controller?.IsFocused == true, 5000), "the remaining editor did not get the keyboard focus");
        for (var round = 0; round < 10 && view.TryGetTarget(out _); round++)
        {
            await DevScene.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(view.TryGetTarget(out _), "the closed editor view is still reachable");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_editor_view_evicted_after_it_was_shown_is_released_although_Dock_caches_its_tab_content()
    {
        using var env = new RealAppHome();
        env.WriteUserSettings("{ \"cscode.shell.liveEditorViews\": 1 }");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();

        var first = await OpenPresentedAsync(well, env.Workspace.WriteFile("first.cs", "class A { }\n"));
        var view = WeakView(first);
        // Two more tabs with a budget of one: the first tab's view is destroyed while the tab stays open (and Dock keeps what it presented for it).
        await OpenPresentedAsync(well, env.Workspace.WriteFile("b.cs", "class B { }\n"));
        await OpenPresentedAsync(well, env.Workspace.WriteFile("c.cs", "class C { }\n"));
        Assert.Null(first.LiveView);

        for (var round = 0; round < 10 && view.TryGetTarget(out _); round++)
        {
            await DevScene.SettleAsync(5);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(view.TryGetTarget(out _), "the evicted editor view is still reachable");
        Assert.Contains(first, well.Tabs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Control> WeakView(EditorTab tab) => new(tab.LiveView!);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_evicted_editor_tab_is_recreated_with_its_selection_and_scroll_position_when_activated_again()
    {
        using var env = new RealAppHome();
        env.WriteUserSettings("{ \"cscode.shell.liveEditorViews\": 1 }");
        var lines = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"// line {i}"));
        var first = env.Workspace.WriteFile("long.cs", lines + "\n");
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();

        var tab = await OpenPresentedAsync(well, first);
        var controller = tab.Controller!;
        var selection = TextRange.FromLines(300, 3, 301, 6);
        controller.SetSelection(selection, reveal: true);
        Assert.True(await WaitLaidOut(() => controller.GetViewState().VerticalOffset > 1000), "the editor did not scroll to the selection");
        var before = controller.GetViewState();

        // Two more tabs with a budget of one: the first tab's view is destroyed.
        await OpenPresentedAsync(well, env.Workspace.WriteFile("b.cs", "class B { }\n"));
        await OpenPresentedAsync(well, env.Workspace.WriteFile("c.cs", "class C { }\n"));
        Assert.Null(tab.LiveView);

        well.Activate(tab);
        Assert.True(await DevHost.WaitFor(() => tab.LiveView is not null, 10_000), "Dock did not present the reactivated tab");
        var again = tab.Controller!;
        Assert.NotSame(controller, again);
        Assert.Equal(selection, again.Selections.Single());
        Assert.Equal(before.Caret, again.Caret);
        Assert.True(await WaitLaidOut(() => Math.Abs(again.GetViewState().VerticalOffset - before.VerticalOffset) < 1),
            $"scroll offset {again.GetViewState().VerticalOffset} after recreation, {before.VerticalOffset} before");
    }

    /// <summary>Opens, focuses and closes a tab in a frame of its own (no local outlives it) and returns a weak reference to its view.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<Control>> OpenFocusAndCloseAsync(DocumentWell well, IEditorViewFactory editors, MainWindow main, Uri file)
    {
        var tab = await OpenPresentedAsync(well, file.LocalPath);
        var view = tab.LiveView!;
        var controller = editors.GetController(view)!;
        controller.Focus();
        Assert.True(await DevHost.WaitFor(() => controller.IsFocused), "the editor did not take keyboard focus");

        await well.CloseAsync(tab, prompt: false);
        await DevScene.SettleAsync(5);

        var focused = main.FocusManager?.GetFocusedElement() as Visual;
        Assert.False(focused is not null && (ReferenceEquals(focused, view) || view.IsVisualAncestorOf(focused)), "keyboard focus is still inside the destroyed editor");
        return new WeakReference<Control>(view);
    }

    private static async Task<EditorTab> OpenPresentedAsync(DocumentWell well, string path)
    {
        var tab = await well.OpenAsync(new Uri(path));
        Assert.True(await DevHost.WaitFor(() => tab.LiveView is not null && tab.Controller is not null, 10_000), $"Dock did not present {path}");
        return tab;
    }

    private static async Task<bool> WaitLaidOut(Func<bool> condition) => await DevHost.WaitFor(() =>
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return condition();
    }, 5000);
}
