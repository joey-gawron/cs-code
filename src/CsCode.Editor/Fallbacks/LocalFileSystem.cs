using CsCode.Core.Platform;
using CsCode.Editor.Threading;

// This file is the one place in the module that performs synchronous file I/O, and only ever on the dedicated IO lane thread
// (never the UI thread): it is the fallback used when the Platform module's IFileSystem is not registered.
#pragma warning disable RS0030

namespace CsCode.Editor.Fallbacks;

/// <summary>
/// Minimal <c>file:</c>-scheme <see cref="IFileSystem"/> used when no Platform implementation is registered (tests, tools,
/// standalone hosting). Every call hops to the IO lane. <see cref="RenameAsync"/> preserves the Unix file mode of an overwritten
/// target so an atomic save (temp file + rename) does not lose the executable bit.
/// </summary>
internal sealed class LocalFileSystem(DedicatedLane lane) : IFileSystem
{
    private static string PathOf(Uri uri)
    {
        if (!uri.IsFile) throw new NotSupportedException($"LocalFileSystem handles only file: URIs, got '{uri.Scheme}'");
        return uri.LocalPath;
    }

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) =>
        new(lane.Run<FileStat?>(() =>
        {
            var path = PathOf(uri);
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                var type = fi.LinkTarget is null ? FileType.File : FileType.File | FileType.SymbolicLink;
                return new FileStat(type, fi.Length, fi.LastWriteTimeUtc, fi.IsReadOnly);
            }
            var di = new DirectoryInfo(path);
            return di.Exists ? new FileStat(FileType.Directory, 0, di.LastWriteTimeUtc) : null;
        }, ct));

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        new(lane.Run<IReadOnlyList<DirectoryEntry>>(() =>
            new DirectoryInfo(PathOf(uri)).EnumerateFileSystemInfos()
                .Select(i => new DirectoryEntry(i.Name, i is DirectoryInfo ? FileType.Directory : FileType.File))
                .ToArray(), ct));

    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) =>
        new(lane.Run(() => File.ReadAllBytes(PathOf(uri)), ct));

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default)
    {
        var copy = content.ToArray(); // the caller's buffer may be reused after we return
        return new ValueTask(lane.Run(() =>
        {
            var path = PathOf(uri);
            if (createParents && Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, copy);
        }, ct));
    }

    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        new(lane.Run(() => { Directory.CreateDirectory(PathOf(uri)); }, ct));

    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) =>
        new(lane.Run(() =>
        {
            var path = PathOf(uri);
            if (Directory.Exists(path)) Directory.Delete(path, recursive);
            else File.Delete(path);
        }, ct));

    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) =>
        new(lane.Run(() =>
        {
            var from = PathOf(source);
            var to = PathOf(target);
            if (overwrite && File.Exists(to) && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(from, File.GetUnixFileMode(to));
                }
                catch (IOException)
                {
                    // best effort: a mode we cannot copy must not fail the save
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to, overwrite);
        }, ct));

    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) =>
        new(lane.Run(() => { File.Copy(PathOf(source), PathOf(target), overwrite); }, ct));
}
