using CsCode.App.Startup;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

public class CommandLineTests
{
    [Fact]
    public void Paths_flags_and_unknown_switches_are_separated()
    {
        using var dir = new TempDirectory("cscode-cli-");
        dir.WriteFile("a.cs", "");
        Directory.CreateDirectory(dir.Combine("proj"));
        var o = CommandLineOptions.Parse(["--new-window", "--wait", "a.cs", "proj", "--weird", "-n"], dir.Path);
        Assert.True(o.NewWindow);
        Assert.True(o.Wait);
        Assert.Equal([dir.Combine("a.cs"), dir.Combine("proj")], o.Targets.Select(t => t.Path));
        Assert.Equal(["--weird"], o.UnknownSwitches);
    }

    [Theory]
    [InlineData("--help", true, false)]
    [InlineData("-h", true, false)]
    [InlineData("--version", false, true)]
    [InlineData("-v", false, true)]
    public void Help_and_version_flags(string flag, bool help, bool version)
    {
        var o = CommandLineOptions.Parse([flag]);
        Assert.Equal((help, version), (o.ShowHelp, o.ShowVersion));
    }

    [Fact]
    public void Positions_are_parsed_from_the_end_of_a_path_and_missing_files_keep_their_name()
    {
        using var dir = new TempDirectory("cscode-cli-");
        var o = CommandLineOptions.Parse(["src/x.cs:12:3", "y.cs:7", "--goto", "z.cs:1:2"], dir.Path);
        Assert.Equal((dir.Combine("src", "x.cs"), 12, 3), (o.Targets[0].Path, o.Targets[0].Line, o.Targets[0].Column));
        Assert.Equal((dir.Combine("y.cs"), 7, (int?)null), (o.Targets[1].Path, o.Targets[1].Line, o.Targets[1].Column));
        Assert.Equal((dir.Combine("z.cs"), 1, 2), (o.Targets[2].Path, o.Targets[2].Line, o.Targets[2].Column));
    }

    [Fact]
    public void A_real_file_whose_name_contains_colons_is_not_split()
    {
        using var dir = new TempDirectory("cscode-cli-");
        var weird = dir.WriteFile("notes:1.txt", "x");
        var o = CommandLineOptions.Parse(["notes:1.txt"], dir.Path);
        Assert.Equal(weird, o.Targets.Single().Path);
        Assert.Null(o.Targets.Single().Line);
    }

    [Fact]
    public void No_arguments_means_no_targets_and_help_text_mentions_the_flags()
    {
        Assert.Empty(CommandLineOptions.Parse([]).Targets);
        Assert.Contains("--new-window", CommandLineOptions.HelpText);
        Assert.Contains("--wait", CommandLineOptions.HelpText);
    }
}

public class CrashHandlerTests
{
    [Fact]
    public void A_report_writes_a_log_with_the_exception_chain_and_raises_the_event()
    {
        using var dir = new TempDirectory("cscode-crash-");
        var handler = new CrashHandler(dir.Path);
        (string Summary, string Path, bool Fatal)? seen = null;
        handler.Reported += (s, p, f) => seen = (s, p, f);

        var ex = new InvalidOperationException("outer", new IOException("inner problem"));
        var path = handler.Report(ex, fatal: false, source: "UI");

        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("recovered", text);
        Assert.Contains("outer", text);
        Assert.Contains("inner problem", text);
        Assert.Contains("inner exception 1", text);
        Assert.Equal("InvalidOperationException: outer", seen!.Value.Summary);
        Assert.Equal(path, seen.Value.Path);
        Assert.False(seen.Value.Fatal);
        Assert.Equal(1, handler.ReportCount);
    }

    [Fact]
    public void Fatal_reports_are_marked_and_old_logs_are_pruned()
    {
        using var dir = new TempDirectory("cscode-crash-");
        var clock = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var handler = new CrashHandler(dir.Path, () => clock = clock.AddSeconds(1));
        for (var i = 0; i < CrashHandler.MaxLogsKept + 7; i++) handler.Report(new Exception("e" + i), fatal: i == 0);
        var logs = Directory.GetFiles(dir.Path, "crash-*.log");
        Assert.Equal(CrashHandler.MaxLogsKept, logs.Length);
        Assert.DoesNotContain(logs, l => File.ReadAllText(l).Contains("FATAL")); // the oldest (the fatal one) was pruned
    }

    [Fact]
    public void An_unwritable_log_directory_never_loses_or_throws_and_still_notifies()
    {
        using var dir = new TempDirectory("cscode-crash-");
        var blocker = dir.WriteFile("file-not-dir", "x");
        var handler = new CrashHandler(Path.Combine(blocker, "logs")); // a path under a regular file cannot be created
        string? reportedPath = null;
        handler.Reported += (_, p, _) => reportedPath = p;
        var path = handler.Report(new Exception("boom"), fatal: true);
        Assert.Null(path);
        Assert.Equal("(not written)", reportedPath);
    }

    [Fact]
    public void A_throwing_subscriber_cannot_mask_the_crash_report()
    {
        using var dir = new TempDirectory("cscode-crash-");
        var handler = new CrashHandler(dir.Path);
        handler.Reported += (_, _, _) => throw new InvalidOperationException("notifier broke");
        var path = handler.Report(new Exception("boom"), fatal: false);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Aggregate_exceptions_list_their_members()
    {
        var text = CrashHandler.Format(new AggregateException(new Exception("first"), new Exception("second")), true, "Task", DateTimeOffset.UnixEpoch);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
        Assert.Contains("FATAL", text);
    }

    [Fact]
    public void The_default_log_directory_honours_CSCODE_HOME()
    {
        var old = Environment.GetEnvironmentVariable("CSCODE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", "/tmp/cscode-home-test");
            Assert.Equal(Path.Combine("/tmp/cscode-home-test", "logs"), CrashHandler.DefaultLogDirectory());
        }
        finally { Environment.SetEnvironmentVariable("CSCODE_HOME", old); }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/xdg/state", null)]
    [InlineData("relative/state", null)] // XDG: relative values are ignored
    [InlineData("/xdg/state", "/portable")]
    public void Crash_logs_go_where_the_platform_logger_writes(string? xdgState, string? cscodeHome)
    {
        var env = new Dictionary<string, string?> { ["XDG_STATE_HOME"] = xdgState, ["CSCODE_HOME"] = cscodeHome };
        string? Env(string name) => env.GetValueOrDefault(name);
        // The rolling file logger writes to IAppPaths.LogsDir: one directory holds every log the user may be asked for.
        Assert.Equal(new Platform.AppPaths(Env).LogsDir, CrashHandler.DefaultLogDirectory(Env));
    }

    [Fact]
    public void Without_CSCODE_HOME_crash_logs_follow_the_XDG_state_directory_on_Linux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("XDG layout: Linux only");
        string? Env(string name) => name == "XDG_STATE_HOME" ? "/xdg/state" : null;
        Assert.Equal("/xdg/state/cscode/logs", CrashHandler.DefaultLogDirectory(Env)); // was ~/.cscode/logs, apart from the platform log
    }

    [Fact]
    public void The_crash_handler_is_resolvable_so_shell_failures_after_startup_are_reported()
    {
        // MainWindow reports deferred-startup and closing failures through the container; without a registration they were silently dropped.
        using var sp = AppServices.Build([]);
        Assert.Same(App.Crash, sp.GetService<CrashHandler>());
    }
}
