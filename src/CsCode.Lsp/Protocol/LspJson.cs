using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsCode.Lsp.Protocol;

/// <summary>Marker for open string enums (unknown values round-trip; see StringEnumJsonConverter).</summary>
public interface IStringEnum<TSelf> where TSelf : struct, IStringEnum<TSelf>
{
    string Value { get; }
    static abstract TSelf FromString(string value);
}

public sealed class StringEnumJsonConverter<T> : JsonConverter<T> where T : struct, IStringEnum<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Expected string for {typeof(T).Name}, got {reader.TokenType}");
        return T.FromString(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value ?? string.Empty);
}

/// <summary>Placeholder params type for requests/notifications without parameters.</summary>
public sealed record NoParams
{
    public static readonly NoParams Instance = new();
}

/// <summary>Typed request descriptor: the method name plus the parameter/result CLR types.</summary>
public sealed class LspRequest<TParams, TResult>(string method)
{
    public string Method { get; } = method;
    public override string ToString() => Method;
}

public sealed class LspNotification<TParams>(string method)
{
    public string Method { get; } = method;
    public override string ToString() => Method;
}

[Flags]
internal enum JsonKinds
{
    None = 0, Null = 1, String = 2, Number = 4, True = 8, False = 16, Array = 32, Object = 64,
    Bool = True | False,
    All = Null | String | Number | True | False | Array | Object,
}

/// <summary>
/// What JSON a CLR type can be read from: a token-kind mask (cheap pre-filter, no parsing) and a structural matcher
/// (used only when a union has several arms of the same token kind, e.g. TextEdit | InsertReplaceEdit).
/// </summary>
internal sealed class JsonShape
{
    private static readonly ConcurrentDictionary<Type, JsonShape> Cache = new(); // bounded by the (finite) set of protocol types

    public JsonKinds Kinds { get; private init; }
    private Func<JsonElement, bool>? _structural;

    public static JsonShape Of(Type t) => Cache.GetOrAdd(t, static type => Build(type));

    public static JsonKinds KindOf(JsonTokenType token) => token switch
    {
        JsonTokenType.String => JsonKinds.String,
        JsonTokenType.Number => JsonKinds.Number,
        JsonTokenType.True => JsonKinds.True,
        JsonTokenType.False => JsonKinds.False,
        JsonTokenType.StartArray => JsonKinds.Array,
        JsonTokenType.StartObject => JsonKinds.Object,
        JsonTokenType.Null => JsonKinds.Null,
        _ => JsonKinds.None,
    };

    public static JsonKinds KindOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => JsonKinds.String,
        JsonValueKind.Number => JsonKinds.Number,
        JsonValueKind.True => JsonKinds.True,
        JsonValueKind.False => JsonKinds.False,
        JsonValueKind.Array => JsonKinds.Array,
        JsonValueKind.Object => JsonKinds.Object,
        JsonValueKind.Null => JsonKinds.Null,
        _ => JsonKinds.None,
    };

    public bool Matches(JsonElement e) => (Kinds & KindOf(e)) != 0 && (_structural is null || _structural(e));

    private static JsonShape Build(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null)
        {
            var inner = Of(underlying);
            return new JsonShape { Kinds = inner.Kinds | JsonKinds.Null, _structural = e => e.ValueKind == JsonValueKind.Null || inner.Matches(e) };
        }
        if (t == typeof(JsonElement)) return new JsonShape { Kinds = JsonKinds.All };
        if (t == typeof(string)) return new JsonShape { Kinds = JsonKinds.String };
        if (t == typeof(bool)) return new JsonShape { Kinds = JsonKinds.Bool };
        if (t.IsEnum || t == typeof(int) || t == typeof(long) || t == typeof(uint) || t == typeof(double) || t == typeof(float) || t == typeof(short))
            return new JsonShape { Kinds = JsonKinds.Number };
        if (t.IsArray)
        {
            var elem = Of(t.GetElementType()!);
            return new JsonShape { Kinds = JsonKinds.Array, _structural = e => e.GetArrayLength() == 0 || elem.Matches(e[0]) };
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return new JsonShape { Kinds = JsonKinds.Object };
        if (typeof(IOneOf).IsAssignableFrom(t))
        {
            var arms = t.GetGenericArguments().Select(Of).ToArray();
            var kinds = arms.Aggregate(JsonKinds.None, (acc, a) => acc | a.Kinds);
            return new JsonShape { Kinds = kinds, _structural = e => arms.Any(a => a.Matches(e)) };
        }
        if (t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStringEnum<>)))
            return new JsonShape { Kinds = JsonKinds.String };

        var m = t.GetMethod("MatchesShape", BindingFlags.Public | BindingFlags.Static, [typeof(JsonElement)]);
        if (m is not null)
        {
            var f = m.CreateDelegate<Func<JsonElement, bool>>();
            return new JsonShape { Kinds = JsonKinds.Object, _structural = f };
        }
        return new JsonShape { Kinds = JsonKinds.Object };
    }
}

/// <summary>Reads/writes <see cref="OneOf{T0,T1}"/> and friends; picks the arm by token kind, then by structure.</summary>
public sealed class OneOfJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsValueType && typeToConvert.IsGenericType && typeof(IOneOf).IsAssignableFrom(typeToConvert);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(typeof(OneOfJsonConverter<>).MakeGenericType(typeToConvert));
}

internal sealed class OneOfJsonConverter<T> : JsonConverter<T> where T : struct, IOneOf, IOneOfFactory<T>
{
    private static readonly Type[] Arms = typeof(T).GetGenericArguments();
    private static readonly JsonShape[] Shapes = Arms.Select(JsonShape.Of).ToArray();

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var token = JsonShape.KindOf(reader.TokenType);
        int only = -1, count = 0;
        for (var i = 0; i < Shapes.Length; i++)
        {
            if ((Shapes[i].Kinds & token) == 0) continue;
            only = i;
            count++;
        }
        if (count == 0) throw new JsonException($"No arm of {typeof(T).Name} accepts JSON {reader.TokenType}");
        if (count == 1) return T.Create(only, JsonSerializer.Deserialize(ref reader, Arms[only], options));

        // Several arms share the token kind: look at the structure.
        using var doc = JsonDocument.ParseValue(ref reader);
        var el = doc.RootElement;
        var chosen = -1;
        for (var i = 0; i < Shapes.Length; i++)
            if (Shapes[i].Matches(el)) { chosen = i; break; }
        if (chosen < 0)
            for (var i = 0; i < Shapes.Length; i++)
                if ((Shapes[i].Kinds & token) != 0) { chosen = i; break; } // tolerant fallback: first arm of that token kind
        return T.Create(chosen, el.Deserialize(Arms[chosen], options));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value.Index < 0 || value.Value is null) { writer.WriteNullValue(); return; }
        JsonSerializer.Serialize(writer, value.Value, Arms[value.Index], options);
    }
}

/// <summary>The single set of serializer options for everything that crosses the LSP wire.</summary>
public static class LspJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.Strict,
            // Servers send huge-but-legal documents; depth is bounded by the protocol, not by us.
            MaxDepth = 256,
        };
        o.Converters.Add(new OneOfJsonConverterFactory());
        return o;
    }

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
