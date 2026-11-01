using System.Diagnostics;
using System.Text;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;
using CsCode.Remote.Ssh;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Remote.Tests;

public class ShellQuotingFuzzTests
{
    private const string Hostile = ";$()`'\"&|\n\\ *?~#!<>{}[]=%\t\r";

    /// <summary>Runs the command line the way a remote login shell would (sh -c "&lt;line&gt;") and returns what the script saw as its arguments.</summary>
    private static string[] RunThroughShell(IReadOnlyList<string> args)
    {
        // The script prints each argument terminated by NUL, so newlines inside arguments survive.
        var line = PosixShell.ScriptCommand("for a in \"$@\"; do printf '%s\\0' \"$a\"; done", args);
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(line);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var parts = output.Split('\0');
        return parts[..^1];
    }

    [Fact]
    public void Every_hostile_character_reaches_the_script_literally()
    {
        Assert.SkipUnless(File.Exists("/bin/sh"), "no /bin/sh");
        string[] args = ["; rm -rf ~", "$(touch /tmp/pwned)", "`id`", "it's", "\"double\"", "a & b | c", "line1\nline2", "back\\slash", "", " spaced ", Hostile];
        Assert.Equal(args, RunThroughShell(args));
    }

    [Fact]
    public void Random_strings_from_the_hostile_alphabet_round_trip()
    {
        Assert.SkipUnless(File.Exists("/bin/sh"), "no /bin/sh");
        var rng = new Random(20261029);
        var alphabet = Hostile + "abcXYZ019-_./ü文";
        for (var round = 0; round < 25; round++)
        {
            var args = Enumerable.Range(0, 8).Select(_ =>
            {
                var sb = new StringBuilder();
                var n = rng.Next(0, 24);
                for (var i = 0; i < n; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
                return sb.ToString();
            }).ToArray();
            Assert.Equal(args, RunThroughShell(args));
        }
    }

    [Fact]
    public void Nul_cannot_be_quoted() => Assert.Throws<ArgumentException>(() => PosixShell.Quote("a\0b"));
}

public class SshArgsTests
{
    [Fact]
    public void The_command_line_is_a_fixed_vector_with_the_destination_after_double_dash()
    {
        var o = new SshOptions { ConfigFile = "/tmp/cfg", ExtraOptions = ["Compression=yes"], ConnectTimeout = TimeSpan.FromSeconds(7) };
        var args = SshArgs.Command(o, new RemoteAuthority("box", "me", 2222), "sh -c 'x'");
        Assert.Equal(["-T", "-F", "/tmp/cfg", "-o", "Compression=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4", "-o", "ExitOnForwardFailure=yes",
            "-o", "ConnectTimeout=7", "-l", "me", "-p", "2222", "--", "box", "sh -c 'x'"], args);
        Assert.Equal(["-G", "-F", "/tmp/cfg", "-o", "Compression=yes", "-l", "me", "--", "box"], SshArgs.ResolveConfig(o, new RemoteAuthority("box", "me")));
    }

    [Fact]
    public void Going_around_a_shared_connection_puts_ControlPath_none_before_the_user_s_options()
    {
        // ssh takes the first value of a repeated -o option: a user's "ControlPath=…" in remote.ssh.options must not win over it.
        var o = new SshOptions { ExtraOptions = ["ControlPath=/tmp/mine", "ControlMaster=auto"], BypassConnectionSharing = true };
        var args = SshArgs.Command(o, new RemoteAuthority("box"), "true");
        Assert.Equal(["-T", "-o", "ControlPath=none", "-o", "ControlPath=/tmp/mine", "-o", "ControlMaster=auto"], args.Take(7));
        Assert.Equal(["-G", "-o", "ControlPath=none"], SshArgs.ResolveConfig(o, new RemoteAuthority("box")).Take(3));
        Assert.DoesNotContain("ControlPath=none", SshArgs.Command(o with { BypassConnectionSharing = false }, new RemoteAuthority("box"), "true"));
    }

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/x")]
    [InlineData("box name")]
    [InlineData("box\nother")]
    public void Hostile_destinations_never_become_options(string host) =>
        Assert.Throws<ArgumentException>(() => SshArgs.Command(new SshOptions(), new RemoteAuthority(host), "true"));

