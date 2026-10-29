using System.Globalization;
using System.Text.Json;

namespace CsCode.Platform.Context;

internal enum CtxKind : byte { Null, Bool, Number, String, Json, Other }

/// <summary>
/// A context value viewed without boxing or allocating: bool, number, string, a <see cref="JsonElement"/> (settings, JSON-ish values) or
/// any other object (collections for <c>in</c>). All when-clause comparisons work on this struct so evaluating a clause on every keystroke
/// allocates nothing.
/// </summary>
internal readonly struct CtxValue
{
    public readonly CtxKind Kind;
    public readonly bool Bool;
    public readonly double Number;
    public readonly string? Text;
    public readonly object? Other;
    public readonly JsonElement Json;

    private CtxValue(CtxKind kind, bool b = false, double n = 0, string? text = null, object? other = null, JsonElement json = default)
    {
        Kind = kind; Bool = b; Number = n; Text = text; Other = other; Json = json;
    }

    public static readonly CtxValue Null = default;
    public static CtxValue FromBool(bool b) => new(CtxKind.Bool, b: b);
    public static CtxValue FromNumber(double d) => new(CtxKind.Number, n: d);
    public static CtxValue FromString(string s) => new(CtxKind.String, text: s);
    public static CtxValue FromJson(JsonElement e) => e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? Null : new(CtxKind.Json, json: e);

    /// <summary>Maps a stored context object. Unboxing only: nothing here allocates.</summary>
    public static CtxValue From(object? o) => o switch
    {
        null => Null,
        bool b => FromBool(b),
        string s => FromString(s),
        int i => FromNumber(i),
        long l => FromNumber(l),
        double d => FromNumber(d),
        float f => FromNumber(f),
        short sh => FromNumber(sh),
        byte by => FromNumber(by),
        uint ui => FromNumber(ui),
        ulong ul => FromNumber(ul),
        decimal m => FromNumber((double)m),
        JsonElement je => FromJson(je),
        _ => new(CtxKind.Other, other: o),
    };

    /// <summary>JavaScript-style truthiness (what VS Code's <c>!!value</c> does), so bare-key clauses behave identically.</summary>
    public bool IsTruthy => Kind switch
    {
        CtxKind.Null => false,
        CtxKind.Bool => Bool,
        CtxKind.Number => Number != 0 && !double.IsNaN(Number),
        CtxKind.String => Text!.Length > 0,
        CtxKind.Json => Json.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => Json.TryGetDouble(out var d) && d != 0 && !double.IsNaN(d),
            JsonValueKind.String => !Json.ValueEquals(ReadOnlySpan<char>.Empty),
            _ => true,
        },
        _ => true,
    };

    /// <summary>The value as a number, or NaN when it is not numeric (numeric strings count).</summary>
    public double AsNumber() => Kind switch
    {
        CtxKind.Number => Number,
        CtxKind.String => double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        CtxKind.Json => Json.ValueKind switch
        {
            JsonValueKind.Number => Json.TryGetDouble(out var d) ? d : double.NaN,
            JsonValueKind.String => double.TryParse(Json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : double.NaN,
            _ => double.NaN,
        },
        _ => double.NaN,
    };

    /// <summary>Text for regex matching; null/undefined match as the empty string.</summary>
    public string AsText() => Kind switch
    {
        CtxKind.String => Text!,
        CtxKind.Bool => Bool ? "true" : "false",
        CtxKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        CtxKind.Json => Json.ValueKind == JsonValueKind.String ? Json.GetString() ?? "" : Json.ToString(),
        CtxKind.Other => Other?.ToString() ?? "",
        _ => "",
    };

    /// <summary>True for a string-like value; exposes the text without allocating.</summary>
    public bool TryGetString(out string text)
    {
        if (Kind == CtxKind.String) { text = Text!; return true; }
        text = "";
        return false;
    }

    public bool StringEquals(string s)
    {
        if (Kind == CtxKind.String) return string.Equals(Text, s, StringComparison.Ordinal);
        if (Kind == CtxKind.Json && Json.ValueKind == JsonValueKind.String) return Json.ValueEquals(s);
        return false;
    }

    /// <summary>Loose equality between two context values (used by <c>in</c> membership tests).</summary>
    public bool LooseEquals(in CtxValue other)
    {
        switch (Kind)
        {
            case CtxKind.Null: return other.Kind == CtxKind.Null;
            case CtxKind.Bool: return other.IsBoolLike && other.AsBool == Bool;
            case CtxKind.Number:
            {
                var n = other.AsNumber();
                return !double.IsNaN(n) && n == Number && other.Kind != CtxKind.Bool;
            }
            case CtxKind.String:
                if (other.StringEquals(Text!)) return true;
                if (other.Kind == CtxKind.Number) { var me = AsNumber(); return !double.IsNaN(me) && me == other.Number; }
                return false;
            case CtxKind.Json:
                return Json.ValueKind switch
                {
                    JsonValueKind.String => other.StringEquals(Json.GetString()!) || (other.Kind == CtxKind.Json && other.Json.ValueKind == JsonValueKind.String && other.Json.ValueEquals(Json.GetString()!)),
                    JsonValueKind.Number => Json.TryGetDouble(out var d) && other.AsNumber() == d,
                    JsonValueKind.True or JsonValueKind.False => other.IsBoolLike && other.AsBool == (Json.ValueKind == JsonValueKind.True),
                    _ => false,
                };
            default: return ReferenceEquals(Other, other.Other) || (Other?.Equals(other.Other) ?? false);
        }
    }

    public bool IsBoolLike => Kind == CtxKind.Bool || (Kind == CtxKind.Json && Json.ValueKind is JsonValueKind.True or JsonValueKind.False);
    public bool AsBool => Kind == CtxKind.Bool ? Bool : Kind == CtxKind.Json && Json.ValueKind == JsonValueKind.True;
}
