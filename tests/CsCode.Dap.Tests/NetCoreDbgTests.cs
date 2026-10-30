using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

public class ArchiveSafetyTests
{
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil")]
    [InlineData("a\\..\\..\\evil")]
    [InlineData("")]
    public void Escaping_names_are_rejected(string name)
    {
        using var dir = new TempDirectory();
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.SafeDestination(dir.Path, name));
    }

    [Theory]
    [InlineData("netcoredbg/netcoredbg")]
    [InlineData("a/b/c.txt")]
    [InlineData("./x")]
    [InlineData("dir/")]
    public void Normal_names_stay_inside_the_root(string name)
    {
        using var dir = new TempDirectory();
        var full = NetCoreDbgInstaller.SafeDestination(dir.Path, name);
        Assert.StartsWith(Path.GetFullPath(dir.Path), full);
    }

    [Fact]
    public void A_name_that_only_shares_a_prefix_with_the_root_is_not_inside_it()
    {
        using var dir = new TempDirectory();
        var sibling = dir.Path + "-evil/x";
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.SafeDestination(dir.Path, "../" + Path.GetFileName(dir.Path) + "-evil/x"));
        Assert.NotNull(sibling);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        return ms.ToArray();
    }

    private static byte[] TarGz(Action<TarWriter> fill)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, true))
            fill(tar);
        return ms.ToArray();
    }

    private static TarEntry File(string name, string content, UnixFileMode mode = (UnixFileMode)0b110_100_100)
    {
        var e = new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
        return e;
    }

    [Fact]
    public void Zip_slip_entries_are_refused_and_nothing_is_written_outside()
    {
        using var root = new TempDirectory();
        var dest = Path.Combine(root.Path, "dest");
        Directory.CreateDirectory(dest);
        var bytes = Zip(("ok.txt", "fine"), ("../evil.txt", "pwned"));
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractZip(new MemoryStream(bytes), dest));
        Assert.False(System.IO.File.Exists(Path.Combine(root.Path, "evil.txt")));
    }

    [Fact]
    public void Zip_extracts_nested_files()
    {
        using var root = new TempDirectory();
        NetCoreDbgInstaller.ExtractZip(new MemoryStream(Zip(("netcoredbg/netcoredbg.exe", "MZ"), ("netcoredbg/lib/x.dll", "x"))), root.Path);
        Assert.Equal("MZ", System.IO.File.ReadAllText(Path.Combine(root.Path, "netcoredbg", "netcoredbg.exe")));
        Assert.True(System.IO.File.Exists(Path.Combine(root.Path, "netcoredbg", "lib", "x.dll")));
    }

    [Fact]
    public void Tar_slip_and_absolute_entries_are_refused()
    {
        using var root = new TempDirectory();
        var dest = Path.Combine(root.Path, "dest");
        Directory.CreateDirectory(dest);
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(TarGz(t => t.WriteEntry(File("../evil", "x")))), dest));
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(TarGz(t => t.WriteEntry(File("/tmp/evil-abs", "x")))), dest));
        Assert.False(System.IO.File.Exists(Path.Combine(root.Path, "evil")));
    }

    [Fact]
    public void Symlinks_pointing_outside_the_destination_are_refused()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "symlinks");
        using var root = new TempDirectory();
        var dest = Path.Combine(root.Path, "dest");
        Directory.CreateDirectory(dest);
        foreach (var target in new[] { "/etc/passwd", "../../outside", "a/../../../x" })
        {
            var bytes = TarGz(t => t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = target }));
            Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(bytes), dest));
        }
    }

    [Fact]
    public void Hard_links_may_not_reach_outside_either()
    {
        using var root = new TempDirectory();
        var dest = Path.Combine(root.Path, "dest");
        Directory.CreateDirectory(dest);
        var bytes = TarGz(t => t.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "h") { LinkName = "../secret" }));
        Assert.Throws<UnsafeArchiveException>(() => NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(bytes), dest));
    }

    [Fact]
    public void Tar_extracts_files_with_modes_directories_and_safe_relative_symlinks()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "unix modes");
        using var root = new TempDirectory();
        var bytes = TarGz(t =>
        {
            t.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "netcoredbg/"));
            t.WriteEntry(File("netcoredbg/netcoredbg", "#!/bin/sh\n", (UnixFileMode)0b111_101_101));
            t.WriteEntry(File("netcoredbg/data.txt", "data"));
            t.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "netcoredbg/alias") { LinkName = "data.txt" });
        });
        NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(bytes), root.Path);
        var exe = Path.Combine(root.Path, "netcoredbg", "netcoredbg");
        Assert.Equal("data", System.IO.File.ReadAllText(Path.Combine(root.Path, "netcoredbg", "alias")));
        if (OperatingSystem.IsWindows()) return; // (modes do not exist there; guard also satisfies the platform analyzer)
        Assert.True(System.IO.File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        Assert.False(System.IO.File.GetUnixFileMode(Path.Combine(root.Path, "netcoredbg", "data.txt")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Device_nodes_and_fifos_are_never_created()
    {
        using var root = new TempDirectory();
        var bytes = TarGz(t => t.WriteEntry(new PaxTarEntry(TarEntryType.Fifo, "pipe")));
        NetCoreDbgInstaller.ExtractTarGz(new MemoryStream(bytes), root.Path);
        Assert.False(System.IO.File.Exists(Path.Combine(root.Path, "pipe")));
    }
}

public class NetCoreDbgInstallerTests
{
    private sealed class FakeGitHub(byte[] archive, string assetName, string? digest, HttpStatusCode apiStatus = HttpStatusCode.OK, HttpStatusCode downloadStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            if (request.RequestUri!.Host == "api.github.com")
            {
                if (apiStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(apiStatus));
                var asset = new Dictionary<string, object?>
                {
                    ["name"] = assetName, ["size"] = archive.Length,
                    ["browser_download_url"] = $"https://github.com/Samsung/netcoredbg/releases/download/9.9.9-1/{assetName}",
                };
                if (digest is not null) asset["digest"] = "sha256:" + digest;
                var json = JsonSerializer.Serialize(new { tag_name = "9.9.9-1", assets = new[] { asset } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            if (downloadStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(downloadStatus));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }

    private static (byte[] Archive, string AssetName) MakeArchive()
    {
        var asset = NetCoreDbgInstaller.CurrentPlatformAsset() ?? "netcoredbg-linux-amd64.tar.gz";
        using var ms = new MemoryStream();
        if (asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var e = zip.CreateEntry(OperatingSystem.IsWindows() ? "netcoredbg/netcoredbg.exe" : "netcoredbg/netcoredbg");
                using var w = new StreamWriter(e.Open());
                w.Write("fake");
            }
        }
        else
        {
            using var gz = new GZipStream(ms, CompressionLevel.Fastest, true);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax, true);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg/netcoredbg") { Mode = (UnixFileMode)0b110_100_100, DataStream = new MemoryStream("#!/bin/sh\necho fake\n"u8.ToArray()) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg/libdbgshim.so") { Mode = (UnixFileMode)0b110_100_100, DataStream = new MemoryStream("lib"u8.ToArray()) });
        }
        return (ms.ToArray(), asset);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void The_platform_asset_matches_this_machine()
    {
        Assert.NotNull(NetCoreDbgInstaller.CurrentPlatformAsset());
    }

    [Fact]
    public async Task Install_downloads_verifies_extracts_and_makes_the_binary_executable()
    {
        var (archive, asset) = MakeArchive();
        var gh = new FakeGitHub(archive, asset, Sha(archive));
        using var tools = new TempDirectory();
        var reports = new List<(string?, double?)>();
        var exe = await new NetCoreDbgInstaller(gh).InstallAsync(tools.Path, new Progress(reports), Ct);

        Assert.Equal(Path.Combine(tools.Path, "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName), exe);
        Assert.True(File.Exists(exe));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("9.9.9-1", File.ReadAllText(Path.Combine(tools.Path, "netcoredbg", "VERSION")).Trim());
        Assert.Equal([Path.Combine(tools.Path, "netcoredbg")], Directory.GetDirectories(tools.Path)); // no staging leftovers
        Assert.Empty(Directory.GetFiles(tools.Path));
        Assert.Contains(reports, r => r.Item2 is 100.0);
    }

    [Fact]
    public async Task A_tampered_archive_is_rejected_before_extraction()
    {
        var (archive, asset) = MakeArchive();
        var gh = new FakeGitHub(archive, asset, new string('0', 64));
        using var tools = new TempDirectory();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () => await new NetCoreDbgInstaller(gh).InstallAsync(tools.Path, null, Ct));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(tools.Path)); // nothing extracted, nothing left behind
    }

    [Fact]
    public async Task Without_a_published_digest_the_install_still_works_relying_on_archive_integrity()
    {
        var (archive, asset) = MakeArchive();
        using var tools = new TempDirectory();
        var exe = await new NetCoreDbgInstaller(new FakeGitHub(archive, asset, null)).InstallAsync(tools.Path, null, Ct);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task A_corrupt_archive_with_a_matching_digest_fails_safely()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not an archive at all");
        var asset = NetCoreDbgInstaller.CurrentPlatformAsset()!;
        using var tools = new TempDirectory();
        await Assert.ThrowsAnyAsync<Exception>(async () => await new NetCoreDbgInstaller(new FakeGitHub(garbage, asset, Sha(garbage))).InstallAsync(tools.Path, null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(tools.Path));
    }

    [Fact]
    public async Task An_archive_without_the_binary_is_rejected()
    {
        var asset = NetCoreDbgInstaller.CurrentPlatformAsset()!;
        byte[] archive;
        using (var ms = new MemoryStream())
        {
            if (asset.EndsWith(".zip", StringComparison.Ordinal))
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) zip.CreateEntry("netcoredbg/readme.txt");
            }
            else
            {
                using var gz = new GZipStream(ms, CompressionLevel.Fastest, true);
                using var tar = new TarWriter(gz, TarEntryFormat.Pax, true);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg/readme.txt") { DataStream = new MemoryStream("x"u8.ToArray()) });
            }
            archive = ms.ToArray();
        }
        using var tools = new TempDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new NetCoreDbgInstaller(new FakeGitHub(archive, asset, Sha(archive))).InstallAsync(tools.Path, null, Ct));
    }

    [Fact]
    public async Task A_reinstall_replaces_the_previous_version()
    {
        var (archive, asset) = MakeArchive();
        using var tools = new TempDirectory();
        var installer = new NetCoreDbgInstaller(new FakeGitHub(archive, asset, Sha(archive)));
        await installer.InstallAsync(tools.Path, null, Ct);
        File.WriteAllText(Path.Combine(tools.Path, "netcoredbg", "stale.txt"), "old");
        await installer.InstallAsync(tools.Path, null, Ct);
        Assert.False(File.Exists(Path.Combine(tools.Path, "netcoredbg", "stale.txt")));
    }

    [Fact]
    public async Task Http_failures_surface_as_exceptions_and_leave_no_residue()
    {
        var (archive, asset) = MakeArchive();
        using var tools = new TempDirectory();
        await Assert.ThrowsAsync<HttpRequestException>(async () => await new NetCoreDbgInstaller(new FakeGitHub(archive, asset, null, apiStatus: HttpStatusCode.Forbidden)).InstallAsync(tools.Path, null, Ct));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await new NetCoreDbgInstaller(new FakeGitHub(archive, asset, null, downloadStatus: HttpStatusCode.NotFound)).InstallAsync(tools.Path, null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(tools.Path));
    }

    [Fact]
    public async Task A_missing_asset_for_this_platform_is_a_clear_error()
    {
        var (archive, _) = MakeArchive();
        using var tools = new TempDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new NetCoreDbgInstaller(new FakeGitHub(archive, "netcoredbg-some-other-os.zip", null)).InstallAsync(tools.Path, null, Ct));
    }

    [Fact]
    public void Only_the_intel_mac_asset_is_pinned_to_its_last_release()
    {
        var pin = NetCoreDbgInstaller.PinnedRelease("netcoredbg-osx-amd64.tar.gz");
        Assert.NotNull(pin);
        Assert.Equal("3.1.3-1062", pin.Tag);
        Assert.Equal("https://github.com/Samsung/netcoredbg/releases/download/3.1.3-1062/netcoredbg-osx-amd64.tar.gz", pin.DownloadUrl.ToString());
        Assert.Equal(3479468, pin.Size);
        Assert.Equal("49459b066836b6a452f418501d7ecab57bcd7e60d8464faac21ff70b496b8634", pin.Sha256);
        foreach (var asset in new[] { "netcoredbg-linux-amd64.tar.gz", "netcoredbg-linux-arm64.tar.gz", "netcoredbg-osx-arm64.zip", "netcoredbg-win64.zip" })
            Assert.Null(NetCoreDbgInstaller.PinnedRelease(asset));
    }

    /// <summary>
    /// An Intel Mac downloads the pinned release without asking the API for the latest one (which has no osx-amd64 asset), and the pinned
    /// digest is checked: the fake's archive is not the real one, so it is refused before anything is extracted.
    /// </summary>
    [Fact]
    public async Task An_intel_mac_downloads_the_pinned_release_and_checks_it_against_the_pinned_digest()
    {
        var (archive, _) = MakeArchive();
        var gh = new FakeGitHub(archive, "netcoredbg-osx-amd64.tar.gz", Sha(archive));
        using var tools = new TempDirectory();
        var installer = new NetCoreDbgInstaller(gh) { PlatformAsset = () => "netcoredbg-osx-amd64.tar.gz" };
        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () => await installer.InstallAsync(tools.Path, null, Ct));
        Assert.Contains("49459b066836b6a452f418501d7ecab57bcd7e60d8464faac21ff70b496b8634", ex.Message);
        Assert.Equal(["https://github.com/Samsung/netcoredbg/releases/download/3.1.3-1062/netcoredbg-osx-amd64.tar.gz"], gh.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(tools.Path));
    }

    [Fact]
    public async Task Cancellation_stops_the_download()
    {
        var (archive, asset) = MakeArchive();
        using var tools = new TempDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new NetCoreDbgInstaller(new FakeGitHub(archive, asset, null)).InstallAsync(tools.Path, null, cts.Token));
    }

    private sealed class Progress(List<(string?, double?)> sink) : CsCode.Core.Platform.IProgressReporter
    {
        public void Report(string? message = null, double? percent = null) => sink.Add((message, percent));
    }
}

