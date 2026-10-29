using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Platform.Configuration;

/// <summary>
/// Edits a JSONC settings document IN PLACE with the smallest possible text change, so a user's comments, ordering, blank lines and
/// indentation survive a programmatic update (re-serialising the whole file would destroy all of them):
/// <list type="bullet">
/// <item>existing key (flat <c>"a.b"</c> or nested <c>{"a":{"b":..}}</c>): only that value's span is replaced;</item>
/// <item>new key: one member is inserted before the closing brace using the file's own indentation, EOL and comma style
/// (a trailing-comma file stays a trailing-comma file); a missing parent object gets the member added to it;</item>
/// <item><c>null</c> value: the member is removed together with its comma/line.</item>
/// </list>
/// </summary>
internal static class JsoncEditor
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly record struct Edit(int Start, int Length, string Text);

    /// <summary>Returns <paramref name="text"/> with <paramref name="key"/> set to <paramref name="value"/> (or removed when null).</summary>
    /// <exception cref="JsoncException">The document is not valid JSONC, or its root is not an object.</exception>
    public static string Set(string text, string key, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var root = JsoncParser.Parse(text);

        if (root is null)
        {
            if (value is null) return text;
            return CreateDocument(text, key, value, eol);
        }
        if (root is not JsoncObject rootObj) throw new JsoncException("The settings file must contain a JSON object at the top level", root.Start);

        var unit = DetectIndentUnit(text, rootObj);
        var segments = key.Split('.');
        var loc = Locate(rootObj, segments, 0);

        var edits = new List<Edit>(2);
        if (loc.Member is { } existing)
        {
            if (value is null) RemoveMember(text, loc.Container, existing, edits);
            else edits.Add(new Edit(existing.Value.Start, existing.Value.End - existing.Value.Start,
                    Format(value, LineIndent(text, existing.NameStart), unit, eol)));
        }
        else
        {
            if (value is null) return text; // nothing to remove
            InsertMember(text, loc.Container, loc.RemainingName, value, unit, eol, edits);
        }
        return Apply(text, edits);
    }

    // ---- locating ----------------------------------------------------------------------------------------------------------------

    private readonly record struct Location(JsoncObject Container, JsoncMember? Member, string RemainingName);

    /// <summary>
    /// Finds where the dotted <c>segments[i..]</c> lives: an exact member (flat dotted name or nested chain), or the deepest existing object
    /// into which a new member named by the remaining segments should be inserted.
    /// </summary>
    private static Location Locate(JsoncObject obj, string[] segments, int i)
    {
        var remaining = segments.Length - i;
        Location? deepestInsert = null;

        // Longest name first: "editor.minimap.enabled" as one flat member beats descending into "editor".
        for (var len = remaining; len >= 1; len--)
        {
            var name = len == 1 ? segments[i] : string.Join('.', segments, i, len);
            var member = FindLast(obj, name);
            if (member is null) continue;

            if (len == remaining) return new Location(obj, member, "");
            if (member.Value is JsoncObject child)
            {
                var inner = Locate(child, segments, i + len);
                if (inner.Member is not null) return inner;
                deepestInsert ??= inner;
            }
        }
        return deepestInsert ?? new Location(obj, null, string.Join('.', segments, i, remaining));
    }

    private static JsoncMember? FindLast(JsoncObject obj, string name)
    {
        for (var k = obj.Members.Count - 1; k >= 0; k--)
            if (obj.Members[k].Name == name) return obj.Members[k];
        return null;
    }

    // ---- inserting ---------------------------------------------------------------------------------------------------------------

    private static string CreateDocument(string existing, string key, JsonNode value, string eol)
    {
        var member = MemberText(key, value, "    ", "    ", eol);
        var body = "{" + eol + "    " + member + eol + "}" + eol;
        if (existing.Trim().Length == 0) return body;                  // blank file
        return (existing.EndsWith('\n') ? existing : existing + eol) + body; // file holding only comments: keep them, add the object after
    }

    private static void InsertMember(string text, JsoncObject obj, string name, JsonNode value, string unit, string eol, List<Edit> edits)
    {
        var open = obj.Start;
        var close = obj.CloseBrace;
        var baseIndent = LineIndent(text, open);

        if (obj.Members.Count == 0)
        {
            var memberIndent = baseIndent + unit;
            var member = MemberText(name, value, memberIndent, unit, eol);
            var inner = text.AsSpan(open + 1, close - open - 1);
            if (inner.IsWhiteSpace())
            {
                edits.Add(new Edit(open + 1, close - open - 1, eol + memberIndent + member + eol + baseIndent));
            }
            else
            {
                // The empty object contains comments: keep them, put the member on its own line just before the closing brace.
                var closeLineStart = LineStart(text, close);
                if (text.AsSpan(closeLineStart, close - closeLineStart).IsWhiteSpace())
                    edits.Add(new Edit(closeLineStart, 0, memberIndent + member + eol));
                else
                    edits.Add(new Edit(close, 0, eol + memberIndent + member + eol + baseIndent));
            }
            return;
        }

        var last = obj.Members[^1];
        var hasTrailingComma = last.CommaPos >= 0;
        var multiline = text.AsSpan(open, close - open).IndexOfAny('\r', '\n') >= 0;

        if (!multiline)
        {
            // {"a": 1}  ->  {"a": 1, "b": 2}   (single-line style stays single-line)
            var member = MemberText(name, value, baseIndent, unit, eol);
            if (hasTrailingComma) edits.Add(new Edit(last.CommaPos + 1, 0, " " + member + ","));
            else edits.Add(new Edit(last.Value.End, 0, ", " + member));
            return;
        }

        var indent = LineIndent(text, last.NameStart);
        var memberText = MemberText(name, value, indent, unit, eol);
        var scanFrom = Math.Max(last.Value.End, last.CommaPos + 1);
        var lineEnd = LineEnd(text, scanFrom);
        var insertAt = Math.Min(lineEnd, close); // ragged `"a": 1 }` on one line: insert right before the brace

        var newLine = eol + indent + memberText + (hasTrailingComma ? "," : "");
        if (hasTrailingComma)
        {
            edits.Add(new Edit(insertAt, 0, newLine));
        }
        else if (last.Value.End == insertAt)
        {
            // One edit: two zero-length inserts at the same offset would apply in the wrong order and put the comma after the new member.
            edits.Add(new Edit(insertAt, 0, "," + newLine));
        }
        else
        {
            // A trailing comment sits between the value and the end of its line: the comma must go right after the value, before the comment.
            edits.Add(new Edit(last.Value.End, 0, ","));
            edits.Add(new Edit(insertAt, 0, newLine));
        }
    }

    private static string MemberText(string name, JsonNode value, string memberIndent, string unit, string eol) =>
        JsonSerializer.Serialize(name, Relaxed) + ": " + Format(value, memberIndent, unit, eol);

    // ---- removing ----------------------------------------------------------------------------------------------------------------

    private static void RemoveMember(string text, JsoncObject obj, JsoncMember member, List<Edit> edits)
    {
        var idx = obj.Members.IndexOf(member);
        var prev = idx > 0 ? obj.Members[idx - 1] : null;
        var next = idx + 1 < obj.Members.Count ? obj.Members[idx + 1] : null;
        var afterEnd = Math.Max(member.Value.End, member.CommaPos + 1);

        var lineStart = LineStart(text, member.NameStart);
        var lineEnd = LineEnd(text, afterEnd);
        var onOwnLines = text.AsSpan(lineStart, member.NameStart - lineStart).IsWhiteSpace()
                         && lineEnd <= obj.CloseBrace
                         && IsOnlyTriviaOrComment(text, afterEnd, lineEnd);

        if (onOwnLines)
        {
            // Drop the whole line(s), including the line break and any trailing comment that belonged to the entry.
            var end = lineEnd;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
            edits.Add(new Edit(lineStart, end - lineStart, ""));
            // Removing the last member (which had no trailing comma) would leave a dangling comma on the previous one.
            if (next is null && member.CommaPos < 0 && prev is { CommaPos: >= 0 })
                edits.Add(new Edit(prev.CommaPos, 1, ""));
            return;
        }

        // Inline style: `{"a": 1, "b": 2}`
        if (next is not null) edits.Add(new Edit(member.NameStart, next.NameStart - member.NameStart, ""));
        else if (prev is not null) edits.Add(new Edit(prev.Value.End, afterEnd - prev.Value.End, ""));
        else edits.Add(new Edit(member.NameStart, afterEnd - member.NameStart, ""));
    }

    private static bool IsOnlyTriviaOrComment(string text, int from, int to)
    {
        var s = text.AsSpan(from, to - from).Trim();
        return s.IsEmpty || s.StartsWith("//") || (s.StartsWith("/*") && s.EndsWith("*/"));
    }

    // ---- formatting --------------------------------------------------------------------------------------------------------------

    /// <summary>Formats a value for insertion: scalars compact; objects/arrays pretty-printed in the file's own indentation style.</summary>
    internal static string Format(JsonNode? node, string baseIndent, string unit, string eol)
    {
        var sb = new StringBuilder();
        Write(sb, node, baseIndent, unit, eol);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, string indent, string unit, string eol)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{').Append(eol);
                var n = 0;
                foreach (var (k, v) in o)
                {
                    sb.Append(indent).Append(unit).Append(JsonSerializer.Serialize(k, Relaxed)).Append(": ");
                    Write(sb, v, indent + unit, unit, eol);
                    sb.Append(++n < o.Count ? "," : "").Append(eol);
                }
                sb.Append(indent).Append('}');
                break;
            case JsonArray a:
                if (a.Count == 0) { sb.Append("[]"); break; }
                if (a.All(static x => x is null or JsonValue))
                {
                    var inline = "[" + string.Join(", ", a.Select(static x => x?.ToJsonString(Relaxed) ?? "null")) + "]";
                    if (inline.Length <= 72) { sb.Append(inline); break; }
                }
                sb.Append('[').Append(eol);
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(indent).Append(unit);
                    Write(sb, a[i], indent + unit, unit, eol);
                    sb.Append(i + 1 < a.Count ? "," : "").Append(eol);
                }
                sb.Append(indent).Append(']');
                break;
            default: sb.Append(node.ToJsonString(Relaxed)); break;
        }
    }

    // ---- text helpers ------------------------------------------------------------------------------------------------------------

    private static string DetectIndentUnit(string text, JsoncObject root)
    {
        foreach (var m in root.Members)
        {
            var ls = LineStart(text, m.NameStart);
            var ws = text.AsSpan(ls, m.NameStart - ls);
            if (ws.IsEmpty || !ws.IsWhiteSpace()) continue;
            return ws[0] == '\t' ? "\t" : ws.ToString();
        }
        return "    ";
    }

    private static int LineStart(string text, int pos)
    {
        var i = pos;
        while (i > 0 && text[i - 1] is not ('\n' or '\r')) i--;
        return i;
    }

    private static int LineEnd(string text, int pos)
    {
        var i = pos;
        while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
        return i;
    }

    private static string LineIndent(string text, int pos)
    {
        var ls = LineStart(text, pos);
        var i = ls;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return text.Substring(ls, i - ls);
    }

    private static string Apply(string text, List<Edit> edits)
    {
        if (edits.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(static e => e.Start).ThenByDescending(static e => e.Length))
            sb.Remove(e.Start, e.Length).Insert(e.Start, e.Text);
        return sb.ToString();
    }
}
