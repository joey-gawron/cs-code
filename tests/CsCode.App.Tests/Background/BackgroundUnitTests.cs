using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.App.Background;
using CsCode.App.Docking;
using CsCode.App.Menus;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.Background;

public class BackgroundCommandLineTests
{
    [Fact]
    public void Session_switches_are_parsed_and_their_values_are_not_paths()
    {
        using var dir = new TempDirectory("cscode-cli-");
        var o = CommandLineOptions.Parse(["--session", "work", "a.cs:3", "--wait", "--headless-test"], dir.Path);
        Assert.Equal("work", o.Session);
        Assert.True(o.Wait);
        Assert.True(o.HeadlessTest);
        Assert.True(o.IsLauncher);
        Assert.Equal((dir.Combine("a.cs"), 3), (Assert.Single(o.Targets).Path, o.Targets[0].Line!.Value));

        var host = CommandLineOptions.Parse(["--host", "--session", "x", "--seed-from", "y", "--unnamed", "--hidden"]);
        Assert.Equal(("x", "y", true, true, true, false), (host.Session, host.SeedFrom, host.Unnamed, host.Hidden, host.Host, host.IsLauncher));
        Assert.Empty(host.Targets);
        Assert.True(CommandLineOptions.Parse(["--host", "--spare"]).Spare);
        Assert.False(CommandLineOptions.Parse(["--foreground", "f.cs"]).IsLauncher);
        Assert.False(CommandLineOptions.Parse(["--version"]).IsLauncher);
    }

    [Fact]
    public void Quit_takes_an_optional_name_or_all()
    {
        var named = CommandLineOptions.Parse(["--quit", "work"]);
        Assert.Equal((true, "work", false), (named.Quit, named.Session, named.All));
        Assert.Empty(named.Targets);
        var all = CommandLineOptions.Parse(["--quit", "--all"]);
        Assert.Equal((true, null, true), (all.Quit, all.Session, all.All));
        Assert.Null(CommandLineOptions.Parse(["--quit"]).Session);
    }

    [Fact]
    public void The_launcher_maps_the_command_line_to_requests()
    {
        using var dir = new TempDirectory("cscode-cli-");
        Directory.CreateDirectory(dir.Combine("proj"));
        var open = LauncherEntry.ToRequest(CommandLineOptions.Parse(["-n", "proj", "f.cs:2:5", "-w"], dir.Path));
        Assert.Equal((LaunchKind.Open, true, true), (open.Kind, open.NewWindow, open.Wait));
        Assert.Equal([new LaunchTarget(dir.Combine("proj"), true), new LaunchTarget(dir.Combine("f.cs"), false, 2, 5)], open.Targets);
        Assert.Equal(LaunchKind.ListSessions, LauncherEntry.ToRequest(CommandLineOptions.Parse(["--list-sessions"])).Kind);
        Assert.Equal(LaunchKind.Background, LauncherEntry.ToRequest(CommandLineOptions.Parse(["--background"])).Kind);
        var quit = LauncherEntry.ToRequest(CommandLineOptions.Parse(["--quit", "--all"]));
        Assert.Equal((LaunchKind.Quit, true), (quit.Kind, quit.All));
        // T5: ssh:// targets go through the launcher to the session (background mode is the default for `cscode`).
        var remote = LauncherEntry.ToRequest(CommandLineOptions.Parse(["ssh://me@box:2222/home/me/src", "proj"], dir.Path));
        Assert.Equal(["ssh://me@box:2222/home/me/src"], remote.RemoteTargets);
        Assert.Equal([new LaunchTarget(dir.Combine("proj"), true)], remote.Targets);
    }

    [Fact]
    public void The_readme_says_background_mode_is_the_default_and_how_to_run_in_the_foreground()
    {
        var readme = File.ReadAllText(Path.Combine(PublishedAppFixture.RepoRoot(), "README.md"));
        Assert.Contains("background mode is the default", readme);
        Assert.Contains("dotnet run --project src/CsCode.App -- --foreground", readme);
        foreach (var flag in new[] { "--session", "--list-sessions", "--quit", "--foreground" }) Assert.Contains(flag, readme);
        Assert.Contains("tools/publish-app.sh", readme);
    }

