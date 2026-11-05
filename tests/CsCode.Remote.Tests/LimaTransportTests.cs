using System.Diagnostics;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;
using CsCode.Remote.Shell;
using CsCode.Remote.Transport;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>The lima:// authority, its limactl carrier's argument vector and the instance list (no Lima needed).</summary>
public class LimaTransportTests
{
    [Fact]
    public void A_lima_authority_parses_maps_and_refuses_option_like_or_odd_names()
    {
        var lima = RemoteAuthority.Parse("lima://NixOS-x86");
        Assert.Equal((RemoteSchemes.Lima, "NixOS-x86", null), (lima.Scheme, lima.Host, lima.User));
        Assert.Equal("lima://nixos-x86/", lima.Root.AbsoluteUri); // URIs lower-case the name; the authority keeps it for limactl
        Assert.Equal("lima://nixos-x86", lima.ToString());
        Assert.True(new SshUriMapper(lima).Handles(new Uri("lima://NIXOS-X86/home/lima.guest/src")));
        Assert.Equal("lima://nixos-x86/home/lima.guest/src", new SshUriMapper(lima).ToClient(new Uri("file:///home/lima.guest/src")).AbsoluteUri);
        Assert.True(RemoteSchemes.IsRemote("lima"));
        foreach (var bad in new[] { "lima://-f", "lima://a;b", "lima://a$b", "lima://x|y", "lima://a b" })
            Assert.ThrowsAny<ArgumentException>(() => RemoteAuthority.Parse(bad));
    }

    [Fact]
    public void The_lima_shell_starts_the_instance_and_passes_the_constant_script_and_every_value_as_separate_arguments()
    {
        var hostile = "x'; rm -rf / #$(id)`id`";
        var shell = new LimaRemoteShell("/usr/local/bin/limactl", RemoteAuthority.Parse("lima://nixos"));
        var launch = shell.Launch(ServerScripts.Start, ["", hostile]);
        Assert.Equal("/usr/local/bin/limactl", launch.FileName);
        Assert.Equal(["shell", "--start", "--workdir", "/", "nixos", "--", "sh", "-c", ServerScripts.Start, "cscode", "", hostile], launch.Arguments);
        Assert.Equal("lima nixos", shell.Description);
        Assert.Throws<ArgumentException>(() => new LimaRemoteShell("limactl", RemoteAuthority.Parse("docker://nixos")));
    }

    private const string Listing = """
        {"name":"jiffy-ubuntu","hostname":"lima-jiffy-ubuntu","status":"Running","dir":"/Users/me/.lima/jiffy-ubuntu","vmType":"vz","arch":"aarch64","cpus":6}
        {"name":"nixos-x86","status":"Stopped","vmType":"qemu","arch":"x86_64"}

        {"name":"-evil","status":"Running"}
        {"name":"a b","status":"Running"}
        not json at all
        {"hostname":"nameless"}
        {"name":"nixos","status":"Running","vmType":"vz","arch":"aarch64"}
        """;

    [Fact]
    public void The_instance_list_reads_limactls_json_lines_sorted_and_skips_names_that_could_not_be_an_argument()
    {
        var all = LimaInstances.Parse(Listing);
        Assert.Equal(["jiffy-ubuntu", "nixos", "nixos-x86"], all.Select(i => i.Name));
        var nixos = all.Single(i => i.Name == "nixos");
        Assert.True(nixos.Running);
        Assert.Equal(("aarch64", "vz", "lima://nixos"), (nixos.Arch, nixos.VmType, nixos.Target));
        Assert.False(all.Single(i => i.Name == "nixos-x86").Running);
        Assert.Empty(LimaInstances.Parse(""));
        Assert.Empty(LimaInstances.Parse("garbage"));
    }

    [Fact]
    public void The_instance_list_also_reads_a_json_array()
    {
        var all = LimaInstances.Parse("""[{"name":"default","status":"Running"},{"name":"alpine","status":"Stopped","arch":"x86_64"}]""");
        Assert.Equal(["alpine", "default"], all.Select(i => i.Name));
        Assert.Equal("Unknown", LimaInstances.Parse("""[{"name":"x"}]""").Single().Status);
    }

    [Fact]
    public async Task Listing_without_limactl_says_how_to_fix_it()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => LimaInstances.ListAsync("/nonexistent/limactl", null, TestEnv.Ct));
        Assert.Contains("remote.lima.path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_bar_and_remote_name_show_the_vm()
    {
        var connected = new RemoteConnectionStatus(RemoteConnectionState.Connected, "lima://nixos", new Uri("lima://nixos/"));
        Assert.Equal("lima", RemoteShellContributions.RemoteName(connected));
        Assert.Equal("Lima: nixos", RemoteShellContributions.Label(connected));
    }

    /// <summary>The instance a real-VM run connects to: set CSCODE_TEST_LIMA=nixos (a running instance; its server bundles from CSCODE_SERVER_BUNDLES or .scratch).</summary>
    private static string? RealInstance => Environment.GetEnvironmentVariable("CSCODE_TEST_LIMA") is { Length: > 0 } n ? n : null;

    private static (int Code, string Out, string Err) Limactl(params string[] args)
    {
        var psi = new ProcessStartInfo("limactl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(true); return (-1, "", "limactl timed out"); }
        return (p.ExitCode, o.Result.Trim(), e.Result.Trim());
    }

    [Fact]
    public async Task A_real_lima_vm_is_bootstrapped_through_limactl_shell_and_its_files_work()
    {
        Assert.SkipWhen(RealInstance is null, "set CSCODE_TEST_LIMA=<running instance> to connect to a real Lima VM");
        var instance = RealInstance!;
        Assert.SkipWhen(InstallerTests.PublishedBundles() is null, "no server bundles: run tools/publish-server.sh or set CSCODE_SERVER_BUNDLES");
        var listed = await LimaInstances.ListAsync("limactl", null, TestEnv.Ct);
        Assert.True(listed.Any(i => i.Name == instance && i.Running), $"{instance} is not a running Lima instance");
        var dir = "/tmp/cscode-lima-test-" + Guid.NewGuid().ToString("N")[..10];
        RemoteTestHost? hostRef = null;
        try
        {
            await using var host = new RemoteTestHost(o =>
            {
                o.Bundles = InstallerTests.PublishedBundles();
                o.GracePeriod = TimeSpan.Zero;
            }, loopback: false, r3: true);
            hostRef = host;
            await host.Manager.ConnectAsync("lima://" + instance, TestEnv.Ct);
            Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
            var server = host.Manager.Session!.Server;
            Assert.StartsWith("/home/", server.Home, StringComparison.Ordinal); // the VM user's own file system
            var file = host.Manager.ToUri(dir + "/src/hello.txt")!;
            Assert.Equal("lima", file.Scheme);
            await host.FileSystem.WriteFileAsync(file, "needle in the VM\n"u8.ToArray(), true, TestEnv.Ct);
            Assert.Equal("needle in the VM", Limactl("shell", "--workdir", "/", instance, "--", "cat", dir + "/src/hello.txt").Out);
            Assert.Equal("needle in the VM\n", System.Text.Encoding.UTF8.GetString(await host.FileSystem.ReadFileAsync(file, TestEnv.Ct)));
            await host.Manager.DisconnectAsync(TestEnv.Ct);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.SkipException)
        {
            throw new Xunit.Sdk.XunitException(ex + "\n--- client Remote log ---\n" + hostRef?.Output.Text(RemoteConnectionManager.OutputChannel));
        }
        finally
        {
            Limactl("shell", "--workdir", "/", instance, "--", "rm", "-rf", dir);
        }
    }
}
