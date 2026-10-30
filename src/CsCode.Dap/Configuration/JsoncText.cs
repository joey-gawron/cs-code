using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsCode.Dap.Configuration;

/// <summary>
/// One edit of a text: <see cref="Text"/> replaces the <see cref="Length"/> characters at <see cref="Position"/> (only whitespace is ever
/// replaced); the first new element starts at <see cref="ElementOffset"/> in the result.
/// </summary>
internal readonly record struct JsoncInsertion(int Position, int Length, string Text, int ElementOffset)
{
    public string ApplyTo(string original) => string.Concat(original.AsSpan(0, Position), Text, original.AsSpan(Position + Length));
}

/// <summary>
/// Minimal edits of a JSONC document (launch.json, tasks.json) that keep everything the user wrote — comments, formatting, trailing commas —
/// as it is: elements are appended to an array property of the root object (VS Code's "Add Configuration…" and the C# assets' tasks.json
/// merge do the same through jsonc-parser).
/// </summary>
internal static class JsoncText
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "<node_internals>/**" as written
    };

    /// <summary>
    /// The insertion that appends <paramref name="elements"/> to the root object's array <paramref name="property"/> (created when missing),
    /// or null when the text's root is not an object, <paramref name="property"/> is not an array, or the text is not well-formed enough to edit.
    /// </summary>
    public static JsoncInsertion? AppendToArray(string text, string property, IReadOnlyList<JsonNode> elements)
    {
        if (elements.Count == 0) return null;
        var s = new Scanner(text);
        s.SkipTrivia();
        if (s.AtEnd || s.Current != '{') return null;
        var rootOpen = s.Pos++;
        var lastRoot = rootOpen; // last significant character directly inside the root (end of a value, or a comma)
        int firstPropertyStart = -1, propertyStart = -1, arrayOpen = -1, arrayClose = -1, lastInArray = -1, firstElementStart = -1;
        while (true)
        {
            s.SkipTrivia();
            if (s.AtEnd) return null;
            var c = s.Current;
            if (c == '}') break;
            if (c == ',') { lastRoot = s.Pos++; continue; }
            if (c != '"') return null;
            var nameStart = s.Pos;
            if (firstPropertyStart < 0) firstPropertyStart = nameStart;
            var name = s.ReadString();
            if (name is null) return null;
            s.SkipTrivia();
            if (s.AtEnd || s.Current != ':') return null;
            s.Pos++;
            s.SkipTrivia();
            if (s.AtEnd) return null;
            if (name == property && propertyStart < 0)
            {
                if (s.Current != '[') return null;
                propertyStart = nameStart;
                arrayOpen = s.Pos++;
                while (true)
                {
                    s.SkipTrivia();
                    if (s.AtEnd) return null;
                    if (s.Current == ']') { arrayClose = s.Pos++; break; }
                    if (s.Current == ',') { lastInArray = s.Pos++; continue; }
                    if (firstElementStart < 0) firstElementStart = s.Pos;
                    if (!s.SkipValue()) return null;
                    lastInArray = s.Pos - 1;
                }
            }
            else if (!s.SkipValue()) return null;
            lastRoot = s.Pos - 1;
        }

        var unit = Unit(text, firstPropertyStart);
        if (propertyStart < 0)
        {
            // no such property: add it after the root's last member
            var elementIndent = unit + unit;
            var body = string.Join(",\n" + elementIndent, elements.Select(e => Render(e, elementIndent)));
            var head = (text[lastRoot] is '{' or ',' ? "" : ",") + "\n" + unit + JsonSerializer.Serialize(property) + ": [\n" + elementIndent;
            var insertion = head + body + "\n" + unit + "]" + (text[lastRoot] == '{' ? "\n" : "");
            return new JsoncInsertion(lastRoot + 1, 0, insertion, lastRoot + 1 + head.Length);
        }

        var propertyIndent = LineIndent(text, propertyStart);
        var indent = firstElementStart >= 0 && StartsLine(text, firstElementStart) ? LineIndent(text, firstElementStart) : propertyIndent + unit;
        var elementsText = string.Join(",\n" + indent, elements.Select(e => Render(e, indent)));
        if (lastInArray < 0)
        {
            // empty array: [] or [ ] (or only comments inside)
            var inside = text[(arrayOpen + 1)..arrayClose];
            var prefix = "\n" + indent;
            if (string.IsNullOrWhiteSpace(inside))
                return new JsoncInsertion(arrayOpen + 1, inside.Length, prefix + elementsText + "\n" + propertyIndent, arrayOpen + 1 + prefix.Length);
            return new JsoncInsertion(arrayOpen + 1, 0, prefix + elementsText, arrayOpen + 1 + prefix.Length);
        }
        var separator = (text[lastInArray] == ',' ? "" : ",") + "\n" + indent;
        return new JsoncInsertion(lastInArray + 1, 0, separator + elementsText, lastInArray + 1 + separator.Length);
    }

    /// <summary>One indentation unit: the root's first member's indentation (a tab or spaces), four spaces when it has none.</summary>
    private static string Unit(string text, int firstPropertyStart)
    {
        if (firstPropertyStart < 0 || !StartsLine(text, firstPropertyStart)) return "    ";
        var indent = LineIndent(text, firstPropertyStart);
        return indent.Length > 0 ? indent : "    ";
    }

    private static string LineIndent(string text, int position)
    {
        var start = position;
        while (start > 0 && text[start - 1] is not ('\n' or '\r')) start--;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }

    private static bool StartsLine(string text, int position)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (text[i] is '\n' or '\r') return true;
            if (text[i] is not (' ' or '\t')) return false;
        }
        return true;
    }

    private static string Render(JsonNode element, string indent) =>
        string.Join("\n" + indent, element.ToJsonString(Pretty).Split('\n').Select(l => l.TrimEnd('\r')));

    /// <summary>JSONC tokens: strings, comments (line and block), nested values.</summary>
    private sealed class Scanner(string text)
    {
        public int Pos;
        public bool AtEnd => Pos >= text.Length;
        public char Current => text[Pos];

        public void SkipTrivia()
        {
            while (Pos < text.Length)
            {
                var c = text[Pos];
                if (char.IsWhiteSpace(c) || c == '﻿') { Pos++; continue; }
                if (c == '/' && Pos + 1 < text.Length && text[Pos + 1] == '/')
                {
                    while (Pos < text.Length && text[Pos] is not ('\n' or '\r')) Pos++;
                    continue;
                }
                if (c == '/' && Pos + 1 < text.Length && text[Pos + 1] == '*')
                {
                    var end = text.IndexOf("*/", Pos + 2, StringComparison.Ordinal);
                    Pos = end < 0 ? text.Length : end + 2;
                    continue;
                }
                return;
            }
        }

        /// <summary>Reads a string token at <see cref="Pos"/> (which is a quote); null when it is not terminated.</summary>
        public string? ReadString()
        {
            var start = Pos;
            Pos++;
            while (Pos < text.Length)
            {
                var c = text[Pos];
                if (c == '\\') { Pos += 2; continue; }
                if (c is '\n' or '\r') return null;
                Pos++;
                if (c == '"')
                {
                    try { return JsonSerializer.Deserialize<string>(text.AsSpan(start, Pos - start)); }
                    catch (JsonException) { return null; }
                }
            }
            return null;
        }

        /// <summary>Skips one value (string, literal, number, object, array); false when the text ends or is malformed.</summary>
        public bool SkipValue()
        {
            if (AtEnd) return false;
            var c = Current;
            if (c == '"') return ReadString() is not null;
            if (c is '{' or '[')
            {
                var depth = 0;
                while (Pos < text.Length)
                {
                    SkipTrivia();
                    if (AtEnd) return false;
                    var d = Current;
                    if (d == '"') { if (ReadString() is null) return false; continue; }
                    Pos++;
                    if (d is '{' or '[') depth++;
                    else if (d is '}' or ']' && --depth == 0) return true;
                }
                return false;
            }
            var start = Pos;
            while (Pos < text.Length && !char.IsWhiteSpace(text[Pos]) && text[Pos] is not (',' or '}' or ']' or '/')) Pos++;
            return Pos > start;
        }
    }
}
