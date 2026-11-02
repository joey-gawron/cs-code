using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsCode.Build.CMake;

/// <summary>A visible configure preset with what the integration needs resolved: its build directory, generator, build type and cache variables.</summary>
internal sealed record ConfigurePreset(string Name, string DisplayName, string? Description, string? BinaryDir, string? Generator, string? BuildType,
    IReadOnlyDictionary<string, string> CacheVariables);

/// <summary>A visible build preset: the configure preset it builds, its configuration and default targets.</summary>
internal sealed record BuildPreset(string Name, string DisplayName, string? Description, string? ConfigurePreset, string? Configuration, IReadOnlyList<string> Targets);

/// <summary>
/// <c>CMakePresets.json</c> and <c>CMakeUserPresets.json</c> (which implicitly includes the first), schema versions 1–10: <c>include</c>,
/// <c>inherits</c> (single or list, first wins), <c>hidden</c>, <c>condition</c> (const, equals / notEquals, inList / notInList, matches /
/// notMatches, anyOf / allOf / not) and the macros CMake expands in <c>binaryDir</c> (<c>${sourceDir}</c>, <c>${sourceParentDir}</c>,
/// <c>${sourceDirName}</c>, <c>${presetName}</c>, <c>${generator}</c>, <c>${hostSystemName}</c>, <c>${fileDir}</c>, <c>${dollar}</c>,
/// <c>${pathListSep}</c>, <c>$env{…}</c>, <c>$penv{…}</c>). cmake itself configures with <c>--preset</c>; this only predicts where the build
/// directory will be (the File API query goes there first) and lists the presets for the quick pick. A file that does not parse yields no presets
/// and an <see cref="Error"/>.
/// </summary>
internal sealed class CMakePresets
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Regex Macro = new(@"\$(env|penv|vendor)?\{([^}]*)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const int MaxIncludes = 32;

    public IReadOnlyList<ConfigurePreset> Configure { get; private init; } = [];
    public IReadOnlyList<BuildPreset> Build { get; private init; } = [];
    public string? Error { get; private init; }

    /// <summary>True when either presets file exists (an empty list then still means "presets mode").</summary>
    public bool Exists { get; private init; }

    public static string HostSystemName => OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : "Linux";

    private sealed record Raw(string Name, JsonElement Element, string FileDir);

    /// <summary>Reads the presets of <paramref name="sourceDir"/>; <paramref name="environment"/> answers <c>$env{}</c> / <c>$penv{}</c>.</summary>
    public static CMakePresets Load(string sourceDir, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var main = Path.Combine(sourceDir, "CMakePresets.json");
        var user = Path.Combine(sourceDir, "CMakeUserPresets.json");
        if (!File.Exists(main) && !File.Exists(user)) return new CMakePresets();
        var configure = new Dictionary<string, Raw>(StringComparer.Ordinal);
        var build = new Dictionary<string, Raw>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(main)) Read(main, configure, build, seen);
            if (File.Exists(user)) Read(user, configure, build, seen);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CMakePresets { Exists = true, Error = $"The CMake presets could not be read: {ex.Message}" };
        }

        var configurePresets = new List<ConfigurePreset>();
        foreach (var raw in configure.Values)
        {
            if (Bool(Lookup(raw, configure, "hidden", inherit: false)) == true) continue;
            var macros = new MacroContext(sourceDir, raw.Name, raw.FileDir, Str(Lookup(raw, configure, "generator")), Env(raw, configure), environment);
            if (!Condition(Lookup(raw, configure, "condition", inherit: false), macros)) continue;
            var cache = CacheVariables(raw, configure);
            var binary = Str(Lookup(raw, configure, "binaryDir")) is { } b ? Expand(b, macros) : null;
            if (binary is not null && !Path.IsPathRooted(binary)) binary = Path.GetFullPath(Path.Combine(sourceDir, binary));
            configurePresets.Add(new ConfigurePreset(raw.Name, Str(raw.Element, "displayName") ?? raw.Name, Str(raw.Element, "description"),
                binary is null ? null : Path.GetFullPath(binary), macros.Generator, cache.GetValueOrDefault("CMAKE_BUILD_TYPE"), cache));
        }

        var buildPresets = new List<BuildPreset>();
        foreach (var raw in build.Values)
        {
            if (Bool(Lookup(raw, build, "hidden", inherit: false)) == true) continue;
            var macros = new MacroContext(sourceDir, raw.Name, raw.FileDir, null, Env(raw, build), environment);
            if (!Condition(Lookup(raw, build, "condition", inherit: false), macros)) continue;
            var targets = Lookup(raw, build, "targets") is { } t
                ? t.ValueKind == JsonValueKind.String ? [t.GetString()!] : t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : []
                : Array.Empty<string>();
            buildPresets.Add(new BuildPreset(raw.Name, Str(raw.Element, "displayName") ?? raw.Name, Str(raw.Element, "description"),
                Str(Lookup(raw, build, "configurePreset")), Str(Lookup(raw, build, "configuration")), targets));
        }
        return new CMakePresets { Exists = true, Configure = configurePresets, Build = buildPresets };
    }

    private static void Read(string file, Dictionary<string, Raw> configure, Dictionary<string, Raw> build, HashSet<string> seen)
    {
        var full = Path.GetFullPath(file);
        if (!seen.Add(full) || seen.Count > MaxIncludes) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(full), Lenient);
        var root = doc.RootElement;
        var dir = Path.GetDirectoryName(full)!;
        // CMakeUserPresets.json includes CMakePresets.json implicitly; includes come first so this file's presets can inherit from them
        if (string.Equals(Path.GetFileName(full), "CMakeUserPresets.json", StringComparison.Ordinal))
        {
            var main = Path.Combine(dir, "CMakePresets.json");
            if (File.Exists(main)) Read(main, configure, build, seen);
        }
        if (root.TryGetProperty("include", out var includes) && includes.ValueKind == JsonValueKind.Array)
            foreach (var inc in includes.EnumerateArray())
                if (inc.GetString() is { Length: > 0 } path)
                {
                    var target = Path.IsPathRooted(path) ? path : Path.Combine(dir, path);
                    if (File.Exists(target)) Read(target, configure, build, seen);
                }
        Collect(root, "configurePresets", configure, dir);
        Collect(root, "buildPresets", build, dir);
    }

    private static void Collect(JsonElement root, string key, Dictionary<string, Raw> into, string dir)
    {
        if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var p in list.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Object && Str(p, "name") is { Length: > 0 } name) into.TryAdd(name, new Raw(name, p.Clone(), dir));
    }

    /// <summary>The preset's own value of <paramref name="key"/>, else the first parent's (depth-first, cycles cut).</summary>
    private static JsonElement? Lookup(Raw raw, Dictionary<string, Raw> all, string key, bool inherit = true, int depth = 0)
    {
        if (raw.Element.TryGetProperty(key, out var own) && own.ValueKind != JsonValueKind.Null) return own;
        if (!inherit || depth > 16) return null;
        foreach (var parent in Parents(raw, all))
            if (Lookup(parent, all, key, true, depth + 1) is { } v) return v;
        return null;
    }

    private static IEnumerable<Raw> Parents(Raw raw, Dictionary<string, Raw> all)
    {
        if (!raw.Element.TryGetProperty("inherits", out var inh)) yield break;
        if (inh.ValueKind == JsonValueKind.String) { if (all.TryGetValue(inh.GetString()!, out var p)) yield return p; }
        else if (inh.ValueKind == JsonValueKind.Array)
            foreach (var n in inh.EnumerateArray())
                if (n.GetString() is { } name && all.TryGetValue(name, out var p)) yield return p;
    }

    /// <summary>Cache variables merged down the inheritance chain (the child and earlier parents win); <c>{type, value}</c> objects and booleans become strings.</summary>
    private static Dictionary<string, string> CacheVariables(Raw raw, Dictionary<string, Raw> all, int depth = 0)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (depth > 16) return result;
        if (raw.Element.TryGetProperty("cacheVariables", out var vars) && vars.ValueKind == JsonValueKind.Object)
            foreach (var v in vars.EnumerateObject())
            {
                var value = v.Value.ValueKind switch
                {
                    JsonValueKind.String => v.Value.GetString(),
                    JsonValueKind.True => "ON",
                    JsonValueKind.False => "OFF",
                    JsonValueKind.Object when v.Value.TryGetProperty("value", out var inner) => inner.ValueKind == JsonValueKind.String ? inner.GetString() : inner.ValueKind == JsonValueKind.True ? "ON" : "OFF",
                    _ => null,
                };
                if (value is not null) result[v.Name] = value;
            }
        foreach (var parent in Parents(raw, all))
            foreach (var (k, v) in CacheVariables(parent, all, depth + 1))
                result.TryAdd(k, v);
        return result;
    }

    private static Dictionary<string, string> Env(Raw raw, Dictionary<string, Raw> all, int depth = 0)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (depth > 16) return result;
        if (raw.Element.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
            foreach (var v in env.EnumerateObject())
                if (v.Value.ValueKind == JsonValueKind.String) result[v.Name] = v.Value.GetString()!;
        foreach (var parent in Parents(raw, all))
            foreach (var (k, v) in Env(parent, all, depth + 1))
                result.TryAdd(k, v);
        return result;
    }

    internal sealed record MacroContext(string SourceDir, string PresetName, string FileDir, string? Generator, IReadOnlyDictionary<string, string> PresetEnvironment,
        Func<string, string?> Environment);

    internal static string Expand(string text, MacroContext m, int depth = 0)
    {
        if (depth > 8 || !text.Contains('{', StringComparison.Ordinal)) return text;
        try
        {
            return Macro.Replace(text, match =>
            {
                var name = match.Groups[2].Value;
                return match.Groups[1].Value switch
                {
                    "env" => m.PresetEnvironment.TryGetValue(name, out var pv) ? Expand(pv, m, depth + 1) : m.Environment(name) ?? "",
                    "penv" => m.Environment(name) ?? "",
                    "vendor" => match.Value,
                    _ => name switch
                    {
                        "sourceDir" => m.SourceDir,
                        "sourceParentDir" => Path.GetDirectoryName(m.SourceDir.TrimEnd(Path.DirectorySeparatorChar)) ?? m.SourceDir,
                        "sourceDirName" => Path.GetFileName(m.SourceDir.TrimEnd(Path.DirectorySeparatorChar)),
                        "presetName" => m.PresetName,
                        "generator" => m.Generator ?? "",
                        "hostSystemName" => HostSystemName,
                        "fileDir" => m.FileDir,
                        "dollar" => "$",
                        "pathListSep" => Path.PathSeparator.ToString(),
                        _ => match.Value,
                    },
                };
            });
        }
        catch (RegexMatchTimeoutException) { return text; }
    }

    /// <summary>Evaluates a preset's <c>condition</c>; a missing or unknown one is true (CMake treats null as true).</summary>
    internal static bool Condition(JsonElement? condition, MacroContext m)
    {
        if (condition is not { } c || c.ValueKind == JsonValueKind.Null) return true;
        if (c.ValueKind is JsonValueKind.True or JsonValueKind.False) return c.GetBoolean();
        if (c.ValueKind != JsonValueKind.Object) return true;
        string? S(string key) => Str(c, key) is { } s ? Expand(s, m) : null;
        switch (Str(c, "type"))
        {
            case "const": return Bool(c.TryGetProperty("value", out var v) ? v : null) ?? true;
            case "equals": return S("lhs") == S("rhs");
            case "notEquals": return S("lhs") != S("rhs");
            case "inList":
            case "notInList":
                var item = S("string");
                var found = c.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
                    && list.EnumerateArray().Any(x => x.GetString() is { } s && Expand(s, m) == item);
                return Str(c, "type") == "inList" ? found : !found;
            case "matches":
            case "notMatches":
                bool matches;
                try { matches = Regex.IsMatch(S("string") ?? "", S("regex") ?? "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { matches = false; }
                return Str(c, "type") == "matches" ? matches : !matches;
            case "anyOf": return Conditions(c).Any(x => Condition(x, m));
            case "allOf": return Conditions(c).All(x => Condition(x, m));
            case "not": return !Condition(c.TryGetProperty("condition", out var inner) ? inner : null, m);
            default: return true;
        }
    }

    private static IEnumerable<JsonElement> Conditions(JsonElement c) =>
        c.TryGetProperty("conditions", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

    private static string? Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static bool? Bool(JsonElement? e) => e is { ValueKind: JsonValueKind.True } ? true : e is { ValueKind: JsonValueKind.False } ? false : null;
}
