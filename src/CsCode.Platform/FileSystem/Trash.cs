using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CsCode.Platform.FileSystem;

/// <summary>Moves a file or directory to the OS trash/recycle bin. Blocking: call from the IO lane only. Throws <see cref="IOException"/> when it cannot (callers decide whether to delete permanently).</summary>
public interface ITrashService
{
    void MoveToTrash(string path, CancellationToken ct = default);
}

public static class PlatformTrash
{
    public static ITrashService Create(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        if (OperatingSystem.IsWindows()) return new WindowsRecycleBin();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return new MacTrash(Path.Combine(home, ".Trash"));
        var xdg = getEnv("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        return new FreedesktopTrash(Path.Combine(dataHome, "Trash"));
    }
}

/// <summary>
/// freedesktop.org Trash specification 1.0 (home trash): <c>files/</c> holds the item, <c>info/&lt;name&gt;.trashinfo</c> records where it came from
/// and when, so file managers can "Restore". The .trashinfo is created first with CreateNew, which atomically CLAIMS the trash name
/// (two processes trashing "a.txt" at once get "a.txt" and "a.2.txt"). Items on another file system are copied then removed.
/// </summary>
public sealed class FreedesktopTrash(string trashRoot, TimeProvider? clock = null) : ITrashService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string FilesDir => Path.Combine(trashRoot, "files");
    public string InfoDir => Path.Combine(trashRoot, "info");

    public void MoveToTrash(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path).TrimEnd('/');
        var isDir = Directory.Exists(path) && new FileInfo(path).LinkTarget is null;
        if (!isDir && !File.Exists(path) && new FileInfo(path).LinkTarget is null) throw new FileNotFoundException("Nothing to trash", path);

        CreatePrivateDirectory(trashRoot);
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InfoDir);

        var name = Path.GetFileName(path);
        var (stem, ext) = isDir ? (name, "") : SplitExtension(name);
        string candidate = name, infoPath;
        for (var n = 1; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            candidate = n == 1 ? name : $"{stem}.{n}{ext}";
            infoPath = Path.Combine(InfoDir, candidate + ".trashinfo");
            if (File.Exists(Path.Combine(FilesDir, candidate)) || Directory.Exists(Path.Combine(FilesDir, candidate))) continue; // stale item without info: keep it
            try
            {
                using var fs = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var text = "[Trash Info]\nPath=" + EncodePath(path) + "\nDeletionDate=" + _clock.GetLocalNow().ToString("yyyy-MM-dd'T'HH:mm:ss") + "\n";
                fs.Write(new UTF8Encoding(false).GetBytes(text));
                break;
            }
            catch (IOException) when (File.Exists(infoPath)) { /* name taken by a concurrent trasher: try the next one */ }
        }

        var destination = Path.Combine(FilesDir, candidate);
        try
        {
            Move(path, destination, isDir, ct);
        }
        catch (Exception ex)
        {
            try { File.Delete(infoPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (ex is OperationCanceledException) throw;
            throw new IOException($"Could not move '{path}' to the trash: {ex.Message}", ex);
        }
    }

    private static void Move(string source, string destination, bool isDir, CancellationToken ct)
    {
        try
        {
            if (isDir) Directory.Move(source, destination);
            else File.Move(source, destination); // handles cross-device itself (copy + delete)
        }
        catch (IOException) when (isDir && Directory.Exists(source) && !Directory.Exists(destination))
        {
            // Directory.Move cannot cross file systems: copy the tree, then remove the original.
            CopyTree(source, destination, ct);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link) { if (entry is DirectoryInfo) Directory.CreateSymbolicLink(target, link); else File.CreateSymbolicLink(target, link); }
            else if (entry is DirectoryInfo) CopyTree(entry.FullName, target, ct);
            else File.Copy(entry.FullName, target);
        }
    }

    private static void CreatePrivateDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); return; }
        Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // the trash may hold private files
    }

    private static (string Stem, string Ext) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, "");
    }

    /// <summary>Percent-encodes an absolute path the way .trashinfo wants it (RFC 2396 style: '/' kept, everything unusual escaped).</summary>
    internal static string EncodePath(string path) =>
        "/" + string.Join('/', path.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
}

/// <summary>macOS: move into <c>~/.Trash</c> (Finder's "Put Back" metadata is not written).</summary>
public sealed class MacTrash(string trashDir) : ITrashService
{
    public void MoveToTrash(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path).TrimEnd('/');
        var isDir = Directory.Exists(path) && new FileInfo(path).LinkTarget is null;
        if (!isDir && !File.Exists(path) && new FileInfo(path).LinkTarget is null) throw new FileNotFoundException("Nothing to trash", path);
        Directory.CreateDirectory(trashDir);

        var name = Path.GetFileName(path);
        var dot = isDir ? -1 : name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var destination = Path.Combine(trashDir, name);
        for (var n = 2; File.Exists(destination) || Directory.Exists(destination); n++) destination = Path.Combine(trashDir, $"{stem} {n}{ext}"); // Finder style: "name 2.ext"
        try
        {
            if (isDir) Directory.Move(path, destination); else File.Move(path, destination);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Could not move '{path}' to the trash: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Windows recycle bin through <c>SHFileOperationW</c> with FOF_ALLOWUNDO. NOTE: written against the documented API but NOT exercised on
/// Windows in this repository's test runs (Linux-only CI); see docs/PLATFORM.md.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : ITrashService
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct ShFileOpStruct32
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation64(ref ShFileOpStruct64 op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHFileOperation32(ref ShFileOpStruct32 op);

    public void MoveToTrash(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Nothing to recycle", path);
        var from = path + "\0\0"; // pFrom is a double-null-terminated list
        const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI;

        int rc;
        bool aborted;
        if (IntPtr.Size == 8)
        {
            var op = new ShFileOpStruct64 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            rc = SHFileOperation64(ref op);
            aborted = op.fAnyOperationsAborted != 0;
        }
        else
        {
            var op = new ShFileOpStruct32 { wFunc = FO_DELETE, pFrom = from, fFlags = flags };
            rc = SHFileOperation32(ref op);
            aborted = op.fAnyOperationsAborted != 0;
        }
        if (rc != 0 || aborted) throw new IOException($"Could not move '{path}' to the recycle bin (SHFileOperation returned 0x{rc:X})");
    }
}
