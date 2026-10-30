using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Lsp.Runtime;

/// <summary>
/// Substitutes <c>${workspaceFolder}</c> (the server's root directory) and <c>${userHome}</c> in a definition's args and
/// <c>initializationOptions</c>, so a definition can point a server at project-local tools (the Vue server's TypeScript SDK in
/// <c>node_modules</c>, for example), and in its <c>env</c> also <c>${env:NAME}</c> and a leading <c>~</c> (<see cref="ExpandEnvironment(IReadOnlyDictionary{string, string}?, string?)"/>). Unknown variables are left as written. Values without a <c>${</c> are returned unchanged (no copy).
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

    /// <summary>
    /// A definition's <c>env</c> (lsp-9): besides <c>${workspaceFolder}</c> and <c>${userHome}</c>, <c>${env:NAME}</c> is CsCode's own value of NAME
    /// (empty when unset, as in VS Code) and a leading <c>~</c> (<c>~</c> or <c>~/…</c>) is the home directory, so
    /// <c>"PATH": "~/.volta/bin:${env:PATH}"</c> extends the PATH instead of replacing it. The same dictionary is returned when nothing changes.
    /// The language client applies it on macOS only (Linux and Windows keep passing env values as written).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ExpandEnvironment(IReadOnlyDictionary<string, string>? env, string? workspaceFolder) =>
        ExpandEnvironment(env, workspaceFolder, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static IReadOnlyDictionary<string, string>? ExpandEnvironment(IReadOnlyDictionary<string, string>? env, string? workspaceFolder,
        Func<string, string?> environment, string home)
    {
        if (env is null || !env.Values.Any(v => v.Contains("${", StringComparison.Ordinal) || v.StartsWith('~'))) return env;
        var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in env) expanded[k] = ExpandEnvironmentValue(v, workspaceFolder, environment, home);
        return expanded;
    }

    private static string ExpandEnvironmentValue(string value, string? workspaceFolder, Func<string, string?> environment, string home)
    {
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)) value = home + value[1..];
        if (!value.Contains("${", StringComparison.Ordinal)) return value;
        if (workspaceFolder is not null) value = value.Replace("${workspaceFolder}", Path.TrimEndingDirectorySeparator(workspaceFolder), StringComparison.Ordinal);
        value = value.Replace("${userHome}", home, StringComparison.Ordinal);
        // ${env:NAME} last, so a variable's value is inserted as it is (never expanded again)
        var text = new System.Text.StringBuilder(value.Length);
        var at = 0;
        while (true)
        {
            var start = value.IndexOf("${env:", at, StringComparison.Ordinal);
            var end = start < 0 ? -1 : value.IndexOf('}', start);
            if (end < 0) break;
            text.Append(value, at, start - at).Append(environment(value[(start + 6)..end]) ?? "");
            at = end + 1;
        }
        text.Append(value, at, value.Length - at);
        return text.ToString();
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
