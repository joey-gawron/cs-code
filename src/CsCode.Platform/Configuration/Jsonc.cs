using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CsCode.Platform.Configuration;

public sealed class JsoncException(string message, int position) : FormatException($"{message} (at offset {position})")
{
    public int Position { get; } = position;
}

internal abstract class JsoncNode
{
    /// <summary>Offsets into the source text: [Start, End).</summary>
    public int Start { get; init; }
    public int End { get; set; }
}

internal sealed class JsoncMember
{
    public required string Name { get; init; }
    public int NameStart { get; init; }
    public required JsoncNode Value { get; init; }
    /// <summary>Offset of the comma that follows this member, or -1 (last member without a trailing comma).</summary>
    public int CommaPos { get; set; } = -1;
}

internal sealed class JsoncObject : JsoncNode
{
    public List<JsoncMember> Members { get; } = new();
    public int CloseBrace => End - 1;
}

internal sealed class JsoncArray : JsoncNode
{
    public List<JsoncNode> Items { get; } = new();
}

internal enum JsoncPrimitiveKind { String, Number, True, False, Null }

internal sealed class JsoncPrimitive : JsoncNode
{
    public JsoncPrimitiveKind Kind { get; init; }
    public string? StringValue { get; init; }
    public string RawText { get; init; } = "";
}

/// <summary>
/// Parser for JSON with comments (<c>//</c>, <c>/* */</c>) and trailing commas — the dialect of VS Code's settings.json,
/// keybindings.json and launch.json. Unlike <see cref="System.Text.Json"/> it records the source SPAN of every value and the position of
/// every comma, which is exactly what the minimal-edit writer needs. Duplicate keys are kept (last one wins when converted).
/// </summary>
internal sealed class JsoncParser
{
    private readonly string _s;
    private int _i;
    private int _depth;
    private const int MaxDepth = 200;

    private JsoncParser(string text) => _s = text;

    /// <summary>Parses a document. Returns null when it holds only whitespace/comments. Throws <see cref="JsoncException"/> on malformed input.</summary>
    public static JsoncNode? Parse(string text)
    {
        var p = new JsoncParser(text);
        p.SkipTrivia();
        if (p._i >= text.Length) return null;
        var root = p.ParseValue();
        p.SkipTrivia();
        if (p._i < text.Length) throw new JsoncException("Unexpected content after the top-level value", p._i);
        return root;
    }

