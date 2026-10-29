using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;

namespace CsCode.Dap.Configuration;

public sealed class LaunchJsonException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parsed launch.json. Compounds are surfaced as configurations of type <c>"compound"</c> (see <see cref="LaunchJson.CompoundType"/>).</summary>
public sealed record LaunchFile(
    string SourcePath,
    IReadOnlyList<DebugConfiguration> Configurations,
    IReadOnlyList<DebugConfiguration> Compounds,
    IReadOnlyDictionary<string, JsonElement> Inputs)
{
    public static LaunchFile Empty(string path) => new(path, [], [], new Dictionary<string, JsonElement>());
    public IEnumerable<DebugConfiguration> All => Configurations.Concat(Compounds);
}

/// <summary>JSONC reader for launch.json: comments and trailing commas allowed, platform blocks merged, compounds and inputs collected.</summary>
public static class LaunchJson
{
    public const string CompoundType = "compound";
    public const string VersionHeader = "0.2.0";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LaunchFile Parse(string text, Uri? folder, string sourcePath)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: Lenient); }
        catch (JsonException ex)
        {
            throw new LaunchJsonException($"{sourcePath}: invalid JSON at line {ex.LineNumber + 1}: {ex.Message}", ex);
        }
        if (root is not JsonObject obj) throw new LaunchJsonException($"{sourcePath}: the root must be a JSON object.");

        var configurations = new List<DebugConfiguration>();
        if (obj["configurations"] is JsonArray configs)
            foreach (var item in configs)
            {
                if (item is not JsonObject c) continue;
                var merged = VariableResolver.MergePlatformBlock(c);
                configurations.Add(DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(merged), folder));
            }

        var compounds = new List<DebugConfiguration>();
        if (obj["compounds"] is JsonArray comps)
            foreach (var item in comps)
            {
                if (item is not JsonObject c) continue;
                var merged = VariableResolver.MergePlatformBlock(c);
                merged["type"] = CompoundType;
                merged["request"] = "launch";
                compounds.Add(DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(merged), folder));
            }

        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (obj["inputs"] is JsonArray ins)
            foreach (var item in ins)
                if (item is JsonObject i && i["id"]?.GetValue<string>() is { } id)
                    inputs[id] = JsonSerializer.SerializeToElement(i);

        return new LaunchFile(sourcePath, configurations, compounds, inputs);
    }

    /// <summary>Member configuration names of a compound (entries are strings or <c>{name, folder}</c>).</summary>
    public static IReadOnlyList<string> CompoundMembers(DebugConfiguration compound)
    {
        var names = new List<string>();
        if (compound.Properties.TryGetValue("configurations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String) names.Add(e.GetString()!);
                else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) names.Add(n.GetString()!);
            }
        return names;
    }

    public static bool CompoundStopsAll(DebugConfiguration compound) =>
        compound.Properties.TryGetValue("stopAll", out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The file layout VS Code writes for a fresh launch.json (header comments + version + configurations).</summary>
    public static string Render(IReadOnlyList<DebugConfiguration> configurations)
    {
        var array = new JsonArray();
        foreach (var c in configurations)
        {
            var o = new JsonObject();
            foreach (var (k, v) in c.Properties) o[k] = VariableResolver.ToNode(v);
            array.Add(o);
        }
        var body = array.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 });
        var indented = string.Join("\n    ", body.Split('\n').Select(l => l.TrimEnd('\r')));
        return
            "{\n" +
            "    // Use IntelliSense to learn about possible attributes.\n" +
            "    // Hover to view descriptions of existing attributes.\n" +
            "    // For more information, visit: https://go.microsoft.com/fwlink/?linkid=830387\n" +
            $"    \"version\": \"{VersionHeader}\",\n" +
            $"    \"configurations\": {indented}\n" +
            "}\n";
    }
}
