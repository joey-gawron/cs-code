using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsCode.Sessions;

/// <summary>What the file on disk looked like when a backup was taken (to detect that it changed meanwhile).</summary>
public sealed record DiskIdentity(long Size, DateTimeOffset ModifiedUtc, string? Sha256);

/// <summary>Unsaved text of one document, as the shell captured it (UI thread: a string snapshot, nothing else).</summary>
public sealed record BackupEntry(string Id, string Uri, string Text, string Encoding, string EndOfLine, string? LanguageId, int DocumentVersion);

/// <summary>A stored backup (metadata; the text is read on demand).</summary>
public sealed record BackupInfo(
    int Version,
    string Id,
    string Uri,
    string Encoding,
    string EndOfLine,
    string? LanguageId,
    int DocumentVersion,
    DiskIdentity? Base,
    long TextBytes,
    DateTimeOffset WrittenUtc)
{
    public const int CurrentVersion = 1;
}

public enum BackupDiskState
{
    /// <summary>The file is as it was when the backup was taken (or the buffer is untitled): restore the unsaved text silently.</summary>
    Unchanged,
    /// <summary>The file changed on disk after the backup was taken: ask the user (keep the unsaved text, or load the disk version).</summary>
    ChangedOnDisk,
    /// <summary>The file was deleted meanwhile: the unsaved text is restored into a buffer that will recreate it when saved.</summary>
    MissingOnDisk,
    Untitled,
}

public sealed record BackupWriteResult(IReadOnlyList<string> Written, IReadOnlyList<string> Skipped, long TotalBytes, bool OverLimit);

/// <summary>
/// Hot-exit backups (VS Code's "hot exit"): the unsaved text of every dirty document of a session, so the session restores without save prompts.
/// Layout: <c>{UserDataDir}/backups/&lt;session&gt;/&lt;id&gt;.txt</c> (UTF-8 text) + <c>&lt;id&gt;.json</c> (<see cref="BackupInfo"/>: uri, encoding/EOL,
/// and the on-disk <see cref="DiskIdentity"/> at backup time, so a restore can tell that the file changed meanwhile). Each file is written
/// atomically, text first; a backup without readable metadata is ignored. The total per session is bounded (<c>cscode.sessions.maxBackupMb</c>):
/// above it, the remaining documents are not backed up and the result says so (the shell warns and falls back to save prompts for them).
/// Synchronous; runs on the session lane.
/// </summary>
public sealed class HotExitBackupStore
{
    /// <summary>Files above this size are identified by size and time only (hashing them on every autosave would cost too much).</summary>
    public const long HashLimitBytes = 32L * 1024 * 1024;

    private readonly Func<DateTimeOffset> _now;

