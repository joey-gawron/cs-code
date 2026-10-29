using System.Text;

namespace CsCode.Sessions;

public enum SessionLoadStatus
{
    Loaded,
    /// <summary>No such session (a new one starts empty).</summary>
    Missing,
    /// <summary>The main file was unreadable; the previous version (<c>.bak</c>) was used.</summary>
    RecoveredFromBackup,
    /// <summary>Neither the file nor its backup could be read (the session starts empty; the files are kept for inspection).</summary>
    Corrupt,
    /// <summary>Written by a newer CsCode: not loaded, and must not be overwritten.</summary>
    TooNew,
}

public sealed record SessionLoadResult(SessionDocument? Session, SessionLoadStatus Status, string? Message = null);

/// <summary>
/// The saved sessions: <c>{UserDataDir}/sessions/&lt;name&gt;.json</c> (+ <c>.bak</c>, the previous version) and the per-session hot-exit backup
/// directories (<see cref="HotExitBackupStore"/>). Writes are atomic (<see cref="AtomicFile"/>); reads tolerate a corrupt file by falling back to
/// the backup. Synchronous and thread-safe for distinct sessions; callers run it on a background lane (never the UI thread). Several processes
/// share the directory: each session is written only by the process that holds its lock (<see cref="SessionLock"/>).
/// </summary>
public sealed class SessionStore
{
    public const string LastActiveFile = "last-active";

    // Writes of one session are serialised in this process (striped: bounded however many sessions there are). Across processes the session lock
    // guarantees a single writer.
    private readonly object[] _stripes = Enumerable.Range(0, 16).Select(_ => new object()).ToArray();

    private object StripeOf(string name) => _stripes[(int)((uint)StringComparer.Ordinal.GetHashCode(name) % (uint)_stripes.Length)];

    public SessionStore(string userDataDir)
    {
        SessionsDir = Path.Combine(userDataDir, "sessions");
        Backups = new HotExitBackupStore(Path.Combine(userDataDir, "backups"));
    }

    public string SessionsDir { get; }
    public HotExitBackupStore Backups { get; }

    public string PathOf(string name) => Path.Combine(SessionsDir, name + ".json");
    private string BackupPathOf(string name) => PathOf(name) + ".bak";

    public bool Exists(string name) => SessionNames.IsValid(name) && (File.Exists(PathOf(name)) || File.Exists(BackupPathOf(name)));

    public SessionLoadResult Load(string name)
    {
        if (SessionNames.Validate(name) is { } invalid) return new SessionLoadResult(null, SessionLoadStatus.Missing, invalid);
        var main = TryRead(PathOf(name), out var mainError);
        if (main.Session is not null) return new SessionLoadResult(main.Session with { Name = name }, SessionLoadStatus.Loaded);
        if (main.Error == SessionFormatError.TooNew) return new SessionLoadResult(null, SessionLoadStatus.TooNew, mainError);

        var backup = TryRead(BackupPathOf(name), out var backupError);
        if (backup.Session is not null)
            return new SessionLoadResult(backup.Session with { Name = name }, SessionLoadStatus.RecoveredFromBackup,
                main.Missing ? "the session file was missing; restored the previous version" : $"the session file was unreadable ({mainError}); restored the previous version");
        if (backup.Error == SessionFormatError.TooNew) return new SessionLoadResult(null, SessionLoadStatus.TooNew, backupError);
        if (main.Missing && backup.Missing) return new SessionLoadResult(null, SessionLoadStatus.Missing);
        return new SessionLoadResult(null, SessionLoadStatus.Corrupt, mainError ?? backupError);
    }

