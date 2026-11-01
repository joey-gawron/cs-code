using System.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Panels.Git.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Git.Tests;

/// <summary>
/// Fetch, pull and push can neither prompt on a terminal nor hang: the environment they get (<see cref="GitNetwork"/>), and real git runs against a
/// fake ssh (a script set as <c>core.sshCommand</c>, named <c>ssh</c>) that records what it was given.
/// </summary>
public sealed class NetworkTests : IDisposable
{
    private readonly List<IDisposable> _owned = new();

    private T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    private static readonly IReadOnlyDictionary<string, string> NoConfig = new Dictionary<string, string>();

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) => name => vars.FirstOrDefault(v => v.Name == name).Value;

    // ------------------------------------------------------------------------------------- the environment

    [Fact]
    public void Without_an_askpass_helper_ssh_runs_in_batch_mode_with_connect_and_keep_alive_timeouts()
    {
        var env = GitNetwork.Environment(Env(), NoConfig, null);
        Assert.Equal("ssh " + GitNetwork.SshOptions + " -o BatchMode=yes", env["GIT_SSH_COMMAND"]);
        Assert.Contains("ConnectTimeout=30", env["GIT_SSH_COMMAND"]);
        Assert.Contains("ServerAliveInterval=15", env["GIT_SSH_COMMAND"]);
        Assert.False(env.ContainsKey("SSH_ASKPASS"));
        Assert.False(env.ContainsKey("GIT_ASKPASS"));
    }

    [Fact]
    public void With_an_askpass_helper_ssh_and_git_ask_through_it_and_ssh_is_not_in_batch_mode()
    {
        var askpass = new Dictionary<string, string?> { ["SSH_ASKPASS"] = "/tmp/a/askpass.sh", ["SSH_ASKPASS_REQUIRE"] = "force", ["CSCODE_ASKPASS_TOKEN"] = "t" };
        var env = GitNetwork.Environment(Env(), NoConfig, ("/tmp/a/askpass.sh", askpass));
        Assert.Equal("ssh " + GitNetwork.SshOptions, env["GIT_SSH_COMMAND"]);
        Assert.DoesNotContain("BatchMode", env["GIT_SSH_COMMAND"]);
        Assert.Equal("/tmp/a/askpass.sh", env["SSH_ASKPASS"]);
        Assert.Equal("force", env["SSH_ASKPASS_REQUIRE"]);
        Assert.Equal("/tmp/a/askpass.sh", env["GIT_ASKPASS"]);
        Assert.Equal("t", env["CSCODE_ASKPASS_TOKEN"]);
    }

    [Fact]
    public void The_users_own_ssh_command_keeps_its_options_first_and_any_other_program_is_left_alone()
    {
        // GIT_SSH_COMMAND running ssh: ours appended (ssh takes an option's first value, so the user's -o choices win)
        Assert.Equal("ssh -i ~/.ssh/work -o BatchMode=no " + GitNetwork.SshOptions + " -o BatchMode=yes",
            GitNetwork.Environment(Env(("GIT_SSH_COMMAND", "ssh -i ~/.ssh/work -o BatchMode=no")), NoConfig, null)["GIT_SSH_COMMAND"]);
        // core.sshCommand with a quoted path to an ssh
        var config = new Dictionary<string, string> { ["core.sshcommand"] = "\"/opt/My Tools/ssh\" -F cfg" };
        Assert.Equal("\"/opt/My Tools/ssh\" -F cfg " + GitNetwork.SshOptions + " -o BatchMode=yes", GitNetwork.Environment(Env(), config, null)["GIT_SSH_COMMAND"]);
        // the environment variable beats the configuration, as in git
        Assert.StartsWith("ssh -p 2222 ", GitNetwork.Environment(Env(("GIT_SSH_COMMAND", "ssh -p 2222")), config, null)["GIT_SSH_COMMAND"]);
        // a wrapper or plink: untouched (git uses the user's value as it is)
        Assert.False(GitNetwork.Environment(Env(("GIT_SSH_COMMAND", "plink -batch")), NoConfig, null).ContainsKey("GIT_SSH_COMMAND"));
        Assert.False(GitNetwork.Environment(Env(), new Dictionary<string, string> { ["core.sshcommand"] = "/usr/local/bin/my-ssh-wrapper" }, null).ContainsKey("GIT_SSH_COMMAND"));
        // GIT_SSH (a program, no arguments): untouched, and not overridden by a GIT_SSH_COMMAND of ours
        Assert.False(GitNetwork.Environment(Env(("GIT_SSH", "/usr/bin/plink")), NoConfig, null).ContainsKey("GIT_SSH_COMMAND"));
    }

    [Fact]
    public void Http_transfers_get_no_low_speed_limit_so_a_slow_server_side_phase_is_not_given_up()
    {
        // curl gives up a transfer whose average stays under GIT_HTTP_LOW_SPEED_LIMIT bytes/s for GIT_HTTP_LOW_SPEED_TIME seconds: a server busy
        // counting or compressing objects, or running a slow pre-receive hook, sends only progress lines and keep-alives for minutes. As in git and
        // VS Code nothing is set (the user's http.lowSpeedLimit/Time still apply, git reads them itself); git.networkTimeout ends a real hang.
        var env = GitNetwork.Environment(Env(), NoConfig, null);
        Assert.False(env.ContainsKey("GIT_HTTP_LOW_SPEED_LIMIT"));
        Assert.False(env.ContainsKey("GIT_HTTP_LOW_SPEED_TIME"));
        Assert.Equal(new Dictionary<string, string> { ["core.sshcommand"] = "ssh -v" },
            GitNetwork.ParseConfig("core.sshcommand ssh -i global-key\ncore.sshcommand ssh -v\n")); // the repository's value (last) wins
    }

    [Fact]
    public void Ssh_options_the_users_ssh_config_sets_are_not_passed_so_the_config_wins()
    {
        using var dir = new TempDirectory("cscode-git-sshcfg-");
        var config = Path.Combine(dir.Path, "config");
        File.WriteAllText(config, "# ConnectTimeout 5\nInclude work.conf\nHost *.corp\n  ServerAliveInterval=30\n");
        File.WriteAllText(Path.Combine(dir.Path, "work.conf"), "Host build\n  connecttimeout 120\n");
        var set = GitNetwork.ConfiguredSshKeywords([config]);
        Assert.Contains("connecttimeout", set);       // from the included file (relative to the including file's directory), in any case
        Assert.Contains("serveraliveinterval", set);  // keyword=value
        Assert.DoesNotContain("serveralivecountmax", set);
        Assert.Equal("-o ServerAliveCountMax=4", GitNetwork.SshOptionsFor(set));
        Assert.Equal("ssh -o ServerAliveCountMax=4 -o BatchMode=yes", GitNetwork.Environment(Env(), NoConfig, null, set)["GIT_SSH_COMMAND"]);
        // a BatchMode of the configuration (e.g. no, to let one's own SSH_ASKPASS answer) is not overridden either
        Assert.Equal("ssh " + GitNetwork.SshOptions, GitNetwork.Environment(Env(), NoConfig, null, new HashSet<string> { "batchmode" })["GIT_SSH_COMMAND"]);
        // nothing configured: all of ours
        Assert.Equal(GitNetwork.SshOptions, GitNetwork.SshOptionsFor(GitNetwork.ConfiguredSshKeywords([Path.Combine(dir.Path, "missing")])));
        // the files read: ~/.ssh/config and the system's, or only the user's -F file (ssh then reads no other)
        Assert.Equal([Path.Combine("/home/u", ".ssh", "config"), "/etc/ssh/ssh_config"], GitNetwork.SshConfigFiles(null, "/home/u"));
        Assert.Equal([Path.Combine("/home/u", "cfg")], GitNetwork.SshConfigFiles("ssh -F ~/cfg -i key", "/home/u"));
        Assert.Equal(["/opt/c"], GitNetwork.SshConfigFiles("ssh -F/opt/c", "/home/u"));
        // on Windows the system-wide files are Windows OpenSSH's (%ProgramData%) and Git for Windows' own ssh's (etc of its install), not /etc
        Assert.Equal(["/etc/ssh/ssh_config"], GitNetwork.SystemSshConfigFiles(false, "/pd", "/usr/bin/git"));
        Assert.Equal([Path.Join("/pd", "ssh", "ssh_config"), Path.Join("/G", "etc", "ssh", "ssh_config")],
            GitNetwork.SystemSshConfigFiles(true, "/pd", Path.Join("/G", "cmd", "git.exe")));
        Assert.Equal([Path.Join("/G", "etc", "ssh", "ssh_config")], GitNetwork.SystemSshConfigFiles(true, null, Path.Join("/G", "mingw64", "bin", "git.exe")));
        Assert.Equal([Path.Join("/G", "etc", "ssh", "ssh_config")], GitNetwork.SystemSshConfigFiles(true, "", Path.Join("/G", "bin", "git.exe")));
        Assert.Equal([Path.Combine("/home/u", ".ssh", "config"), "C:/pd/ssh/ssh_config"], GitNetwork.SshConfigFiles(null, "/home/u", ["C:/pd/ssh/ssh_config"]));
    }

    [Fact]
    public void Network_operations_start_under_setsid_even_when_it_is_busyboxs_and_get_gits_exit_code()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "setsid is used on Linux only");
        var busybox = new[] { "/bin/busybox", "/usr/bin/busybox" }.FirstOrDefault(File.Exists);
        var utilLinux = new[] { "/usr/bin/setsid", "/bin/setsid" }
            .FirstOrDefault(p => File.Exists(p) && File.ResolveLinkTarget(p, true)?.FullName.EndsWith("busybox", StringComparison.Ordinal) != true);
        Assert.SkipWhen(busybox is null && utilLinux is null, "neither busybox nor util-linux setsid is installed");
        using var dir = new TempDirectory("cscode-git-setsid-");
        var candidates = new List<string>();
        if (busybox is not null)
        {
            var applet = Path.Combine(dir.Path, "setsid"); // busybox picks the applet by the name it is started as (Alpine's /usr/bin/setsid)
            File.CreateSymbolicLink(applet, busybox);
            candidates.Add(applet);
        }
        if (utilLinux is not null) candidates.Add(utilLinux);
        foreach (var setsid in candidates)
        {
            var launcher = GitNetwork.ResolveDetach([setsid], []);
            Assert.NotNull(launcher);
            var psi = new ProcessStartInfo(launcher[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in launcher.Skip(1)) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "/bin/sh", "-c", "ps -o sid= -p $$; exit 3" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 3, $"{setsid}: exit {p.ExitCode}: {error}");
            Assert.NotEqual(Ps("sid=", Environment.ProcessId), output.Trim());
        }
    }

    // ------------------------------------------------------------------------------------- real git, fake ssh

    private sealed class FakeAskpass(string program) : IAskpassService, IAskpassSession
    {
        public int Started;
        public bool Disposed;
        public IAskpassSession? Start() { Started++; return this; }
        public string Program => program;
        public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?> { ["SSH_ASKPASS"] = program, ["SSH_ASKPASS_REQUIRE"] = "force" };
        public void Dispose() => Disposed = true;
    }

    /// <summary>A repository whose origin is an ssh URL and whose <c>core.sshCommand</c> is a script named ssh that appends what it saw to a log.</summary>
    private async Task<(TestRepo Repo, GitService Git, TestConfigurationService Config, string Log)> OpenWithFakeSshAsync(IAskpassService? askpass, int sleepSeconds = 0)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake ssh is a shell script");
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = Own(new TestRepo());
        repo.Write("a.txt", "one\n");
        repo.Commit("initial");
        var bin = Directory.CreateDirectory(Path.Combine(repo.Dir, "fake-bin")).FullName;
        var log = Path.Combine(repo.Dir, "ssh.log");
        var ssh = Path.Combine(bin, "ssh");
        File.WriteAllText(ssh, $$"""
            #!/bin/sh
            {
              echo "ARGS: $*"
              echo "PGID: $(ps -o pgid= -p $$ | tr -d ' ')"
              echo "REQUIRE: $SSH_ASKPASS_REQUIRE"
              if [ -n "$SSH_ASKPASS" ]; then echo "ANSWER: $("$SSH_ASKPASS" "Enter passphrase for key '/home/u/.ssh/id_ed25519':")"; fi
            } >> '{{log}}'
            if [ {{sleepSeconds}} -gt 0 ]; then sleep {{sleepSeconds}}; fi
            echo "fake ssh: Could not resolve hostname example.invalid" >&2
            exit 255
            """.Replace("\r", ""));
        MakeExecutable(ssh);
        repo.Git("config", "core.sshCommand", ssh + " -F /dev/null"); // no ssh configuration of the machine running the tests
        repo.Git("remote", "add", "origin", "ssh://example.invalid/project.git");
        var config = new TestConfigurationService();
        var ws = Own(new TestWorkspace(false, repo.Uri));
        var git = Own(new GitService(ws, config, askpass: askpass));
        await git.Initialization;
        return (repo, git, config, log);
    }

    private static string AnswerScript(string dir)
    {
        var path = Path.Combine(dir, "askpass.sh");
        File.WriteAllText(path, "#!/bin/sh\necho s3cret\n");
        MakeExecutable(path);
        return path;
    }

    [Fact]
    public async Task A_fetch_without_an_askpass_helper_runs_the_users_ssh_in_batch_mode()
    {
        var (_, git, _, log) = await OpenWithFakeSshAsync(askpass: null);
        var ex = await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].FetchAsync());
        Assert.Contains("Could not resolve hostname", ex.Message);
        var seen = File.ReadAllText(log);
        Assert.Contains("-o BatchMode=yes", seen);
        Assert.Contains("ConnectTimeout=30", seen);
        Assert.DoesNotContain("ANSWER:", seen);
    }

    [Fact]
    public async Task A_fetch_with_the_askpass_helper_lets_ssh_ask_the_gui_and_ends_the_helper_afterwards()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake ssh is a shell script");
        using var dir = new TempDirectory("cscode-git-askpass-");
        var askpass = new FakeAskpass(AnswerScript(dir.Path));
        var (_, git, _, log) = await OpenWithFakeSshAsync(askpass);
        await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].PullAsync());
        var seen = File.ReadAllText(log);
        Assert.Contains("ANSWER: s3cret", seen);
        Assert.Contains("REQUIRE: force", seen);
        Assert.DoesNotContain("BatchMode", seen);
        Assert.Equal(1, askpass.Started);
        Assert.True(askpass.Disposed);
    }

    [Fact]
    public async Task Turning_the_integrated_askpass_off_falls_back_to_batch_mode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake ssh is a shell script");
        using var dir = new TempDirectory("cscode-git-askpass-");
        var askpass = new FakeAskpass(AnswerScript(dir.Path));
        var (_, git, config, log) = await OpenWithFakeSshAsync(askpass);
        config.Set(GitSettings.UseIntegratedAskPass, false);
        await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].FetchAsync());
        Assert.Contains("-o BatchMode=yes", File.ReadAllText(log));
        Assert.Equal(0, askpass.Started);
    }

    [Fact]
    public async Task Network_operations_run_in_a_session_of_their_own_so_ssh_cannot_reach_the_apps_terminal()
    {
        var (_, git, _, log) = await OpenWithFakeSshAsync(askpass: null);
        Assert.SkipWhen(GitNetwork.DetachLauncher is null, "neither setsid nor perl is installed");
        await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].FetchAsync());
        var sshGroup = File.ReadAllLines(log).Single(l => l.StartsWith("PGID: ", StringComparison.Ordinal))["PGID: ".Length..].Trim();
        // without a session of its own git, and the ssh it starts, stay in this process's group (and session, with its terminal)
        Assert.NotEqual(ProcessGroupOf(Environment.ProcessId), sshGroup);
    }

    [Fact]
    public async Task A_network_operation_that_hangs_is_stopped_at_the_network_timeout()
    {
        var (_, git, config, _) = await OpenWithFakeSshAsync(askpass: null, sleepSeconds: 120);
        config.Set(GitSettings.NetworkTimeout, 1);
        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].PushAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("timed out after 1 s", ex.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_network_timeout_too_long_for_a_timer_means_no_limit_instead_of_failing_every_operation()
    {
        var (_, git, config, _) = await OpenWithFakeSshAsync(askpass: null);
        foreach (var seconds in new[] { 10_000_000d, 1e300 }) // 115 days (a timer takes at most 49.7), and more than a TimeSpan holds
        {
            config.Set(GitSettings.NetworkTimeout, seconds);
            var ex = await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].FetchAsync());
            Assert.Contains("Could not resolve hostname", ex.Message);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ProcessGroupOf(int pid) => Ps("pgid=", pid);

    private static string Ps(string field, int pid)
    {
        var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-o", field, "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }
}
