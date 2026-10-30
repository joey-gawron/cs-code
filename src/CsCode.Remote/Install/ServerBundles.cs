using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsCode.Remote.Install;

/// <summary>Self-contained single file for one runtime id, or a framework-dependent build that runs anywhere a .NET 10 runtime exists.</summary>
public enum BundleFlavour { SelfContained, FrameworkDependent }

/// <param name="Hash">Install directory name on the remote (<c>~/.cscode-server/bin/&lt;hash&gt;</c>): 16 hex digits of the bundle's SHA-256.</param>
/// <param name="Rid">Runtime id (<c>linux-x64</c>…) of a self-contained bundle; <c>any</c> for framework-dependent.</param>
/// <param name="Runtime">Minimum Microsoft.NETCore.App major.minor a framework-dependent bundle needs.</param>
public sealed record ServerBundle(BundleFlavour Flavour, string Rid, string File, string Sha256, string Hash, string? Runtime = null)
{
    [JsonIgnore] public string? Directory { get; init; }
    [JsonIgnore] public string FullPath => Path.Combine(Directory ?? ".", File);
}

/// <summary>
/// The server bundles shipped with (or built next to) the client, described by <c>manifest.json</c>, which <c>tools/publish-server.sh</c> writes.
/// Looked up in <c>remote.ssh.serverBundles</c>, then <c>{InstallDir}/server-bundles</c>, then (development) <c>.scratch/server-bundles</c> of the
/// source tree the binary runs from.
/// </summary>
public sealed class ServerBundleSet
{
    public const string ManifestFile = "manifest.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) } };

    public ServerBundleSet(string directory, IReadOnlyList<ServerBundle> bundles, string? version = null)
    {
        Directory = directory;
        Bundles = bundles.Select(b => b with { Directory = directory }).ToArray();
        Version = version;
    }

    public string Directory { get; }
    public IReadOnlyList<ServerBundle> Bundles { get; }
    public string? Version { get; }

    public ServerBundle? SelfContained(string rid) => Bundles.FirstOrDefault(b => b.Flavour == BundleFlavour.SelfContained && b.Rid == rid);
    public ServerBundle? FrameworkDependent => Bundles.FirstOrDefault(b => b.Flavour == BundleFlavour.FrameworkDependent);

    /// <summary>Every install hash the start script should look for, preferred first.</summary>
    public IReadOnlyList<string> CandidateHashes =>
        Bundles.OrderBy(b => b.Flavour == BundleFlavour.SelfContained ? 0 : 1).Select(b => b.Hash).Distinct().ToArray();

    private sealed record Manifest(string? Version, IReadOnlyList<ServerBundle> Bundles);

    public static ServerBundleSet Load(string directory)
    {
        var path = Path.Combine(directory, ManifestFile);
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"Empty manifest: {path}");
        foreach (var b in manifest.Bundles)
            if (b.Hash.Length != 16 || !b.Hash.All(char.IsAsciiHexDigitLower))
                throw new InvalidDataException($"{path}: bundle hash '{b.Hash}' must be 16 lower-case hex digits");
        return new ServerBundleSet(directory, manifest.Bundles, manifest.Version);
    }

    public static string Serialize(IReadOnlyList<ServerBundle> bundles, string? version) =>
        JsonSerializer.Serialize(new Manifest(version, bundles), new JsonSerializerOptions(Json) { WriteIndented = true });

    /// <summary>Finds a bundle directory: the configured one, then next to the program, then a development tree's <c>.scratch/server-bundles</c>.</summary>
    public static ServerBundleSet? Find(string? configured, string installDir)
    {
        foreach (var dir in Candidates(configured, installDir))
        {
            if (!System.IO.File.Exists(Path.Combine(dir, ManifestFile))) continue;
            try { return Load(dir); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? configured, string installDir)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return Path.Combine(installDir, "server-bundles");
        for (var d = new DirectoryInfo(installDir); d is not null; d = d.Parent)
            if (System.IO.File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) { yield return Path.Combine(d.FullName, ".scratch", "server-bundles"); yield break; }
    }

    /// <summary>SHA-256 of a file, lower-case hex (what the remote install script verifies).</summary>
    public static string Sha256Of(string file)
    {
        using var s = System.IO.File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
