using System.Text;
using System.Text.Json;
using CsCode.Lsp.Extensions.RustAnalyzer;

namespace CsCode.Lsp.Extensions.Clangd;

/// <summary>
/// clangd's <c>textDocument/ast</c> answer (an <c>ASTNode</c>: <c>role</c>, <c>kind</c>, <c>detail</c>, <c>arcana</c>, <c>range</c>,
/// <c>children</c>) as indented text, one node per line, labelled as vscode-clangd's AST view labels them: the kind alone for expressions,
/// statements, declarations and template names, else "kind role"; then the detail and the node's 1-based lines.
/// At most <see cref="MaxNodes"/> nodes (a whole translation unit can be huge).
/// </summary>
internal static class AstText
{
    internal const int MaxNodes = 20_000;

    public static string Render(JsonElement root)
    {
        var sb = new StringBuilder();
        var count = 0;
        Append(sb, root, 0, ref count);
        if (count > MaxNodes) sb.Append("… (more nodes not shown)\n");
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, JsonElement node, int depth, ref int count)
    {
        if (node.ValueKind != JsonValueKind.Object || ++count > MaxNodes) return;
        sb.Append(' ', depth * 2).Append(Label(node.Str("role") ?? "", node.Str("kind") ?? "?"));
        if (node.Str("detail") is { Length: > 0 } detail) sb.Append(' ').Append(detail);
        if (node.TryGetProperty("range", out var range) && Line(range, "start") is { } from && Line(range, "end") is { } to)
            sb.Append(from == to ? $"  [line {from}]" : $"  [lines {from}-{to}]");
        sb.Append('\n');
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Append(sb, child, depth + 1, ref count);
    }

    internal static string Label(string role, string kind) =>
        role is "expression" or "statement" or "declaration" or "template name" or "" ? kind : kind + " " + role;

    private static int? Line(JsonElement range, string end) =>
        range.ValueKind == JsonValueKind.Object && range.TryGetProperty(end, out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? line.GetInt32() + 1 : null;
}
