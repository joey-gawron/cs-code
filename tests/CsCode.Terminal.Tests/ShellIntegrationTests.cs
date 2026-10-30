using System.Diagnostics;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// Shell-integration injection (terminal.integrated.shellIntegration.enabled, on by default): which shells and arguments are injected and
/// how, and the bash script for real in the sandboxed shell — prompt marks, exit codes, the reported command line (nonce-checked), the
/// last command's output, the working directory, and that the user's own startup file, PROMPT_COMMAND and DEBUG trap keep working. Shells
/// never read the user's rc files here: bash runs with --noprofile and --norc (or --rcfile with a file of the test's own).
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class ShellIntegrationTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();
    private readonly TestConfigurationService _config = new();
    private readonly TerminalService _service;

    public ShellIntegrationTests()
    {
        _service = new TerminalService(_config, environment: () => _sandbox.Environment());
    }

    public void Dispose()
    {
        _service.Dispose();
        _sandbox.Dispose();
    }

    private static string Scripts => ShellIntegrationInjector.DefaultScriptsDirectory() ?? throw new InvalidOperationException("the shell-integration scripts were not copied next to the tests");

    private TerminalCreateOptions Bash(params string[] args) => new()
    {
        ShellPath = ShellSandbox.Bash,
        ShellArgs = args.Length == 0 ? ["--noprofile", "--norc"] : args,
        Cwd = _sandbox.Home,
        Show = false,
        InitialSize = (100, 30),
    };

    private static async Task<string> WaitScreenAsync(TerminalSession s, string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        var screen = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            screen = await s.InvokeAsync(e => e.ScreenText());
            if (screen.Contains(text, StringComparison.Ordinal)) return screen;
            await Task.Delay(20);
        }
        Assert.Fail($"'{text}' did not appear on the screen:\n{screen}");
        return screen;
    }

    private static async Task<T> WaitAsync<T>(Func<T?> probe, string what, int timeoutMs = 10_000) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }
        Assert.Fail(what);
        return null!;
    }

    /// <summary>Runs a command line and waits for the shell to report it finished.</summary>
    private static async Task<TerminalCommandSummary> RunAsync(TerminalSession s, string line)
    {
        var before = s.LastCommand;
        await s.SendTextAsync(line);
        return await WaitAsync(() => s.LastCommand is { } c && !ReferenceEquals(c, before) ? c : null, $"'{line}' was never reported finished");
    }

    // ------------------------------------------------------------------ which shells, which arguments

    [Theory]
    [InlineData("/usr/bin/bash", "", "--init-file {bash}", "")]
    [InlineData("/bin/bash", "-l", "--init-file {bash}", "CSCODE_SHELL_LOGIN=1")]
    [InlineData("/bin/bash", "--login -i", "--init-file {bash}", "CSCODE_SHELL_LOGIN=1")]
    [InlineData("/bin/bash", "--noprofile", "--init-file {bash}", "CSCODE_BASH_NOPROFILE=1")]
    // --norc turns --init-file off: the arguments stay and the first prompt sources the script
    [InlineData("/bin/bash", "--noprofile --norc", "--noprofile --norc", "CSCODE_BASH_BOOTSTRAP=1")]
    [InlineData("/bin/bash", "--rcfile /x/rc", "--init-file {bash}", "CSCODE_BASH_RCFILE=/x/rc")]
    [InlineData("/usr/bin/zsh", "", "", "ZDOTDIR={zsh} CSCODE_USER_ZDOTDIR=/home/u CSCODE_ZDOTDIR_UNSET=1")]
    [InlineData("/usr/bin/zsh", "-l", "-l", "ZDOTDIR={zsh} CSCODE_USER_ZDOTDIR=/home/u")]
    [InlineData("/usr/bin/fish", "", "--init-command source '{fish}'", "")]
    [InlineData("/usr/bin/fish", "-l", "-l --init-command source '{fish}'", "")]
    [InlineData("/opt/pwsh/pwsh", "-NoLogo", "-NoLogo -NoExit -Command . '{pwsh}'", "")]
    [InlineData("/opt/pwsh/pwsh.exe", "", "-NoExit -Command . '{pwsh}'", "")]
    public void Plain_interactive_shells_are_injected(string shell, string args, string expectedArgs, string expectedEnv)
    {
        string Expand(string s) => s.Replace("{bash}", Path.Combine(Scripts, "cscode.bash"), StringComparison.Ordinal)
            .Replace("{fish}", Path.Combine(Scripts, "cscode.fish"), StringComparison.Ordinal)
            .Replace("{pwsh}", Path.Combine(Scripts, "cscode.ps1"), StringComparison.Ordinal)
            .Replace("{zsh}", Path.Combine(Scripts, "zsh"), StringComparison.Ordinal);
        var env = new Dictionary<string, string> { ["HOME"] = "/home/u" };
        if (expectedEnv.Contains("CSCODE_ZDOTDIR_UNSET", StringComparison.Ordinal) is false && shell.EndsWith("zsh", StringComparison.Ordinal)) env["ZDOTDIR"] = "/home/u";
        var split = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var injection = ShellIntegrationInjector.For(shell, split, env, Scripts, "n0nce");
        Assert.NotNull(injection);
        var expected = Expand(expectedArgs);
        var actual = string.Join(' ', injection.Args);
        // the command strings carry spaces inside one argument: compare the joined form
        Assert.Equal(expected, actual);
        Assert.Equal("1", injection.Environment["CSCODE_INJECTION"]);
        Assert.Equal("n0nce", injection.Environment["CSCODE_NONCE"]);
        foreach (var pair in Expand(expectedEnv).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (k, v) = (pair[..pair.IndexOf('=', StringComparison.Ordinal)], pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            Assert.Equal(v, injection.Environment[k]);
        }
        if (injection.Environment.ContainsKey("CSCODE_BASH_BOOTSTRAP"))
            Assert.Equal($"builtin source '{Path.Combine(Scripts, "cscode.bash")}'", injection.Environment["PROMPT_COMMAND"]);
    }

    [Theory]
    [InlineData("/bin/bash", "-c echo")]
    [InlineData("/bin/bash", "script.sh")]
    [InlineData("/bin/bash", "--posix")]
    [InlineData("/usr/bin/zsh", "-f")]
    [InlineData("/usr/bin/fish", "-c ls")]
    [InlineData("/opt/pwsh/pwsh", "-File x.ps1")]
    [InlineData("/bin/sh", "")]
    [InlineData("/usr/bin/tmux", "")]
    [InlineData("/usr/bin/python3", "")]
    public void Other_programs_and_arguments_are_left_alone(string shell, string args) =>
        Assert.Null(ShellIntegrationInjector.For(shell, args.Split(' ', StringSplitOptions.RemoveEmptyEntries), new Dictionary<string, string>(), Scripts, "n"));

    [Fact]
    public void The_resolver_injects_by_default_never_into_a_feature_terminal_and_not_when_turned_off()
    {
        var resolver = _service.Resolver;
        var shell = resolver.Resolve(Bash("--noprofile", "--rcfile", "/x/rc"), 80, 24);
        Assert.Equal(["--init-file", Path.Combine(Scripts, "cscode.bash")], shell.Arguments);
        Assert.Equal("/x/rc", shell.Environment["CSCODE_BASH_RCFILE"]);
        Assert.Equal(32, shell.Environment["CSCODE_NONCE"].Length);
        Assert.NotEqual(shell.Environment["CSCODE_NONCE"], resolver.Resolve(Bash(), 80, 24).Environment["CSCODE_NONCE"]); // one per terminal
        var norc = resolver.Resolve(Bash(), 80, 24);
        Assert.Equal(["--noprofile", "--norc"], norc.Arguments);
        Assert.StartsWith("builtin source ", norc.Environment["PROMPT_COMMAND"], StringComparison.Ordinal);

        var task = resolver.Resolve(Bash() with { IsTransient = true }, 80, 24);
        Assert.Equal(["--noprofile", "--norc"], task.Arguments);
        Assert.False(task.Environment.ContainsKey("CSCODE_NONCE"));

        _config.Set(TerminalSettingKeys.ShellIntegrationEnabled, false);
        var off = resolver.Resolve(Bash(), 80, 24);
        Assert.Equal(["--noprofile", "--norc"], off.Arguments);
        Assert.False(off.Environment.ContainsKey("CSCODE_INJECTION"));
    }

    // ------------------------------------------------------------------ bash, for real

    [Fact]
    public async Task Bash_reports_prompts_command_lines_exit_codes_and_output()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        Assert.True(t.ShellIntegrationInjected);
        await WaitScreenAsync(t, "$");

        var ok = await RunAsync(t, "echo hello-$((6*7))");
        Assert.Equal("echo hello-$((6*7))", ok.Command.CommandLine);
        Assert.Equal(0, ok.Command.ExitCode);
        Assert.Equal("hello-42", ok.OutputTail);

        var failed = await RunAsync(t, "printf 'one\\ntwo\\n'; (exit 3)");
        Assert.Equal("printf 'one\\ntwo\\n'; (exit 3)", failed.Command.CommandLine);
        Assert.Equal(3, failed.Command.ExitCode);
        Assert.Equal("one\ntwo", failed.OutputTail);
        Assert.Equal("one\ntwo", await t.GetLastCommandOutputAsync());

        // each prompt row carries its command's exit code (the decorations), Ctrl+Up finds them
        var marks = await t.InvokeAsync(e => Enumerable.Range(0, e.Rows).Select(r => e.GetScreenRow(r)).Where(r => r.Mark == RowMark.PromptStart)
            .Select(r => (r.GetText(), r.MarkExitCode)).ToList());
        Assert.Contains(("$ echo hello-$((6*7))", (int?)0), marks);
        Assert.Contains(("$ printf 'one\\ntwo\\n'; (exit 3)", (int?)3), marks);
        Assert.True(await t.InvokeAsync(e => e.HasShellIntegration));

        // a command line with characters the protocol escapes, and a multi-line one
        var odd = await RunAsync(t, "echo 'a;b\\c' ; true");
        Assert.Equal("echo 'a;b\\c' ; true", odd.Command.CommandLine);
        Assert.Equal("a;b\\c", odd.OutputTail);
        var multi = await RunAsync(t, "for i in 1 2; do\necho \"n$i\"\ndone");
        Assert.Equal("for i in 1 2; do echo \"n$i\"; done", multi.Command.CommandLine); // as bash's history keeps it (cmdhist)
        Assert.Equal("n1\nn2", multi.OutputTail);
    }

    [Fact]
    public async Task Bash_reports_the_working_directory_even_with_characters_the_protocol_escapes()
    {
        var odd = Directory.CreateDirectory(_sandbox.Dir.Combine("dir; with \\ odd")).FullName;
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        await WaitScreenAsync(t, "$");
        await RunAsync(t, $"cd '{odd}'");
        await WaitAsync(() => t.CurrentDirectory == odd ? t.CurrentDirectory : null, $"cwd stayed {t.CurrentDirectory}");
    }

    /// <summary>
    /// The --init-file path (a profile without --norc). bash itself still reads a system-wide rc file first where it was built with one
    /// (Debian's /etc/bash.bashrc, never a file of the user's: HOME is the sandbox); the test's rc file then sets its own prompt.
    /// </summary>
    [Fact]
    public async Task Bash_reads_the_profiles_rc_file_first_and_its_prompt_command_and_debug_trap_keep_working()
    {
        var rc = _sandbox.Dir.Combine("user-rc");
        File.WriteAllText(rc, """
            PS1='$ '
            alias hi='echo hi-from-rc'
            USER_RC_LOADED=yes
            PROMPT_COMMAND='user_saw_status=$?;'
            trap 'user_traps=$((user_traps+1))' DEBUG
            HISTCONTROL=ignorespace
            """);
        var t = (TerminalSession)await _service.CreateAsync(Bash("--noprofile", "--rcfile", rc));
        await WaitScreenAsync(t, "$");

        var alias = await RunAsync(t, "hi");
        Assert.Equal("hi-from-rc", alias.OutputTail);
        Assert.Equal("hi", alias.Command.CommandLine);
        await RunAsync(t, "false");
        // the user's PROMPT_COMMAND (ending in ';') ran after the command and saw its status; the user's DEBUG trap still runs
        var check = await RunAsync(t, "echo \"rc=$USER_RC_LOADED status=$user_saw_status traps=$((user_traps > 0))\"");
        Assert.Equal("rc=yes status=1 traps=1", check.OutputTail);
        // history did not keep a line starting with a space: the line reported is the command bash ran
        var hidden = await RunAsync(t, " echo not-in-history");
        Assert.Equal("echo not-in-history", hidden.Command.CommandLine);
        Assert.Equal("not-in-history", hidden.OutputTail);
    }

    [Fact]
    public async Task A_command_line_reported_without_the_nonce_is_ignored_and_nothing_leaks_to_child_processes()
    {
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        await WaitScreenAsync(t, "$");
        // a program printing its own E (no or a wrong nonce) cannot claim what the command was
        var spoof = await RunAsync(t, "printf '\\033]633;E;rm -rf /;forged\\007'; echo spoofed");
        Assert.Equal("printf '\\033]633;E;rm -rf /;forged\\007'; echo spoofed", spoof.Command.CommandLine);
        // programs started from the shell see none of CsCode's variables (a nested bash is a plain bash)
        var env = await RunAsync(t, "env | grep -c '^CSCODE_' ; true");
        Assert.Equal("0", env.OutputTail);
    }

    [Fact]
    public async Task With_shell_integration_off_bash_runs_as_configured_and_reports_nothing()
    {
        _config.Set(TerminalSettingKeys.ShellIntegrationEnabled, false);
        var t = (TerminalSession)await _service.CreateAsync(Bash());
        Assert.False(t.ShellIntegrationInjected);
        await WaitScreenAsync(t, "$");
        await t.SendTextAsync("echo plain-$((1+1))");
        await WaitScreenAsync(t, "plain-2");
        await Task.Delay(200);
        Assert.Null(t.LastCommand);
        Assert.False(await t.InvokeAsync(e => e.HasShellIntegration));
    }
}
