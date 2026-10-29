namespace CsCode.Sessions;

/// <summary>
/// Crash-safe file replacement: the new content goes to a temp file next to the target, is flushed to the device, and then renamed over the target
/// (rename is atomic on POSIX and NTFS), so a reader sees either the old or the new file, never a torn one. With a backup path the previous
/// version is kept as <c>.bak</c> (the store falls back to it when the main file is unreadable). Synchronous by design: callers run it on a
/// background lane, never on the UI thread.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> content, string? backupPath = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (backupPath is not null && File.Exists(path))
            {
                // The current file becomes the backup only when it is complete: it was itself written by this method, so it is.
                File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
            }
            else File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* best effort: a leftover temp file is ignored by every reader */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads the whole file, or null when it does not exist.</summary>
    public static byte[]? ReadOrNull(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