    [Fact]
    public void The_help_text_documents_the_stable_interface()
    {
        foreach (var flag in new[] { "--session", "--new-window", "--wait", "--list-sessions", "--background", "--quit", "--all", "--foreground", "--version" })
            Assert.Contains(flag, CommandLineOptions.HelpText);
        Assert.DoesNotContain("--host", CommandLineOptions.HelpText); // internal switches stay out of the help
    }
}

public class TrayMenuModelTests
{
    [Fact]
    public void Running_sessions_come_first_then_saved_ones_new_window_and_quit_all()
    {
        var running = new[]
        {
            new ResidentProcess(10, "e", "work", ProcessStates.Hidden, null, false, 300L * 1024 * 1024, true),
            new ResidentProcess(11, "e", null, ProcessStates.Spare, null, false, 100L * 1024 * 1024, true),
            new ResidentProcess(12, "e", "stuck", ProcessStates.Shown, null, false, null, false),
        };
        var saved = new[] { new SessionSummary("work", false, DateTimeOffset.UtcNow, [], 1), new SessionSummary("old", false, DateTimeOffset.UtcNow, [], 2) };

        var menu = TrayMenuModel.Build(running, saved);

        Assert.Equal("work (hidden, 300 MB)", menu[0].Header);
        Assert.Equal("work", menu[0].Launch!.Session);
        Assert.True(menu[1].IsSeparator);
        var open = menu[2];
        Assert.Equal("Open Session", open.Header);
        Assert.Equal(["old"], open.Children!.Select(c => c.Header)); // running ones are not offered again
        Assert.True(menu[3].Launch!.NewWindow);
        Assert.True(menu[^1].Launch!.All);
        Assert.Equal(LaunchKind.Quit, menu[^1].Launch!.Kind);
    }

    [Fact]
    public void Without_running_sessions_the_menu_says_so()
    {
        var menu = TrayMenuModel.Build([], []);
        Assert.False(menu[0].Enabled);
        Assert.False(menu.Single(e => e.Header == "Open Session").Enabled);
    }
}

public class AutostartEntryTests
{
    [Fact]
    public void The_xdg_entry_is_written_where_xdg_says_and_removed_again()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux XDG autostart");
        using var dir = new TempDirectory("cscode-autostart-");
        string? Env(string k) => k == "XDG_CONFIG_HOME" ? dir.Path : null;

        Assert.Equal(Path.Combine(dir.Path, "autostart", AutostartEntry.FileName), AutostartEntry.PathFor(Env));
        Assert.False(AutostartEntry.IsInstalled(Env));
        AutostartEntry.Install("/opt/cs code/cscode", Env);
        Assert.True(AutostartEntry.IsInstalled(Env));
        var text = File.ReadAllText(AutostartEntry.PathFor(Env)!);
        Assert.Contains("Exec=\"/opt/cs code/cscode\" --background\n", text);
        Assert.Contains("Type=Application", text);
        AutostartEntry.Uninstall(Env);
        Assert.False(AutostartEntry.IsInstalled(Env));
    }

    [Fact]
    public void The_confirmation_says_what_starts_at_login_and_nothing_with_the_defaults()
    {
        Assert.StartsWith("nothing yet", AutostartEntry.WhatStartsAtLogin(new LaunchSettings()));
        Assert.Contains(LaunchSettings.WarmSpareKey, AutostartEntry.WhatStartsAtLogin(new LaunchSettings()));
        Assert.StartsWith("the warm spare;", AutostartEntry.WhatStartsAtLogin(new LaunchSettings { WarmSpare = true }));
        Assert.StartsWith("the sessions that were running", AutostartEntry.WhatStartsAtLogin(new LaunchSettings { RestoreOnLogin = true }));
        Assert.StartsWith("the warm spare and the sessions", AutostartEntry.WhatStartsAtLogin(new LaunchSettings { WarmSpare = true, RestoreOnLogin = true }));
    }

    [Fact]
    public void The_registered_warm_spare_setting_defaults_to_off()
    {
        var property = Assert.Single(BackgroundSettings.Properties, p => p.Key == BackgroundSettings.WarmSpare);
        Assert.Equal(System.Text.Json.JsonValueKind.False, property.Default!.Value.ValueKind);
        Assert.False(new LaunchSettings().WarmSpare); // the launcher's own default agrees
    }

    [Fact]
    public void A_relative_xdg_config_home_is_ignored()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux XDG autostart");
        string? Env(string k) => k switch { "XDG_CONFIG_HOME" => "relative", "HOME" => "/home/u", _ => null };
        Assert.Equal("/home/u/.config/autostart/" + AutostartEntry.FileName, AutostartEntry.PathFor(Env));
    }
}