    [Theory]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("NoValue")]
    [InlineData("Key=a\nb")]
    public void Extra_options_must_be_key_value_pairs(string option) =>
        Assert.Throws<ArgumentException>(() => SshArgs.Command(new SshOptions { ExtraOptions = [option] }, new RemoteAuthority("box"), "true"));

    [Fact]
    public void Ssh_minus_G_output_is_parsed_into_effective_settings()
    {
        var cfg = SshEffectiveConfig.Parse("""
            user me
            hostname 10.0.0.5
            port 2222
            identityfile ~/.ssh/id_ed25519
            identityfile ~/.ssh/id_rsa
            proxyjump bastion,inner
            serveraliveinterval 15
            sendenv LANG
            sendenv LC_*
            """);
        Assert.Equal("me", cfg.User);
        Assert.Equal("10.0.0.5", cfg.HostName);
        Assert.Equal(2222, cfg.Port);
        Assert.Equal(["~/.ssh/id_ed25519", "~/.ssh/id_rsa"], cfg.All("IdentityFile"));
        Assert.Equal(["bastion", "inner"], cfg.ProxyJump);
        Assert.Equal(["LANG", "LC_*"], cfg.All("sendenv"));
        Assert.Empty(SshEffectiveConfig.Parse("proxyjump none").ProxyJump);
    }

    [Fact]
    public void Real_ssh_minus_G_is_parsed_without_reading_the_users_config()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/ssh"), "no ssh client");
        using var dir = new TempDirectory("cscode-sshg-");
        var cfg = dir.WriteFile("config", "Host alias\n  HostName 127.0.0.9\n  Port 2299\n  User someone\n");
        var psi = new ProcessStartInfo("/usr/bin/ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in SshArgs.ResolveConfig(new SshOptions { ConfigFile = cfg }, new RemoteAuthority("alias"))) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var parsed = SshEffectiveConfig.Parse(output);
        Assert.Equal("127.0.0.9", parsed.HostName);
        Assert.Equal(2299, parsed.Port);
        Assert.Equal("someone", parsed.User);
    }

    [Fact]
    public void Host_entries_are_listed_without_wildcards_following_includes()
    {
        using var dir = new TempDirectory("cscode-sshcfg-");
        dir.WriteFile("conf.d/work.conf", "Host work-a work-b\n  HostName w\nHost *.internal\n");
        dir.WriteFile("conf.d/home.conf", "Host=pi\n");
        var main = dir.WriteFile("config", """
            # comment
            Host box "quoted host" !negated
              HostName 10.0.0.1
            Host *
              ServerAliveInterval 30
            Include conf.d/*.conf
            Match host foo
            Host box
            host lower?case
            """);
        Assert.Equal(["box", "quoted host", "pi", "work-a", "work-b"], SshConfigHosts.List(main, dir.Path));
        Assert.Equal(["a", "b"], SshConfigHosts.Parse("Host a b *\nHost !c\nHost a"));
        Assert.Empty(SshConfigHosts.List(Path.Combine(dir.Path, "missing")));
    }
}

public class InstallerDecisionTests
{
    private static ServerBundleSet Bundles(params ServerBundle[] b) => new("/bundles", b);
    private static readonly ServerBundle Sc = new(BundleFlavour.SelfContained, "linux-x64", "sc.tar.gz", "aa", "0123456789abcdef");
    private static readonly ServerBundle Fd = new(BundleFlavour.FrameworkDependent, "any", "fd.tar.gz", "bb", "fedcba9876543210", "10.0");

    [Fact]
    public void The_probe_output_is_parsed()
    {
        var p = RemoteProbe.Parse("os=Linux\narch=x86_64\nhome=/home/me\nroot=/home/me/.cscode-server\n" +
                                  "dotnet=Microsoft.AspNetCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n" +
                                  "dotnet=Microsoft.NETCore.App 8.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n" +
                                  "dotnet=Microsoft.NETCore.App 10.0.12 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n");
        Assert.Equal("linux-x64", p.Rid);
        Assert.Equal("/home/me", p.Home);
        Assert.Equal([new Version(8, 0, 11), new Version(10, 0, 12)], p.DotnetRuntimes);
    }

