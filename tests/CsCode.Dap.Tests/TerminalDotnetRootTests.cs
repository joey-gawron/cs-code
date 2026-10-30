using CsCode.Core.Platform;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Protocol;
using CsCode.Dap.Session;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tasks;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Cross-branch review C3, through the REAL terminal: a task shown in a local terminal and a <c>runInTerminal</c> debuggee run in a PTY of the
/// real <see cref="TerminalService"/> (the task through the real <see cref="TaskTerminalHost"/>), and their programs write the environment they
/// got. CsCode was started by <c>dotnet run</c>/<c>dotnet test</c>: it inherited its apphost's DOTNET_ROOT_&lt;ARCH&gt; and no DOTNET_ROOT. The
/// terminal's launch resolver folds that into DOTNET_ROOT in the environment the terminal inherits, then applies <c>terminal.integrated.env</c>
/// and the caller's variables on top, as for any terminal; the debugger and the task runner add nothing of CsCode's own environment. So the
/// user's <c>terminal.integrated.env</c> DOTNET_ROOT wins and <c>terminal.integrated.inheritEnv: false</c> keeps CsCode's runtime out, as in a
/// terminal the user opens.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class TerminalDotnetRootTests : IDisposable
{
    private static readonly string Arch = InheritedDotnetRoot.ArchRootVariable;
    private const string UserRoot = "/opt/my-dotnet";

    private readonly TempDirectory _dir = new("c3-dotnet-root-");
    private readonly TestConfigurationService _config = new();
    private readonly TerminalService _terminals;
    private readonly TaskTerminalHost _host;
    private readonly DebugLane _lane = new("c3-dotnet-root");
    private readonly string? _processRoot = Environment.GetEnvironmentVariable(InheritedDotnetRoot.RootVariable);
    private readonly string? _processArchRoot = Environment.GetEnvironmentVariable(Arch);

    public TerminalDotnetRootTests()
    {
        Directory.CreateDirectory(_dir.Combine("home"));
        // CsCode as dotnet run/test starts it: its apphost's DOTNET_ROOT_<ARCH>, no DOTNET_ROOT. In the process (what the debugger could read
        // of its own) and in what the terminals inherit (a fixed environment: a temp HOME, no rc file, the fold is the resolver's). The serial
        // collection runs alone, so no other test sees the process's variables changed; Dispose restores them.
        Environment.SetEnvironmentVariable(InheritedDotnetRoot.RootVariable, null);
        Environment.SetEnvironmentVariable(Arch, Sdk);
        _terminals = new TerminalService(_config, environment: () => new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = _dir.Combine("home"),
            ["LANG"] = "C.UTF-8",
            [Arch] = Sdk,
        });
        _host = new TaskTerminalHost(_terminals);
    }

    private string Sdk => _dir.Combine("sdk", "dotnet");

    public void Dispose()
    {
        _host.Dispose();
        _terminals.Dispose(); // kills what still runs in its terminals
        _lane.Dispose();
        _dir.Dispose();
        Environment.SetEnvironmentVariable(InheritedDotnetRoot.RootVariable, _processRoot);
        Environment.SetEnvironmentVariable(Arch, _processArchRoot);
    }

    public enum Setup
    {
        /// <summary>No terminal settings: the resolver's fold (DOTNET_ROOT is CsCode's arch root, the arch variable is gone).</summary>
        Inherited,
        /// <summary><c>terminal.integrated.env.linux</c> sets DOTNET_ROOT: the user's value.</summary>
        UserDotnetRoot,
        /// <summary><c>terminal.integrated.inheritEnv: false</c>: neither variable (only PATH, HOME, ... are inherited).</summary>
        NoInheritance,
    }

    /// <summary>Applies the settings; returns the DOTNET_ROOT the program must see (null: none).</summary>
    private string? Configure(Setup setup)
    {
        switch (setup)
        {
            case Setup.UserDotnetRoot:
                _config.Set(TerminalSettingKeys.EnvKey, new Dictionary<string, string?> { ["DOTNET_ROOT"] = UserRoot });
                return UserRoot;
            case Setup.NoInheritance:
                _config.Set(TerminalSettingKeys.InheritEnv, false);
                return null;
            default:
                return Sdk;
        }
    }

    private Dictionary<string, string> ReadEnvironment(string file) =>
        File.ReadAllLines(_dir.Combine(file)).Where(l => l.Contains('=', StringComparison.Ordinal)).Select(l => l.Split('=', 2))
            .GroupBy(p => p[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

    private static void AssertDotnetRoot(Dictionary<string, string> env, string? expected)
    {
        Assert.False(env.ContainsKey(Arch), $"{Arch}={env.GetValueOrDefault(Arch)} reached the program");
        Assert.Equal(expected, env.GetValueOrDefault("DOTNET_ROOT"));
    }

    [Theory]
    [InlineData(Setup.Inherited)]
    [InlineData(Setup.UserDotnetRoot)]
    [InlineData(Setup.NoInheritance)]
    public async Task A_task_in_a_local_terminal_gets_the_terminals_fold_and_the_users_terminal_settings(Setup setup)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Unix PTY and /bin/sh");
        var expected = Configure(setup);
        _dir.WriteFile(".cscode/tasks.json", """
            { "version": "2.0.0", "tasks": [ { "label": "env", "type": "process", "command": "/bin/sh",
              "args": ["-c", "env > task.tmp && mv task.tmp task.env"], "options": { "env": { "MODE": "x" } } } ] }
            """);
        using var tasks = new TaskService(_lane, new DapPlatform { Workspace = new FakeWorkspace(_dir.Uri), TaskTerminals = () => _host });
        var result = await tasks.RunAsync("env", _dir.Uri, Ct);
        Assert.True(result.Succeeded, $"exit {result.ExitCode}: {result.Error}");
        var env = ReadEnvironment("task.env");
        Assert.Equal("x", env["MODE"]);
        AssertDotnetRoot(env, expected);
    }

    [Theory]
    [InlineData(Setup.Inherited)]
    [InlineData(Setup.UserDotnetRoot)]
    [InlineData(Setup.NoInheritance)]
    public async Task A_runInTerminal_debuggee_in_a_local_terminal_gets_the_terminals_fold_and_the_users_terminal_settings(Setup setup)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Unix PTY and /bin/sh");
        var expected = Configure(setup);
        using var launcher = new DebuggeeLauncher(_lane, NullLogger.Instance, (_, _) => { }, () => _terminals);
        await launcher.LaunchAsync(new DapRunInTerminalArguments
        {
            Kind = "integrated",
            Title = "App",
            Cwd = _dir.Path,
            // it stays until the launcher ends it (the launcher checks the debuggee started; one that exited at once could look as if it had not)
            Args = ["/bin/sh", "-c", "env > debuggee.tmp && mv debuggee.tmp debuggee.env && exec sleep 30"],
            Env = new Dictionary<string, string?> { ["X"] = "1" },
        }, "App", Ct);
        await SessionHarness.Eventually(() => File.Exists(_dir.Combine("debuggee.env")), 10_000, "the debuggee's environment");
        var env = ReadEnvironment("debuggee.env");
        Assert.Equal("1", env["X"]);
        AssertDotnetRoot(env, expected);
    }
}
