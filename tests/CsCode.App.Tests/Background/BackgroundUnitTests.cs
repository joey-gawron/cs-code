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
    public void A_relative_xdg_config_home_is_ignored()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux XDG autostart");
        string? Env(string k) => k switch { "XDG_CONFIG_HOME" => "relative", "HOME" => "/home/u", _ => null };
        Assert.Equal("/home/u/.config/autostart/" + AutostartEntry.FileName, AutostartEntry.PathFor(Env));
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
