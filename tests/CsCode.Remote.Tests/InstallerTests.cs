using CsCode.Remote.Client;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// The installer end to end with the bundles tools/publish-server.sh built: the fixed start/install scripts run in a real /bin/sh (the same scripts ssh
/// runs remotely), the bundle is streamed in on stdin, checksum-verified, extracted, renamed into place and started. Skips when no bundles exist.
/// </summary>
public class InstallerTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    internal static ServerBundleSet? PublishedBundles() =>
        ServerBundleSet.Find(Environment.GetEnvironmentVariable("CSCODE_SERVER_BUNDLES"), AppContext.BaseDirectory);

    internal static ServerBundleSet RequireBundles()
    {
        var set = PublishedBundles();
        Assert.SkipWhen(set is null, "no server bundles: run tools/publish-server.sh (writes .scratch/server-bundles)");
        return set!;
    }

    private sealed class Remote : IDisposable
    {
        public readonly TempDirectory Dir = new("cscode-install-");
        public readonly string Marker = "cscode-test-" + Guid.NewGuid().ToString("N")[..12];
        public string Root => Dir.Combine("root");
        public string Data => Dir.Combine("data");
        public string Home => Dir.Combine("home");

        public Remote() => Directory.CreateDirectory(Home);

        public LocalRemoteShell Shell() => new(environment: new Dictionary<string, string?> { ["HOME"] = Home, ["SSH_AUTH_SOCK"] = null, ["XDG_CACHE_HOME"] = Dir.Combine("cache") });

        public ShellServerTransport Transport(ServerBundleSet bundles, bool force = false) =>
            new(Shell(), new ServerLaunchOptions { InstallRoot = Root, DataDir = Data, Marker = Marker, Bundles = bundles, ForceReinstall = force });

        public void Dispose() => Dir.Dispose();
    }

    private static async Task<(HelloResult Hello, List<string> Progress)> ConnectOnceAsync(IRemoteTransport transport)
    {
        var progress = new List<string>();
        var session = await RemoteSession.ConnectAsync(transport, UriMapper.Identity, "test", _ => { }, new SyncProgress(progress), NullLogger.Instance, Ct);
        var hello = session.Server;
        await session.DisposeAsync();
        return (hello, progress);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public async Task The_published_bundle_is_uploaded_verified_installed_started_and_reused()
    {
        var bundles = RequireBundles();
        using var remote = new Remote();
        var (hello, progress) = await ConnectOnceAsync(remote.Transport(bundles));
        Assert.Equal(RemoteProtocol.Version, hello.ProtocolVersion);
        Assert.Contains(progress, p => p.StartsWith("Installing the server", StringComparison.Ordinal));
        var installed = Directory.GetDirectories(Path.Combine(remote.Root, "bin")).Select(d => Path.GetFileName(d)!).ToArray();
        var sc = bundles.SelfContained("linux-x64");
        Assert.Equal([sc?.Hash ?? bundles.FrameworkDependent!.Hash], installed); // self-contained preferred; no temp dirs left behind
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(remote.Root, "bin", installed[0], "cscode-server")).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(remote.Data, hello.DataDir);

        // second connection: already installed, one run of the start script, nothing uploaded
        var (again, progress2) = await ConnectOnceAsync(remote.Transport(bundles));
        Assert.DoesNotContain(progress2, p => p.StartsWith("Installing", StringComparison.Ordinal));
        Assert.Equal(hello.ServerVersion, again.ServerVersion);

        // reinstall: uploads again over the existing install
        var (_, progress3) = await ConnectOnceAsync(remote.Transport(bundles, force: true));
        Assert.Contains(progress3, p => p.StartsWith("Installing", StringComparison.Ordinal));
        await TestEnv.AssertNoProcessesAsync(remote.Marker);
    }

    [Fact]
    public async Task The_portable_bundle_runs_on_the_remotes_own_runtime()
    {
        var bundles = RequireBundles();
        Assert.SkipWhen(bundles.FrameworkDependent is null, "no portable bundle published");
        Assert.SkipWhen(LanguageBridgeTests.FindOnPath("dotnet") is null, "no dotnet on PATH for the 'remote'");
        var portableOnly = new ServerBundleSet(bundles.Directory, [bundles.FrameworkDependent!]);
        using var remote = new Remote();
        var (hello, progress) = await ConnectOnceAsync(remote.Transport(portableOnly));
        Assert.Contains(progress, p => p.Contains("framework-dependent", StringComparison.Ordinal));
        Assert.Equal(RemoteProtocol.Version, hello.ProtocolVersion);
        await TestEnv.AssertNoProcessesAsync(remote.Marker);
    }

    [Fact]
    public async Task A_corrupted_bundle_is_refused_and_nothing_is_installed()
    {
        var bundles = RequireBundles();
        var good = bundles.SelfContained("linux-x64") ?? bundles.FrameworkDependent!;
        var tampered = new ServerBundleSet(bundles.Directory, [good with { Sha256 = new string('0', 64), Hash = "00000000000000aa" }]);
        using var remote = new Remote();
        var ex = await Assert.ThrowsAsync<RemoteBootstrapException>(() => ConnectOnceAsync(remote.Transport(tampered)));
        Assert.Contains("checksum mismatch", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(remote.Root, "bin"))); // the private temp dir was removed too
    }

    [Fact]
    public async Task Older_installs_are_cleaned_up_keeping_the_two_most_recent()
    {
        var bundles = RequireBundles();
        using var remote = new Remote();
        var bin = Path.Combine(remote.Root, "bin");
        string[] old = ["1111111111111111", "2222222222222222", "3333333333333333", "4444444444444444"];
        for (var i = 0; i < old.Length; i++)
        {
            Directory.CreateDirectory(Path.Combine(bin, old[i]));
            Directory.SetLastWriteTimeUtc(Path.Combine(bin, old[i]), DateTime.UtcNow.AddDays(-10 + i)); // 4444… newest
        }
        Directory.CreateDirectory(Path.Combine(bin, "not-a-hash")); // never touched
        await ConnectOnceAsync(remote.Transport(bundles));
        var left = Directory.GetDirectories(bin).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal).ToArray();
        var current = (bundles.SelfContained("linux-x64") ?? bundles.FrameworkDependent!).Hash;
        Assert.Equal(new[] { "3333333333333333", "4444444444444444", current, "not-a-hash" }.Order(StringComparer.Ordinal), left);
    }

    [Fact]
    public async Task A_platform_without_a_bundle_fails_with_the_reason()
    {
        var bundles = RequireBundles();
        var otherPlatform = new ServerBundleSet(bundles.Directory, bundles.Bundles.Where(b => b.Flavour == BundleFlavour.SelfContained).Select(b => b with { Rid = "linux-riscv64" }).ToArray());
        using var remote = new Remote();
        var ex = await Assert.ThrowsAsync<RemoteBootstrapException>(() => ConnectOnceAsync(remote.Transport(otherPlatform)));
        Assert.Contains("No server bundle for Linux", ex.Message);
    }
}
