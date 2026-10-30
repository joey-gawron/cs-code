using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CsCode.Editor.TreeSitter.Native;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>One grammar library of the bundle, as <c>bundle.json</c> records it at build time.</summary>
public sealed record BundledGrammar(string Name, string Source, string File, uint Abi, long Size, string Sha256);

/// <summary>
/// The tree-sitter bundle that ships next to the app (<c>tree-sitter/</c>, built by <c>tools/treesitter/build-grammars.sh</c> from the pinned
/// sources in <c>tools/treesitter/manifest.json</c>): the runtime, the grammars, nvim-treesitter's queries, the licences and
/// <c>bundle.json</c>. This class is the bundle's VERSION GATE: the bundle is used only when its format is known, it was built for this OS
/// and architecture, and its grammar ABI range is the one this engine's runtime accepts; every file is checked against the SHA-256 the build
/// recorded (and every grammar against its recorded ABI) before it is loaded, so a partial copy, a stray file or a grammar from another build
/// is refused with a reason instead of being mapped into the process. Read-only, thread-safe, cheap to create (one small JSON file).
/// </summary>
public sealed class TreeSitterBundle
{
    public const int SupportedFormat = 1;
    public const string FolderName = "tree-sitter";

    private readonly Dictionary<string, BundledGrammar> _grammars;
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _verified = new(StringComparer.Ordinal);

    private TreeSitterBundle(string directory, string target, string runtimeFile, string runtimeSha256, string runtimeVersion, string queriesCommit,
        Dictionary<string, BundledGrammar> grammars)
    {
        Directory = directory;
        Target = target;
        RuntimePath = Path.Combine(directory, runtimeFile);
        RuntimeSha256 = runtimeSha256;
        RuntimeVersion = runtimeVersion;
        QueriesCommit = queriesCommit;
        _grammars = grammars;
    }

    public string Directory { get; }

    /// <summary>The runtime identifier it was built for (<c>linux-x64</c>).</summary>
    public string Target { get; }

    public string RuntimePath { get; }

    internal string RuntimeSha256 { get; }

    /// <summary>The tree-sitter release the runtime was built from (<c>v0.26.11</c>).</summary>
    public string RuntimeVersion { get; }

    /// <summary>The nvim-treesitter commit the queries come from.</summary>
    public string QueriesCommit { get; }

    /// <summary>The root a <see cref="GrammarLocator"/> searches: <c>&lt;name&gt;.so</c> and <c>queries/&lt;name&gt;/*.scm</c>.</summary>
    public string GrammarsRoot => Path.Combine(Directory, "grammars");

    public IReadOnlyCollection<BundledGrammar> Grammars => _grammars.Values;

    public BundledGrammar? Grammar(string name) => _grammars.GetValueOrDefault(name);

    /// <summary>The app's own bundle (<c>&lt;app dir&gt;/tree-sitter</c>), or null with <see cref="DefaultProblem"/> saying why (none built, refused).</summary>
    public static TreeSitterBundle? Default => DefaultLazy.Value.Bundle;

    /// <summary>Why <see cref="Default"/> is null: "not present" or the gate's reason.</summary>
    public static string? DefaultProblem => DefaultLazy.Value.Problem;

    /// <summary>
    /// Whether the Nix store is searched for a runtime and grammars: a DEVELOPMENT fallback (this machine's Neovim/Helix grammars), on only
    /// when the app has no bundle of its own. <c>CSCODE_TREESITTER_NIX=1</c> forces it on, <c>=0</c> off.
    /// </summary>
    public static bool NixStoreFallback => Environment.GetEnvironmentVariable("CSCODE_TREESITTER_NIX") switch
    {
        "1" => true,
        "0" => false,
        _ => Default is null,
    };

    private static readonly Lazy<(TreeSitterBundle? Bundle, string? Problem)> DefaultLazy = new(() =>
    {
        var bundle = TryOpen(Path.Combine(AppContext.BaseDirectory, FolderName), out var problem);
        return (bundle, problem);
    });