    private static (SessionDocument? Session, SessionFormatError? Error, bool Missing) TryRead(string path, out string? message)
    {
        message = null;
        byte[]? bytes;
        try { bytes = AtomicFile.ReadOrNull(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { message = ex.Message; return (null, SessionFormatError.Corrupt, false); }
        if (bytes is null) return (null, null, true);
        try { return (SessionSerializer.Deserialize(bytes), null, false); }
        catch (SessionFormatException ex) { message = ex.Message; return (null, ex.Error, false); }
    }

    /// <summary>Writes the session atomically (the previous version becomes <c>.bak</c>). Refuses to overwrite a file written by a newer CsCode.</summary>
    public void Save(SessionDocument session)
    {
        if (SessionNames.Validate(session.Name) is { } invalid) throw new ArgumentException(invalid, nameof(session));
        var path = PathOf(session.Name);
        var bytes = SessionSerializer.Serialize(session);
        lock (StripeOf(session.Name))
        {
            var existing = TryRead(path, out _);
            if (existing.Error == SessionFormatError.TooNew)
                throw new InvalidOperationException($"Session '{session.Name}' was written by a newer CsCode; not overwriting it.");
            AtomicFile.Write(path, bytes, existing.Session is not null ? BackupPathOf(session.Name) : null);
        }
    }

    /// <summary>Saved sessions, most recently used first. Unreadable files are listed by name with no details rather than hidden.</summary>
    public IReadOnlyList<SessionSummary> List()
    {
        if (!Directory.Exists(SessionsDir)) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json*"))
        {
            var fileName = Path.GetFileName(file);
            var stem = fileName.EndsWith(".json", StringComparison.Ordinal) ? fileName[..^5]
                : fileName.EndsWith(".json.bak", StringComparison.Ordinal) ? fileName[..^9] : null;
            if (stem is not null && SessionNames.IsValid(stem)) names.Add(stem);
        }
        var list = new List<SessionSummary>(names.Count);
        foreach (var name in names)
        {
            // Fast path: the summary fields straight from the main file. Anything unusual (missing, damaged, old format) takes the full load.
            SessionSummary? summary = null;
            try
            {
                if (AtomicFile.ReadOrNull(PathOf(name)) is { } bytes) summary = SessionSerializer.TryReadSummary(bytes, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (summary is null)
            {
                var loaded = Load(name);
                summary = loaded.Session is { } s
                    ? new SessionSummary(name, s.IsUnnamed, s.LastUsedUtc, s.Folders, s.Editors.Count, s.WasResident)
                    : new SessionSummary(name, false, DateTimeOffset.MinValue, [], 0);
            }
            list.Add(summary);
        }
        return list.OrderByDescending(s => s.LastUsedUtc).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Renames a saved session and its hot-exit backups. The caller holds both names' locks.</summary>
    public void Rename(string from, string to)
    {
        if (SessionNames.Validate(to) is { } invalid) throw new ArgumentException(invalid, nameof(to));
        if (Exists(to)) throw new InvalidOperationException($"A session named '{to}' already exists.");
        var loaded = Load(from);
        if (loaded.Session is null) throw new InvalidOperationException($"Session '{from}' cannot be read: {loaded.Message ?? loaded.Status.ToString()}");
        Save(loaded.Session with { Name = to });
        Backups.Rename(from, to);
        DeleteFiles(from);
        if (ReadLastActive() == from) WriteLastActive(to);
    }

    /// <summary>Deletes a saved session and its hot-exit backups. The caller holds its lock.</summary>
    public void Delete(string name)
    {
        if (!SessionNames.IsValid(name)) return;
        DeleteFiles(name);
        Backups.DeleteAll(name);
    }

    private void DeleteFiles(string name)
    {
        foreach (var path in new[] { PathOf(name), BackupPathOf(name) })
            if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The session that was shown last (the launcher's default for <c>cscode</c> with no session).</summary>
    public string? ReadLastActive()
    {
        try
        {
            var bytes = AtomicFile.ReadOrNull(Path.Combine(SessionsDir, LastActiveFile));
            var name = bytes is null ? null : Encoding.UTF8.GetString(bytes).Trim();
            return SessionNames.IsValid(name) ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void WriteLastActive(string name)
    {
        if (!SessionNames.IsValid(name)) return;
        AtomicFile.Write(Path.Combine(SessionsDir, LastActiveFile), Encoding.UTF8.GetBytes(name));
    }
}
