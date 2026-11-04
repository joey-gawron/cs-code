using System.Text;
using System.Text.Json;

namespace CsCode.SpellCheck.Settings;

/// <summary>
/// Adds words to a string array of a JSON (with comments) file — a project's <c>cspell.json</c> <c>words</c> — by inserting text, so the
/// file's comments, order and formatting stay as they were. The array is created (first member of the root object) when it is missing.
/// </summary>
public static class JsonWordListEditor
{
    /// <summary>
    /// <paramref name="json"/> with <paramref name="words"/> appended to the root object's <paramref name="property"/> array (words already there,
    /// in any case, are skipped). Throws <see cref="JsonException"/> when the text is not a JSON object.
    /// </summary>
    public static string AddWords(string json, string property, IEnumerable<string> words)
    {
        var bom = json.Length > 0 && json[0] == '﻿';
        var text = bom ? json[1..] : json;
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var existing = new List<string>();
        int rootOpen = -1, arrayOpen = -1, arrayClose = -1, lastValueEnd = -1, firstValueStart = -1, rootClose = -1;
        var inArray = false;
        var depth = 0;
        var nextIsTarget = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (depth == 0) rootOpen = (int)reader.TokenStartIndex;
                    depth++;
                    nextIsTarget = false;
                    break;
                case JsonTokenType.EndObject:
                    depth--;
                    if (depth == 0) rootClose = (int)reader.TokenStartIndex;
                    break;
                case JsonTokenType.PropertyName:
                    nextIsTarget = depth == 1 && reader.ValueTextEquals(property);
                    break;
                case JsonTokenType.StartArray:
                    if (nextIsTarget && arrayOpen < 0)
                    {
                        arrayOpen = (int)reader.TokenStartIndex;
                        inArray = true;
                    }
                    depth++;
                    nextIsTarget = false;
                    break;
                case JsonTokenType.EndArray:
                    depth--;
                    if (inArray && depth == 1)
                    {
                        arrayClose = (int)reader.TokenStartIndex;
                        inArray = false;
                    }
                    break;
                default:
                    if (inArray && depth == 2)
                    {
                        if (firstValueStart < 0) firstValueStart = (int)reader.TokenStartIndex;
                        lastValueEnd = (int)reader.BytesConsumed;
                        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } s) existing.Add(s);
                    }
                    nextIsTarget = false;
                    break;
            }
        }
        if (rootOpen < 0 || rootClose < 0) throw new JsonException("The file is not a JSON object.");

        var toAdd = new List<string>();
        foreach (var w in words)
            if (!existing.Contains(w, StringComparer.OrdinalIgnoreCase) && !toAdd.Contains(w, StringComparer.OrdinalIgnoreCase)) toAdd.Add(w);
        if (toAdd.Count == 0) return json;

        string insertion;
        int at;
        if (arrayOpen >= 0 && lastValueEnd > arrayOpen)
        {
            // after the last element, on a line of its own when the array is written one element per line
            var multiline = Array.IndexOf(bytes, (byte)'\n', arrayOpen, firstValueStart - arrayOpen) >= 0;
            var separator = multiline ? "\n" + IndentOf(bytes, firstValueStart) : " ";
            insertion = string.Concat(toAdd.Select(w => "," + separator + Quote(w)));
            at = lastValueEnd;
        }
        else if (arrayOpen >= 0)
        {
            insertion = string.Join(", ", toAdd.Select(Quote));
            at = arrayOpen + 1;
        }
        else
        {
            // a new first member of the root object
            var empty = IsOnlyWhitespace(bytes, rootOpen + 1, rootClose);
            var indent = empty ? "  " : MemberIndent(bytes, rootOpen);
            insertion = "\n" + indent + Quote(property) + ": [" + string.Join(", ", toAdd.Select(Quote)) + "]" + (empty ? "\n" : ",");
            at = rootOpen + 1;
        }
        var result = Encoding.UTF8.GetString(bytes, 0, at) + insertion + Encoding.UTF8.GetString(bytes, at, bytes.Length - at);
        return bom ? "﻿" + result : result;
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    private static string IndentOf(byte[] bytes, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && bytes[lineStart - 1] != (byte)'\n') lineStart--;
        var end = lineStart;
        while (end < index && bytes[end] is (byte)' ' or (byte)'\t') end++;
        return Encoding.UTF8.GetString(bytes, lineStart, end - lineStart);
    }

    /// <summary>The indentation of the root object's first member (two spaces when it shares the brace's line).</summary>
    private static string MemberIndent(byte[] bytes, int rootOpen)
    {
        var i = rootOpen + 1;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r') i++;
        if (i >= bytes.Length || bytes[i] != (byte)'\n') return "  ";
        i++;
        var start = i;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t') i++;
        return i > start ? Encoding.UTF8.GetString(bytes, start, i - start) : "  ";
    }

    private static bool IsOnlyWhitespace(byte[] bytes, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (bytes[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }
}