    /// <summary>Opens the bundle in <paramref name="directory"/>; null with the reason when it is absent or does not pass the gate.</summary>
    public static TreeSitterBundle? TryOpen(string directory, out string? problem)
    {
        var manifest = Path.Combine(directory, "bundle.json");
        if (!File.Exists(manifest))
        {
            problem = $"no tree-sitter bundle in {directory}";
            return null;
        }
        try
        {
            using var stream = File.OpenRead(manifest);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var format = root.GetProperty("format").GetInt32();
            if (format != SupportedFormat)
            {
                problem = $"{manifest}: bundle format {format} is not supported (this build reads format {SupportedFormat})";
                return null;
            }
            var target = root.GetProperty("target").GetString() ?? "";
            if (!string.Equals(target, CurrentTarget, StringComparison.Ordinal))
            {
                problem = $"{manifest}: built for {target}, this process is {CurrentTarget}";
                return null;
            }
            var abi = root.GetProperty("abi");
            var min = abi.GetProperty("min").GetUInt32();
            var max = abi.GetProperty("max").GetUInt32();
            if (min != TsRuntime.MinAbi || max != TsRuntime.MaxAbi)
            {
                problem = $"{manifest}: built for grammar ABI {min}-{max}, this engine accepts {TsRuntime.MinAbi}-{TsRuntime.MaxAbi}";
                return null;
            }
            var runtime = root.GetProperty("runtime");
            var grammars = new Dictionary<string, BundledGrammar>(StringComparer.Ordinal);
            foreach (var g in root.GetProperty("grammars").EnumerateArray())
            {
                var entry = new BundledGrammar(g.GetProperty("name").GetString()!, g.GetProperty("source").GetString()!, g.GetProperty("file").GetString()!,
                    g.GetProperty("abi").GetUInt32(), g.GetProperty("size").GetInt64(), g.GetProperty("sha256").GetString()!);
                if (entry.Abi < min || entry.Abi > max)
                {
                    problem = $"{manifest}: grammar {entry.Name} has ABI {entry.Abi}, outside {min}-{max}";
                    return null;
                }
                grammars[entry.Name] = entry;
            }
            problem = null;
            return new TreeSitterBundle(directory, target, runtime.GetProperty("file").GetString()!, runtime.GetProperty("sha256").GetString()!,
                runtime.GetProperty("version").GetString() ?? "", root.GetProperty("queries").GetProperty("commit").GetString() ?? "", grammars);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problem = $"{manifest} is not a valid bundle manifest: {ex.Message}";
            return null;
        }
    }

    /// <summary>This process as a bundle target: <c>linux-x64</c>, <c>osx-arm64</c>, <c>win-x64</c>…</summary>
    public static string CurrentTarget
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                var other => other.ToString().ToLowerInvariant(),
            };
            return os + "-" + arch;
        }
    }

    /// <summary>Checks the runtime library against its recorded digest (null = fine, else the reason). Cached.</summary>
    public string? VerifyRuntime() => VerifyFile("(runtime)", RuntimePath, RuntimeSha256);

    /// <summary>
    /// Checks <paramref name="path"/> — which must be this bundle's library for grammar <paramref name="name"/> — against the recorded size and
    /// SHA-256 (null = fine, else the reason). Cached per grammar: a grammar is hashed once per process (≈ 5 ms for the 5 MB C++ grammar).
    /// </summary>
    public string? VerifyGrammar(string name, string path)
    {
        if (!_grammars.TryGetValue(name, out var g)) return $"{name} is not part of the bundle";
        var expected = Path.GetFullPath(Path.Combine(Directory, g.File));
        if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.Ordinal)) return $"{path} is not the bundled {name} ({expected})";
        return VerifyFile(name, expected, g.Sha256, g.Size);
    }

    /// <summary>True when <paramref name="path"/> lies inside this bundle (its files are verified before use).</summary>
    public bool Contains(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private string? VerifyFile(string key, string path, string sha256, long size = -1)
    {
        lock (_gate)
        {
            if (_verified.TryGetValue(key, out var cached)) return cached;
            string? problem;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) problem = $"{path} is missing";
                else if (size >= 0 && info.Length != size) problem = $"{path} has {info.Length} bytes, the bundle recorded {size}";
                else
                {
                    using var stream = File.OpenRead(path);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    problem = string.Equals(hash, sha256, StringComparison.Ordinal) ? null : $"{path} does not match the digest recorded at build time";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"{path} cannot be read: {ex.Message}";
            }
            _verified[key] = problem;
            return problem;
        }
    }
}
