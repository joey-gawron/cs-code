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
        Assert.Equal("1000", env["GIT_HTTP_LOW_SPEED_LIMIT"]);
        Assert.Equal("60", env["GIT_HTTP_LOW_SPEED_TIME"]);
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
    public void The_users_own_http_stall_limits_are_kept()
    {
        Assert.False(GitNetwork.Environment(Env(), new Dictionary<string, string> { ["http.lowspeedtime"] = "600" }, null).ContainsKey("GIT_HTTP_LOW_SPEED_TIME"));
        Assert.False(GitNetwork.Environment(Env(("GIT_HTTP_LOW_SPEED_LIMIT", "1")), NoConfig, null).ContainsKey("GIT_HTTP_LOW_SPEED_LIMIT"));
        Assert.Equal(new Dictionary<string, string> { ["core.sshcommand"] = "ssh -v", ["http.lowspeedtime"] = "600" },
            GitNetwork.ParseConfig("core.sshcommand ssh -i global-key\ncore.sshcommand ssh -v\nhttp.lowSpeedTime 600\n")); // the repository's value (last) wins
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
        repo.Git("config", "core.sshCommand", ssh);
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

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ProcessGroupOf(int pid)
    {
        var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-o", "pgid=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }
}
