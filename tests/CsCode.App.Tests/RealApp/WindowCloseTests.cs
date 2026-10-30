using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Closing the main window with a dirty document over the REAL composition: the layout saved when the close begins (every tab, so the next start
/// restores them) is the one the next start reads, also when the user takes a while to answer the Save prompt (each tab the close closes used to
/// schedule a debounced save that kept only the tabs not closed yet) or closes again while it waits.
/// </summary>
public class WindowCloseTests
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
        await DevScene.SettleAsync(10);
        var c = host.Shell.Well.EditorTabs.Single(t => t.Title == "c.txt");
        c.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]); // last in tab order: the prompt comes after a and b closed
        await DevScene.SettleAsync(5);
        return host;
    }

    private static string[] SavedDocuments(RealAppHost host) =>
        LayoutSerializer.Deserialize(host.Get<IStateStorage>().Get<string>(LayoutPersistence.StateKey, StorageScope.Application))!
            .Documents.Select(d => Path.GetFileName(new Uri(d.Uri).LocalPath)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Waits longer than the layout's debounce (1 s) while the UI keeps running.</summary>
    private static async Task WaitPastTheSaveDebounceAsync()
    {
        var until = DateTime.UtcNow.AddMilliseconds(2500);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            await DevScene.SettleAsync(1);
        }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_slow_answer_to_the_Save_prompt_keeps_every_tab_in_the_saved_layout()
    {
        await using var host = await StartAsync();
        var dialogs = host.Get<DialogHostViewModel>();
        var closed = false;
        host.Window.Closed += (_, _) => closed = true;

        host.Window.Close();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        Assert.Single(host.Shell.Well.EditorTabs); // a and b closed already
        await WaitPastTheSaveDebounceAsync();
        Assert.Equal(["a.txt", "b.txt", "c.txt"], SavedDocuments(host));
        dialogs.Choose("Don't Save");
        Assert.True(await RealAppHost.WaitFor(() => closed), "the window did not close");
        await WaitPastTheSaveDebounceAsync();
        Assert.Equal(["a.txt", "b.txt", "c.txt"], SavedDocuments(host));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Closing_again_while_the_prompt_waits_keeps_every_tab_and_a_cancel_keeps_what_is_open()
    {
        await using var host = await StartAsync();
        var dialogs = host.Get<DialogHostViewModel>();

        host.Window.Close();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        host.Window.Close(); // a second click on X: the prompt is asked again
        await DevScene.SettleAsync(5);
        Assert.True(dialogs.IsVisible);
        await WaitPastTheSaveDebounceAsync();
        Assert.Equal(["a.txt", "b.txt", "c.txt"], SavedDocuments(host));

        // Cancel: the window stays, with what is still open, and the layout says so; saves go on as before.
        dialogs.Choose("Cancel");
        await DevScene.SettleAsync(5);
        Assert.True(host.Window.IsVisible);
        Assert.Equal(["c.txt"], SavedDocuments(host));
        await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        await WaitPastTheSaveDebounceAsync();
        Assert.Equal(["a.txt", "c.txt"], SavedDocuments(host));
    }
}
