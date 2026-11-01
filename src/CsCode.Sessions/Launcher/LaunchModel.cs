using System.Text.Json;
using CsCode.Sessions.Ipc;

namespace CsCode.Sessions.Launcher;

public enum LaunchKind
{
    /// <summary><c>cscode [--session NAME | --new-window] [paths...] [--wait]</c>.</summary>
    Open,
    /// <summary><c>--list-sessions</c>.</summary>
    ListSessions,
    /// <summary><c>--background</c>: ensure the warm spare (and, with restoreOnLogin, the resident sessions, hidden); show nothing.</summary>
    Background,
    /// <summary><c>--quit [NAME | --all]</c>.</summary>
    Quit,
}

/// <summary>A command-line path, already absolute (the launcher's working directory is not the session's).</summary>
public sealed record LaunchTarget(string Path, bool IsDirectory, int? Line = null, int? Column = null)
{
    public IpcPath ToIpc() => new(Path, Line, Column);
}

public sealed record LaunchRequest(LaunchKind Kind)
{
    public string? Session { get; init; }
    public bool NewWindow { get; init; }
    public IReadOnlyList<LaunchTarget> Targets { get; init; } = [];
    /// <summary><c>ssh://[user@]host[:port]/path</c> (and other remote URIs) from the command line: the session connects and opens them as folders.</summary>
    public IReadOnlyList<string> RemoteTargets { get; init; } = [];
    public bool Wait { get; init; }
    /// <summary><c>--quit --all</c>.</summary>
    public bool All { get; init; }
}

/// <summary>The settings the launcher honours, read straight from <c>settings.json</c> (the launcher runs before any service exists).</summary>
public sealed record LaunchSettings
{
    public const string DefaultSessionKey = "cscode.sessions.default";
    public const string WarmSpareKey = "cscode.background.warmSpare";
    public const string MaxResidentKey = "cscode.background.maxResidentSessions";
    public const string RestoreOnLoginKey = "cscode.background.restoreOnLogin";

    /// <summary><c>last</c> (the session shown last), <c>new</c> (a new unnamed session) or a session name.</summary>
    public string DefaultSession { get; init; } = "last";
    /// <summary>
    /// Keep a pre-started process to claim. Off by default (the user's decision, docs/briefs/wave-3.md): it costs a whole idle process (≈ 60 MB private on Linux, ≈ 150 MB on macOS; docs/SESSIONS.md)
    /// for every user to make the first open of a session that is not running faster; resident sessions are shown just as fast without it.
    /// </summary>
    public bool WarmSpare { get; init; }
    public int MaxResidentSessions { get; init; } = 3;
    public bool RestoreOnLogin { get; init; }

    /// <summary>Reads <c>{ConfigDir}/settings.json</c> (JSON with comments). Missing or unreadable values keep their defaults.</summary>
    public static LaunchSettings Read(string configDir)
    {
        var path = Path.Combine(configDir, "settings.json");
        byte[]? bytes;
        try { bytes = AtomicFile.ReadOrNull(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new LaunchSettings(); }
        if (bytes is null) return new LaunchSettings();
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new LaunchSettings();
            var s = new LaunchSettings();
            if (Find(root, DefaultSessionKey) is { ValueKind: JsonValueKind.String } d && d.GetString() is { Length: > 0 } ds) s = s with { DefaultSession = ds };
            if (Find(root, WarmSpareKey) is { ValueKind: JsonValueKind.True or JsonValueKind.False } w) s = s with { WarmSpare = w.GetBoolean() };
            if (Find(root, MaxResidentKey) is { ValueKind: JsonValueKind.Number } m && m.TryGetInt32(out var max)) s = s with { MaxResidentSessions = Math.Clamp(max, 1, 64) };
            if (Find(root, RestoreOnLoginKey) is { ValueKind: JsonValueKind.True or JsonValueKind.False } r) s = s with { RestoreOnLogin = r.GetBoolean() };
            return s;
        }
        catch (JsonException) { return new LaunchSettings(); }
    }

    /// <summary>A dotted key as a flat property (<c>"a.b.c": v</c>, VS Code style) or nested objects (<c>"a": {"b": {"c": v}}</c>).</summary>
    private static JsonElement? Find(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var flat)) return flat;
        var current = root;
        foreach (var part in key.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current)) return null;
        }
        return current;
    }
}

public enum LaunchAction
{
    /// <summary>A process already owns the session: show its window.</summary>
    Show,
    /// <summary>No process owns it: the warm spare takes it.</summary>
    Claim,
    /// <summary>No process owns it and there is no spare: start a session process.</summary>
    Start,
    /// <summary>
    /// The session's process is quitting and still holds its lock (its final save): wait, bounded, until it lets go, then plan again. One that does
    /// not let go in time counts as wedged.
    /// </summary>
    AwaitQuit,
    List,
    Quit,
    Background,
    Fail,
}

/// <summary>What the launcher will do (the output of <see cref="LaunchPlanner"/>; a pure decision, unit-tested as a table).</summary>
public sealed record LaunchPlan(LaunchAction Action)
{
    public string? Session { get; init; }
    /// <summary>The process to show / claim.</summary>
    public int? Pid { get; init; }
    /// <summary>Start: the new session begins as a copy of this one (its owner does not respond).</summary>
    public string? SeedFrom { get; init; }
    public bool NewUnnamed { get; init; }
    /// <summary>Start a spare afterwards (the action consumed the only one, or there was none).</summary>
    public bool EnsureSpare { get; init; }
    /// <summary>Hidden sessions to quit afterwards (more than <c>maxResidentSessions</c> would stay resident).</summary>
    public IReadOnlyList<int> Evict { get; init; } = [];
    /// <summary>Quit: the processes to quit.</summary>
    public IReadOnlyList<int> QuitPids { get; init; } = [];
    /// <summary>Background: sessions to start hidden.</summary>
    public IReadOnlyList<string> StartHidden { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>Everything the planner looks at, gathered by the launcher (processes over IPC, saved sessions from the store, lock probes).</summary>
public sealed record LaunchWorld(IReadOnlyList<ResidentProcess> Processes, IReadOnlyList<SessionSummary> Saved, string? LastActive, Func<string, bool> IsSessionLocked)
{
    public static LaunchWorld Empty { get; } = new([], [], null, _ => false);
}
