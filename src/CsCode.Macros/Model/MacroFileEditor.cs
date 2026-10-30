using System.Text;
using System.Text.Json;

namespace CsCode.Macros;

/// <summary>macros.json cannot be changed safely (it has errors): the user fixes it first.</summary>
public sealed class MacroFileException(string message) : Exception(message);

/// <summary>
/// Changes macros.json the way a careful person would edit it: one macro object is replaced or appended (or removed) as text, everything else
/// (comments, formatting, other properties, the order of the other macros) stays as the user wrote it. Pure; runs off the UI thread.
/// </summary>
public static class MacroFileEditor
{
    private static readonly JsonReaderOptions s_reader = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    /// <summary>The file with <paramref name="macro"/> added, or replacing the macro of the same name. A missing or empty file gets the guide too.</summary>
    /// <exception cref="MacroFileException">The existing text is not valid JSON (or not an object): it is left for the user to fix.</exception>
    public static string Upsert(string? text, Macro macro)
    {
        if (string.IsNullOrWhiteSpace(text)) return MacroJson.FormatFile([macro]);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var bytes = Encoding.UTF8.GetBytes(text);
        var map = Scan(bytes);
        if (map.Macros is null)
        {
            // no "macros" property yet: add it as the last property of the root object, after whatever follows the last property (a comment on
            // its line stays there). The separating comma goes right after the last property's value: after a comment it would be inside it.
            var end = LastNonSpace(bytes, map.RootEnd) + 1;
            var property = $"{newline}  \"macros\": [{newline}    {MacroJson.FormatMacro(macro, "    ", "  ", newline)}{newline}  ]{newline}";
            var tail = property + Encoding.UTF8.GetString(bytes, map.RootEnd, bytes.Length - map.RootEnd);
            if (map.LastValueEnd < 0 || HasComma(bytes, map.LastValueEnd, map.RootEnd)) return Encoding.UTF8.GetString(bytes, 0, end) + tail;
            return Encoding.UTF8.GetString(bytes, 0, map.LastValueEnd) + "," + Encoding.UTF8.GetString(bytes, map.LastValueEnd, end - map.LastValueEnd) + tail;
        }
        var array = map.Macros;
        if (array.Items.FirstOrDefault(i => i.Name == macro.Name) is { } existing)
        {
            var indent = IndentOf(bytes, existing.Start);
            return Splice(bytes, existing.Start, existing.End, MacroJson.FormatMacro(macro, indent, Unit(indent, IndentOf(bytes, array.Start)), newline));
        }
        if (array.Items.Count == 0)
        {
            var outer = IndentOf(bytes, array.Start);
            var indent = outer + "  ";
            return Splice(bytes, array.Start + 1, array.End, $"{newline}{indent}{MacroJson.FormatMacro(macro, indent, "  ", newline)}{newline}{outer}");
        }
        var last = array.Items[^1];
        var itemIndent = IndentOf(bytes, last.Start);
        var trailingComma = HasComma(bytes, last.End, array.End);
        var item = newline + itemIndent + MacroJson.FormatMacro(macro, itemIndent, Unit(itemIndent, IndentOf(bytes, array.Start)), newline) + (trailingComma ? "," : "");
        var at = trailingComma ? CommaAfter(bytes, last.End, array.End) + 1 : last.End;
        // a comment on the rest of the last item's line stays with that item: the new one goes after the end of the line
        var lineEnd = Array.IndexOf(bytes, (byte)'\n', at, array.End - at);
        if (lineEnd > 0 && OnlyCommentOrSpace(bytes, at, lineEnd))
        {
            if (bytes[lineEnd - 1] == (byte)'\r') lineEnd--;
            return trailingComma ? Splice(bytes, lineEnd, lineEnd, item) : Splice2(bytes, at, ",", lineEnd, item);
        }
        // otherwise right after the last item (or after its trailing comma), keeping whatever followed it (the closing bracket's line)
        return Splice(bytes, at, at, (trailingComma ? "" : ",") + item);
    }

