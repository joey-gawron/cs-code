using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Core.Tests;

/// <summary>
/// lsp-1 / dbg-4: reading the login shell's environment for a Mac app started from the Dock (<see cref="LoginShellEnvironment"/>). The decision, the
/// shell's arguments, the parsing and the merge run on any OS with an injected runner; the real runner runs a stand-in shell script (Unix).
/// </summary>
public class LoginShellEnvironmentTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) => name => vars.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void Only_a_Mac_app_not_started_from_a_terminal_resolves_the_environment()
    {
        Assert.True(LoginShellEnvironment.ShouldResolve(Env(("PATH", "/usr/bin:/bin")), isMacOS: true));
        Assert.False(LoginShellEnvironment.ShouldResolve(Env(("PATH", "/usr/bin:/bin")), isMacOS: false)); // Linux and Windows: unchanged
        Assert.False(LoginShellEnvironment.ShouldResolve(Env(("TERM", "xterm-256color")), isMacOS: true)); // started from a terminal: already the shell's
        Assert.False(LoginShellEnvironment.ShouldResolve(Env((LoginShellEnvironment.SkipVariable, "1")), isMacOS: true));
    }

    [Fact]
    public void The_shell_is_asked_as_a_login_interactive_shell_in_its_own_syntax()
    {
        Assert.Equal(["-l", "-i", "-c", "cmd"], LoginShellEnvironment.ShellArguments("/bin/zsh", "cmd"));
        Assert.Equal(["-l", "-i", "-c", "cmd"], LoginShellEnvironment.ShellArguments("/opt/homebrew/bin/fish", "cmd"));
        Assert.Equal(["-i", "-c", "cmd"], LoginShellEnvironment.ShellArguments("/bin/tcsh", "cmd")); // tcsh takes -l only alone
        Assert.Equal(["-Login", "-Command", "cmd"], LoginShellEnvironment.ShellArguments("/usr/local/bin/pwsh", "cmd"));
    }

    [Fact]
    public void What_the_rc_files_print_around_the_markers_is_ignored()
    {
        const string M = "cscode-env-1234";
        var output = "Last login: today\nWelcome!\n" + M + "PATH=/opt/homebrew/bin:/usr/bin\0EQ=a=b\0MULTI=line1\nline2\0=no-name\0" + M + "bye\n";
        var env = LoginShellEnvironment.Parse(output, M);
        Assert.NotNull(env);
        Assert.Equal("/opt/homebrew/bin:/usr/bin", env["PATH"]);
        Assert.Equal("a=b", env["EQ"]);
        Assert.Equal("line1\nline2", env["MULTI"]);
        Assert.Equal(3, env.Count);
        Assert.Null(LoginShellEnvironment.Parse("PATH=/usr/bin\0", M)); // no marker: the command did not run
        Assert.Null(LoginShellEnvironment.Parse(M + "PATH=/usr/bin\0", M)); // cut short
    }

    [Fact]
    public async Task The_runner_is_given_the_shell_its_arguments_and_the_resolving_variable()
    {
        string? shell = null;
        IReadOnlyList<string>? args = null;
        IReadOnlyDictionary<string, string>? extra = null;
        var resolution = await LoginShellEnvironment.ResolveAsync("/bin/zsh", (s, a, e, _) =>
        {
            (shell, args, extra) = (s, a, e);
            var marker = a[^1].Split('\'')[3]; // printf '%s' '<marker>'; …
            return Task.FromResult(new LoginShellEnvironment.ShellRun(0, "motd\n" + marker + "PATH=/opt/homebrew/bin:/usr/bin\0HOME=/Users/me\0" + marker, ""));
        }, TimeSpan.FromSeconds(5));

        Assert.Equal("/bin/zsh", shell);
        Assert.Equal(["-l", "-i", "-c"], args!.Take(3));
        Assert.Contains("/usr/bin/env -0", args![3], StringComparison.Ordinal);
        Assert.Equal("1", extra![LoginShellEnvironment.ResolvingVariable]);
        Assert.Equal("/opt/homebrew/bin:/usr/bin", resolution.Environment!["PATH"]);
        Assert.Contains("read 2 variable(s) from /bin/zsh", resolution.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_that_times_out_fails_or_cannot_start_leaves_the_environment_alone()
    {
        var timedOut = await LoginShellEnvironment.ResolveAsync("/bin/zsh",
            (_, _, _, _) => Task.FromResult(new LoginShellEnvironment.ShellRun(null, "", "")), TimeSpan.FromSeconds(3));
        Assert.Null(timedOut.Environment);
        Assert.Contains("did not finish within 3 s", timedOut.Description, StringComparison.Ordinal);

        var failed = await LoginShellEnvironment.ResolveAsync("/bin/zsh",
            (_, _, _, _) => Task.FromResult(new LoginShellEnvironment.ShellRun(1, "", "zsh: parse error in .zshrc")), TimeSpan.FromSeconds(3));
        Assert.Null(failed.Environment);
        Assert.Contains("parse error in .zshrc", failed.Description, StringComparison.Ordinal);

        var missing = await LoginShellEnvironment.ResolveAsync("/no/such/shell",
            (_, _, _, _) => throw new System.ComponentModel.Win32Exception(2, "No such file or directory"), TimeSpan.FromSeconds(3));
        Assert.Null(missing.Environment);
        Assert.Contains("could not be started", missing.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_merge_adds_and_replaces_but_keeps_CsCode_own_and_the_shell_own_variables()
    {
        var current = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/Users/me", ["CSCODE_HOME"] = "/portable", ["LAUNCHD_ONLY"] = "kept",
        };
        var shell = new Dictionary<string, string>
        {
            ["PATH"] = "/opt/homebrew/bin:/usr/bin:/bin", ["HOME"] = "/Users/me", ["GOPATH"] = "/Users/me/go", ["CSCODE_HOME"] = "/elsewhere",
            [LoginShellEnvironment.ResolvingVariable] = "1", ["PWD"] = "/Users/me", ["SHLVL"] = "1", ["_"] = "/usr/bin/env",
        };
        var changed = LoginShellEnvironment.Merge(shell, n => current.GetValueOrDefault(n), (n, v) => current[n] = v);

        Assert.Equal(["GOPATH", "PATH"], changed);
        Assert.Equal("/opt/homebrew/bin:/usr/bin:/bin", current["PATH"]);
        Assert.Equal("/portable", current["CSCODE_HOME"]);
        Assert.Equal("kept", current["LAUNCHD_ONLY"]); // nothing is removed
        Assert.False(current.ContainsKey("PWD"));
        Assert.False(current.ContainsKey("SHLVL"));
        Assert.False(current.ContainsKey(LoginShellEnvironment.ResolvingVariable));
    }

    [Fact]
    public async Task The_real_runner_reads_the_environment_of_a_shell_that_prints_around_it()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("a shell script stands in for the login shell: Unix only"); return; }
        using var dir = new TempDirectory("cscode-shellenv-");
        // stands in for zsh: prints a banner on both streams, then runs the command it was given (-l -i -c <command>) with a variable of its own
        var shell = Script(dir.Combine("fake-zsh"), "#!/bin/sh\necho 'Welcome!'\necho 'warning' >&2\nFROM_RC='set in .zshrc' exec /bin/sh -c \"$4\"\n");

        var resolution = await LoginShellEnvironment.ResolveAsync(shell, LoginShellEnvironment.DefaultRunner, TimeSpan.FromSeconds(30));

        Assert.NotNull(resolution.Environment);
        Assert.Equal("set in .zshrc", resolution.Environment["FROM_RC"]);
        Assert.Equal("1", resolution.Environment[LoginShellEnvironment.ResolvingVariable]);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), resolution.Environment["PATH"]);
    }

    [Fact]
    public async Task The_real_runner_keeps_the_environment_when_a_background_job_holds_the_output_open()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("a shell script stands in for the login shell: Unix only"); return; }
        using var dir = new TempDirectory("cscode-shellenv-");
        // an rc file that starts a job in the background without redirecting its output: the pipe stays open after the shell exited
        var shell = Script(dir.Combine("daemon-zsh"), "#!/bin/sh\n(sleep 5 &)\nFROM_RC=kept exec /bin/sh -c \"$4\"\n");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var resolution = await LoginShellEnvironment.ResolveAsync(shell, LoginShellEnvironment.DefaultRunner, TimeSpan.FromSeconds(30));

        Assert.True(resolution.Environment is not null, resolution.Description);
        Assert.Equal("kept", resolution.Environment["FROM_RC"]);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), $"took {started.Elapsed}: waited for the background job");
    }

    [Fact]
    public void Without_the_shell_environment_the_install_directories_that_exist_are_appended_to_PATH()
    {
        const string Home = "/Users/me";
        var existing = new HashSet<string>
        {
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/local/share/dotnet", Home + "/.cargo/bin", Home + "/.dotnet/tools", Home + "/.volta/bin",
            Home + "/.nvm/versions/node", Home + "/.nvm/versions/node/v20.11.1/bin", Home + "/.nvm/versions/node/v9.0.0/bin",
        };
        string[] versions = ["v9.0.0", "v20.11.1", "system"];
        var path = string.Join(Path.PathSeparator, "/usr/bin", "/bin", "/usr/local/bin/");

        var extended = LoginShellEnvironment.FallbackPath(path, Home, existing.Contains, _ => versions);

        Assert.Equal(string.Join(Path.PathSeparator, "/usr/bin", "/bin", "/usr/local/bin/", "/opt/homebrew/bin", Home + "/.cargo/bin", Home + "/.dotnet/tools",
            "/usr/local/share/dotnet", Home + "/.volta/bin", Home + "/.nvm/versions/node/v20.11.1/bin"), extended); // launchd's PATH keeps winning
        Assert.Null(LoginShellEnvironment.FallbackPath(extended, Home, existing.Contains, _ => versions)); // nothing more to add
        Assert.Null(LoginShellEnvironment.FallbackPath("/usr/bin", Home, _ => false, _ => []));
    }

    [Fact]
    public async Task The_user_login_shell_on_a_Mac_gives_its_environment()
    {
        if (!OperatingSystem.IsMacOS()) { Assert.Skip("the login shell is only asked on macOS"); return; }
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/zsh";

        var resolution = await LoginShellEnvironment.ResolveAsync(shell, LoginShellEnvironment.DefaultRunner, LoginShellEnvironment.DefaultTimeout);

        Assert.True(resolution.Environment is not null, resolution.Description);
        Assert.Contains("/usr/bin", resolution.Environment["PATH"].Split(':'));
        Assert.Equal("1", resolution.Environment[LoginShellEnvironment.ResolvingVariable]);
    }

    [Fact]
    public async Task The_real_runner_kills_a_shell_that_does_not_finish()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("a shell script stands in for the login shell: Unix only"); return; }
        using var dir = new TempDirectory("cscode-shellenv-");
        var shell = Script(dir.Combine("hanging-zsh"), "#!/bin/sh\nexec sleep 60\n"); // an rc file waiting for something

        var started = System.Diagnostics.Stopwatch.StartNew();
        var resolution = await LoginShellEnvironment.ResolveAsync(shell, LoginShellEnvironment.DefaultRunner, TimeSpan.FromMilliseconds(500));

        Assert.Null(resolution.Environment);
        Assert.Contains("did not finish", resolution.Description, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
    }

    private static string Script(string path, string text)
    {
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
