using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Lsp.Runtime;

/// <summary>
/// Substitutes <c>${workspaceFolder}</c> (the server's root directory) and <c>${userHome}</c> in a definition's args and
/// <c>initializationOptions</c>, so a definition can point a server at project-local tools (the Vue server's TypeScript SDK in
/// <c>node_modules</c>, for example). Unknown variables are left as written. Values without a <c>${</c> are returned unchanged (no copy).
/// </summary>
public static class LaunchVariables
{
    public static string Expand(string value, string? workspaceFolder)
    {
        if (!value.Contains("${", StringComparison.Ordinal)) return value;
        var root = workspaceFolder is null ? null : Path.TrimEndingDirectorySeparator(workspaceFolder);
        if (root is not null) value = value.Replace("${workspaceFolder}", root, StringComparison.Ordinal);
        return value.Replace("${userHome}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> Expand(IReadOnlyList<string> args, string? workspaceFolder) =>
        args.Any(a => a.Contains("${", StringComparison.Ordinal)) ? args.Select(a => Expand(a, workspaceFolder)).ToArray() : args;

    public static JsonElement? Expand(JsonElement? options, string? workspaceFolder)
    {
        if (options is not { } o || !o.GetRawText().Contains("${", StringComparison.Ordinal)) return options;
        var node = JsonNode.Parse(o.GetRawText());
        if (node is JsonValue top && top.TryGetValue<string>(out var single)) return JsonSerializer.SerializeToElement(Expand(single, workspaceFolder));
        Rewrite(node, workspaceFolder);
        return node is null ? options : JsonSerializer.SerializeToElement(node);
    }

    // Containers are rewritten in place; only string leaves are replaced (a node cannot be re-assigned to the parent that already holds it).
    private static void Rewrite(JsonNode? node, string? root)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s)) obj[key] = Expand(s, root);
                else Rewrite(obj[key], root);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = Expand(s, root);
                else Rewrite(arr[i], root);
            }
        }
    }
}