    private static bool OnlyCommentOrSpace(byte[] bytes, int from, int to)
    {
        var i = from;
        while (i < to && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r') i++;
        return i == to || (i + 1 < to && bytes[i] == (byte)'/' && bytes[i + 1] == (byte)'/');
    }

    private static string Splice2(byte[] bytes, int first, string firstInsert, int second, string secondInsert) =>
        Encoding.UTF8.GetString(bytes, 0, first) + firstInsert + Encoding.UTF8.GetString(bytes, first, second - first) + secondInsert +
        Encoding.UTF8.GetString(bytes, second, bytes.Length - second);

    /// <summary>The file without the macro <paramref name="name"/> (unchanged when there is none).</summary>
    public static string Remove(string text, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var map = Scan(bytes);
        if (map.Macros is not { } array) return text;
        var index = array.Items.FindIndex(i => i.Name == name);
        if (index < 0) return text;
        var item = array.Items[index];
        // remove the item with the comma that separates it from its neighbour, and the line it stood on
        int start, end;
        if (index < array.Items.Count - 1)
        {
            start = LineStartOrSelf(bytes, item.Start);
            end = LineStartOrSelf(bytes, array.Items[index + 1].Start);
        }
        else if (index > 0)
        {
            var previous = array.Items[index - 1];
            start = previous.End;
            end = HasComma(bytes, item.End, array.End) ? CommaAfter(bytes, item.End, array.End) + 1 : item.End;
        }
        else
        {
            start = array.Start + 1;
            end = array.End;
        }
        return Splice(bytes, start, end, "");
    }

    // ------------------------------------------------------------------------------------------------------------ scanning

    private sealed record Item(int Start, int End, string? Name);
    private sealed record ArraySpan(int Start, int End, List<Item> Items);
    /// <param name="LastValueEnd">Where the root object's last property value ends (-1: the object has no property).</param>
    private sealed record FileMap(int RootEnd, ArraySpan? Macros, int LastValueEnd);

    private static FileMap Scan(byte[] bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, s_reader);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new MacroFileException($"{MacroCommands.FileName} must contain an object with a \"macros\" array: fix it first.");
            ArraySpan? macros = null;
            var lastValueEnd = -1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    var rootEnd = (int)reader.TokenStartIndex;
                    if (reader.Read()) throw new MacroFileException($"{MacroCommands.FileName} has text after its closing brace: fix it first.");
                    return new FileMap(rootEnd, macros, lastValueEnd);
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var isMacros = reader.ValueTextEquals("macros"u8);
                reader.Read();
                if (!isMacros || reader.TokenType != JsonTokenType.StartArray)
                {
                    if (isMacros) throw new MacroFileException("\"macros\" in " + MacroCommands.FileName + " is not an array: fix it first.");
                    reader.Skip();
                    lastValueEnd = (int)reader.BytesConsumed; // the end of the value (comments after it are not read yet)
                    continue;
                }
                var start = (int)reader.TokenStartIndex;
                var items = new List<Item>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var itemStart = (int)reader.TokenStartIndex;
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        items.Add(new Item(itemStart, (int)reader.BytesConsumed, null));
                        continue;
                    }
                    string? name = null;
                    var depth = reader.CurrentDepth;
                    while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == depth + 1 && reader.ValueTextEquals("name"u8))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String) name = reader.GetString();
                            else reader.Skip();
                        }
                        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                    }
                    items.Add(new Item(itemStart, (int)reader.BytesConsumed, name));
                }
                macros = new ArraySpan(start, (int)reader.TokenStartIndex, items);
                lastValueEnd = (int)reader.BytesConsumed;
            }
            throw new MacroFileException($"{MacroCommands.FileName} ends too early: fix it first.");
        }
        catch (JsonException ex)
        {
            throw new MacroFileException($"{MacroCommands.FileName} is not valid JSON ({ex.Message}): fix it first.");
        }
    }

    private static string Splice(byte[] bytes, int start, int end, string insert) =>
        Encoding.UTF8.GetString(bytes, 0, start) + insert + Encoding.UTF8.GetString(bytes, end, bytes.Length - end);

    /// <summary>The whitespace before <paramref name="at"/> on its line (the item's indentation), or "" when something else precedes it.</summary>
    private static string IndentOf(byte[] bytes, int at)
    {
        var i = at;
        while (i > 0 && bytes[i - 1] is (byte)' ' or (byte)'\t') i--;
        return i == 0 || bytes[i - 1] == (byte)'\n' ? Encoding.UTF8.GetString(bytes, i, at - i) : "";
    }

    private static string Unit(string itemIndent, string arrayIndent) =>
        itemIndent.Length > arrayIndent.Length && itemIndent.StartsWith(arrayIndent, StringComparison.Ordinal) ? itemIndent[arrayIndent.Length..] : "  ";

    private static int LineStartOrSelf(byte[] bytes, int at)
    {
        var i = at;
        while (i > 0 && bytes[i - 1] is (byte)' ' or (byte)'\t') i--;
        return i == 0 || bytes[i - 1] == (byte)'\n' ? i : at;
    }

    private static int LastNonSpace(byte[] bytes, int before)
    {
        // skips whitespace only: a comment right before the brace keeps the insert after it
        var i = before - 1;
        while (i >= 0 && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i--;
        return i;
    }

    /// <summary>Whether a ',' (outside comments) lies between <paramref name="from"/> and <paramref name="to"/>.</summary>
    private static bool HasComma(byte[] bytes, int from, int to) => CommaAfter(bytes, from, to) >= 0;

    private static int CommaAfter(byte[] bytes, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (bytes[i] == (byte)',') return i;
            if (bytes[i] == (byte)'/' && i + 1 < to)
            {
                if (bytes[i + 1] == (byte)'/') { while (i < to && bytes[i] != (byte)'\n') i++; }
                else if (bytes[i + 1] == (byte)'*') { i += 2; while (i + 1 < to && !(bytes[i] == (byte)'*' && bytes[i + 1] == (byte)'/')) i++; i++; }
            }
        }
        return -1;
    }
}
