using System.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Terminal;
using CsCode.Remote.Client;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>The docker:// and wsl:// authorities and the argument vectors of their carriers (no docker needed).</summary>
public class TransportSeamTests
{
    [Fact]
    public void Docker_and_wsl_authorities_parse_map_and_refuse_option_like_or_odd_names()
    {
        var docker = RemoteAuthority.Parse("docker://web_1.dev-2");
        Assert.Equal((RemoteSchemes.Docker, "web_1.dev-2", (string?)null), (docker.Scheme, docker.Host, docker.User));
        Assert.Equal("docker://web_1.dev-2/", docker.Root.AbsoluteUri);
        var mapper = new SshUriMapper(docker);
        var uri = mapper.FromRemotePath("/srv/my app/#a.cs");
        Assert.Equal("docker", uri.Scheme);
        Assert.Equal("/srv/my app/#a.cs", mapper.ToRemotePath(uri));
        Assert.True(mapper.Handles(uri));
        Assert.False(mapper.Handles(new Uri("ssh://web_1.dev-2/srv/a.cs"))); // same name, another transport
        Assert.Equal(new Uri("file:///srv/my%20app/%23a.cs"), mapper.ToServer(uri));

        var wsl = RemoteAuthority.Parse("wsl://me@Ubuntu-22.04");
        Assert.Equal((RemoteSchemes.Wsl, "Ubuntu-22.04", "me"), (wsl.Scheme, wsl.Host, wsl.User));
        Assert.Equal("wsl://me@ubuntu-22.04/", wsl.Root.AbsoluteUri); // URIs lower-case the name; the authority keeps it for wsl.exe
        Assert.True(new SshUriMapper(wsl).Handles(new Uri("wsl://me@UBUNTU-22.04/home/me")));
        Assert.Equal(RemoteSchemes.Ssh, RemoteAuthority.Parse("me@box:22").Scheme);

        foreach (var bad in new[] { "docker://-rm", "docker://a;b", "docker://a$b", "wsl://x|y" })
            Assert.ThrowsAny<Exception>(() => RemoteAuthority.Parse(bad));
    }

    [Fact]
    public void A_server_that_cannot_find_libssl_is_reported_with_what_to_install()
    {
        var message = ShellServerTransport.LastLines("cscode-server starting\nNo usable version of libssl was found\n");
        Assert.Contains("No usable version of libssl was found", message);
        Assert.Contains("libssl3", message);
        Assert.Equal("a | b", ShellServerTransport.LastLines("a\nb\n"));
    }

    [Fact]
    public void The_docker_and_wsl_shells_pass_the_constant_script_and_every_value_as_separate_arguments()
    {
        var hostile = "x'; rm -rf / #$(id)`id`";
        var docker = new DockerRemoteShell("/usr/bin/docker", RemoteAuthority.Parse("docker://root@c0ffee"));
        var d = docker.Launch(ServerScripts.Start, ["", hostile]);
        Assert.Equal("/usr/bin/docker", d.FileName);
        Assert.Equal(["exec", "-i", "-u", "root", "c0ffee", "sh", "-c", ServerScripts.Start, "cscode", "", hostile], d.Arguments);
        var wsl = new WslRemoteShell("wsl.exe", RemoteAuthority.Parse("wsl://Debian"));
        Assert.Equal(["-d", "Debian", "--exec", "sh", "-c", "echo", "cscode", hostile], wsl.Launch("echo", [hostile]).Arguments);
        Assert.Throws<ArgumentException>(() => new DockerRemoteShell("docker", RemoteAuthority.Parse("ssh://box")));
    }
}

/// <summary>
/// The Docker transport against a REAL container: a small official image pinned by digest (tools/fetch-docker-test-image.sh), a container named
/// <c>cscode-remote-test-&lt;random&gt;</c> with no network and no mounts, removed by its ID after the test (also on failure). The client
/// bootstraps the published linux-x64 server into it through <c>docker exec</c> (upload, checksum, install, start as a persistent server),
/// then edits files, searches and runs a terminal in the container.
/// </summary>
public class DockerTransportTests
{
    /// <summary>mcr.microsoft.com/dotnet/runtime-deps:10.0 (Microsoft's base image for self-contained .NET apps), pinned by digest.</summary>
    public const string Image = "mcr.microsoft.com/dotnet/runtime-deps@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832";
    private static CancellationToken Ct => TestEnv.Ct;