    private void SkipTrivia()
    {
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c is ' ' or '\t' or '\r' or '\n' or '﻿') { _i++; continue; }
            if (c == '/' && _i + 1 < _s.Length)
            {
                if (_s[_i + 1] == '/')
                {
                    _i += 2;
                    while (_i < _s.Length && _s[_i] is not ('\n' or '\r')) _i++;
                    continue;
                }
                if (_s[_i + 1] == '*')
                {
                    var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0) throw new JsoncException("Unterminated block comment", _i);
                    _i = end + 2;
                    continue;
                }
            }
            break;
        }
    }

    private JsoncNode ParseValue()
    {
        if (_i >= _s.Length) throw new JsoncException("Unexpected end of input", _i);
        if (++_depth > MaxDepth) throw new JsoncException("Document is nested too deeply", _i);
        try
        {
            return _s[_i] switch
            {
                '{' => ParseObject(),
                '[' => ParseArray(),
                '"' => ParseStringValue(),
                't' => ParseLiteral("true", JsoncPrimitiveKind.True),
                'f' => ParseLiteral("false", JsoncPrimitiveKind.False),
                'n' => ParseLiteral("null", JsoncPrimitiveKind.Null),
                '-' or (>= '0' and <= '9') => ParseNumber(),
                var c => throw new JsoncException($"Unexpected character '{c}'", _i),
            };
        }
        finally { _depth--; }
    }

    private JsoncObject ParseObject()
    {
        var obj = new JsoncObject { Start = _i };
        _i++; // {
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw new JsoncException("Unterminated object", obj.Start);
            if (_s[_i] == '}') { _i++; break; }
            if (_s[_i] != '"') throw new JsoncException("Expected a property name (a double-quoted string)", _i);

            var nameStart = _i;
            var name = ReadString();
            SkipTrivia();
            if (_i >= _s.Length || _s[_i] != ':') throw new JsoncException("Expected ':' after the property name", _i);
            _i++;
            SkipTrivia();
            var value = ParseValue();
            var member = new JsoncMember { Name = name, NameStart = nameStart, Value = value };
            obj.Members.Add(member);

            SkipTrivia();
            if (_i >= _s.Length) throw new JsoncException("Unterminated object", obj.Start);
            if (_s[_i] == ',') { member.CommaPos = _i; _i++; continue; }
            if (_s[_i] == '}') { _i++; break; }
            throw new JsoncException("Expected ',' or '}'", _i);
        }
        obj.End = _i;
        return obj;
    }

    private JsoncArray ParseArray()
    {
        var arr = new JsoncArray { Start = _i };
        _i++; // [
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw new JsoncException("Unterminated array", arr.Start);
            if (_s[_i] == ']') { _i++; break; }
            arr.Items.Add(ParseValue());
            SkipTrivia();
            if (_i >= _s.Length) throw new JsoncException("Unterminated array", arr.Start);
            if (_s[_i] == ',') { _i++; continue; }
            if (_s[_i] == ']') { _i++; break; }
            throw new JsoncException("Expected ',' or ']'", _i);
        }
        arr.End = _i;
        return arr;
    }

    private JsoncPrimitive ParseStringValue()
    {
        var start = _i;
        var value = ReadString();
        return new JsoncPrimitive { Start = start, End = _i, Kind = JsoncPrimitiveKind.String, StringValue = value, RawText = _s[start.._i] };
    }

    private string ReadString()
    {
        var start = _i;
        _i++; // opening quote
        StringBuilder? sb = null;
        var runStart = _i;
        while (true)
        {
            if (_i >= _s.Length) throw new JsoncException("Unterminated string", start);
            var c = _s[_i];
            if (c == '"')
            {
                var result = sb is null ? _s[runStart.._i] : sb.Append(_s, runStart, _i - runStart).ToString();
                _i++;
                return result;
            }
            if (c is '\n' or '\r') throw new JsoncException("Newline inside a string", _i);
            if (c == '\\')
            {
                sb ??= new StringBuilder();
                sb.Append(_s, runStart, _i - runStart);
                if (_i + 1 >= _s.Length) throw new JsoncException("Unterminated escape sequence", _i);
                var e = _s[_i + 1];
                switch (e)
                {
                    case '"': sb.Append('"'); _i += 2; break;
                    case '\\': sb.Append('\\'); _i += 2; break;
                    case '/': sb.Append('/'); _i += 2; break;
                    case 'b': sb.Append('\b'); _i += 2; break;
                    case 'f': sb.Append('\f'); _i += 2; break;
                    case 'n': sb.Append('\n'); _i += 2; break;
                    case 'r': sb.Append('\r'); _i += 2; break;
                    case 't': sb.Append('\t'); _i += 2; break;
                    case 'u':
                        if (_i + 5 >= _s.Length || !ushort.TryParse(_s.AsSpan(_i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            throw new JsoncException("Invalid \\u escape", _i);
                        sb.Append((char)code);
                        _i += 6;
                        break;
                    default: throw new JsoncException($"Invalid escape '\\{e}'", _i);
                }
                runStart = _i;
                continue;
            }
            _i++;
        }
    }

    private JsoncPrimitive ParseLiteral(string word, JsoncPrimitiveKind kind)
    {
        if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw new JsoncException($"Unexpected token (expected '{word}')", _i);
        var start = _i;
        _i += word.Length;
        if (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) throw new JsoncException("Unexpected token", start);
        return new JsoncPrimitive { Start = start, End = _i, Kind = kind, RawText = word };
    }

    private JsoncPrimitive ParseNumber()
    {
        var start = _i;
        if (_s[_i] == '-') _i++;
        var digitsStart = _i;
        while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
        if (_i == digitsStart) throw new JsoncException("Invalid number", start);
        if (_s[digitsStart] == '0' && _i - digitsStart > 1) throw new JsoncException("Leading zeros are not allowed in numbers", start);
        if (_i < _s.Length && _s[_i] == '.')
        {
            _i++;
            var f = _i;
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
            if (_i == f) throw new JsoncException("Invalid number", start);
        }
        if (_i < _s.Length && _s[_i] is 'e' or 'E')
        {
            _i++;
            if (_i < _s.Length && _s[_i] is '+' or '-') _i++;
            var f = _i;
            while (_i < _s.Length && char.IsAsciiDigit(_s[_i])) _i++;
            if (_i == f) throw new JsoncException("Invalid number", start);
        }
        return new JsoncPrimitive { Start = start, End = _i, Kind = JsoncPrimitiveKind.Number, RawText = _s[start.._i] };
    }

    // ---- conversion --------------------------------------------------------------------------------------------------------------

    /// <summary>Converts a parsed tree into a mutable <see cref="JsonNode"/> tree. Duplicate keys: the last one wins.</summary>
    public static JsonNode? ToJsonNode(JsoncNode? node) => node switch
    {
        null => null,
        JsoncObject o => ToJsonObject(o),
        JsoncArray a => new JsonArray(a.Items.Select(ToJsonNode).ToArray()),
        JsoncPrimitive { Kind: JsoncPrimitiveKind.String } p => JsonValue.Create(p.StringValue),
        JsoncPrimitive { Kind: JsoncPrimitiveKind.True } => JsonValue.Create(true),
        JsoncPrimitive { Kind: JsoncPrimitiveKind.False } => JsonValue.Create(false),
        JsoncPrimitive { Kind: JsoncPrimitiveKind.Null } => null,
        JsoncPrimitive p => JsonNode.Parse(p.RawText), // keep the exact number text (no double round trip)
        _ => null,
    };

    public static JsonObject ToJsonObject(JsoncObject o)
    {
        var result = new JsonObject();
        foreach (var m in o.Members) result[m.Name] = ToJsonNode(m.Value);
        return result;
    }
}
