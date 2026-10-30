using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Dock.Avalonia.Controls;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// T14 on the REAL composition: Window &gt; Split Editor Right / Down (and the tab menu's New Vertical / Horizontal Document Group) create a document
/// group without Dock.Avalonia throwing. The cause was the shell's data template for editor tabs, which also matched the tab strip's close-button
/// presenters (PART_ClosePresenter / PART_CompactClosePresenter): they then took an inactive tab's shared view host, and moving the tab into the new
/// group failed with "The control Border already has a visual parent".
/// </summary>
public class SplitEditorTests
{
    private static async Task<RealAppHost> StartAsync()
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\n");
            h.Files.WriteFile("b.txt", "beta\n");
            h.Files.WriteFile("c.txt", "gamma\n");
        });
        await host.StartAsync([host.Files.Combine("a.txt"), host.Files.Combine("b.txt"), host.Files.Combine("c.txt")]);
        await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Count == 3);
        await DevScene.SettleAsync(20);
        return host;
    }

    /// <summary>Renders a frame (as the screen would), so every template Dock applies is built and attached.</summary>
    private static void Render(RealAppHost host)
    {
        Dispatcher.UIThread.RunJobs();
        using var _ = host.Window.CaptureRenderedFrame();
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Split_editor_right_and_down_create_document_groups_that_show_their_editors()
    {
        await using var host = await StartAsync();
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var errors = new List<Exception>();
        void OnError(object? s, DispatcherUnhandledExceptionEventArgs e) => errors.Add(e.Exception);
        Dispatcher.UIThread.UnhandledException += OnError;
        try
        {
            Render(host);
            var b = well.EditorTabs.Single(t => t.Title == "b.txt");
            well.Activate(b);
            await DevScene.SettleAsync(5);
            Render(host);

            await host.RunAsync("workbench.action.splitEditorRight");
            Render(host);
            Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 2), "split right made no second group");
            Assert.Same(b, well.ActiveEditor);
            Render(host);
            // Every group's active editor is on screen, in its own group.
            foreach (var g in factory.DocumentGroups())
            {
                var active = Assert.IsType<EditorTab>(g.ActiveDockable);
                Assert.True(await RealAppHost.WaitFor(() => active.LiveView is { } v && v.IsEffectivelyVisible && TopLevel.GetTopLevel(v) is not null), active.Title + " is not shown");
            }

            var c = well.EditorTabs.Single(t => t.Title == "c.txt");
            well.Activate(c);
            await DevScene.SettleAsync(5);
            await host.RunAsync("workbench.action.splitEditorDown");
            Render(host);
            Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 3), "split down made no third group");
            Render(host);
            Assert.Equal(3, well.EditorTabs.Count);
            Assert.Equal(["a.txt", "b.txt", "c.txt"], factory.DocumentGroups().Select(g => ((EditorTab)g.ActiveDockable!).Title).Order());

            // Moving between groups (Window > Move Editor to Next Group) and closing the moved tab work too.
            await host.RunAsync("workbench.action.moveEditorToNextGroup");
            Render(host);
            await host.RunAsync(KnownCommands.CloseActiveEditor);
            Render(host);
            Assert.Equal(2, well.EditorTabs.Count);

            // No close-button presenter of a tab strip ever presents a tab's view host (the cause of T14).
            foreach (var presenter in host.Window.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.Name is "PART_ClosePresenter" or "PART_CompactClosePresenter"))
                Assert.False(presenter.Child is Border { Child: not null } && well.Tabs.Any(t => ReferenceEquals(t.LiveView, ((Border)presenter.Child).Child)), "a close presenter shows a tab's view");
            Assert.Empty(errors);
        }
        finally { Dispatcher.UIThread.UnhandledException -= OnError; }
    }
}