public class SessionStartupFailureTests
{
    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_session_process_whose_start_up_failed_answers_with_the_reason_releases_its_session_and_quits_on_request()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var env = new RealAppHome();
        // A private runtime directory under the test's CSCODE_HOME: this host's endpoint and locks are the test's alone.
        var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? env.Home.Path : null);
        var store = new SessionStore(env.Home.Combine("data"));
        var host = BackgroundHost.Start(CommandLineOptions.Parse(["--host", "--session", "F", "--headless-test"]), new CrashHandler(env.CrashLogs.Path), runtime, store, out var refused);
        Assert.Null(refused);
        Assert.NotNull(host);
        var previous = App.Background;
        App.Background = host;
        try
        {
            Assert.True(FileLock.IsHeldElsewhere(store.LockPathOf("F")));
            await using var run = new RealAppRun(env, s => s.AddSingleton<CsCode.Core.Platform.IWorkspaceService>(_ => throw new InvalidOperationException("workspace module is broken")));
            Assert.True(await DevHost.WaitFor(() => run.Session.Window is CrashWindow, 60_000), "no crash window");

            // What the launcher sends: the reason comes back at once (not after the 30 s show timeout).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var show = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Show }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));
            Assert.True(clock.ElapsedMilliseconds < 5000, $"the show reply took {clock.ElapsedMilliseconds} ms");
            Assert.NotEqual(true, show.Ok);
            Assert.Contains("CsCode could not start", show.Error);
            Assert.Contains("workspace module is broken", show.Error);
            var hello = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
            Assert.Equal(ProcessStates.Quitting, hello.State);
            // The next launch of F starts a fresh process instead of taking this one for a wedged owner.
            Assert.False(FileLock.IsHeldElsewhere(store.LockPathOf("F")));

            var quit = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Quit, Force = true }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)));
            Assert.True(quit.Ok == true, quit.Error);
            await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, run.Lifetime.ShutdownExitCode);
        }
        finally
        {
            App.Background = previous;
            host.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_warm_spare_whose_start_up_failed_refuses_a_claim_without_keeping_the_session()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var env = new RealAppHome();
        var runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? env.Home.Path : null);
        var store = new SessionStore(env.Home.Combine("data"));
        var host = BackgroundHost.Start(CommandLineOptions.Parse(["--host", "--spare", "--headless-test"]), new CrashHandler(env.CrashLogs.Path), runtime, store, out _);
        Assert.NotNull(host);
        var previous = App.Background;
        App.Background = host;
        try
        {
            await using var run = new RealAppRun(env, s => s.AddSingleton<CsCode.Core.Platform.IWorkspaceService>(_ => throw new InvalidOperationException("workspace module is broken")));
            Assert.True(await DevHost.WaitFor(() => run.Session.Window is CrashWindow, 60_000), "no crash window");

            // The launcher's claim fails with the reason, and the session is free for the fresh process the launcher starts instead.
            var claim = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Claim, Session = "S", Hidden = true }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));
            Assert.NotEqual(true, claim.Ok);
            Assert.Contains("workspace module is broken", claim.Error);
            Assert.False(FileLock.IsHeldElsewhere(store.LockPathOf("S")));

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
}

public class SpareMemoryGraceTests
{
    // From review: the memory monitor does not log the start-up ramp as steady growth for 3 minutes after its creation, but a warm spare creates it
    // when the process starts and restores its session (folder, layout, editors, grammars, language servers) at the claim, minutes or hours later,
    // and a window opens a folder whenever the user does: both start the grace over, through the real composition.
    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_claim_and_a_folder_opened_afterwards_each_start_the_memory_growth_grace_over()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var env = new RealAppHome();
        env.WriteUserSettings("""{ "workbench.startupEditor": "none", "cscode.background.showTrayIcon": false }""");
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
            var monitor = run.Services.GetRequiredService<CsCode.Profiling.MemoryMonitor>();
            Assert.True(monitor.IsStarted); // with the spare's window, long before any claim

