using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// rust-analyzer's <c>snippetTextEdit</c>: an assist's text edits may carry <c>"insertTextFormat": 2</c> and snippet syntax (<c>$0</c>,
/// <c>${1:name}</c>) where VS Code puts the caret or a placeholder. CsCode applies workspace edits as plain text, so such an edit's text is
/// made plain before it is mapped: tab stops vanish, placeholders keep their default text, escapes are undone. The caret is not moved to
/// <c>$0</c> (a known gap, docs/RUST.md).
/// </summary>
internal static class Snippets
{
    public static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "textDocument/codeAction", "codeAction/resolve" };

    /// <summary>The answer with every snippet text edit made plain (the same element when it has none).</summary>
    public static JsonElement MakePlain(JsonElement answer)
    {
        if (!answer.GetRawText().Contains("\"insertTextFormat\"", StringComparison.Ordinal)) return answer;
        var node = JsonNode.Parse(answer.GetRawText());
        if (node is null) return answer;
        Walk(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["insertTextFormat"] is JsonValue f && f.TryGetValue<int>(out var format) && format == 2 && o["newText"] is JsonValue t && t.TryGetValue<string>(out var text))
                {
                    o["newText"] = ToPlainText(text);
                    o.Remove("insertTextFormat");
                }
                foreach (var (_, child) in o.ToList())
                    if (child is not null) Walk(child);
                break;
            case JsonArray a:
                foreach (var child in a)
                    if (child is not null) Walk(child);
                break;
        }
    }

    /// <summary>
    /// Snippet text (TextMate/VS Code syntax) as the text it inserts: <c>$1</c> and <c>${2}</c> vanish, <c>${1:default}</c> and
    /// <c>${1|a,b|}</c> become their default (the first choice), <c>\$</c>, <c>\}</c> and <c>\\</c> are unescaped.
    /// </summary>
    public static string ToPlainText(string snippet)
    {
        var sb = new StringBuilder(snippet.Length);
        var i = 0;
        Append(snippet, ref i, sb, inPlaceholder: false);
        return sb.ToString();
    }

    private static void Append(string s, ref int i, StringBuilder sb, bool inPlaceholder)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] is '$' or '}' or '\\' or ',' or '|')
            {
                sb.Append(s[i + 1]);
                i += 2;
            }
            else if (c == '}' && inPlaceholder)
            {
                i++;
                return;
            }
            else if (c == '$' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
            {
                i++;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            }
            else if (c == '$' && i + 2 < s.Length && s[i + 1] == '{' && char.IsAsciiDigit(s[i + 2]))
            {
                i += 2;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                if (i < s.Length && s[i] == ':')
                {
                    i++;
                    Append(s, ref i, sb, inPlaceholder: true);
                }
                else if (i < s.Length && s[i] == '|')
                {
                    var end = s.IndexOf("|}", i + 1, StringComparison.Ordinal);
                    var choices = end < 0 ? "" : s[(i + 1)..end];
                    var comma = choices.IndexOf(',');
                    sb.Append(comma < 0 ? choices : choices[..comma]);
                    i = end < 0 ? s.Length : end + 2;
                }
                else if (i < s.Length && s[i] == '}') i++;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
    }
}
