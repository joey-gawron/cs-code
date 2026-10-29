using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Adapters;

/// <summary>An archive that tries to write outside its extraction root ("zip-slip") or has an unsafe link.</summary>
public sealed class UnsafeArchiveException(string message) : IOException(message);

/// <summary>
/// Downloads Samsung's netcoredbg (MIT) from its GitHub releases into <c>{ToolsDir}/netcoredbg</c>. The archive's SHA-256 is verified
/// against the digest GitHub publishes for the release asset before anything is extracted; extraction refuses entries that escape the
/// destination (zip-slip, absolute paths, symlinks pointing outside); the main binary is made executable. NOT vsdbg: its licence
/// forbids use outside Microsoft products.
/// </summary>
public sealed class NetCoreDbgInstaller
{
    public const string Repository = "Samsung/netcoredbg";
    public const string ReleasesApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public NetCoreDbgInstaller(HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CsCode", "1.0")); // GitHub's API rejects requests without one
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Release asset suffix for this machine (linux-amd64, linux-arm64, osx-arm64, win64), or null when netcoredbg has no build for it.</summary>
    public static string? CurrentPlatformAsset()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsLinux()) return arch switch { Architecture.X64 => "netcoredbg-linux-amd64.tar.gz", Architecture.Arm64 => "netcoredbg-linux-arm64.tar.gz", _ => null };
        if (OperatingSystem.IsMacOS()) return arch == Architecture.Arm64 ? "netcoredbg-osx-arm64.zip" : null;
        if (OperatingSystem.IsWindows()) return arch == Architecture.X64 ? "netcoredbg-win64.zip" : null;
        return null;
    }

    public sealed record ReleaseAsset(string Tag, string Name, Uri DownloadUrl, long Size, string? Sha256);

    public async Task<ReleaseAsset> ResolveLatestAsync(string assetName, CancellationToken ct)
    {
        using var response = await _http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("The release has no tag.");
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;
            string? digest = null;
            if (asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                digest = d.GetString()![7..];
            return new ReleaseAsset(tag, assetName, new Uri(asset.GetProperty("browser_download_url").GetString()!),
                asset.TryGetProperty("size", out var s) ? s.GetInt64() : -1, digest);
        }
        throw new InvalidOperationException($"Release {tag} has no asset '{assetName}'.");
    }

    /// <summary>Downloads, verifies and extracts into <c>{toolsDir}/netcoredbg</c>; returns the path of the netcoredbg executable.</summary>
    public async Task<string> InstallAsync(string toolsDir, IProgressReporter? progress, CancellationToken ct, ReleaseAsset? release = null)
    {
        var assetName = CurrentPlatformAsset() ?? throw new PlatformNotSupportedException("netcoredbg has no release build for this OS/architecture.");
        progress?.Report("Looking up the latest netcoredbg release…");
        release ??= await ResolveLatestAsync(assetName, ct).ConfigureAwait(false);

        Directory.CreateDirectory(toolsDir);
        var staging = Path.Combine(toolsDir, ".netcoredbg-" + Guid.NewGuid().ToString("N")[..8]);
        var archivePath = staging + ".download";
        Directory.CreateDirectory(staging);
        try
        {
            await DownloadAsync(release, archivePath, progress, ct).ConfigureAwait(false);
            progress?.Report("Extracting…");
            await using (var archive = File.OpenRead(archivePath))
            {
                if (assetName.EndsWith(".zip", StringComparison.Ordinal)) ExtractZip(archive, staging);
                else ExtractTarGz(archive, staging);
            }

            // The archives contain a top-level "netcoredbg/" directory.
            var inner = Path.Combine(staging, "netcoredbg");
            var source = Directory.Exists(inner) ? inner : staging;
            var exeName = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
            if (!File.Exists(Path.Combine(source, exeName))) throw new InvalidOperationException($"The archive does not contain '{exeName}'.");

            var target = Path.Combine(toolsDir, "netcoredbg");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(source, target);
            var exe = Path.Combine(target, exeName);
            MakeExecutable(exe);
            File.WriteAllText(Path.Combine(target, "VERSION"), release.Tag + "\n");
            _logger.LogInformation("Installed netcoredbg {Tag} to {Dir}", release.Tag, target);
            return exe;
        }
        finally
        {
            TryDelete(archivePath);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(ReleaseAsset release, string destination, IProgressReporter? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.Size;
        using var sha = SHA256.Create();
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = File.Create(destination))
        {
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report($"Downloading netcoredbg {release.Tag}…", done * 100.0 / total);
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (release.Sha256 is { } expected)
        {
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destination);
                throw new InvalidDataException($"The downloaded netcoredbg archive is corrupt or has been tampered with (SHA-256 {actual}, expected {expected.ToLowerInvariant()}).");
            }
        }
        else _logger.LogWarning("GitHub published no SHA-256 for {Asset}; relying on archive integrity checks only", release.Name);
    }

    // ------------------------------------------------------------------ safe extraction

    /// <summary>Resolves an entry name under <paramref name="root"/>; throws if it would land outside.</summary>
    internal static string SafeDestination(string root, string entryName)
    {
        if (string.IsNullOrEmpty(entryName)) throw new UnsafeArchiveException("Archive entry with an empty name.");
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || (normalized.Length > 1 && normalized[1] == ':') || normalized.Split('/').Contains(".."))
            throw new UnsafeArchiveException($"Archive entry '{entryName}' would be extracted outside the destination.");
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnsafeArchiveException($"Archive entry '{entryName}' would be extracted outside the destination.");
        return full;
    }

    public static void ExtractZip(Stream archive, string destination)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var path = SafeDestination(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
            if (!OperatingSystem.IsWindows() && entry.ExternalAttributes != 0)
            {
                var unix = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0xFFF);
                if (unix != 0) File.SetUnixFileMode(path, unix);
            }
        }
    }

    /// <summary>
    /// Extracts a .tar.gz under <paramref name="destination"/> and nowhere else. Entry names are checked lexically (<see cref="SafeDestination"/>),
    /// and links cannot be used to escape: a symlink's target must be relative and must not contain a <c>..</c> segment (a chain such as
    /// <c>y -&gt; .</c>, <c>x -&gt; y/..</c> is lexically inside yet resolves outside), and no entry is ever written through a symlink — neither a
    /// directory on its way nor the entry's own path.
    /// </summary>
    public static void ExtractTarGz(Stream archive, string destination)
    {
        using var gz = new GZipStream(archive, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        var rootFull = Path.GetFullPath(destination);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;
            var path = SafeDestination(destination, entry.Name);
            RefuseWritesThroughLinks(rootFull, path, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                    if (!OperatingSystem.IsWindows() && entry.Mode != 0) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0xFFF);
                    break;
                case TarEntryType.SymbolicLink:
                {
                    var linkTarget = entry.LinkName ?? throw new UnsafeArchiveException($"Symlink '{entry.Name}' has no target.");
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, linkTarget));
                    if (Path.IsPathRooted(linkTarget) || linkTarget.Replace('\\', '/').Split('/').Contains("..")
                        || (resolved != rootFull && !resolved.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                        throw new UnsafeArchiveException($"Symlink '{entry.Name}' points outside the destination ('{linkTarget}').");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.CreateSymbolicLink(path, linkTarget);
                    break;
                }
                case TarEntryType.HardLink:
                {
                    var linkTarget = SafeDestination(destination, entry.LinkName ?? "");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.Copy(linkTarget, path, overwrite: true); // a copy is as good as a hard link here and cannot alias outside files
                    break;
                }
                default:
                    break; // devices, fifos…: never create them
            }
        }
    }

    /// <summary>Throws when <paramref name="path"/> or any directory between <paramref name="rootFull"/> and it is an existing symlink.</summary>
    private static void RefuseWritesThroughLinks(string rootFull, string path, string entryName)
    {
        for (var current = path; current.Length > rootFull.Length && current.StartsWith(rootFull, StringComparison.Ordinal); current = Path.GetDirectoryName(current) ?? "")
        {
            if (new FileInfo(current).LinkTarget is not null) // dangling links included
                throw new UnsafeArchiveException($"Archive entry '{entryName}' would be written through the link '{Path.GetRelativePath(rootFull, current)}'.");
        }
    }

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
