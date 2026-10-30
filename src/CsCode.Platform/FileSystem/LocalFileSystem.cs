using System.IO.Enumeration;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.FileSystem;

/// <summary>
/// The <c>file:</c> file system. Every operation is performed by a blocking call on the dedicated IO lane (see <see cref="IoLane"/>): the
/// caller gets a task back immediately, and neither a slow disk nor a huge copy can occupy the UI thread or the shared thread pool.
/// Large reads/writes work in 64 KB chunks and check cancellation between chunks. (Not <c>FileStream</c>'s async API on purpose: on
/// Unix .NET implements it by queueing synchronous calls to the thread pool, which is exactly what the lane is there to avoid.)
/// <list type="bullet">
/// <item>Other URI schemes throw <see cref="NotSupportedException"/>.</item>
/// <item>Writes are atomic (temp file + fsync + rename) and keep the file: hard links, owner, group, mode and extended attributes (written in
/// place when a rename would lose them, see <see cref="AtomicFile"/>); they write THROUGH symlinks and refuse read-only files.</item>
/// <item>Delete can use the trash (freedesktop spec on Linux incl. <c>.trashinfo</c>, Finder's Trash with "Put Back" on macOS, recycle bin on Windows).</item>
/// <item>Whether names that differ only in case are one entry is decided per volume (<see cref="IsCaseSensitive"/>).</item>
/// <item>Directory symlinks are never followed by recursive delete/copy, so a link cycle cannot make them run away.</item>
/// </list>
/// </summary>
public sealed class LocalFileSystem : IFileSystem
{
    private const long ChunkedCopyThreshold = 4L * 1024 * 1024;

    private readonly IoLane _lane;
    private readonly ITrashService _trash;
    private readonly ILogger _log;

    public LocalFileSystem(IoLane lane, ITrashService? trash = null, ILogger<LocalFileSystem>? logger = null)
    {
        _lane = lane;
        _trash = trash ?? PlatformTrash.Create();
        _log = logger ?? NullLogger<LocalFileSystem>.Instance;
    }

    /// <summary>Every <c>file:</c> write keeps the file's links, owner, group, mode and xattrs (lsp-core-8): savers should write the target directly.</summary>
    public bool PreservesFileOnWrite(Uri uri) => uri.IsFile;

    /// <summary>Fault-injection point for tests: runs after the temp file is durable and before the atomic rename.</summary>
    internal Action? BeforeAtomicCommit { get; set; }

    /// <summary>Test hook invoked after every 64 KB chunk of a read, write or chunked copy (lets tests cancel deterministically mid-operation).</summary>
    internal Action? ChunkHook { get; set; }

    /// <summary>
    /// macOS: big copies are APFS clones where the volume can make one. False copies the data block by block, as on a volume that cannot clone,
    /// so a test can cancel one mid-file.
    /// </summary>
    internal bool CloneCopies { get; set; } = true;

    /// <summary>
    /// Whether the volume holding a path tells names apart by case (files-8: asked per volume on macOS, where APFS can be either). Settable so a
    /// test on a case-sensitive Linux file system can drive the case-insensitive paths.
    /// </summary>
    internal Func<string, bool> IsCaseSensitive { get; set; } = FileSystemNative.IsCaseSensitive;

    private ValueTask<T> Run<T>(Uri uri, Func<string, CancellationToken, T> work, CancellationToken ct) =>
        new(_lane.RunAsync(token => work(FileUri.ToPath(uri), token), ct)); // path conversion (and its NotSupportedException) happens inside the task

    private ValueTask Run(Uri uri, Action<string, CancellationToken> work, CancellationToken ct) =>
        new(_lane.RunAsync(token => work(FileUri.ToPath(uri), token), ct));

    // ---- stat / list / read ------------------------------------------------------------------------------------------------------

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => Run(uri, (p, _) => StatCore(p), ct);

