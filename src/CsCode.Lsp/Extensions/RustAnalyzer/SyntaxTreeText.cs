using System.Text;
using System.Text.Json;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// <c>rust-analyzer/viewSyntaxTree</c> answers a JSON tree (since 2024: <c>{"type": "Node"|"Token", "kind", "start": [offset, line, column],
/// "end", "children"}</c>, which VS Code shows as a tree view); CsCode shows it as the indented text the older <c>syntaxTree</c> request gave:
/// <c>FN@12..40</c> per node, tokens with their range. Bounded at 200,000 lines (a huge file says where it was cut).
/// </summary>
internal static class SyntaxTreeText
{
    private const int MaxLines = 200_000;

    public static string Render(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1024 });
            var sb = new StringBuilder();
            var lines = 0;
            Node(doc.RootElement, 0, sb, ref lines);
            if (lines >= MaxLines) sb.AppendLine($"… (cut at {MaxLines:N0} lines)");
            return sb.ToString();
        }
        catch (JsonException)
        {
            return json; // an older server's text tree: shown as it is
        }
    }

    private static void Node(JsonElement e, int depth, StringBuilder sb, ref int lines)
    {
        if (lines >= MaxLines || e.ValueKind != JsonValueKind.Object) return;
        sb.Append(' ', depth * 2).Append(e.Str("kind") ?? "?").Append('@').Append(Offset(e, "start")).Append("..").Append(Offset(e, "end")).Append('\n');
        lines++;
        if (e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Node(child, depth + 1, sb, ref lines);
    }

    private static string Offset(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].TryGetInt32(out var offset) ? offset.ToString() : "?";
}
