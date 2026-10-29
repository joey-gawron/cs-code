using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsCode.Sessions.Ipc;

/// <summary>A path the launcher asks a session to open, with an optional 1-based position (<c>file.cs:12:3</c>).</summary>
public sealed record IpcPath(string Path, int? Line = null, int? Column = null);

/// <summary>What a process is doing (reported by <c>hello</c>).</summary>
public static class ProcessStates
{
    /// <summary>Started, building its services; owns its session (if any) already.</summary>
    public const string Starting = "starting";
    /// <summary>Warm spare: services and window built, no session; waits to be claimed.</summary>
    public const string Spare = "spare";
    public const string Shown = "shown";
    public const string Hidden = "hidden";
    public const string Quitting = "quitting";
}

/// <summary>
/// One message of the launcher ↔ CsCode process protocol (requests and replies share the shape; unused fields are omitted on the wire).
/// <list type="bullet">
/// <item><c>hello {protocolVersion}</c> → pid, session, state, lastShownUnixMs, debugging, workingSetBytes.</item>
/// <item><c>show {paths?, wait?}</c> → shows the window (opening the paths) and replies once it painted; with <c>wait</c> the reply comes when the
/// (single) opened file's tab closes (<c>cscode --wait</c>, e.g. as the git editor).</item>
/// <item><c>hide</c>, <c>claim {session, paths?, hidden?}</c> (a warm spare takes a session), <c>listSessions</c>, <c>quit {force}</c>,
/// <c>focusFile {paths}</c> (open without changing the window's visibility).</item>
/// <item>Test-only verbs, accepted only by a process started with <c>--headless-test</c>: <c>inspect</c>, <c>testEdit</c>, <c>testCloseTab</c>,
/// <c>testCaret</c>, <c>testFold</c>, <c>testCycle</c>.</item>
/// </list>
/// Framing: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON (at most <see cref="IpcFraming.MaxMessageBytes"/>).
/// </summary>
public sealed record IpcMessage
{
    public const int ProtocolVersionCurrent = 1;

    public required string Type { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public int? ProtocolVersion { get; init; }
    public int? Pid { get; init; }
    public string? Session { get; init; }
    public string? State { get; init; }
    public long? LastShownUnixMs { get; init; }
    public long? WorkingSetBytes { get; init; }
    public bool? Debugging { get; init; }
    public IReadOnlyList<IpcPath>? Paths { get; init; }
    public bool? Wait { get; init; }
    public bool? Force { get; init; }
    public bool? Hidden { get; init; }
    /// <summary>Claim: the session is a new unnamed one (<c>window-N</c>).</summary>
    public bool? Unnamed { get; init; }
    public IReadOnlyList<SessionSummary>? Sessions { get; init; }
    /// <summary>Free-form payload (inspect results, timings).</summary>
    public JsonElement? Data { get; init; }
    public string? Uri { get; init; }
    public string? Text { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? Count { get; init; }

    public static IpcMessage Reply(bool ok = true, string? error = null) => new() { Type = "reply", Ok = ok, Error = error };
    public static IpcMessage Failure(string error) => Reply(false, error);
}

public static class IpcVerbs
{
    public const string Hello = "hello";
    public const string Show = "show";
    public const string Hide = "hide";
    public const string Claim = "claim";
    public const string ListSessions = "listSessions";
    public const string Quit = "quit";
    public const string FocusFile = "focusFile";
    public const string Inspect = "inspect";
    public const string TestEdit = "testEdit";
    public const string TestCloseTab = "testCloseTab";
    public const string TestCaret = "testCaret";
    public const string TestFold = "testFold";
    public const string TestCycle = "testCycle";

    public static bool IsTestOnly(string type) => type is Inspect or TestEdit or TestCloseTab or TestCaret or TestFold or TestCycle;
}

public sealed class IpcProtocolException(string message) : IOException(message);

public static class IpcFraming
{
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    public static async ValueTask WriteAsync(Stream stream, IpcMessage message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.IpcMessage);
        if (body.Length > MaxMessageBytes) throw new IpcProtocolException($"message too large ({body.Length} bytes)");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next message, or null when the peer closed the connection cleanly before a new frame.</summary>
    public static async ValueTask<IpcMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, allowEof: true, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes) throw new IpcProtocolException($"invalid frame length {length}");
        var body = new byte[length];
        await FillAsync(stream, body, allowEof: false, ct).ConfigureAwait(false);
        try
        {
            var message = JsonSerializer.Deserialize(body, IpcJsonContext.Default.IpcMessage);
            if (message is null || string.IsNullOrEmpty(message.Type)) throw new IpcProtocolException("message without a type");
            return message;
        }
        catch (JsonException ex) { throw new IpcProtocolException("malformed message: " + ex.Message); }
    }

    private static async ValueTask<bool> FillAsync(Stream stream, byte[] buffer, bool allowEof, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0 && allowEof) return false;
                throw new IpcProtocolException("connection closed mid-message");
            }
            read += n;
        }
        return true;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