    private static (int Code, string Out, string Err) Docker(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(true); return (-1, "", "docker timed out"); }
        return (p.ExitCode, o.Result.Trim(), e.Result.Trim());
    }

    private static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/docker")) return "needs the docker CLI";
        if (Docker("version", "--format", "{{.Server.Version}}").Code != 0) return "the docker daemon is not usable without sudo";
        if (Docker("image", "inspect", Image).Code != 0)
        {
            if (Environment.GetEnvironmentVariable("CSCODE_TEST_DOCKER_PULL") != "1") return $"the pinned test image is not present (run tools/fetch-docker-test-image.sh, or set CSCODE_TEST_DOCKER_PULL=1)";
            if (Docker("pull", Image).Code != 0) return "pulling the pinned test image failed";
        }
        if (InstallerTests.PublishedBundles()?.SelfContained("linux-x64") is null) return "no linux-x64 server bundle: run tools/publish-server.sh";
        return null;
    }

    [Fact]
    public async Task A_container_is_bootstrapped_through_docker_exec_and_its_files_search_and_terminal_work()
    {
        Assert.SkipWhen(SkipReason() is { } why, SkipReason() ?? "");
        var name = "cscode-remote-test-" + Guid.NewGuid().ToString("N")[..10];
        var run = Docker("run", "-d", "--name", name, "--network", "none", "--label", "cscode-remote-test=1", Image, "sleep", "infinity");
        Assert.True(run.Code == 0, "docker run failed: " + run.Err);
        var id = run.Out;
        RemoteTestHost? hostRef = null;
        try
        {
            await using var host = new RemoteTestHost(o =>
            {
                o.Bundles = InstallerTests.PublishedBundles();
                o.GracePeriod = Environment.GetEnvironmentVariable("CSCODE_TEST_DOCKER_STDIO") == "1" ? TimeSpan.Zero : TimeSpan.FromSeconds(60);
            }, loopback: false, r3: true);
            hostRef = host;
            var target = "docker://" + name;
            await host.Manager.ConnectAsync(target, Ct);
            Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
            var server = host.Manager.Session!.Server;
            Assert.Equal("/root", server.Home); // the container's own user and file system
            Assert.Equal(Environment.GetEnvironmentVariable("CSCODE_TEST_DOCKER_STDIO") != "1", server.Capabilities.Contains(RemoteProtocol.Capabilities.Persistent));
            Assert.Contains("Installing the server", host.Output.Text(RemoteConnectionManager.OutputChannel), StringComparison.Ordinal);

            // files: written through the docker:// file system, visible inside the container (and only there)
            var folder = host.Manager.ToUri("/root/ws/")!;
            Assert.Equal("docker", folder.Scheme);
            await host.FileSystem.WriteFileAsync(host.Manager.ToUri("/root/ws/src/hello.c")!, "int main(void) { return 42; } /* needle */\n"u8.ToArray(), true, Ct);
            Assert.Equal("int main(void) { return 42; } /* needle */", Docker("exec", id, "cat", "/root/ws/src/hello.c").Out);
            await host.WorkspaceService.OpenFolderAsync(folder, Ct);
            var direct = new List<Uri>();
            await foreach (var u in host.Services.GetServices<IFileSearchProvider>().Single(p => p.Scheme == "docker").FindFilesAsync(folder, new FileSearchOptions(null, []), Ct)) direct.Add(u);
            Assert.Equal([host.Manager.ToUri("/root/ws/src/hello.c")!], direct);
            var files = new List<Uri>();
            await foreach (var u in host.WorkspaceService.FindFilesAsync(null, 0, Ct)) files.Add(u);
            Assert.True(files.SequenceEqual([host.Manager.ToUri("/root/ws/src/hello.c")!]), "found: " + string.Join(", ", files) + " folders: " + string.Join(", ", host.WorkspaceService.Folders));

            // server-side text search under the docker scheme
            var provider = host.Services.GetServices<ITextSearchProvider>().Single(p => p.Scheme == "docker");
            var hits = new List<TextSearchHit>();
            await foreach (var batch in provider.SearchAsync(new TextSearchQuery([folder], "needle", false, false, false, [], true, 100, 1 << 20), Ct)) hits.AddRange(batch);
            Assert.Equal(host.Manager.ToUri("/root/ws/src/hello.c"), Assert.Single(hits).Uri);

            // a terminal in the container
            var terminals = host.Services.GetRequiredService<TerminalService>();
            var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
            await t.SendTextAsync("grep PRETTY_NAME /etc/os-release; pwd", true, Ct);
            var screen = await TerminalProbe.WaitForTextAsync(t, "\nPRETTY_NAME=\"Ubuntu"); // runtime-deps:10.0 is Ubuntu 24.04 based
            Assert.Contains("/root/ws", screen);
            await terminals.KillAsync(t, Ct);

            Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
            await host.Manager.DisconnectAsync(Ct);
            // a deliberate disconnect ends the persistent server inside the container too
            await TestEnv.WaitUntilAsync(() => !Docker("exec", id, "sh", "-c", "cat /proc/[0-9]*/cmdline 2>/dev/null | tr '\\0' ' '").Out.Contains("cscode-server", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20), "the server to exit in the container");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.SkipException)
        {
            // the server's own log lives in the container: attach it before the container goes
            var logs = Docker("exec", id, "sh", "-c", "find /root/.cscode-server -maxdepth 3 | head -40; for f in $(find /root/.cscode-server/data -name '*.log' 2>/dev/null); do echo \"== $f\"; tail -n 60 \"$f\"; done; cat /proc/[0-9]*/cmdline 2>/dev/null | tr '\\0' ' '");
            throw new Xunit.Sdk.XunitException(ex + "\n--- server files and log in the container ---\n" + logs.Out + logs.Err + "\n--- client Remote log ---\n" + hostRef?.Output.Text(RemoteConnectionManager.OutputChannel));
        }
        finally
        {
            var removed = Docker("rm", "-f", id); // by ID: only the container this test created
            Assert.True(removed.Code == 0 || removed.Err.Contains("No such container", StringComparison.Ordinal), "docker rm failed: " + removed.Err);
        }
    }
}
