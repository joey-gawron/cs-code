using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Docking;
using CsCode.App.Menus;
using CsCode.App.Notifications;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// macOS convention over the REAL composition (audit ui-6): closing the main window of a foreground run leaves the app running in the Dock without
/// a window (its services shut down as at an exit), and a click on the Dock icon, documents handed over or New Window build a new window with
/// services of its own; ⌘Q and File &gt; Exit still end the run. The test lifetime says it keeps running without windows, as
/// <see cref="AvaloniaDesktopHost"/> does on macOS only; with the default (Linux, Windows) closing the window still ends the run.
/// </summary>
public class WindowlessRunTests
{
    private static TestDesktopLifetime Mac() => new() { KeepsRunningWithoutWindows = true };

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Closing_the_window_on_a_Mac_keeps_the_run_going_without_a_window_with_its_services_shut_down_and_state_on_disk()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env, lifetime: Mac());
        var main = await run.MainWindowAsync();
        run.Services.GetRequiredService<IStateStorage>().Set("windowless.test", "written-before-close");

        main.Close(); // the close button: not File > Exit
        Assert.True(await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000), "the run did not settle without a window");
        Assert.False(run.Session.Ended.IsCompleted, "closing the window ended the run");
        Assert.Null(run.Lifetime.ShutdownExitCode);
        Assert.Null(run.Session.Window);
        Assert.Null(run.Lifetime.MainWindow);
        Assert.Null(run.Session.Services); // the window's container was disposed
        var report = run.Session.LastWindowReport;
        Assert.NotNull(report); // the orderly shutdown ran, as at an exit
        Assert.True(report.TimedOut.Count == 0 && report.Failures.Count == 0, $"timed out: {string.Join(", ", report.TimedOut)}; failures: {string.Join(" | ", report.Failures)}");

        await using var after = AppServices.Build([], env.Configure);
        Assert.Equal("written-before-close", after.GetRequiredService<IStateStorage>().Get<string>("windowless.test"));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_Dock_click_after_the_last_window_closed_builds_a_new_window_with_services_of_its_own()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env, lifetime: Mac());
        var first = await run.MainWindowAsync();
        var firstServices = run.Services;
        first.Close();
        await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000);

        run.Lifetime.Activate(); // reopen: no paths
        var second = await run.MainWindowAsync();
        Assert.NotSame(first, second);
        Assert.NotSame(firstServices, run.Services);
        Assert.True(second.IsVisible);
        Assert.Same(second, run.Lifetime.MainWindow);
        Assert.False(run.Session.IsWindowless);
        Assert.Equal(2, run.Session.WindowsOpened);

        // And again: the second window's close leaves the run going too.
        second.Close();
        Assert.True(await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000));
        Assert.False(run.Session.Ended.IsCompleted);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_file_handed_over_with_no_window_opens_in_a_new_window()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("from-finder.txt", "opened with no window\n");
        await using var run = new RealAppRun(env, lifetime: Mac());
        (await run.MainWindowAsync()).Close();
        await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000);

        run.Lifetime.Activate(file);
        await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();
        Assert.True(await DevHost.WaitFor(() => well.Find(new Uri(file)) is not null), "the file handed over was not opened in the new window");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Documents_handed_over_while_the_closed_window_s_services_shut_down_open_once_they_are_gone()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("early.txt", "handed over during the shutdown\n");
        await using var run = new RealAppRun(env, lifetime: Mac());
        var first = await run.MainWindowAsync();
        first.Close();
        Assert.False(run.Session.IsWindowless, "the shutdown of the closed window's services is asynchronous"); // still shutting down
        run.Lifetime.Activate(file);

        Assert.True(await DevHost.WaitFor(() => run.Session.Window is MainWindow m && !ReferenceEquals(m, first), 60_000), "no new window after the shutdown");
        Assert.NotNull(run.Session.LastWindowReport); // the new window came after the old services were shut down
        var well = run.Services.GetRequiredService<DocumentWell>();
        Assert.True(await DevHost.WaitFor(() => well.Find(new Uri(file)) is not null), "the file handed over was not opened");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Command_Q_with_no_window_ends_the_run()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env, lifetime: Mac());
        (await run.MainWindowAsync()).Close();
        await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000);

        Assert.True(run.Lifetime.RequestQuit(), "the lifetime's own quit was let through");
        await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, run.Lifetime.ShutdownExitCode);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Command_Q_while_the_closed_window_s_services_shut_down_ends_the_run_once_they_are_gone()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env, lifetime: Mac());
        (await run.MainWindowAsync()).Close();
        Assert.True(run.Lifetime.RequestQuit());
        await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(run.Session.LastWindowReport); // the shutdown ran to its end before the run ended
        Assert.Equal(0, run.Lifetime.ShutdownExitCode);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task File_Exit_and_Command_Q_with_a_window_still_end_the_run_on_a_Mac()
    {
        using var env = new RealAppHome();
        await using (var run = new RealAppRun(env, lifetime: Mac()))
        {
            await run.MainWindowAsync();
            await run.Services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Quit);
            Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(run.Session.IsWindowless);
        }
        await using (var run = new RealAppRun(env, lifetime: Mac()))
        {
            await run.MainWindowAsync();
            Assert.True(run.Lifetime.RequestQuit());
            Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(run.Session.IsWindowless);
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_cancelled_File_Exit_leaves_the_next_close_of_the_window_to_keep_the_run_going()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("dirty.txt", "text\n");
        await using var run = new RealAppRun(env, lifetime: Mac());
        var main = await run.MainWindowAsync();
        var doc = await run.Services.GetRequiredService<INavigationService>().OpenAsync(new Uri(file)) ?? throw new InvalidOperationException("not opened");
        var end = doc.PositionAt(doc.Length);
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(end, end), "x")]));
        var dialog = run.Services.GetRequiredService<DialogHostViewModel>();

        var quit = run.Services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Quit).AsTask();
        await DevHost.WaitFor(() => dialog.IsVisible);
        dialog.Choose("Cancel");
        await quit;
        Assert.False(main.IsExiting);

        main.Close();
        await DevHost.WaitFor(() => dialog.IsVisible);
        dialog.Choose("Don't Save");
        Assert.True(await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000), "the close after a cancelled exit ended the run");
        Assert.False(run.Session.Ended.IsCompleted);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_the_setting_off_closing_the_window_quits_on_a_Mac_too()
    {
        using var env = new RealAppHome();
        env.WriteUserSettings($$"""{ "{{AppSession.KeepRunningSetting}}": false }""");
        await using var run = new RealAppRun(env, lifetime: Mac());
        (await run.MainWindowAsync()).Close();
        Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(run.Session.IsWindowless);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Elsewhere_closing_the_window_still_ends_the_run()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env); // KeepsRunningWithoutWindows false: Linux, Windows
        (await run.MainWindowAsync()).Close();
        Assert.NotNull(await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(run.Session.IsWindowless);
        Assert.Equal(1, run.Session.WindowsOpened);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task New_Window_builds_a_window_when_there_is_none_and_brings_the_foreground_window_forward_otherwise()
    {
        using var env = new RealAppHome();
        await using var run = new RealAppRun(env, lifetime: Mac());
        var first = await run.MainWindowAsync();
        first.WindowState = WindowState.Minimized;
        run.Session.NewWindow(); // a foreground run has one window: it comes back
        Assert.Equal(WindowState.Normal, first.WindowState);
        Assert.Equal(1, run.Session.WindowsOpened);

        first.Close();
        await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000);
        run.Session.NewWindow();
        Assert.NotSame(first, await run.MainWindowAsync());
        Assert.Equal(2, run.Session.WindowsOpened);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Nothing_of_the_closed_window_stays_in_memory()
    {
        using var env = new RealAppHome();
        // A home CsCode has run in before. In a brand-new one the first state write creates data/globalStorage while file watches wait for
        // folders below data/ to appear, and the window stays reachable after its container is disposed (no static path: a native or lane
        // root): docs/handoff/known-issues.md, "first-run retention".
        Directory.CreateDirectory(env.Home.Combine("data/globalStorage"));
        await using var run = new RealAppRun(env, lifetime: Mac());
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var main = await run.MainWindowAsync();
            var services = run.Services;
            object[] watched = [main, services, services.GetRequiredService<DocumentWell>()];
            main.Close();
            await DevHost.WaitFor(() => run.Session.IsWindowless, 30_000);
            await DevScene.SettleAsync(5);
            return watched;
        });
    }

    // ------------------------------------------------------------------ the menus

    [AvaloniaFact]
    public void The_application_menu_s_New_Window_runs_the_app_s_and_carries_shift_command_N_while_no_keybinding_takes_it()
    {
        using var app = NativeMenuExport.BuildApplicationMenu();
        var item = app.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "New Window");
        Assert.Equal(new KeyGesture(Key.N, KeyModifiers.Meta | KeyModifiers.Shift), item.Gesture); // no window: no keybindings, the menu's key
        item.RaiseClick(); // nothing set: nothing happens
        var asked = 0;
        app.NewWindow = () => asked++;
        item.RaiseClick();
        Assert.Equal(1, asked);

        var dock = NativeMenuExport.BuildDockMenu(() => asked++);
        Assert.Equal(["New Window"], dock.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        ((NativeMenuItem)dock.Items[0]).RaiseClick();
        Assert.Equal(2, asked);
    }
}