    public HotExitBackupStore(string root, Func<DateTimeOffset>? now = null)
    {
        Root = root;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string Root { get; }

    public string DirectoryOf(string session) => Path.Combine(Root, session);

    /// <summary>Stable id of a document's backup within a session (hash of its URI), so re-backing up a document replaces its files.</summary>
    public static string IdFor(string uri) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri)))[..20];

    /// <summary>
    /// Makes the session's backups exactly <paramref name="keep"/> ∪ the ids of <paramref name="changed"/>: writes the changed entries (whose text
    /// differs from the stored one), deletes every other backup. <paramref name="keep"/> lists ids whose stored backup is still current.
    /// </summary>
    public BackupWriteResult Write(string session, IReadOnlyList<BackupEntry> changed, IReadOnlyCollection<string> keep, long maxBytes)
    {
        var dir = DirectoryOf(session);
        var existing = List(session).ToDictionary(b => b.Id, StringComparer.Ordinal);
        long total = keep.Where(existing.ContainsKey).Sum(id => existing[id].TextBytes);
        var written = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in changed)
        {
            var bytes = Encoding.UTF8.GetBytes(entry.Text);
            if (total + bytes.LongLength > maxBytes) { skipped.Add(entry.Uri); continue; }
            Directory.CreateDirectory(dir);
            var info = new BackupInfo(BackupInfo.CurrentVersion, entry.Id, entry.Uri, entry.Encoding, entry.EndOfLine, entry.LanguageId, entry.DocumentVersion,
                Identify(entry.Uri), bytes.LongLength, _now());
            AtomicFile.Write(Path.Combine(dir, entry.Id + ".txt"), bytes);
            AtomicFile.Write(Path.Combine(dir, entry.Id + ".json"), JsonSerializer.SerializeToUtf8Bytes(info, BackupJsonContext.Default.BackupInfo));
            total += bytes.LongLength;
            written.Add(entry.Id);
        }
        var live = new HashSet<string>(keep, StringComparer.Ordinal);
        live.UnionWith(written);
        foreach (var stale in existing.Keys.Where(id => !live.Contains(id))) DeleteBackup(dir, stale);
        if (live.Count == 0 && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch (IOException) { /* raced with a writer: harmless */ }
        }
        return new BackupWriteResult(written, skipped, total, skipped.Count > 0);
    }

    private static void DeleteBackup(string dir, string id)
    {
        foreach (var ext in new[] { ".json", ".txt" })
        {
            var p = Path.Combine(dir, id + ext);
            try { if (File.Exists(p)) File.Delete(p); }
            catch (IOException) { /* deleted on the next write */ }
        }
    }

    /// <summary>The session's readable backups (metadata whose text file exists).</summary>
    public IReadOnlyList<BackupInfo> List(string session)
    {
        var dir = DirectoryOf(session);
        if (!Directory.Exists(dir)) return [];
        var result = new List<BackupInfo>();
        foreach (var meta in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var bytes = AtomicFile.ReadOrNull(meta);
                if (bytes is null) continue;
                var info = JsonSerializer.Deserialize(bytes, BackupJsonContext.Default.BackupInfo);
                if (info is null || info.Version > BackupInfo.CurrentVersion || !File.Exists(Path.Combine(dir, info.Id + ".txt"))) continue;
                result.Add(info);
            }
            catch (JsonException) { /* a damaged metadata file: that backup is lost, the others are not */ }
            catch (IOException) { }
        }
        return result;
    }

    public string? ReadText(string session, string id)
    {
        var bytes = AtomicFile.ReadOrNull(Path.Combine(DirectoryOf(session), id + ".txt"));
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Compares the file a backup belongs to with its state when the backup was taken.</summary>
    public static BackupDiskState Check(BackupInfo backup)
    {
        if (!System.Uri.TryCreate(backup.Uri, UriKind.Absolute, out var uri) || !uri.IsFile) return BackupDiskState.Untitled;
        var now = Identify(backup.Uri);
        if (now is null) return BackupDiskState.MissingOnDisk;
        if (backup.Base is not { } before) return BackupDiskState.ChangedOnDisk; // it did not exist then, it does now
        if (before.Sha256 is not null && now.Sha256 is not null) return before.Sha256 == now.Sha256 ? BackupDiskState.Unchanged : BackupDiskState.ChangedOnDisk;
        return before.Size == now.Size && before.ModifiedUtc == now.ModifiedUtc ? BackupDiskState.Unchanged : BackupDiskState.ChangedOnDisk;
    }

    /// <summary>Size, time and (below <see cref="HashLimitBytes"/>) content hash of a local file; null for missing files and non-file URIs.</summary>
    public static DiskIdentity? Identify(string uriText)
    {
        if (!System.Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        try
        {
            var file = new FileInfo(uri.LocalPath);
            if (!file.Exists) return null;
            string? hash = null;
            if (file.Length <= HashLimitBytes)
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            return new DiskIdentity(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void DeleteAll(string session)
    {
        var dir = DirectoryOf(session);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public void Rename(string from, string to)
    {
        var source = DirectoryOf(from);
        if (!Directory.Exists(source)) return;
        var target = DirectoryOf(to);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(source, target);
    }

    /// <summary>Total bytes of the session's backups.</summary>
    public long SizeOf(string session) => List(session).Sum(b => b.TextBytes);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BackupInfo))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
