using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CsCode.Sessions;

/// <summary>Why a session file could not be read.</summary>
public enum SessionFormatError { Corrupt, TooNew }

public sealed class SessionFormatException(SessionFormatError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public SessionFormatError Error { get; } = error;
}

/// <summary>
/// Versioned JSON for <see cref="SessionDocument"/> (source-generated: the launcher reads session files before anything else is loaded).
/// Files written by older builds are migrated step by step (<see cref="Migrate"/>); files from a newer build are refused with
/// <see cref="SessionFormatError.TooNew"/> so they are never overwritten by a downgrade.
/// <para>History: v1 (development builds of 2026-10-29) held only folders, the layout and a flat <c>openFiles</c> list; v2 holds editors with view
/// state and hot-exit backup ids, the window bounds and the resident flag.</para>
/// </summary>
public static class SessionSerializer
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static byte[] Serialize(SessionDocument session) =>
        JsonSerializer.SerializeToUtf8Bytes(session with { Version = SessionDocument.CurrentVersion }, SessionJsonContext.Default.SessionDocument);

    /// <exception cref="SessionFormatException">The content is not a session (corrupt) or comes from a newer CsCode.</exception>
    public static SessionDocument Deserialize(ReadOnlySpan<byte> utf8)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(utf8, NodeOptions, DocumentOptions) as JsonObject
                ?? throw new SessionFormatException(SessionFormatError.Corrupt, "a session file must hold a JSON object");
        }
        catch (JsonException ex) { throw new SessionFormatException(SessionFormatError.Corrupt, "not valid JSON: " + ex.Message, ex); }

        var version = ReadVersion(root);
        if (version > SessionDocument.CurrentVersion)
            throw new SessionFormatException(SessionFormatError.TooNew, $"written by a newer CsCode (session format {version}, this build reads up to {SessionDocument.CurrentVersion})");
        if (version < SessionDocument.CurrentVersion) root = Migrate(root, version);

        SessionDocument? session;
        try { session = root.Deserialize(SessionJsonContext.Default.SessionDocument); }
        catch (JsonException ex) { throw new SessionFormatException(SessionFormatError.Corrupt, "unexpected content: " + ex.Message, ex); }
        if (session is null || string.IsNullOrEmpty(session.Name)) throw new SessionFormatException(SessionFormatError.Corrupt, "the session has no name");
        // A hand-edited file may hold nulls where lists belong: normalise rather than fail.
        return session with
        {
            Folders = session.Folders?.Where(f => !string.IsNullOrEmpty(f)).ToArray() ?? [],
            Editors = session.Editors?.Where(e => e is not null && !string.IsNullOrEmpty(e.Uri)).ToArray() ?? [],
        };
    }

    /// <summary>
    /// The list fields of a current-version session read with a forward-only reader (no document model: the launcher lists sessions before
    /// anything else is warm). Null when the content is not a well-formed current-version session (the caller then does a full load).
    /// </summary>
    public static SessionSummary? TryReadSummary(ReadOnlySpan<byte> utf8, string name)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            int? version = null;
            bool unnamed = false, resident = false;
            var lastUsed = DateTimeOffset.MinValue;
            var folders = new List<string>();
            var editors = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var property = reader.GetString();
                if (!reader.Read()) return null;
                switch (property?.ToLowerInvariant())
                {
                    case "version" when reader.TokenType == JsonTokenType.Number: version = reader.GetInt32(); break;
                    case "isunnamed" when reader.TokenType is JsonTokenType.True or JsonTokenType.False: unnamed = reader.GetBoolean(); break;
                    case "wasresident" when reader.TokenType is JsonTokenType.True or JsonTokenType.False: resident = reader.GetBoolean(); break;
                    case "lastusedutc" when reader.TokenType == JsonTokenType.String: lastUsed = reader.GetDateTimeOffset(); break;
                    case "folders" when reader.TokenType == JsonTokenType.StartArray:
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } f) folders.Add(f);
                        break;
                    case "editors" when reader.TokenType == JsonTokenType.StartArray:
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType == JsonTokenType.StartObject) editors++;
                            reader.Skip();
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || version != SessionDocument.CurrentVersion) return null;
            return new SessionSummary(name, unnamed, lastUsed, folders, editors, resident);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    private static int ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue("version", out var node) || node is null) return 1; // v1 files were written before the field was mandatory
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) { throw new SessionFormatException(SessionFormatError.Corrupt, "the version is not a number", ex); }
    }

    /// <summary>Upgrades <paramref name="root"/> from <paramref name="fromVersion"/> to <see cref="SessionDocument.CurrentVersion"/>, one step at a time.</summary>
    internal static JsonObject Migrate(JsonObject root, int fromVersion)
    {
        var version = fromVersion;
        if (version < 1) throw new SessionFormatException(SessionFormatError.Corrupt, $"unknown session format {version}");
        if (version == 1) { root = MigrateV1ToV2(root); version = 2; }
        root["version"] = version;
        return root;
    }

    /// <summary>v1 → v2: the flat <c>openFiles</c> list becomes editors in tab order, <c>activeFile</c> becomes <c>activeEditor</c>.</summary>
    private static JsonObject MigrateV1ToV2(JsonObject v1)
    {
        var editors = new JsonArray();
        if (v1["openFiles"] is JsonArray files)
        {
            var order = 0;
            foreach (var f in files)
                if (f?.GetValueKind() == JsonValueKind.String) editors.Add(new JsonObject { ["uri"] = f.GetValue<string>(), ["order"] = order++ });
        }
        v1.Remove("openFiles");
        v1["editors"] = editors;
        if (v1["activeFile"] is { } active)
        {
            v1.Remove("activeFile");
            v1["activeEditor"] = active;
        }
        return v1;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(SessionSummary))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
