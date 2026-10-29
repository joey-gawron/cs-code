using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CsCode.Core.Languages;

namespace CsCode.Remote.Protocol;

/// <summary>
/// The wire's System.Text.Json configuration. Core's models are records and cross the wire as they are; the few shapes the serializer cannot
/// handle on its own get converters here: <see cref="Uri"/> (mapped per connection, also as a dictionary key: <see cref="WorkspaceEdit.Changes"/>),
/// value tuples (<c>ParameterInformation.LabelRange</c>, file-operation pairs) as two-element arrays, and the <see cref="ResourceOperation"/>
/// hierarchy as a <c>kind</c>-discriminated union. Nulls are omitted. One options instance per connection (it carries the URI mapper).
/// </summary>
public static class RemoteJson
{
    public static JsonSerializerOptions Create(UriMapper? mapper = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { AddPolymorphism } },
            MaxDepth = 256, // SelectionRange parents, nested document symbols
        };
        options.Converters.Add(new MappedUriConverter(mapper ?? UriMapper.Identity));
        options.Converters.Add(new ValueTupleConverterFactory());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Plain options (identity mapping) for messages that carry no document URIs.</summary>
    public static readonly JsonSerializerOptions Default = Create();

    private static void AddPolymorphism(JsonTypeInfo info)
    {
        if (info.Type != typeof(ResourceOperation)) return;
        info.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "kind",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            DerivedTypes =
            {
                new JsonDerivedType(typeof(CreateFileOperation), "create"),
                new JsonDerivedType(typeof(RenameFileOperation), "rename"),
                new JsonDerivedType(typeof(DeleteFileOperation), "delete"),
            },
        };
    }

    /// <summary>Writes client URIs as server URIs and reads server URIs as client URIs (see <see cref="UriMapper"/>).</summary>
    private sealed class MappedUriConverter(UriMapper mapper) : JsonConverter<Uri>
    {
        public override Uri? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options) => writer.WriteStringValue(Format(value));

        public override Uri ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString()) ?? throw new JsonException("Empty URI key");

        public override void WriteAsPropertyName(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options) => writer.WritePropertyName(Format(value));

        private Uri? Parse(string? s)
        {
            if (s is null) return null;
            if (!Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out var u)) throw new JsonException($"Invalid URI '{s}'");
            return u.IsAbsoluteUri ? mapper.ToClient(u) : u;
        }

        private string Format(Uri u) => u.IsAbsoluteUri ? mapper.ToServer(u).AbsoluteUri : u.OriginalString;
    }

    /// <summary>(T1, T2) ⇄ [t1, t2]. System.Text.Json ignores tuple fields, so without this a tuple silently became <c>{}</c>.</summary>
    private sealed class ValueTupleConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueTuple<,>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(PairConverter<,>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

        private sealed class PairConverter<T1, T2> : JsonConverter<ValueTuple<T1, T2>>
        {
            public override (T1, T2) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a two-element array");
                reader.Read();
                var a = JsonSerializer.Deserialize<T1>(ref reader, options)!;
                reader.Read();
                var b = JsonSerializer.Deserialize<T2>(ref reader, options)!;
                reader.Read();
                if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected a two-element array");
                return (a, b);
            }

            public override void Write(Utf8JsonWriter writer, (T1, T2) value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                JsonSerializer.Serialize(writer, value.Item1, options);
                JsonSerializer.Serialize(writer, value.Item2, options);
                writer.WriteEndArray();
            }
        }
    }

    /// <summary>Deserialises a JSON-RPC params/result element (null/undefined → default).</summary>
    public static T? As<T>(this JsonElement element, JsonSerializerOptions options) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : element.Deserialize<T>(options);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static JsonElement ToElement<T>(T value, JsonSerializerOptions options) => JsonSerializer.SerializeToElement(value, options);
}
