using System.IO.Compression;
using System.Net.Http.Headers;
using CsCode.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Installs Microsoft's <b>debugpy</b> (MIT, github.com/microsoft/debugpy) for interpreters that do not have it: the pure-Python wheel
/// (<c>py2.py3-none-any</c>, no compiled speedups, so one copy serves every interpreter and platform) unpacked to
/// <c>{ToolsDir}/debugpy/&lt;version&gt;/</c> and run as <c>&lt;interpreter&gt; {ToolsDir}/debugpy/&lt;version&gt;/debugpy/adapter</c>, as VS Code's
/// Python Debugger extension runs its bundled copy. The version, size and SHA-256 are PINNED (the digest PyPI publishes): the wheel is refused unless
/// its digest matches, before anything is extracted; only its <c>debugpy/</c> entries are extracted, with no absolute paths, no <c>..</c>, no links,
/// and at most <see cref="MaxExtractedBytes"/>. <c>tools/fetch-debugpy.sh</c> reproduces exactly this. Nothing is downloaded without the user's
/// consent (<see cref="DebugpyAdapterFactory"/> asks first).
/// </summary>
public sealed class DebugpyInstaller
{
    public const string Version = "1.8.22";
    public const string WheelName = "debugpy-" + Version + "-py2.py3-none-any.whl";
    public const long Size = 5_374_857;
    public const string Sha256 = "a9e9d3550e15ca479c59333e90845029190531f0cacfedab3b815a57bd913947";
    public static readonly Uri DownloadUrl = new("https://files.pythonhosted.org/packages/56/3d/c7dc9f35bc9e22cdb53679f844bee40fd5cff871862f60595869a433400d/" + WheelName);
    /// <summary>The wheel unpacks to about 20 MB: a guard against an archive that unpacks to far more than it says.</summary>
    public const long MaxExtractedBytes = 200L << 20;
    private const int MaxEntries = 10_000;

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DebugpyInstaller(HttpMessageHandler? handler = null, ILogger? logger = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CsCode", "1.0"));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary><c>{toolsDir}/debugpy/&lt;version&gt;</c>: the directory an install creates (it contains <c>debugpy/</c>).</summary>
    public static string InstallDirectory(string toolsDir) => Path.Combine(toolsDir, "debugpy", Version);

    /// <summary>The adapter of an install: a directory with a <c>__main__.py</c>, run by the interpreter.</summary>
    public static string AdapterPath(string toolsDir) => Path.Combine(InstallDirectory(toolsDir), "debugpy", "adapter");

    /// <summary>Downloads the pinned wheel, verifies it and extracts it; returns <see cref="AdapterPath"/>.</summary>
    public async Task<string> InstallAsync(string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        var parent = Path.Combine(toolsDir, "debugpy");
        Directory.CreateDirectory(parent);
        var wheel = Path.Combine(parent, ".download-" + Guid.NewGuid().ToString("N")[..8] + ".whl");
        try
        {
            progress?.Report($"Downloading debugpy {Version}…");
            using (var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(wheel);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (done > Size * 2) throw new InvalidDataException($"The debugpy download is far larger than the expected {Size:N0} bytes; refusing it.");
                    progress?.Report($"Downloading debugpy {Version}…", Math.Min(100, done * 100.0 / Size));
                }
            }
            return await InstallFromWheelAsync(wheel, toolsDir, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(wheel)) File.Delete(wheel); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Installs from a wheel already on disk: refused unless its SHA-256 is the pinned one; replaces a previous install of this version.</summary>
    public async Task<string> InstallFromWheelAsync(string wheelPath, string toolsDir, IProgressReporter? progress, CancellationToken ct)
    {
        progress?.Report("Verifying debugpy…");
        var actual = await JsDebugInstaller.ComputeSha256Async(wheelPath, ct).ConfigureAwait(false);
        if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The debugpy wheel is corrupt or has been tampered with (SHA-256 {actual}, expected {Sha256}).");
        var parent = Path.Combine(toolsDir, "debugpy");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report("Extracting debugpy…");
            await Task.Run(() =>
            {
                using (var stream = File.OpenRead(wheelPath)) ExtractPackage(stream, staging, ct);
                if (!File.Exists(Path.Combine(staging, "debugpy", "adapter", "__main__.py"))) throw new InvalidDataException("The debugpy wheel has no debugpy/adapter.");
                File.WriteAllText(Path.Combine(staging, "VERSION"), Version + "\n");
                var target = InstallDirectory(toolsDir);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Installed debugpy {Version} to {Dir}", Version, InstallDirectory(toolsDir));
            return AdapterPath(toolsDir);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Extracts the <c>debugpy/</c> entries of the wheel under <paramref name="destination"/> (names checked, no links, size-capped first).</summary>
    public static void ExtractPackage(Stream wheel, string destination, CancellationToken ct = default, long maxBytes = MaxExtractedBytes)
    {
        using var zip = new ZipArchive(wheel, ZipArchiveMode.Read);
        var entries = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("debugpy/", StringComparison.Ordinal)).ToList();
        if (entries.Count > MaxEntries) throw new UnsafeArchiveException($"The wheel has {entries.Count:N0} entries; refusing it.");
        long declared = 0;
        foreach (var entry in entries)
        {
            NetCoreDbgInstaller.SafeDestination(destination, entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new UnsafeArchiveException($"Archive entry '{entry.FullName}' is a link.");
            declared += entry.Length;
            if (declared > maxBytes) throw new UnsafeArchiveException($"The wheel unpacks to more than {maxBytes:N0} bytes; refusing it.");
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = NetCoreDbgInstaller.SafeDestination(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }
}
