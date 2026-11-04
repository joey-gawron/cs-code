using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsCode.Core.History;

namespace CsCode.Panels.Timeline.LocalHistory;

/// <summary>One entry as <c>entries.json</c> holds it (VS Code's serialized entry): the file name of the copy, milliseconds since 1970, the source.</summary>
internal sealed record StoredEntry(string Id, long Timestamp, string? Source, string? SourceDescription)
{
    public string SourceOrDefault => Source ?? LocalHistorySources.FileSaved;
}

/// <summary>A file's history as stored: the resource it belongs to and its entries, oldest first.</summary>
internal sealed record StoredHistory(string Resource, IReadOnlyList<StoredEntry> Entries);

/// <summary>
/// VS Code's local history layout: <c>History/&lt;hash&gt;/entries.json</c> plus one file per entry, where &lt;hash&gt; is VS Code's string hash of
/// the resource URI in hex (it can be negative: <c>-12ac7d2b</c>) and each entry file is named by four random characters and the resource's
/// extension (<c>Xy3z.cs</c>).
/// </summary>
internal static class HistoryFormat
{
    public const string EntriesFile = "entries.json";
    private const string IdChars = "abcdefghijklmnopqrstuvwxyz0123456789"; // one case: entry names must not collide on case-insensitive volumes

    /// <summary>VS Code's <c>hash(string)</c> (<c>base/common/hash.ts</c>: h = h * 31 + c over UTF-16 units, seeded with 149417), as hex.</summary>
    public static string Hash(string s)
    {
        var h = 0;
        unchecked
        {
            h = (h << 5) - h + 149417;
            foreach (var c in s) h = (h << 5) - h + c;
        }
        return h < 0 ? "-" + (-(long)h).ToString("x", System.Globalization.CultureInfo.InvariantCulture) : h.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The key a resource is stored under (and the string that is hashed): its URI; a <c>file:</c> URI is spelled from its path, so the editor's
    /// and the Explorer's spellings of one file (escaping) are one key.
    /// </summary>
    public static string Key(Uri resource) => resource.IsFile ? new Uri(resource.LocalPath).AbsoluteUri : resource.AbsoluteUri;

    /// <summary>A new entry file name for <paramref name="resource"/> that is not in <paramref name="taken"/> (compared ignoring case).</summary>
    public static string NewId(Uri resource, IEnumerable<string> taken)
    {
        var ext = Path.GetExtension(resource.AbsolutePath);
        if (ext.Length > 16 || ext.AsSpan().IndexOfAny(SearchValues.Create("/\\:*?\"<>|%")) >= 0) ext = "";
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        Span<char> chars = stackalloc char[4];
        while (true)
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = IdChars[RandomNumberGenerator.GetInt32(IdChars.Length)];
            var id = new string(chars) + ext;
            if (!used.Contains(id) && !string.Equals(id, EntriesFile, StringComparison.OrdinalIgnoreCase)) return id;
        }
    }

    public static byte[] Serialize(StoredHistory history)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("resource", history.Resource);
            w.WriteStartArray("entries");
            foreach (var e in history.Entries)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                // as VS Code: the default source (a save) is left out
                if (e.Source is { } s && s != LocalHistorySources.FileSaved) w.WriteString("source", s);
                if (e.SourceDescription is { } d) w.WriteString("sourceDescription", d);
                w.WriteNumber("timestamp", e.Timestamp);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Parses <c>entries.json</c>; null when it is not one (corrupt, another format). Entries with a bad id are dropped.</summary>
    public static StoredHistory? Parse(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("resource", out var res) || res.ValueKind != JsonValueKind.String) return null;
            var entries = new List<StoredEntry>();
            if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    var name = id.GetString()!;
                    if (!IsSafeId(name)) continue;
                    var ts = e.TryGetProperty("timestamp", out var t) && t.TryGetInt64(out var v) ? v : 0;
                    var source = e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    var description = e.TryGetProperty("sourceDescription", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    entries.Add(new StoredEntry(name, ts, source, description));
                }
            entries.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return new StoredHistory(res.GetString()!, entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An entry name that stays inside its folder (entries.json is read from disk and could have been edited).</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id is not ("." or "..") && !string.Equals(id, EntriesFile, StringComparison.OrdinalIgnoreCase)
        && id.AsSpan().IndexOfAny(SearchValues.Create("/\\:\0")) < 0;

    public static byte[] Sha256(byte[] bytes) => SHA256.HashData(bytes);

    public static string Describe(Uri resource) => resource.IsFile ? resource.LocalPath : Uri.UnescapeDataString(resource.AbsoluteUri);

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
