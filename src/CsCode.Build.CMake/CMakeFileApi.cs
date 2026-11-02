using System.Text.Json;
using CsCode.Core.Build;

namespace CsCode.Build.CMake;

/// <summary>What a File API reply says about a build: the targets of one configuration, the generator and the ctest beside the cmake that ran.</summary>
internal sealed record FileApiReply(IReadOnlyList<CMakeTarget> Targets, IReadOnlyList<string> Configurations, string? Generator, bool MultiConfig, string? CTestPath);

/// <summary>
/// The CMake File API (cmake-file-api(7)), client <c>cscode</c>. <see cref="WriteQuery"/> drops the stateless query file
/// <c>&lt;build&gt;/.cmake/api/v1/query/client-cscode/codemodel-v2</c> before a configure; cmake then writes its reply under
/// <c>.cmake/api/v1/reply/</c>: the newest <c>index-*.json</c> names this client's <c>codemodel-v2</c> object, which lists each configuration's
/// targets, each in a <c>target-*.json</c> with its type, artifacts, sources, dependencies and link line. Paths in the reply are relative to the
/// top-level build directory (artifacts) or source directory (sources) unless absolute. Generator-provided targets (ALL_BUILD, ZERO_CHECK) are
/// left out. Runs on the CMake lane; every file is bounded in size.
/// </summary>
internal static class CMakeFileApi
{
    public const string Client = "client-cscode";
    public const int MaxSourcesPerTarget = 2000;
    private const long MaxReplyBytes = 64L * 1024 * 1024;

    public static string QueryDirectory(string buildDir) => Path.Combine(buildDir, ".cmake", "api", "v1", "query", Client);

    public static string ReplyDirectory(string buildDir) => Path.Combine(buildDir, ".cmake", "api", "v1", "reply");

    public static void WriteQuery(string buildDir)
    {
        var dir = QueryDirectory(buildDir);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "codemodel-v2");
        if (!File.Exists(file)) File.WriteAllBytes(file, []);
    }

    /// <summary>The reply for <paramref name="configuration"/> (null: the first configuration), or null when there is none yet or it does not parse.</summary>
    public static FileApiReply? Read(string buildDir, string? configuration)
    {
        var replyDir = ReplyDirectory(buildDir);
        if (!Directory.Exists(replyDir)) return null;
        // index files are named index-<timestamp>.json: the newest sorts last
        var index = Directory.GetFiles(replyDir, "index-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        if (index is null) return null;
        try
        {
            using var indexDoc = Parse(index);
            var root = indexDoc.RootElement;
            string? generator = null, ctest = null;
            var multi = false;
            if (root.TryGetProperty("cmake", out var cmake))
            {
                if (cmake.TryGetProperty("generator", out var gen))
                {
                    generator = Str(gen, "name");
                    multi = gen.TryGetProperty("multiConfig", out var m) && m.ValueKind == JsonValueKind.True;
                }
                if (cmake.TryGetProperty("paths", out var paths)) ctest = Str(paths, "ctest");
            }
            if (!root.TryGetProperty("reply", out var reply) || !reply.TryGetProperty(Client, out var client)
                || !client.TryGetProperty("codemodel-v2", out var codemodelRef) || Str(codemodelRef, "jsonFile") is not { } codemodelFile)
                return null;
            using var codemodel = Parse(Path.Combine(replyDir, codemodelFile));
            var cm = codemodel.RootElement;
            var sourceRoot = cm.TryGetProperty("paths", out var cmPaths) ? Str(cmPaths, "source") ?? "" : "";
            var buildRoot = cm.TryGetProperty("paths", out cmPaths) ? Str(cmPaths, "build") ?? buildDir : buildDir;
            var configs = cm.GetProperty("configurations").EnumerateArray().ToList();
            var names = configs.Select(c => Str(c, "name") ?? "").ToList();
            var config = configs.FirstOrDefault(c => configuration is not null && string.Equals(Str(c, "name"), configuration, StringComparison.OrdinalIgnoreCase));
            if (config.ValueKind == JsonValueKind.Undefined) config = configs.FirstOrDefault();
            if (config.ValueKind == JsonValueKind.Undefined) return new FileApiReply([], names, generator, multi, ctest);

            var refs = config.GetProperty("targets").EnumerateArray().Select(t => (Id: Str(t, "id") ?? "", Name: Str(t, "name") ?? "", File: Str(t, "jsonFile") ?? "")).ToList();
            var nameOf = refs.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
            var targets = new List<CMakeTarget>(refs.Count);
            foreach (var r in refs)
            {
                if (r.File.Length == 0) continue;
                using var doc = Parse(Path.Combine(replyDir, r.File));
                if (Target(doc.RootElement, r.Name, nameOf, sourceRoot, buildRoot) is { } target) targets.Add(target);
            }
            targets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return new FileApiReply(targets, names, generator, multi, ctest);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal static CMakeTarget? Target(JsonElement t, string name, IReadOnlyDictionary<string, string> nameOf, string sourceRoot, string buildRoot)
    {
        if (t.TryGetProperty("isGeneratorProvided", out var gp) && gp.ValueKind == JsonValueKind.True) return null;
        var type = (Str(t, "type") ?? "") switch
        {
            "EXECUTABLE" => CMakeTargetType.Executable,
            "STATIC_LIBRARY" => CMakeTargetType.StaticLibrary,
            "SHARED_LIBRARY" => CMakeTargetType.SharedLibrary,
            "MODULE_LIBRARY" => CMakeTargetType.ModuleLibrary,
            "OBJECT_LIBRARY" => CMakeTargetType.ObjectLibrary,
            "INTERFACE_LIBRARY" => CMakeTargetType.InterfaceLibrary,
            _ => CMakeTargetType.Utility,
        };
        var artifacts = Array(t, "artifacts").Select(a => Str(a, "path")).OfType<string>().Select(p => Absolute(buildRoot, p)).ToArray();
        var dir = t.TryGetProperty("paths", out var paths) && Str(paths, "source") is { } s ? Absolute(sourceRoot, s) : sourceRoot;
        var deps = Array(t, "dependencies").Select(d => Str(d, "id")).OfType<string>().Select(id => nameOf.TryGetValue(id, out var n) ? n : id.Split("::")[0])
            .Distinct(StringComparer.Ordinal).ToArray();
        var libs = t.TryGetProperty("link", out var link)
            ? Array(link, "commandFragments").Where(f => Str(f, "role") == "libraries").Select(f => Str(f, "fragment")).OfType<string>().ToArray()
            : [];
        var sources = Array(t, "sources").Select(x => Str(x, "path")).OfType<string>()
            .Where(p => !p.EndsWith(".rule", StringComparison.Ordinal)).Take(MaxSourcesPerTarget).Select(p => Absolute(sourceRoot, p)).ToArray();
        return new CMakeTarget(name, type, artifacts, dir, deps, libs, sources);
    }

    private static string Absolute(string root, string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));

    private static IEnumerable<JsonElement> Array(JsonElement e, string key) =>
        e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonDocument Parse(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new IOException($"{file} does not exist");
        if (info.Length > MaxReplyBytes) throw new IOException($"{file} is larger than {MaxReplyBytes / (1024 * 1024)} MB");
        return JsonDocument.Parse(File.ReadAllBytes(file));
    }
}