    [Theory]
    [InlineData("Linux", "aarch64", "linux-arm64")]
    [InlineData("Darwin", "arm64", "osx-arm64")]
    [InlineData("Darwin", "x86_64", "osx-x64")]
    [InlineData("SunOS", "i86pc", null)]
    public void Platforms_map_to_runtime_ids(string os, string arch, string? rid) => Assert.Equal(rid, new RemoteProbe(os, arch, null, null, []).Rid);

    [Fact]
    public void A_self_contained_bundle_for_the_platform_wins()
    {
        var plan = InstallDecision.Decide(new RemoteProbe("Linux", "x86_64", null, null, [new Version(10, 0, 1)]), Bundles(Fd, Sc));
        Assert.Equal(InstallAction.Upload, plan.Action);
        Assert.Same(Sc.File, plan.Bundle!.File);
    }

    [Fact]
    public void Framework_dependent_is_the_fallback_when_the_remote_has_a_compatible_runtime()
    {
        var plan = InstallDecision.Decide(new RemoteProbe("Linux", "aarch64", null, null, [new Version(10, 0, 3)]), Bundles(Sc, Fd));
        Assert.Equal(InstallAction.Upload, plan.Action);
        Assert.Equal(BundleFlavour.FrameworkDependent, plan.Bundle!.Flavour);
    }

    [Fact]
    public void No_fitting_bundle_is_an_explained_refusal()
    {
        var oldRuntime = InstallDecision.Decide(new RemoteProbe("Linux", "aarch64", null, null, [new Version(8, 0, 1)]), Bundles(Sc, Fd));
        Assert.Equal(InstallAction.Unsupported, oldRuntime.Action);
        Assert.Contains(".NET 10", oldRuntime.Reason);
        Assert.Equal(InstallAction.Unsupported, InstallDecision.Decide(new RemoteProbe("", "", null, null, []), Bundles(Sc)).Action); // not a POSIX shell / Windows
        Assert.Equal(InstallAction.Unsupported, InstallDecision.Decide(new RemoteProbe("Linux", "x86_64", null, null, []), null).Action);
        Assert.Contains("linux-arm64", InstallDecision.Decide(new RemoteProbe("Linux", "aarch64", null, null, []), Bundles(Sc)).Reason);
    }

    [Fact]
    public void A_missing_bundle_for_a_supported_platform_says_which_bundles_there_are_and_how_to_build_the_missing_one()
    {
        // Found on a real aarch64 VM: bundles built with the old linux-x64-only default refused it with no way forward.
        var arm = InstallDecision.Decide(new RemoteProbe("Linux", "aarch64", null, null, []), Bundles(Sc, Fd));
        Assert.Equal(InstallAction.Unsupported, arm.Action);
        Assert.Contains("The bundles in /bundles are for linux-x64", arm.Reason);
        Assert.Contains("tools/publish-server.sh --rid linux-arm64", arm.Reason);
        Assert.Contains("remote.ssh.serverBundles", arm.Reason);
        Assert.Contains("tools/publish-server.sh --rid osx-arm64", InstallDecision.Decide(new RemoteProbe("Darwin", "arm64", null, null, []), Bundles(Sc)).Reason);
        Assert.DoesNotContain("publish-server.sh --rid", InstallDecision.Decide(new RemoteProbe("SunOS", "i86pc", null, null, []), Bundles(Sc)).Reason); // nothing to build
    }

    [Fact]
    public void Manifests_round_trip_and_bad_hashes_are_rejected()
    {
        using var dir = new TempDirectory("cscode-manifest-");
        File.WriteAllText(Path.Combine(dir.Path, ServerBundleSet.ManifestFile), ServerBundleSet.Serialize([Sc, Fd], "1.2.3"));
        var set = ServerBundleSet.Load(dir.Path);
        Assert.Equal("1.2.3", set.Version);
        Assert.Equal(["0123456789abcdef", "fedcba9876543210"], set.CandidateHashes);
        Assert.Equal(Path.Combine(dir.Path, "sc.tar.gz"), set.SelfContained("linux-x64")!.FullPath);
        File.WriteAllText(Path.Combine(dir.Path, ServerBundleSet.ManifestFile), ServerBundleSet.Serialize([Sc with { Hash = "../../etc" }], null));
        Assert.Throws<InvalidDataException>(() => ServerBundleSet.Load(dir.Path));
        Assert.Null(ServerBundleSet.Find(null, dir.Path + "/nothing-here"));
    }
}
