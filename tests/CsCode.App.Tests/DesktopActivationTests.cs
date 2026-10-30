using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.App.Background;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// What the system asks of the running app over the REAL composition (macOS raises these through <see cref="AvaloniaDesktopHost"/>; the test
/// lifetime raises them on any OS): documents handed over (Finder's Open With, <c>open file</c>, a drop on the Dock icon) open as the command
/// line's do, a click on the Dock icon brings the window back, and a quit from outside (⌘Q, the Dock's Quit, log out) takes the orderly way out.
/// </summary>
public class DesktopActivationTests
{
    // ------------------------------------------------------------------ file activation and reopen

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Files_handed_over_before_the_window_opens_wait_for_it_and_open_as_editors()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("handed-over.txt", "from Finder\n");
        await using var run = new RealAppRun(env);
        run.Lifetime.Activate(file); // at launch: the services are still being built, no window yet
        await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();
        Assert.True(await DevHost.WaitFor(() => well.Find(new Uri(file)) is not null), "the file handed over at launch was not opened");
        Assert.Same(well.Find(new Uri(file)), well.ActiveEditor);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_folder_handed_over_opens_as_the_workspace_and_a_file_as_an_editor_in_the_running_window()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("later.txt", "dropped on the Dock icon\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();
        await DevHost.WaitFor(() => well.Tabs.Count > 0); // the deferred start ran (the Welcome page)

        run.Lifetime.Activate(env.Workspace.Path);
        var workspace = run.Services.GetRequiredService<IWorkspaceService>();
        Assert.True(await DevHost.WaitFor(() => workspace.Folders.Any(f => f.LocalPath.TrimEnd('/') == env.Workspace.Path.TrimEnd('/'))),
            "the folder handed over did not open as the workspace");
        run.Lifetime.Activate(file);
        Assert.True(await DevHost.WaitFor(() => well.Find(new Uri(file)) is not null), "the file handed over was not opened");
        Assert.True(main.IsVisible);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_click_on_the_Dock_icon_restores_a_minimised_window()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        main.WindowState = WindowState.Minimized;
        run.Lifetime.Activate(); // reopen: no paths
        Assert.Equal(WindowState.Normal, main.WindowState);
        Assert.True(main.IsVisible);
    }

    // ------------------------------------------------------------------ quit from outside

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_quit_from_outside_is_taken_over_and_ends_the_run_through_the_orderly_exit()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();