            // A new session has no folder: only the claim's restore can start the grace over.
            var claimed = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.True(monitor.GrowthGraceStart < claimed);
            var claim = await Task.Run(() => IpcClient.Request(runtime.EndpointOf(Environment.ProcessId), new IpcMessage { Type = IpcVerbs.Claim, Session = "S", Hidden = true }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30)));
            Assert.True(claim.Ok == true, claim.Error);
            Assert.True(monitor.GrowthGraceStart >= claimed, "the claim's restore did not start the growth grace over");

            var opened = System.Diagnostics.Stopwatch.GetTimestamp();
            await run.Services.GetRequiredService<CsCode.Core.Platform.IWorkspaceService>().OpenFolderAsync(new Uri(env.Workspace.Path + "/"));
            Assert.True(monitor.GrowthGraceStart >= opened, "opening a folder did not start the growth grace over");

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
}

public class SessionShellTests
{
    [Fact]
    public void The_file_menu_has_the_session_commands()
    {
        var file = ShellMenus.Build()[0];
        var ids = Flatten(file).Select(n => n.CommandId).OfType<string>().ToArray();
        foreach (var id in new[] { SessionCommandIds.OpenSession, SessionCommandIds.SwitchSession, SessionCommandIds.SaveSessionAs, SessionCommandIds.RenameSession,
                     SessionCommandIds.DeleteSession, SessionCommandIds.CloseWindow, SessionCommandIds.QuitSession, SessionCommandIds.ExitAll, SessionCommandIds.StartAtLogin })
            Assert.Contains(id, ids);

        static IEnumerable<MenuNode> Flatten(MenuNode n) => new[] { n }.Concat((n.Children ?? []).SelectMany(Flatten));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task File_New_Window_is_bound_to_ctrl_shift_N_and_cmd_shift_N_in_both_keymaps_and_stays_disabled_in_a_foreground_run()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var bindings = host.Get<CsCode.Core.Platform.IKeybindingService>();
        var stroke = CsCode.Core.Platform.KeyStrokeParser.ParseStroke(OperatingSystem.IsMacOS() ? "cmd+shift+n" : "ctrl+shift+n");
        foreach (var preset in new[] { "visualstudio", "vscode" })
        {
            await bindings.SetPresetAsync(preset, TestContext.Current.CancellationToken);
            Assert.True(bindings.GetBindingsFor(SessionCommandIds.NewWindow).Any(b => b.Chord.Count == 1 && b.Chord[0] == stroke), $"{preset}: New Window is not on {stroke}");
        }
        // A foreground run is one window (docs/SESSIONS.md): no session process registers the command, so the item is there but disabled.
        host.Shell.Menus.Refresh();
        var item = host.Shell.Menus.Roots.Single(r => r.Header == "_File").Children.Single(c => c.Header == "New Win_dow");
        Assert.Equal(SessionCommandIds.NewWindow, item.CommandId);
        Assert.False(item.IsEnabled);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_close_interceptor_keeps_the_window_open_and_the_session_name_is_in_the_title()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var reasons = new List<WindowCloseReason>();
        host.Window.CloseInterceptor = reason => { reasons.Add(reason); return true; };

        host.Window.Close();
        await DevScene.SettleAsync(5);
        Assert.True(host.Window.IsVisible, "an intercepted close must not close the window");
        Assert.Equal([WindowCloseReason.WindowClosing], reasons);

        host.Window.SessionName = "work";
        Assert.StartsWith("work", host.Window.Title);
        Assert.EndsWith("CsCode", host.Window.Title);
        host.Window.CloseInterceptor = null;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_view_state_set_for_the_next_view_is_applied_when_the_view_is_created()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var file = host.Files.WriteFile("v.cs", string.Join("\n", Enumerable.Range(0, 50).Select(i => $"// line {i}")));
        var well = host.Get<DocumentWell>();
        var tab = await well.OpenAsync(new Uri(file));
        tab.DisposeView();
        tab.SetViewStateForNextView(new EditorViewState(new TextPosition(30, 4), new TextPosition(30, 4), new TextPosition(30, 4), 0, 0, [], 0));

        tab.EnsureHost();
        await DevScene.SettleAsync(5);

        Assert.Equal(new TextPosition(30, 4), tab.Controller!.Caret);
    }
}
