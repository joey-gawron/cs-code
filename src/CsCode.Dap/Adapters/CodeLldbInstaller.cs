using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Installs <b>CodeLLDB</b> (Vadim Chugunov's LLDB-based debug adapter, MIT, github.com/vadimcn/codelldb) with the LLDB it bundles into
/// <c>{ToolsDir}/codelldb/&lt;version&gt;/</c>. CodeLLDB ships as a VS Code extension package per platform (<c>codelldb-&lt;platform&gt;.vsix</c>,
/// a zip); only its <c>extension/</c> tree is extracted, and CsCode runs only the standalone adapter <c>extension/adapter/codelldb</c>, which
/// loads <c>extension/lldb/lib/liblldb</c>. The version and each package's size and SHA-256 are PINNED (the digests GitHub publishes for the
/// release assets): a package is refused unless its digest matches, before anything is extracted, and the extraction refuses entries that
/// would escape the destination, links, and archives that unpack to more than <see cref="MaxExtractedBytes"/>. <c>tools/fetch-codelldb.sh</c>
/// reproduces exactly this for tests and development. Nothing is downloaded without the user's consent (<see cref="CodeLldbAdapterFactory"/>
/// asks first).
/// </summary>
public sealed class CodeLldbInstaller
{
    public const string Repository = "vadimcn/codelldb";
    public const string Version = "v1.12.3";
    /// <summary>What a package may unpack to (v1.12.3 linux-x64: 170 MB): a guard against an archive that unpacks to far more than it says.</summary>
    public const long MaxExtractedBytes = 1L << 30;
    private const int MaxEntries = 20_000;

    /// <summary>One platform package of the pinned release.</summary>
    /// <param name="Platform">CodeLLDB's platform name (<c>linux-x64</c>, <c>darwin-arm64</c>, <c>win32-x64</c>…).</param>
    public sealed record Package(string Platform, string AssetName, long Size, string Sha256)
    {
        public Uri DownloadUrl => new($"https://github.com/{Repository}/releases/download/{Version}/{AssetName}");
        /// <summary>The download size in MB for messages ("about 56 MB").</summary>
        public long SizeMegabytes => (Size + 999_999) / 1_000_000;
    }

    /// <summary>The packages of <see cref="Version"/> and their digests as GitHub publishes them (checked 2026-10-29).</summary>
    public static readonly IReadOnlyList<Package> Packages =
    [
        new("linux-x64", "codelldb-linux-x64.vsix", 55_571_094, "1cd7f386598022b51a5b93b9ffa23e812b23f519cfe1833384ec4bef4bfd1be1"),
        new("linux-arm64", "codelldb-linux-arm64.vsix", 54_544_514, "0887f67d440554617894266f80706b700907c36b95e6e49d23b95a0e05318101"),
        new("linux-armhf", "codelldb-linux-armhf.vsix", 52_535_623, "20294f0ea876a73c662bb09b1814551a859dc77156564b62c75acf1088ecaf30"),
        new("darwin-x64", "codelldb-darwin-x64.vsix", 47_735_596, "e25cc716b94c62c07fec268ff2785d2b797245b160502baef8b9c970a0c4d8e8"),
        new("darwin-arm64", "codelldb-darwin-arm64.vsix", 45_390_048, "2f114a990e1b368dd1dbd33c80c0e719767af2d228391ec0df0571c957f9ac91"),
        new("win32-x64", "codelldb-win32-x64.vsix", 50_943_475, "a916e509308dac817732f63ca604a8b93ed29cd16f38a2fa9f0b64ed58e8f51a"),
    ];

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <param name="package">The package to install instead of this machine's (tests; another platform's).</param>
    public CodeLldbInstaller(HttpMessageHandler? handler = null, ILogger? logger = null, Package? package = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CsCode", "1.0"));
        _logger = logger ?? NullLogger.Instance;
        Target = package ?? CurrentPackage();
    }

    /// <summary>The package this installer installs; null when CodeLLDB has none for this machine.</summary>
    public Package? Target { get; }

    /// <summary>This machine's package, or null when CodeLLDB has no build for it.</summary>
    public static Package? CurrentPackage()
    {
        var arch = RuntimeInformation.OSArchitecture;
        var platform =
            OperatingSystem.IsLinux() ? arch switch { Architecture.X64 => "linux-x64", Architecture.Arm64 => "linux-arm64", Architecture.Arm => "linux-armhf", _ => null }
            : OperatingSystem.IsMacOS() ? arch switch { Architecture.X64 => "darwin-x64", Architecture.Arm64 => "darwin-arm64", _ => null }
            : OperatingSystem.IsWindows() && arch == Architecture.X64 ? "win32-x64"
            : null;
        return platform is null ? null : Packages.First(p => p.Platform == platform);
    }

    public static string AdapterExecutableName => OperatingSystem.IsWindows() ? "codelldb.exe" : "codelldb";

    /// <summary><c>{toolsDir}/codelldb/&lt;version&gt;</c>: the directory an install creates (it contains the package's <c>extension/</c>).</summary>
    public static string InstallDirectory(string toolsDir) => Path.Combine(toolsDir, "codelldb", Version);

    /// <summary>Where the adapter of an install lives.</summary>
    public static string AdapterPath(string toolsDir) => Path.Combine(InstallDirectory(toolsDir), "extension", "adapter", AdapterExecutableName);

    /// <summary>Downloads the pinned package, verifies it, extracts it; returns the path of the adapter.</summary>
    public async Task<string> InstallAsync(string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        var package = Target ?? throw new PlatformNotSupportedException("CodeLLDB has no package for this OS/architecture.");
        var parent = Path.Combine(toolsDir, "codelldb");
        Directory.CreateDirectory(parent);
        var archive = Path.Combine(parent, ".download-" + Guid.NewGuid().ToString("N")[..8] + ".vsix");
        try
        {
            progress?.Report($"Downloading CodeLLDB {Version}…");
            using (var response = await _http.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? package.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(archive);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (done > package.Size * 2) throw new InvalidDataException($"The CodeLLDB download is far larger than the expected {package.Size:N0} bytes; refusing it.");
                    if (total > 0) progress?.Report($"Downloading CodeLLDB {Version}…", Math.Min(100, done * 100.0 / total));
                }
            }
            return await InstallFromArchiveAsync(archive, toolsDir, progress, ct).ConfigureAwait(false);
        }
        finally { TryDelete(archive); }
    }

    /// <summary>
    /// Installs from a package already on disk (the download above, or one fetched earlier): refuses it unless its SHA-256 is the pinned one,
    /// extracts its <c>extension/</c> tree into a staging directory and replaces any previous install of this version with it.
    /// </summary>
    public async Task<string> InstallFromArchiveAsync(string archivePath, string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        var package = Target ?? throw new PlatformNotSupportedException("CodeLLDB has no package for this OS/architecture.");
        progress?.Report("Verifying CodeLLDB…");
        var actual = await JsDebugInstaller.ComputeSha256Async(archivePath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The CodeLLDB package is corrupt or has been tampered with (SHA-256 {actual}, expected {package.Sha256}).");

        var parent = Path.Combine(toolsDir, "codelldb");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Extracting CodeLLDB…");
            // ~170 MB of synchronous file I/O: on the thread pool, never on the caller's lane (the debug lane starts adapters).
            await Task.Run(() =>
            {
                using (var stream = File.OpenRead(archivePath))
                    ExtractExtension(stream, staging, ct);
                var adapter = Path.Combine(staging, "extension", "adapter", AdapterExecutableName);
                if (!File.Exists(adapter)) throw new InvalidDataException($"The CodeLLDB package does not contain extension/adapter/{AdapterExecutableName}.");
                MakeExecutable(adapter);
                File.WriteAllText(Path.Combine(staging, "VERSION"), Version + "\n");
                File.WriteAllText(Path.Combine(staging, "PLATFORM"), package.Platform + "\n");

                var target = InstallDirectory(toolsDir);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Installed CodeLLDB {Version} ({Platform}) to {Dir}", Version, package.Platform, InstallDirectory(toolsDir));
            return AdapterPath(toolsDir);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Extracts the <c>extension/</c> entries of a .vsix under <paramref name="destination"/> and nothing else: names are checked lexically
    /// (<see cref="NetCoreDbgInstaller.SafeDestination"/>: no absolute paths, no <c>..</c>), a symlink entry (Unix mode <c>S_IFLNK</c>) is
    /// refused rather than written as a file, only the permission bits rwx are applied (never setuid/setgid), and the declared sizes may not
    /// add up to more than <paramref name="maxBytes"/> (checked before anything is written).
    /// </summary>
    public static void ExtractExtension(Stream archive, string destination, CancellationToken ct = default, long maxBytes = MaxExtractedBytes)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("extension/", StringComparison.Ordinal)).ToList();
        if (entries.Count > MaxEntries) throw new UnsafeArchiveException($"The package has {entries.Count:N0} entries; refusing it.");
        long declared = 0;
        foreach (var entry in entries)
        {
            NetCoreDbgInstaller.SafeDestination(destination, entry.FullName);
            if (IsSymlink(entry)) throw new UnsafeArchiveException($"Archive entry '{entry.FullName}' is a link.");
            declared += entry.Length;
            if (declared > maxBytes) throw new UnsafeArchiveException($"The package unpacks to more than {maxBytes:N0} bytes; refusing it.");
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = NetCoreDbgInstaller.SafeDestination(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
            if (!OperatingSystem.IsWindows() && UnixMode(entry) is var mode and not 0)
                File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF) | UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static int UnixMode(ZipArchiveEntry entry) => (entry.ExternalAttributes >> 16) & 0xFFFF;

    private static bool IsSymlink(ZipArchiveEntry entry) => (UnixMode(entry) & 0xF000) == 0xA000;

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