        Assert.True(run.Lifetime.RequestQuit(), "the lifetime's own quit went ahead (it skips the orderly exit)");
        var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(report); // the orderly exit ran (an abrupt one reports nothing)
        Assert.Equal(0, run.Lifetime.ShutdownExitCode);
        Assert.Null(run.Session.Services);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_quit_from_outside_with_unsaved_changes_asks_to_save_and_cancel_keeps_the_window()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("dirty.txt", "text\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        await OpenDirtyAsync(run, file);
        var dialog = run.Services.GetRequiredService<DialogHostViewModel>();

        Assert.True(run.Lifetime.RequestQuit());
        await DevHost.WaitFor(() => dialog.IsVisible);
        Assert.Equal(["Save", "Don't Save", "Cancel"], dialog.Buttons);
        Assert.True(run.Lifetime.RequestQuit(), "a second ⌘Q while the prompt is up must not go ahead"); // and asks nothing more
        dialog.Choose("Cancel");
        await DevScene.SettleAsync(10);
        Assert.True(main.IsVisible);
        Assert.False(run.Session.Ended.IsCompleted);

        Assert.True(run.Lifetime.RequestQuit()); // the user quits again: asked again
        await DevHost.WaitFor(() => dialog.IsVisible);
        dialog.Choose("Don't Save");
        Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_log_out_with_nothing_unsaved_goes_on_with_state_and_layout_written_before_the_window_closes()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("kept.txt", "reopened next time\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        await run.Services.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(file));
        run.Services.GetRequiredService<IStateStorage>().Set("logout.test", "written-at-logout");

        Assert.False(run.Lifetime.RequestQuit(osShutdown: true), "a log out with nothing unsaved was held up");
        Assert.False(main.IsVisible);
        Assert.True(run.Session.Ended.IsCompleted, "the exit did not run before the system's answer"); // the process ends right after it
        Assert.Null(await run.Session.Ended);
        Assert.True(await DevHost.WaitFor(() => run.Lifetime.ShutdownExitCode is not null), "the run did not end");

        await using var after = AppServices.Build([], env.Configure);
        var state = after.GetRequiredService<IStateStorage>();
        Assert.Equal("written-at-logout", state.Get<string>("logout.test"));
        await after.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var layout = state.Get<string>(LayoutPersistence.StateKey, StorageScope.Workspace);
        Assert.NotNull(layout);
        Assert.Contains("kept.txt", layout);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_log_out_with_unsaved_changes_is_paused_with_the_window_in_front_its_save_prompt_up_and_the_user_told()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("unsaved.txt", "text\n");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        await OpenDirtyAsync(run, file);
        var dialog = run.Services.GetRequiredService<DialogHostViewModel>();
        main.WindowState = WindowState.Minimized;

        Assert.True(run.Lifetime.RequestQuit(osShutdown: true), "the log out went ahead over unsaved changes");
        Assert.Equal(WindowState.Normal, main.WindowState);
        await DevHost.WaitFor(() => dialog.IsVisible);
        Assert.Equal(["Save", "Don't Save", "Cancel"], dialog.Buttons);
        var center = run.Services.GetRequiredService<NotificationCenter>();
        Assert.True(await DevHost.WaitFor(() => center.History.Any(n => n.Message.Contains("paused the log out"))), "the user was not told why the log out stopped");

        dialog.Choose("Don't Save");
        Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30))); // then the orderly exit
    }

    // From review: in background mode the system's requests go to the session through the launcher's show request, which the unclaimed warm spare
    // refuses (it has no session): a click on its Dock icon, or documents dropped on it, did nothing and said nothing. The spare now opens them as
    // cscode would from a terminal, which claims a spare (this one) and shows them there.
    [AvaloniaFact(Timeout = 180_000)]
    public async Task Documents_handed_to_the_warm_spare_open_in_a_session_it_takes_for_them()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var env = new RealAppHome();
        env.WriteUserSettings("""{ "workbench.startupEditor": "none", "cscode.background.showTrayIcon": false }""");
        var file = env.Workspace.WriteFile("to-the-spare.txt", "dropped on the warm spare's Dock icon\n");
        var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? env.Home.Path : null);
        var store = new SessionStore(env.Home.Combine("data"));
        var host = BackgroundHost.Start(CommandLineOptions.Parse(["--host", "--spare", "--headless-test"]), new CrashHandler(env.CrashLogs.Path), runtime, store, out _);
        Assert.NotNull(host);
        var previous = App.Background;
        App.Background = host;
        try
        {
            await using var run = new RealAppRun(env);
            Assert.True(await DevHost.WaitFor(() => host.State == ProcessStates.Spare, 60_000), "not a spare: " + host.State);

            run.Lifetime.Activate(file);
            Assert.True(await DevHost.WaitFor(() => host.State == ProcessStates.Shown, 60_000), "the spare did not take a session and show it: " + host.State);
            var well = run.Services.GetRequiredService<DocumentWell>();
            Assert.True(await DevHost.WaitFor(() => well.Find(new Uri(file)) is not null), "the file handed to the spare was not opened");

            var quit = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Quit, Force = true }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)));
            Assert.True(quit.Ok == true, quit.Error);
            await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            App.Background = previous;
            host.Dispose();
        }
    }

    // ------------------------------------------------------------------ the Avalonia side

    [Fact]
    public void A_Dock_icon_click_is_a_reopen_and_other_activations_are_not_the_app_s_concern()
    {
        Assert.Same(DesktopActivation.Reopen, AvaloniaDesktopHost.ToActivation(new Avalonia.Controls.ApplicationLifetimes.ActivatedEventArgs(Avalonia.Controls.ApplicationLifetimes.ActivationKind.Reopen)));
        Assert.Null(AvaloniaDesktopHost.ToActivation(new Avalonia.Controls.ApplicationLifetimes.ActivatedEventArgs(Avalonia.Controls.ApplicationLifetimes.ActivationKind.Background)));
        Assert.Empty(AvaloniaDesktopHost.ToActivation(new Avalonia.Controls.ApplicationLifetimes.FileActivatedEventArgs([]))!.Paths);
    }

    [Fact]
    public void Outside_a_quit_Apple_event_no_quit_counts_as_the_end_of_the_desktop_session()
    {
        Assert.Equal(0x7768793Fu, MacQuitReason.Code("why?")); // kAEQuitReason
        // Off macOS always false; on macOS no Apple event is being handled here, which is a plain quit (⌘Q from the menu sends none).
        Assert.False(MacQuitReason.IsSessionEnding());
    }

    /// <summary>Opens <paramref name="path"/> in the real editor and types one character at the end, so a close has something to ask about.</summary>
    private static async Task OpenDirtyAsync(RealAppRun run, string path)
    {
        var doc = await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(path)) ?? throw new InvalidOperationException("not opened");
        var end = doc.PositionAt(doc.Length);
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(end, end), "x")]));
        Assert.True(doc.IsDirty);
    }
}
