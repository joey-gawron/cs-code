using System.Net.Http.Headers;
using System.Security.Cryptography;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Installs Microsoft's standalone JavaScript debug adapter (<c>js-debug-dap</c>, MIT, github.com/microsoft/vscode-js-debug) into
/// <c>{ToolsDir}/js-debug/&lt;version&gt;/</c>. The version and the archive's SHA-256 are PINNED (not "latest"): the archive is verified against
/// the digest compiled in here before anything is extracted, and the extraction refuses entries that would escape the destination.
/// <c>tools/fetch-js-debug.sh</c> reproduces exactly this for tests and development. The adapter runs on the user's Node.js; nothing is
/// downloaded without the user's consent (<see cref="JsDebugAdapterFactory"/> asks first).
/// </summary>
public sealed class JsDebugInstaller
{
    public const string Version = "v1.140.0";
    public const string AssetName = "js-debug-dap-" + Version + ".tar.gz";
    /// <summary>SHA-256 of <see cref="AssetName"/> as published by GitHub for the release asset (checked 2026-10-29).</summary>
    public const string Sha256 = "27dab92937ec1ab35821ae955aac867544fe06a1b6307229049f2d789af10968";
    public const long Size = 1_249_709;
    public static readonly Uri DownloadUrl = new($"https://github.com/microsoft/vscode-js-debug/releases/download/{Version}/{AssetName}");

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public JsDebugInstaller(HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CsCode", "1.0"));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>{toolsDir}/js-debug/&lt;version&gt;</c>: the directory an install creates (it contains the archive's <c>js-debug/</c>).</summary>
    public static string InstallDirectory(string toolsDir) => Path.Combine(toolsDir, "js-debug", Version);

    /// <summary>Where the server script of an install lives.</summary>
    public static string ServerScript(string toolsDir) => Path.Combine(InstallDirectory(toolsDir), "js-debug", "src", "dapDebugServer.js");

    /// <summary>Downloads the pinned archive, verifies it, extracts it; returns the path of <c>dapDebugServer.js</c>.</summary>
    public async Task<string> InstallAsync(string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(toolsDir);
        var archive = Path.Combine(toolsDir, ".js-debug-" + Guid.NewGuid().ToString("N")[..8] + ".download");
        try
        {
            progress?.Report($"Downloading js-debug {Version}…");
            using (var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(archive);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (done > Size * 4) throw new InvalidDataException($"The js-debug download is far larger than the expected {Size:N0} bytes; refusing it.");
                    if (total > 0) progress?.Report($"Downloading js-debug {Version}…", done * 100.0 / total);
                }
            }
            return await InstallFromArchiveAsync(archive, toolsDir, progress, ct).ConfigureAwait(false);
        }
        finally { TryDelete(archive); }
    }

    /// <summary>
    /// Installs from an archive already on disk (the download above, or one fetched earlier): refuses it unless its SHA-256 is the pinned one,
    /// then extracts through the zip-slip-safe extractor into a staging directory and atomically replaces any previous install of this version.
    /// </summary>
    public async Task<string> InstallFromArchiveAsync(string archivePath, string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.Report("Verifying js-debug…");
        var actual = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The js-debug archive is corrupt or has been tampered with (SHA-256 {actual}, expected {Sha256}).");

        Directory.CreateDirectory(Path.Combine(toolsDir, "js-debug"));
        var staging = Path.Combine(toolsDir, "js-debug", ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Extracting js-debug…");
            await using (var stream = File.OpenRead(archivePath))
                NetCoreDbgInstaller.ExtractTarGz(stream, staging);
            if (!File.Exists(Path.Combine(staging, "js-debug", "src", "dapDebugServer.js")))
                throw new InvalidDataException("The js-debug archive does not contain js-debug/src/dapDebugServer.js.");
            File.WriteAllText(Path.Combine(staging, "VERSION"), Version + "\n");

            var target = InstallDirectory(toolsDir);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            _logger.LogInformation("Installed js-debug {Version} to {Dir}", Version, target);
            return ServerScript(toolsDir);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
