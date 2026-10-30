using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using Dock.Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// <c>terminal.integrated.confirmOnExit</c> and the main window's own close (the shell's Save prompts, its layout save, its floating windows), over the
/// real composition with fake PTYs: the question is asked also for terminals only ever shown in a floating window (only the main window decides its
/// close: a floating window's Closing is not raised then), and a close the question holds leaves the window working when the user cancels.
/// </summary>
public sealed class ConfirmOnExitWindowTests
{
    private static async Task<(TerminalAppHost App, List<FakePtyHost> Hosts)> StartAsync()
    {
        var hosts = new List<FakePtyHost>();
        var app = await TerminalAppHost.StartAsync(settings: new() { ["terminal.integrated.confirmOnExit"] = "always" },
            configure: s => s.AddSingleton(sp => CsCode.Terminal.ServiceCollectionExtensions.CreateService(sp, () =>
            {
                var h = new FakePtyHost();
                lock (hosts) hosts.Add(h);
                return h;
            })));
        return (app, hosts);
    }

    private static NotificationItem? Question(TerminalAppHost app) =>
        app.Get<NotificationCenter>().Toasts.FirstOrDefault(t => t.Message.Contains("terminate the active terminal session", StringComparison.Ordinal));

    private static void Answer(NotificationItem question, string action) => question.InvokeCommand.Execute(question.Actions.First(a => a.Title == action));

    private static LayoutSnapshot SavedLayout(TerminalAppHost app) =>
        LayoutSerializer.Deserialize(app.Get<IStateStorage>().Get<string>(LayoutPersistence.StateKey, StorageScope.Application))!;

    private static string[] SavedDocuments(TerminalAppHost app) =>
        SavedLayout(app).Documents.Select(d => Path.GetFileName(new Uri(d.Uri).LocalPath)).Order(StringComparer.Ordinal).ToArray();

    private static bool SavesFloating(TerminalAppHost app, string toolId) => SavedLayout(app).Floating?.Any(w => w.Tools.Contains(toolId)) == true;

    /// <summary>Waits longer than the layout's debounce (1 s) while the UI keeps running.</summary>
    private static async Task WaitPastTheSaveDebounceAsync()
    {
        var until = DateTime.UtcNow.AddMilliseconds(2500);
        while (DateTime.UtcNow < until) await TerminalAppHost.SettleAsync(5);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_main_windows_close_asks_for_terminals_only_ever_shown_in_a_floating_window()
    {
        var (app, hosts) = await StartAsync();
        await using var _ = app;
        // Restored floating, or shown again from where it last floated: shown and floated in one turn, before the main window lays it out.
        app.Shell.Tools.Float(TerminalPanelRuntime.ViewId);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { } v && TopLevel.GetTopLevel(v) is HostWindow && hosts.Count == 1),
            "the Terminal did not show (with its first terminal) in a floating window");
        var closed = false;
        app.Window.Closed += (_, _) => closed = true;

        app.Window.Close(); // the main window's X
        Assert.True(await TerminalAppHost.WaitFor(() => Question(app) is not null), "the running terminal was killed without the question");
        Assert.False(closed);
        Answer(Question(app)!, "Terminate");
        Assert.True(await TerminalAppHost.WaitFor(() => closed), "Terminate did not close the window");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Cancelling_the_question_after_the_save_prompts_leaves_the_window_working_and_the_next_close_asks_again()
    {
        var (app, hosts) = await StartAsync();
        await using var _ = app;
        var shell = app.Shell;
        var a = app.Sandbox.WriteFile("a.txt", "alpha\n");
        var b = app.Sandbox.WriteFile("b.txt", "beta\n");
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await TerminalAppHost.WaitFor(() => hosts.Count == 1));
        await shell.Well.OpenAsync(new Uri(a));
        var tabB = await shell.Well.OpenAsync(new Uri(b));
        tabB.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]);
        shell.Tools.Float("output");
        Assert.True(await TerminalAppHost.WaitFor(() => shell.Tools.IsFloating("output")));
        await TerminalAppHost.SettleAsync(10);
        var dialogs = app.Get<DialogHostViewModel>();
        var closed = false;
        app.Window.Closed += (_, _) => closed = true;

        // The Save prompt, then the terminal's question; Cancel.
        app.Window.Close();
        Assert.True(await TerminalAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        dialogs.Choose("Don't Save");
        Assert.True(await TerminalAppHost.WaitFor(() => Question(app) is not null), "no question about the running terminal");
        Assert.Equal(["a.txt", "b.txt"], SavedDocuments(app)); // saved when the close began
        Answer(Question(app)!, "Cancel");
        await TerminalAppHost.SettleAsync(10);
        Assert.False(closed);
        Assert.True(app.Window.IsVisible);
        Assert.Empty(shell.Well.EditorTabs); // (the Save prompts' close went ahead: the question comes after them)

        // The window works as before: its floating window stays, layout saves go on.
        Assert.True(shell.Tools.IsFloating("output"), "the floating Output window was docked back");
        Assert.False(shell.Factory.IsShuttingDown);
        var again = await shell.Well.OpenAsync(new Uri(a));
        await WaitPastTheSaveDebounceAsync();
        Assert.Equal(["a.txt"], SavedDocuments(app));
        Assert.True(SavesFloating(app, "output"));

        // The next close asks for the dirty document again, then the terminal; Terminate closes, with the layout of that close.
        again.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]);
        app.Window.Close();
        Assert.True(await TerminalAppHost.WaitFor(() => dialogs.IsVisible), "the next close skipped the Save prompt");
        dialogs.Choose("Don't Save");
        Assert.True(await TerminalAppHost.WaitFor(() => Question(app) is not null), "no question the second time");
        Answer(Question(app)!, "Terminate");
        Assert.True(await TerminalAppHost.WaitFor(() => closed), "Terminate did not close the window");
        Assert.Equal(["a.txt"], SavedDocuments(app));
        Assert.True(SavesFloating(app, "output"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Terminate_after_the_save_prompts_keeps_every_tab_and_floating_window_in_the_saved_layout()
    {
        var (app, hosts) = await StartAsync();
        await using var _ = app;
        var shell = app.Shell;
        var a = app.Sandbox.WriteFile("a.txt", "alpha\n");
        var b = app.Sandbox.WriteFile("b.txt", "beta\n");
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await TerminalAppHost.WaitFor(() => hosts.Count == 1));
        await shell.Well.OpenAsync(new Uri(a));
        var tabB = await shell.Well.OpenAsync(new Uri(b));
        tabB.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]);
        shell.Tools.Float("output");
        Assert.True(await TerminalAppHost.WaitFor(() => shell.Tools.IsFloating("output")));
        await TerminalAppHost.SettleAsync(10);
        var dialogs = app.Get<DialogHostViewModel>();
        var closed = false;
        app.Window.Closed += (_, _) => closed = true;

        app.Window.Close();
        Assert.True(await TerminalAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        dialogs.Choose("Don't Save");
        Assert.True(await TerminalAppHost.WaitFor(() => Question(app) is not null), "no question about the running terminal");
        await WaitPastTheSaveDebounceAsync(); // the user takes a while to answer
        Answer(Question(app)!, "Terminate");
        Assert.True(await TerminalAppHost.WaitFor(() => closed), "Terminate did not close the window");
        Assert.Equal(["a.txt", "b.txt"], SavedDocuments(app));
        Assert.True(SavesFloating(app, "output"));
    }
}