public class NetCoreDbgFactoryTests
{
    private static NetCoreDbgAdapterFactory Make(TestConfigurationService? settings = null, TestAppPaths? paths = null, RecordingWindowService? window = null, string? path = "", NetCoreDbgInstaller? installer = null, FakeWorkspace? ws = null) =>
        new(new DapPlatform { Configuration = settings, Paths = paths, Window = window, Workspace = ws }, installer) { PathVariable = () => path };

    private static string FakeExe(string dir, string name = "netcoredbg")
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "#!/bin/sh\n");
        return p;
    }

    private static DebugConfiguration Cfg(string type = "coreclr") => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type, request = "launch", name = "n" }));

    [Fact]
    public async Task It_handles_coreclr_and_the_netcoredbg_alias_only_and_starts_the_vscode_interpreter()
    {
        using var tools = new TempDirectory();
        using var paths = new TestAppPaths();
        var exe = FakeExe(Path.Combine(paths.ToolsDir, "netcoredbg"), NetCoreDbgAdapterFactory.ExecutableName);
        var f = Make(paths: paths);
        foreach (var t in new[] { "coreclr", "netcoredbg", "CoreCLR" })
        {
            var d = Assert.IsType<DebugAdapterExecutable>(await f.CreateDescriptorAsync(Cfg(t), Ct));
            Assert.Equal(exe, d.Command);
            Assert.Equal(["--interpreter=vscode"], d.Args);
        }
        Assert.Null(await f.CreateDescriptorAsync(Cfg("node"), Ct));
    }

    [Fact]
    public async Task The_setting_wins_over_the_tools_dir_which_wins_over_PATH()
    {
        using var scratch = new TempDirectory();
        using var paths = new TestAppPaths();
        var viaSetting = FakeExe(Path.Combine(scratch.Path, "setting"));
        var viaTools = FakeExe(Path.Combine(paths.ToolsDir, "netcoredbg"), NetCoreDbgAdapterFactory.ExecutableName);
        var viaPathDir = Path.Combine(scratch.Path, "pathdir");
        var viaPath = FakeExe(viaPathDir, NetCoreDbgAdapterFactory.ExecutableName);
        var settings = new TestConfigurationService();

        var f = Make(settings, paths, path: viaPathDir);
        Assert.Equal(viaTools, f.Locate());
        settings.Set(NetCoreDbgAdapterFactory.SettingPath, viaSetting);
        Assert.Equal(viaSetting, f.Locate());

        settings.Set(NetCoreDbgAdapterFactory.SettingPath, "/does/not/exist");
        File.Delete(viaTools);
        Assert.Equal(viaPath, f.Locate()); // dead setting and no tools-dir copy: PATH
        File.Delete(viaPath);
        Assert.Null(f.Locate());
    }

    [Fact]
    public async Task Nothing_found_and_no_window_gives_actionable_instructions()
    {
        using var paths = new TestAppPaths();
        var ex = await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make(paths: paths).CreateDescriptorAsync(Cfg(), Ct));
        Assert.Contains("fetch-netcoredbg.sh", ex.Message);
        Assert.Contains(NetCoreDbgAdapterFactory.SettingPath, ex.Message);
    }

    [Fact]
    public async Task Declining_the_download_offer_leaves_nothing_installed()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Cancel");
        await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make(paths: paths, window: window).CreateDescriptorAsync(Cfg(), Ct));
        Assert.Contains(window.Messages, m => m.Message.Contains("netcoredbg") && m.Message.Contains("Samsung"));
        Assert.False(Directory.Exists(Path.Combine(paths.ToolsDir, "netcoredbg")));
    }

    [Fact]
    public async Task Accepting_the_offer_downloads_through_the_progress_service_and_returns_the_installed_binary()
    {
        var (archive, asset) = (MakeArchiveForOffer(), NetCoreDbgInstaller.CurrentPlatformAsset()!);
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var installer = new NetCoreDbgInstaller(new OfferGitHub(archive, asset));
        var f = Make(paths: paths, window: window, installer: installer);
        var d = Assert.IsType<DebugAdapterExecutable>(await f.CreateDescriptorAsync(Cfg(), Ct));
        Assert.Equal(Path.Combine(paths.ToolsDir, "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName), d.Command);
        Assert.True(File.Exists(d.Command));
        Assert.Equal(d.Command, f.Locate()); // a second start finds it without asking again
    }

    [Fact]
    public async Task A_failed_download_tells_the_user_and_aborts()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var installer = new NetCoreDbgInstaller(new OfferGitHub([], NetCoreDbgInstaller.CurrentPlatformAsset()!, fail: true));
        await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make(paths: paths, window: window, installer: installer).CreateDescriptorAsync(Cfg(), Ct));
        Assert.Contains(window.Messages, m => m.Severity == CsCode.Core.Platform.MessageSeverity.Error && m.Message.Contains("Could not install netcoredbg"));
    }

    /// <summary>A release without this machine's asset (as the latest one has none for Intel Macs) is a message, not an unhandled exception.</summary>
    [Fact]
    public async Task A_release_without_this_machines_asset_tells_the_user_and_aborts()
    {
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var installer = new NetCoreDbgInstaller(new OfferGitHub(MakeArchiveForOffer(), "netcoredbg-some-other-os.zip"))
        {
            PlatformAsset = () => "netcoredbg-linux-amd64.tar.gz", // an asset that is not pinned: the latest release is looked up
        };
        await Assert.ThrowsAsync<DebugSessionStartException>(async () => await Make(paths: paths, window: window, installer: installer).CreateDescriptorAsync(Cfg(), Ct));
        Assert.Contains(window.Messages, m => m.Severity == CsCode.Core.Platform.MessageSeverity.Error
            && m.Message.Contains("Could not install netcoredbg") && m.Message.Contains("netcoredbg-linux-amd64.tar.gz"));
        Assert.False(Directory.Exists(Path.Combine(paths.ToolsDir, "netcoredbg")));
    }

    [Fact]
    public async Task Concurrent_starts_trigger_one_offer_and_one_download()
    {
        var archive = MakeArchiveForOffer();
        using var paths = new TestAppPaths();
        var window = new RecordingWindowService();
        window.MessageActionResponses.Enqueue("Download");
        var gh = new OfferGitHub(archive, NetCoreDbgInstaller.CurrentPlatformAsset()!);
        var f = Make(paths: paths, window: window, installer: new NetCoreDbgInstaller(gh));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.CreateDescriptorAsync(Cfg(), Ct).AsTask()));
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Equal(1, gh.DownloadCount);
        Assert.Single(window.Messages);
    }

    [Fact]
    public async Task Missing_cwd_defaults_to_the_program_directory_then_the_workspace()
    {
        var f = Make();
        var withProgram = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "coreclr", request = "launch", name = "n", program = "/w/bin/Debug/app.dll" }));
        Assert.Equal("/w/bin/Debug", (await f.ResolveAsync(null, withProgram, Ct))!.GetString("cwd"));
        var bare = Cfg();
        Assert.Equal("/w", (await f.ResolveAsync(new Uri("file:///w/"), bare, Ct))!.GetString("cwd"));
        var explicitCwd = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "coreclr", request = "launch", name = "n", cwd = "/mine", program = "/x/y.dll" }));
        Assert.Equal("/mine", (await f.ResolveAsync(null, explicitCwd, Ct))!.GetString("cwd"));
        var attach = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "coreclr", request = "attach", name = "n", processId = 5 }));
        Assert.False((await f.ResolveAsync(new Uri("file:///w/"), attach, Ct))!.Properties.ContainsKey("cwd"));
        var other = Cfg("node");
        Assert.Same(other, await f.ResolveAsync(null, other, Ct));
    }

    [Fact]
    public async Task A_textual_attach_pid_from_pickProcess_is_sent_to_netcoredbg_as_a_number()
    {
        var f = Make();
        var attach = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "coreclr", request = "attach", name = "n", processId = "4242" }));
        var resolved = (await f.ResolveAsync(null, attach, Ct))!;
        Assert.Equal(JsonValueKind.Number, resolved.Properties["processId"].ValueKind);
        Assert.Equal(4242, resolved.Properties["processId"].GetInt32());
        var odd = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "coreclr", request = "attach", name = "n", processId = "not-a-pid" }));
        Assert.Equal("not-a-pid", (await f.ResolveAsync(null, odd, Ct))!.GetString("processId")); // left for the adapter to reject
    }

    [Fact]
    public async Task Initial_configurations_follow_the_vscode_template_for_the_projects_dll()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var configs = await f.ProvideInitialAsync(dir.Uri, Ct);
        var launch = configs.Single(c => !c.IsAttach);
        Assert.Equal(("coreclr", ".NET: Launch", "build"), (launch.Type, launch.Name, launch.PreLaunchTask));
        Assert.Equal("${workspaceFolder}/bin/Debug/net9.0/MyApp.dll", launch.GetString("program"));
        Assert.Equal("${workspaceFolder}", launch.GetString("cwd"));
        Assert.Equal("internalConsole", launch.GetString("console"));
        var attach = configs.Single(c => c.IsAttach);
        Assert.Equal("${command:pickProcess}", attach.GetString("processId"));
    }

    [Fact]
    public async Task Initial_configuration_without_a_project_falls_back_to_the_folder_name_and_a_current_tfm()
    {
        using var dir = new TempDirectory("fallback-");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var launch = (await f.ProvideInitialAsync(dir.Uri, Ct)).Single(c => !c.IsAttach);
        Assert.Equal($"${{workspaceFolder}}/bin/Debug/net10.0/{Path.GetFileName(dir.Path)}.dll", launch.GetString("program"));
    }

    [Fact]
    public async Task The_generated_tasks_json_has_a_default_build_task_that_runs_dotnet_build()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Web.csproj", "<Project/>");
        var f = Make(ws: new FakeWorkspace(dir.Uri));
        var (name, content) = Assert.Single(await f.ProvideInitialFilesAsync(dir.Uri, Ct));
        Assert.Equal("tasks.json", name);
        using var doc = JsonDocument.Parse(content);
        var task = doc.RootElement.GetProperty("tasks")[0];
        Assert.Equal(("build", "process", "dotnet"), (task.GetProperty("label").GetString(), task.GetProperty("type").GetString(), task.GetProperty("command").GetString()));
        Assert.Equal("build", task.GetProperty("args")[0].GetString());
        Assert.Equal("${workspaceFolder}/Web.csproj", task.GetProperty("args")[1].GetString());
        Assert.True(task.GetProperty("group").GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task End_to_end_template_creation_writes_launch_json_and_tasks_json_that_a_user_can_run()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        using var lane = new CsCode.Dap.Client.DebugLane("tmpl");
        var ws = new FakeWorkspace(dir.Uri);
        var platform = new DapPlatform { Workspace = ws };
        var factory = new NetCoreDbgAdapterFactory(platform);
        var cfgs = new DebugConfigurationService(lane, platform);
        cfgs.AddProvider("coreclr", factory);
        await Task.Delay(50, Ct);
        var uri = await cfgs.CreateLaunchJsonAsync("coreclr", null, Ct);
        var launch = LaunchJson.Parse(File.ReadAllText(uri.LocalPath), dir.Uri, "x");
        Assert.Equal([".NET: Launch", ".NET: Attach"], launch.Configurations.Select(c => c.Name));
        Assert.True(File.Exists(Path.Combine(dir.Path, ".cscode", "tasks.json")));
        var tasks = await new CsCode.Dap.Tasks.TaskService(lane, platform).GetTasksAsync(null, Ct);
        Assert.Equal("build", Assert.Single(tasks).Label);
        Assert.True(tasks[0].IsDefaultBuild);
        await cfgs.DisposeAsync();
    }

    private static byte[] MakeArchiveForOffer()
    {
        using var ms = new MemoryStream();
        var asset = NetCoreDbgInstaller.CurrentPlatformAsset()!;
        if (asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            // The executable's name on this OS (macOS's arm64 build is a zip too), and the zip closed before its bytes are taken.
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(zip.CreateEntry("netcoredbg/" + NetCoreDbgAdapterFactory.ExecutableName).Open()))
                w.Write("x");
        }
        else
        {
            using var gz = new GZipStream(ms, CompressionLevel.Fastest, true);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax, true);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "netcoredbg/netcoredbg") { Mode = (UnixFileMode)0b110_100_100, DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray()) });
        }
        return ms.ToArray();
    }

    private sealed class OfferGitHub(byte[] archive, string asset, bool fail = false) : HttpMessageHandler
    {
        private int _downloads;
        public int DownloadCount => _downloads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (request.RequestUri!.Host == "api.github.com")
            {
                var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
                var json = JsonSerializer.Serialize(new { tag_name = "1.0.0", assets = new[] { new { name = asset, size = archive.Length, digest = "sha256:" + digest, browser_download_url = "https://github.com/x/" + asset } } });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
            Interlocked.Increment(ref _downloads);
            await Task.Delay(50, cancellationToken); // widen the race window for the concurrency test
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }
    }
}
