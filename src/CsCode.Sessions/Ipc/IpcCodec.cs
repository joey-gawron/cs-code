using System.Buffers;
using System.Text.Json;

namespace CsCode.Sessions.Ipc;

/// <summary>
/// JSON for <see cref="IpcMessage"/>, written and read by hand with <see cref="Utf8JsonWriter"/>/<see cref="Utf8JsonReader"/>. The launcher is a
/// fresh process on every invocation: the framework's (precompiled) reader and writer cost nothing to start, whereas serializer metadata for our
/// types would be built and JIT-compiled on each run. Unknown properties are skipped (an older launcher talks to a newer CsCode and back).
/// </summary>
public static class IpcCodec
{
    public static byte[] Encode(IpcMessage m)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", m.Type);
            if (m.Ok is { } ok) w.WriteBoolean("ok", ok);
            if (m.Error is { } error) w.WriteString("error", error);
            if (m.ProtocolVersion is { } pv) w.WriteNumber("protocolVersion", pv);
            if (m.Pid is { } pid) w.WriteNumber("pid", pid);
            if (m.Session is { } session) w.WriteString("session", session);
            if (m.State is { } state) w.WriteString("state", state);
            if (m.LastShownUnixMs is { } shown) w.WriteNumber("lastShownUnixMs", shown);
            if (m.WorkingSetBytes is { } ws) w.WriteNumber("workingSetBytes", ws);
            if (m.Debugging is { } debugging) w.WriteBoolean("debugging", debugging);
            if (m.Paths is { } paths)
            {
                w.WriteStartArray("paths");
                foreach (var p in paths)
                {
                    w.WriteStartObject();
                    w.WriteString("path", p.Path);
                    if (p.Line is { } line) w.WriteNumber("line", line);
                    if (p.Column is { } column) w.WriteNumber("column", column);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (m.Wait is { } wait) w.WriteBoolean("wait", wait);
            if (m.Force is { } force) w.WriteBoolean("force", force);
            if (m.Hidden is { } hidden) w.WriteBoolean("hidden", hidden);
            if (m.Unnamed is { } unnamed) w.WriteBoolean("unnamed", unnamed);
            if (m.Sessions is { } sessions)
            {
                w.WriteStartArray("sessions");
                foreach (var s in sessions)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name);
                    w.WriteBoolean("isUnnamed", s.IsUnnamed);
                    w.WriteString("lastUsedUtc", s.LastUsedUtc);
                    w.WriteStartArray("folders");
                    foreach (var f in s.Folders) w.WriteStringValue(f);
                    w.WriteEndArray();
                    w.WriteNumber("editorCount", s.EditorCount);
                    w.WriteBoolean("wasResident", s.WasResident);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (m.Data is { } data)
            {
                w.WritePropertyName("data");
                data.WriteTo(w);
            }
            if (m.Uri is { } uri) w.WriteString("uri", uri);
            if (m.Text is { } text) w.WriteString("text", text);
            if (m.Line is { } l) w.WriteNumber("line", l);
            if (m.Column is { } c) w.WriteNumber("column", c);
            if (m.Count is { } n) w.WriteNumber("count", n);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <exception cref="IpcProtocolException">Not a message.</exception>
    public static IpcMessage Decode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var r = new Utf8JsonReader(utf8);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new IpcProtocolException("a message must be a JSON object");
            string? type = null, error = null, session = null, state = null, uri = null, text = null;
            bool? ok = null, debugging = null, wait = null, force = null, hidden = null, unnamed = null;
            int? protocol = null, pid = null, line = null, column = null, count = null;
            long? shown = null, ws = null;
            List<IpcPath>? paths = null;
            List<SessionSummary>? sessions = null;
            JsonElement? data = null;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString();
                r.Read();
                if (r.TokenType == JsonTokenType.Null) continue;
                switch (name)
                {
                    case "type": type = r.GetString(); break;
                    case "ok": ok = r.GetBoolean(); break;
                    case "error": error = r.GetString(); break;
                    case "protocolVersion": protocol = r.GetInt32(); break;
                    case "pid": pid = r.GetInt32(); break;
                    case "session": session = r.GetString(); break;
                    case "state": state = r.GetString(); break;
                    case "lastShownUnixMs": shown = r.GetInt64(); break;
                    case "workingSetBytes": ws = r.GetInt64(); break;
                    case "debugging": debugging = r.GetBoolean(); break;
                    case "paths": paths = ReadPaths(ref r); break;
                    case "wait": wait = r.GetBoolean(); break;
                    case "force": force = r.GetBoolean(); break;
                    case "hidden": hidden = r.GetBoolean(); break;
                    case "unnamed": unnamed = r.GetBoolean(); break;
                    case "sessions": sessions = ReadSessions(ref r); break;
                    case "data": data = JsonElement.ParseValue(ref r); break;
                    case "uri": uri = r.GetString(); break;
                    case "text": text = r.GetString(); break;
                    case "line": line = r.GetInt32(); break;
                    case "column": column = r.GetInt32(); break;
                    case "count": count = r.GetInt32(); break;
                    default: r.Skip(); break;
                }
            }
            if (string.IsNullOrEmpty(type)) throw new IpcProtocolException("message without a type");
            return new IpcMessage
            {
                Type = type, Ok = ok, Error = error, ProtocolVersion = protocol, Pid = pid, Session = session, State = state, LastShownUnixMs = shown,
                WorkingSetBytes = ws, Debugging = debugging, Paths = paths, Wait = wait, Force = force, Hidden = hidden, Unnamed = unnamed,
                Sessions = sessions, Data = data, Uri = uri, Text = text, Line = line, Column = column, Count = count,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new IpcProtocolException("malformed message: " + ex.Message);
        }
    }

    private static List<IpcPath> ReadPaths(ref Utf8JsonReader r)
    {
        var list = new List<IpcPath>();
        if (r.TokenType != JsonTokenType.StartArray) throw new IpcProtocolException("paths must be an array");
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            string? path = null;
            int? line = null, column = null;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString();
                r.Read();
                switch (name)
                {
                    case "path": path = r.GetString(); break;
                    case "line" when r.TokenType == JsonTokenType.Number: line = r.GetInt32(); break;
                    case "column" when r.TokenType == JsonTokenType.Number: column = r.GetInt32(); break;
                    default: r.Skip(); break;
                }
            }
            if (!string.IsNullOrEmpty(path)) list.Add(new IpcPath(path, line, column));
        }
        return list;
    }

    private static List<SessionSummary> ReadSessions(ref Utf8JsonReader r)
    {
        var list = new List<SessionSummary>();
        if (r.TokenType != JsonTokenType.StartArray) throw new IpcProtocolException("sessions must be an array");
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            bool unnamed = false, resident = false;
            var used = DateTimeOffset.MinValue;
            var folders = new List<string>();
            var editors = 0;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var property = r.GetString();
                r.Read();
                switch (property)
                {
                    case "name": name = r.GetString(); break;
                    case "isUnnamed": unnamed = r.GetBoolean(); break;
                    case "lastUsedUtc": used = r.GetDateTimeOffset(); break;
                    case "folders":
                        while (r.Read() && r.TokenType == JsonTokenType.String) folders.Add(r.GetString()!);
                        break;
                    case "editorCount": editors = r.GetInt32(); break;
                    case "wasResident": resident = r.GetBoolean(); break;
                    default: r.Skip(); break;
                }
            }
            if (name is not null) list.Add(new SessionSummary(name, unnamed, used, folders, editors, resident));
        }
        return list;
    }
}
