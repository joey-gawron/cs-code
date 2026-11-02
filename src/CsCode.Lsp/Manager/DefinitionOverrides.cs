using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Lsp.Manager;

/// <summary>
/// Applies the user's <c>cscode.languageServers</c> setting (JSON object keyed by definition id) on top of the registered definitions.
/// Per id: <c>command</c>, <c>args</c>, <c>env</c>, <c>selector</c> (language ids, or {language,scheme,pattern} objects), <c>initializationOptions</c>,
/// <c>settings</c>, <c>rootMarkers</c>, <c>autoStart</c>, <c>enabled</c>. An id that matches no registered definition defines a NEW server
/// (needs at least <c>command</c> and <c>selector</c>).
/// </summary>
internal static class DefinitionOverrides
{
    public readonly record struct Effective(LanguageServerDefinition Definition, bool Enabled);

    public static Dictionary<string, Effective> Apply(IEnumerable<LanguageServerDefinition> registered, JsonElement? setting)
    {
        var result = new Dictionary<string, Effective>();
        // a built-in that is off by default (basedpyright) stays off until its override says "enabled": true
        foreach (var d in registered) result[d.Id] = new Effective(d, BuiltinLanguageServers.Find(d.Id)?.EnabledByDefault ?? true);
        if (setting is not { ValueKind: JsonValueKind.Object } obj) return result;

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var id = prop.Name;
            var o = prop.Value;
            bool? enabled = o.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False ? en.GetBoolean() : null;
            if (result.TryGetValue(id, out var existing))
                result[id] = new Effective(Merge(existing.Definition, o), enabled ?? existing.Enabled);
            else if (TryCreate(id, o) is { } created)
                result[id] = new Effective(created, enabled ?? true);
        }
        return result;
    }

    private static LanguageServerDefinition Merge(LanguageServerDefinition d, JsonElement o)
    {
        if (o.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String) d = d with { Command = c.GetString()! };
        if (o.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array) d = d with { Args = Strings(a) };
        if (o.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object) d = d with { Env = StringMap(e) };
        if (o.TryGetProperty("selector", out var s) && ParseSelector(s) is { } sel) d = d with { Selector = sel };
        if (o.TryGetProperty("initializationOptions", out var io)) d = d with { InitializationOptions = ExpandTopLevelKeys(io) };
        if (o.TryGetProperty("settings", out var st)) d = d with { Settings = ExpandTopLevelKeys(st) };
        if (o.TryGetProperty("rootMarkers", out var rm) && rm.ValueKind == JsonValueKind.Array) d = d with { RootMarkers = Strings(rm) };
        if (o.TryGetProperty("autoStart", out var au) && au.ValueKind is JsonValueKind.True or JsonValueKind.False) d = d with { AutoStart = au.GetBoolean() };
        if (o.TryGetProperty("workingDirectory", out var wd) && wd.ValueKind == JsonValueKind.String) d = d with { WorkingDirectory = wd.GetString() };
        // null or 0 turns a built-in cap off (the TypeScript-engine servers have one, T39)
        if (o.TryGetProperty("restartAboveBytes", out var rb))
        {
            if (rb.ValueKind == JsonValueKind.Null) d = d with { RestartAboveBytes = null };
            else if (rb.ValueKind == JsonValueKind.Number && rb.TryGetInt64(out var bytes)) d = d with { RestartAboveBytes = bytes > 0 ? bytes : null };
        }
        return d;
    }

    private static LanguageServerDefinition? TryCreate(string id, JsonElement o)
    {
        if (!o.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return null;
        if (!o.TryGetProperty("selector", out var s) || ParseSelector(s) is not { } sel) return null;
        var d = new LanguageServerDefinition(id, o.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : id,
            sel, c.GetString()!, [], SettingsKey: LspSettingKeys.LanguageServers + "." + id);
        return Merge(d, o);
    }

    /// <summary>
    /// A server's <c>settings</c> (and <c>initializationOptions</c>) object is written like a settings.json of its own: as there, its TOP-LEVEL dotted
    /// keys are split into nested objects (<c>{"rust-analyzer.check.command": "clippy"}</c> answers a <c>workspace/configuration</c> request for
    /// <c>rust-analyzer</c> with <c>{"check":{"command":"clippy"}}</c>), and every value below is kept as written, so globs, URLs and other member names
    /// with dots survive (<c>{"yaml.schemas": {"https://…/x.json": "*.yml"}}</c>). Objects that meet are merged; a later scalar replaces. Before
    /// lsp-core-2 the configuration service split member names at every depth, which broke the URLs; since it no longer does, this restores the
    /// top-level spelling a user copies from VS Code's settings.json.
    /// </summary>
    internal static JsonElement ExpandTopLevelKeys(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return value.Clone();
        var hasDotted = false;
        foreach (var p in value.EnumerateObject())
            if (p.Name.Contains('.', StringComparison.Ordinal)) { hasDotted = true; break; }
        if (!hasDotted) return value.Clone();

        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in value.EnumerateObject())
        {
            var segments = p.Name.Split('.');
            if (segments.Any(static x => x.Length == 0)) segments = [p.Name]; // "a..b", ".a", "a.": not a path, kept as written
            var target = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (target[segments[i]] is not System.Text.Json.Nodes.JsonObject next)
                {
                    next = new System.Text.Json.Nodes.JsonObject();
                    target[segments[i]] = next;
                }
                target = next;
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
            if (node is System.Text.Json.Nodes.JsonObject incoming && target[segments[^1]] is System.Text.Json.Nodes.JsonObject existing) Merge(existing, incoming);
            else target[segments[^1]] = node;
        }
        return JsonSerializer.SerializeToElement(root);

        static void Merge(System.Text.Json.Nodes.JsonObject into, System.Text.Json.Nodes.JsonObject from)
        {
            foreach (var (k, v) in from.ToArray())
            {
                from.Remove(k);
                if (v is System.Text.Json.Nodes.JsonObject vo && into[k] is System.Text.Json.Nodes.JsonObject eo) Merge(eo, vo);
                else into[k] = v;
            }
        }
    }

    private static IReadOnlyList<string> Strings(JsonElement array) =>
        array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();

    private static Dictionary<string, string> StringMap(JsonElement obj) =>
        obj.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static DocumentSelector? ParseSelector(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Array) return null;
        var filters = new List<DocumentFilter>();
        foreach (var item in s.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) filters.Add(new DocumentFilter(Language: item.GetString()));
            else if (item.ValueKind == JsonValueKind.Object)
                filters.Add(new DocumentFilter(
                    item.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                    item.TryGetProperty("scheme", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null,
                    item.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null));
        }
        return filters.Count == 0 ? null : new DocumentSelector(filters);
    }

    /// <summary>Whether two effective definitions would run the server differently (a restart is needed when this is false).</summary>
    public static bool SameLaunch(LanguageServerDefinition a, LanguageServerDefinition b) =>
        a.Command == b.Command
        && a.Args.SequenceEqual(b.Args)
        && DictEq(a.Env, b.Env)
        && a.WorkingDirectory == b.WorkingDirectory
        && JsonEq(a.InitializationOptions, b.InitializationOptions)
        && JsonEq(a.Settings, b.Settings)
        && (a.RootMarkers ?? []).SequenceEqual(b.RootMarkers ?? []);

    private static bool DictEq(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a ?? new Dictionary<string, string>()).All(kv => b is not null && b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static bool JsonEq(JsonElement? a, JsonElement? b) => a?.GetRawText() == b?.GetRawText();
}
