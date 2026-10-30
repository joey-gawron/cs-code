using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Dap.Adapters;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The CodeLLDB installer: the pinned packages (the same pins as tools/fetch-codelldb.sh), download → verify → extract only <c>extension/</c>,
/// and an extraction that refuses what could escape or explode. Fabricated packages with their own digests; nothing is ever run.
/// </summary>
public class CodeLldbInstallerTests
{
    /// <summary>A fake GitHub release download serving <paramref name="archive"/> (or a status) and counting downloads.</summary>
    internal sealed class FakeRelease(byte[] archive, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? delay = null) : HttpMessageHandler
    {
        private int _downloads;
        public int Downloads => _downloads;
        public List<Uri> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            Interlocked.Increment(ref _downloads);
            if (delay is { } d) await Task.Delay(d, cancellationToken); // widens the race window for the concurrency test
            if (status != HttpStatusCode.OK) return new HttpResponseMessage(status);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }
    }

    internal static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A .vsix as CodeLLDB lays it out: manifest files at the root, the extension under <c>extension/</c>.</summary>
    internal static byte[] Vsix(params (string Name, string Content, int Mode)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content, mode) in entries)
            {
                var e = zip.CreateEntry(name);
                if (mode != 0) e.ExternalAttributes = mode << 16;
                if (name.EndsWith('/')) continue;
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] GoodVsix() => Vsix(
        ("extension.vsixmanifest", "<manifest/>", 0),
        ("[Content_Types].xml", "<types/>", 0),
        ("extension/package.json", "{\"name\":\"vscode-lldb\"}", 0x81A4),            // 0100644
        ("extension/adapter/codelldb", "#!/bin/sh\nexit 0\n", 0x81A4),               // shipped without x: the installer adds it
        ("extension/adapter/scripts/debugger.py", "# python", 0x81A4),
        ("extension/lldb/lib/liblldb.so", "ELF", 0x81ED),                            // 0100755
        ("extension/lldb/bin/lldb-server", "ELF", 0x81ED));

    internal static CodeLldbInstaller.Package PackageFor(byte[] archive) => new("linux-x64", "codelldb-linux-x64.vsix", archive.Length, Sha(archive));

    private sealed class Progress(List<(string?, double?)> sink) : CsCode.Core.Platform.IProgressReporter
    {
        public void Report(string? message = null, double? percent = null) { lock (sink) sink.Add((message, percent)); }
    }

    // ------------------------------------------------------------------ pins

    [Fact]
    public void Every_platform_package_is_pinned_with_a_size_and_a_sha256_and_the_fetch_script_pins_the_same()
    {
        Assert.Equal(["linux-x64", "linux-arm64", "linux-armhf", "darwin-x64", "darwin-arm64", "win32-x64"], CodeLldbInstaller.Packages.Select(p => p.Platform));
        Assert.All(CodeLldbInstaller.Packages, p =>
        {
            Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
            Assert.InRange(p.Size, 10_000_000, 200_000_000);
            Assert.Equal($"codelldb-{p.Platform}.vsix", p.AssetName);
            Assert.Equal($"https://github.com/vadimcn/codelldb/releases/download/v1.12.3/codelldb-{p.Platform}.vsix", p.DownloadUrl.ToString());
        });

        // tools/fetch-codelldb.sh reproduces the app's download: same version, same sizes and digests.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "fetch-codelldb.sh"));
        Assert.Contains($"version=\"{CodeLldbInstaller.Version}\"", script, StringComparison.Ordinal);
        foreach (var p in CodeLldbInstaller.Packages)
            Assert.Matches(new Regex($@"{Regex.Escape(p.Platform)}\)\s+size={p.Size}; sha256=""{p.Sha256}"""), script);
    }

    [Fact]
    public void This_machine_has_a_package()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64, "the pinned digests were checked on linux-x64");
        Assert.Equal("linux-x64", CodeLldbInstaller.CurrentPackage()!.Platform);
        Assert.Equal(56, CodeLldbInstaller.CurrentPackage()!.SizeMegabytes);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    // ------------------------------------------------------------------ install

    [Fact]
    public async Task Install_downloads_the_pinned_package_verifies_it_and_extracts_only_the_extension_with_an_executable_adapter()
    {
        var archive = GoodVsix();
        var release = new FakeRelease(archive);
        using var tools = new TempDirectory();
        var reports = new List<(string?, double?)>();
        var adapter = await new CodeLldbInstaller(release, package: PackageFor(archive)).InstallAsync(tools.Path, new Progress(reports), Ct);

        var root = Path.Combine(tools.Path, "codelldb", "v1.12.3");
        Assert.Equal(Path.Combine(root, "extension", "adapter", "codelldb"), adapter);
        Assert.Equal(CodeLldbInstaller.AdapterPath(tools.Path), adapter);
        Assert.Equal(new Uri("https://github.com/vadimcn/codelldb/releases/download/v1.12.3/codelldb-linux-x64.vsix"), Assert.Single(release.Requests));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(adapter).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(Path.Combine(root, "extension", "lldb", "bin", "lldb-server")).HasFlag(UnixFileMode.UserExecute)); // modes kept
        }
        Assert.True(File.Exists(Path.Combine(root, "extension", "lldb", "lib", "liblldb.so")));
        Assert.False(File.Exists(Path.Combine(root, "extension.vsixmanifest"))); // only extension/ is extracted
        Assert.Equal("v1.12.3", File.ReadAllText(Path.Combine(root, "VERSION")).Trim());
        Assert.Equal("linux-x64", File.ReadAllText(Path.Combine(root, "PLATFORM")).Trim());
        Assert.Equal([root], Directory.GetDirectories(Path.Combine(tools.Path, "codelldb"))); // no staging leftovers
        Assert.Empty(Directory.GetFiles(Path.Combine(tools.Path, "codelldb")));             // no download leftovers
        Assert.Contains(reports, r => r.Item1 == "Verifying CodeLLDB…");
        Assert.Contains(reports, r => r.Item2 is 100.0);
    }

    [Fact]
    public async Task A_package_whose_digest_is_not_the_pinned_one_is_refused_before_anything_is_extracted()
    {
        var archive = GoodVsix();
        var pinned = PackageFor(archive) with { Sha256 = new string('0', 64) };
        using var tools = new TempDirectory();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () => await new CodeLldbInstaller(new FakeRelease(archive), package: pinned).InstallAsync(tools.Path, null, Ct));
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tools.Path, "codelldb"))); // nothing extracted, nothing left behind
    }

    [Fact]
    public async Task A_download_far_larger_than_the_pinned_size_is_refused_while_it_downloads()
    {
        var archive = new byte[300_000];
        var pinned = new CodeLldbInstaller.Package("linux-x64", "codelldb-linux-x64.vsix", 100_000, Sha(archive));
        using var tools = new TempDirectory();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () => await new CodeLldbInstaller(new FakeRelease(archive), package: pinned).InstallAsync(tools.Path, null, Ct));
        Assert.Contains("far larger", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tools.Path, "codelldb")));
    }

    [Fact]
    public async Task Http_failures_surface_and_leave_nothing_behind()
    {
        var archive = GoodVsix();
        using var tools = new TempDirectory();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await new CodeLldbInstaller(new FakeRelease(archive, HttpStatusCode.NotFound), package: PackageFor(archive)).InstallAsync(tools.Path, null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tools.Path, "codelldb")));
    }

    [Fact]
    public async Task A_package_without_the_adapter_is_refused_and_a_reinstall_replaces_the_previous_one()
    {
        var empty = Vsix(("extension/package.json", "{}", 0x81A4));
        using var tools = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new CodeLldbInstaller(new FakeRelease(empty), package: PackageFor(empty)).InstallAsync(tools.Path, null, Ct));
        Assert.False(Directory.Exists(CodeLldbInstaller.InstallDirectory(tools.Path)));

        var archive = GoodVsix();
        var installer = new CodeLldbInstaller(new FakeRelease(archive), package: PackageFor(archive));
        await installer.InstallAsync(tools.Path, null, Ct);
        var stale = Path.Combine(CodeLldbInstaller.InstallDirectory(tools.Path), "extension", "stale.txt");
        File.WriteAllText(stale, "from an earlier install");
        await installer.InstallAsync(tools.Path, null, Ct);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(CodeLldbInstaller.AdapterPath(tools.Path)));
    }

    [Fact]
    public async Task Cancelling_the_download_leaves_nothing_behind()
    {
        var archive = GoodVsix();
        using var tools = new TempDirectory();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new CodeLldbInstaller(new FakeRelease(archive, delay: TimeSpan.FromSeconds(10)), package: PackageFor(archive)).InstallAsync(tools.Path, null, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(tools.Path, "codelldb")));
    }

    // ------------------------------------------------------------------ extraction safety

    [Theory]
    [InlineData("extension/../evil.txt")]
    [InlineData("extension/a/../../../evil.txt")]
    public void Entries_that_would_escape_are_refused_and_nothing_is_written(string name)
    {
        using var root = new TempDirectory();
        var dest = Directory.CreateDirectory(Path.Combine(root.Path, "dest")).FullName;
        var bytes = Vsix(("extension/ok.txt", "fine", 0x81A4), (name, "pwned", 0x81A4));
        Assert.Throws<UnsafeArchiveException>(() => CodeLldbInstaller.ExtractExtension(new MemoryStream(bytes), dest, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(dest)); // checked before the first entry is written
        Assert.False(File.Exists(Path.Combine(root.Path, "evil.txt")));
    }

    [Fact]
    public void A_symlink_entry_is_refused_instead_of_being_written_as_a_file()
    {
        using var root = new TempDirectory();
        var bytes = Vsix(("extension/adapter/codelldb", "x", 0x81ED), ("extension/lldb/lib/liblldb.so", "/etc/passwd", 0xA1FF)); // 0120777
        var ex = Assert.Throws<UnsafeArchiveException>(() => CodeLldbInstaller.ExtractExtension(new MemoryStream(bytes), root.Path, Ct));
        Assert.Contains("link", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public void An_archive_that_unpacks_to_more_than_the_limit_is_refused_before_anything_is_written()
    {
        using var root = new TempDirectory();
        var bytes = Vsix(("extension/a.bin", new string('x', 4_000), 0x81A4), ("extension/b.bin", new string('y', 4_000), 0x81A4));
        Assert.Throws<UnsafeArchiveException>(() => CodeLldbInstaller.ExtractExtension(new MemoryStream(bytes), root.Path, Ct, maxBytes: 6_000));
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
        CodeLldbInstaller.ExtractExtension(new MemoryStream(bytes), root.Path, Ct, maxBytes: 8_000);
        Assert.Equal(4_000, new FileInfo(Path.Combine(root.Path, "extension", "b.bin")).Length);
    }

    [Fact]
    public void Setuid_and_setgid_bits_are_never_applied()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        if (OperatingSystem.IsWindows()) return; // (for the platform analyzer)
        using var root = new TempDirectory();
        var bytes = Vsix(("extension/adapter/codelldb", "x", 0x8DED)); // 0106755: setuid + setgid + rwxr-xr-x
        CodeLldbInstaller.ExtractExtension(new MemoryStream(bytes), root.Path, Ct);
        var mode = File.GetUnixFileMode(Path.Combine(root.Path, "extension", "adapter", "codelldb"));
        Assert.False(mode.HasFlag(UnixFileMode.SetUser));
        Assert.False(mode.HasFlag(UnixFileMode.SetGroup));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    // ------------------------------------------------------------------ the real package (no download, nothing run)

    /// <summary>
    /// The real v1.12.3 linux-x64 package, as tools/fetch-codelldb.sh downloaded it (<c>CSCODE_CODELLDB_ARCHIVE=&lt;…/codelldb-linux-x64.vsix&gt;</c>):
    /// its digest is the pinned one and it installs with the adapter, liblldb and lldb-server in place. Nothing in it is executed.
    /// </summary>
    [Fact]
    [Trait("Category", "RealTools")]
    public async Task The_real_pinned_package_installs()
    {
        var archive = Environment.GetEnvironmentVariable("CSCODE_CODELLDB_ARCHIVE");
        Assert.SkipWhen(string.IsNullOrEmpty(archive) || !File.Exists(archive), "set CSCODE_CODELLDB_ARCHIVE to the codelldb-linux-x64.vsix tools/fetch-codelldb.sh downloaded");
        Assert.SkipUnless(CodeLldbInstaller.CurrentPackage()?.Platform == "linux-x64", "the archive is the linux-x64 package");
        if (!OperatingSystem.IsLinux()) return; // (for the platform analyzer)
        using var tools = new TempDirectory();
        var reports = new List<(string?, double?)>();
        var adapter = await new CodeLldbInstaller().InstallFromArchiveAsync(archive!, tools.Path, new Progress(reports), Ct);
        Assert.Equal(CodeLldbInstaller.AdapterPath(tools.Path), adapter);
        Assert.True(File.GetUnixFileMode(adapter).HasFlag(UnixFileMode.UserExecute));
        var root = CodeLldbInstaller.InstallDirectory(tools.Path);
        foreach (var file in new[] { "extension/lldb/lib/liblldb.so", "extension/lldb/bin/lldb-server", "extension/lang_support/rust.py", "extension/adapter/scripts/debugger.py" })
            Assert.True(File.Exists(Path.Combine(root, file)), file);
        Assert.True(File.GetUnixFileMode(Path.Combine(root, "extension/lldb/bin/lldb-server")).HasFlag(UnixFileMode.UserExecute));
        var size = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        TestMetrics.Report($"[codelldb] installed {CodeLldbInstaller.Version} from {archive}: {size:N0} bytes in {root}");
        Assert.InRange(size, 150_000_000, 200_000_000);
    }
}