    // On Unix .NET reports a link as existing even when its target is gone, and FileInfo.Length of a link is the length of the link text:
    // so a link is stat'ed by resolving it explicitly and then looking at what is (or is not) there.
    internal static FileStat? StatCore(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is null)
        {
            if (File.Exists(path))
                return new FileStat(FileType.File, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.IsReadOnly);
            var dir = new DirectoryInfo(path);
            return dir.Exists ? new FileStat(FileType.Directory, 0, new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero), IsReadOnly: false) : null;
        }

        var finalPath = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        if (finalPath is not null)
        {
            if (Directory.Exists(finalPath))
                return new FileStat(FileType.Directory | FileType.SymbolicLink, 0, new DateTimeOffset(new DirectoryInfo(finalPath).LastWriteTimeUtc, TimeSpan.Zero), false);
            if (File.Exists(finalPath))
            {
                var target = new FileInfo(finalPath);
                return new FileStat(FileType.File | FileType.SymbolicLink, target.Length, new DateTimeOffset(target.LastWriteTimeUtc, TimeSpan.Zero), target.IsReadOnly);
            }
        }
        // A link whose target is missing: it exists, but there is nothing to follow.
        return new FileStat(FileType.SymbolicLink, 0, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        Run<IReadOnlyList<DirectoryEntry>>(uri, ReadDirectoryCore, ct);

    private static IReadOnlyList<DirectoryEntry> ReadDirectoryCore(string path, CancellationToken ct)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var entries = new List<DirectoryEntry>();
        var enumerable = new FileSystemEnumerable<DirectoryEntry>(path, static (ref FileSystemEntry e) =>
        {
            var link = (e.Attributes & FileAttributes.ReparsePoint) != 0;
            var type = e.IsDirectory ? FileType.Directory : FileType.File;
            // a dangling link is neither file nor directory: it is just a link (File.Exists lies for those on Unix, so resolve it)
            if (link && !e.IsDirectory)
            {
                var final = new FileInfo(e.ToFullPath()).ResolveLinkTarget(returnFinalTarget: true);
                if (final is null || !File.Exists(final.FullName)) type = FileType.Unknown;
            }
            return new DirectoryEntry(e.FileName.ToString(), link ? type | FileType.SymbolicLink : type);
        }, options);

        foreach (var entry in enumerable)
        {
            if ((entries.Count & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            entries.Add(entry);
        }
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name)); // deterministic for callers and tests
        return entries;
    }

    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) =>
        Run(uri, (p, token) =>
        {
            if (Directory.Exists(p)) throw new IOException($"'{p}' is a directory, not a file");
            return AtomicFile.ReadAll(p, token, ChunkHook);
        }, ct);

    // ---- write / mkdir -----------------------------------------------------------------------------------------------------------

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) =>
        Run(uri, (p, token) =>
        {
            var target = AtomicFile.ResolveFinalTarget(p);
            if (Directory.Exists(target)) throw new IOException($"'{p}' is a directory, not a file");
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException($"'{p}' is read-only");
            AtomicFile.Write(p, content.Span, createParents, token, BeforeAtomicCommit, ChunkHook);
        }, ct);

    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        Run(uri, static (p, _) => { Directory.CreateDirectory(p); }, ct);

    // ---- delete ------------------------------------------------------------------------------------------------------------------

    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) =>
        Run(uri, (p, token) => DeleteCore(p, recursive, useTrash, token), ct);

    private void DeleteCore(string path, bool recursive, bool useTrash, CancellationToken ct)
    {
        path = path.Length > 1 ? path.TrimEnd('/', '\\') : path;
        var info = new FileInfo(path);
        var isLink = info.LinkTarget is not null;
        var isDir = !isLink && Directory.Exists(path);
        var isLinkToDir = isLink && Directory.Exists(path);
        if (!isLink && !isDir && !File.Exists(path)) throw new FileNotFoundException($"'{path}' does not exist", path);

        if (isDir && !recursive && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException($"Directory is not empty: '{path}' (pass recursive: true to delete its contents)");

        ct.ThrowIfCancellationRequested();
        if (useTrash)
        {
            _trash.MoveToTrash(path, ct);
            return;
        }

        if (isDir) Directory.Delete(path, recursive);
        else if (isLinkToDir) Directory.Delete(path); // removes the link, never the target's contents
        else File.Delete(path);                       // files and (dangling) links
    }

    // ---- rename / copy -----------------------------------------------------------------------------------------------------------

    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) =>
        new(_lane.RunAsync(token => RenameCore(FileUri.ToPath(source), FileUri.ToPath(target), overwrite, token), ct));

    private void RenameCore(string source, string target, bool overwrite, CancellationToken ct)
    {
        source = TrimEnd(source);
        target = TrimEnd(target);
        var srcIsLink = new FileInfo(source).LinkTarget is not null;
        var srcIsDir = !srcIsLink && Directory.Exists(source);
        if (!srcIsLink && !srcIsDir && !File.Exists(source)) throw new FileNotFoundException($"'{source}' does not exist", source);

        var fullSource = Path.GetFullPath(source);
        var fullTarget = Path.GetFullPath(target);
        if (string.Equals(fullSource, fullTarget, StringComparison.Ordinal)) return; // renaming onto itself
        // On a case-insensitive volume "a.txt" -> "A.txt" names the SAME entry: it is a rename, not a collision. On a case-sensitive one (Linux, a
        // case-sensitive APFS volume) an existing "A.txt" is another file and must not be overwritten, so the volume is asked, and the entries
        // themselves have the last word: two the kernel tells apart (different device or inode) are never the same.
        var caseOnlyChange = string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase) && !IsCaseSensitive(fullSource)
            && FileSystemNative.SameEntry(fullSource, fullTarget) != false;

        var targetExists = !caseOnlyChange && (File.Exists(target) || Directory.Exists(target) || new FileInfo(target).LinkTarget is not null);
        if (targetExists)
        {
            if (!overwrite) throw new IOException($"Cannot rename: '{target}' already exists");
            var targetIsDir = Directory.Exists(target) && new FileInfo(target).LinkTarget is null;
            if (targetIsDir) Directory.Delete(target, recursive: true);
            else if (srcIsDir) File.Delete(target); // a file/link is in the way of a directory
            // else File.Move(overwrite: true) below replaces the file atomically
        }

        ct.ThrowIfCancellationRequested();
        if (srcIsDir) MoveDirectory(source, target, caseOnlyChange, ct);
        else File.Move(source, target, overwrite: true);
    }

    private void MoveDirectory(string source, string target, bool caseOnlyChange, CancellationToken ct)
    {
        // "foo" -> "Foo" looks like a move into itself to a case-insensitive comparison: it is the same directory, renamed (files-1).
        if (!caseOnlyChange && IsInside(target, source)) throw new IOException($"Cannot move '{source}' into itself ('{target}')");
        try { Directory.Move(source, target); }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(target))
        {
            // Directory.Move cannot cross file systems: copy then delete
            CopyDirectory(source, target, overwrite: false, ct);
            Directory.Delete(source, recursive: true);
        }
    }

    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) =>
        new(_lane.RunAsync(token => CopyCore(FileUri.ToPath(source), FileUri.ToPath(target), overwrite, token), ct));

    private void CopyCore(string source, string target, bool overwrite, CancellationToken ct)
    {
        source = TrimEnd(source);
        target = TrimEnd(target);
        var srcIsLink = new FileInfo(source).LinkTarget is not null;
        var srcIsDir = !srcIsLink && Directory.Exists(source);
        if (!srcIsLink && !srcIsDir && !File.Exists(source)) throw new FileNotFoundException($"'{source}' does not exist", source);

        if (srcIsDir)
        {
            if (IsInside(target, source) || string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal))
                throw new IOException($"Cannot copy '{source}' into itself ('{target}')");
            CopyDirectory(source, target, overwrite, ct);
        }
        else
        {
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.Ordinal)) throw new IOException("Source and target are the same file");
            CopyEntry(new FileInfo(source), target, overwrite, ct);
        }
    }

    private void CopyDirectory(string source, string target, bool overwrite, CancellationToken ct)
    {
        if (File.Exists(target)) throw new IOException($"Cannot copy: '{target}' is a file");
        if (Directory.Exists(target) && !overwrite) throw new IOException($"Cannot copy: '{target}' already exists");
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo d && d.LinkTarget is null) CopyDirectory(entry.FullName, dest, overwrite, ct);
            else CopyEntry(entry, dest, overwrite, ct);
        }
        // macOS: the copy keeps the folder's ACL, mode, flags and extended attributes (Finder tags, quarantine) as Finder's does (gap-11). After
        // the contents: a read-only or locked folder could not be filled. Best effort, like the per-file metadata.
        if (OperatingSystem.IsMacOS()) FileSystemNative.TryCopyDirectoryMetadata(source, target);
    }

    // A symlink is copied AS a symlink (never followed): copying a tree that contains a cycle must terminate.
    private void CopyEntry(FileSystemInfo entry, string target, bool overwrite, CancellationToken ct)
    {
        if (entry.LinkTarget is { } linkTarget)
        {
            if (File.Exists(target) || new FileInfo(target).LinkTarget is not null || Directory.Exists(target))
            {
                if (!overwrite) throw new IOException($"Cannot copy: '{target}' already exists");
                File.Delete(target);
            }
            if (entry is DirectoryInfo || Directory.Exists(entry.FullName)) Directory.CreateSymbolicLink(target, linkTarget);
            else File.CreateSymbolicLink(target, linkTarget);
            return;
        }

        var file = (FileInfo)entry;
        if (!overwrite && (File.Exists(target) || Directory.Exists(target))) throw new IOException($"Cannot copy: '{target}' already exists");
        // File.Copy keeps the mode, and on macOS also the ACL, extended attributes and quarantine, cloning the data on APFS (O(1)).
        if (file.Length <= ChunkedCopyThreshold) { File.Copy(file.FullName, target, overwrite); return; }

        // Big file: chunked so cancellation takes effect mid-copy; written via a temp file so a cancelled copy leaves nothing behind.
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // copyfile, as File.Copy does, so a big file keeps its ACL, extended attributes and quarantine and is cloned where it can be
                // (gap-11); a copy that is not a clone still stops mid-file on cancellation.
                FileSystemNative.CopyFile(file.FullName, temp, CloneCopies, ChunkHook, ct);
                File.Move(temp, target, overwrite);
                return;
            }
            using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                var buffer = new byte[AtomicFile.ChunkSize];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    ChunkHook?.Invoke();
                }
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(file.FullName));
            File.Move(temp, target, overwrite);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------

    private static string TrimEnd(string p) => p.Length > 1 ? p.TrimEnd('/', '\\') : p;

    private bool IsInside(string candidate, string container)
    {
        var c = Path.GetFullPath(candidate).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var d = Path.GetFullPath(container).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return c.StartsWith(d, IsCaseSensitive(container) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
