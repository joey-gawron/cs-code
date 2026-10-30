namespace CsCode.Platform.Io;

/// <summary>
/// Crash-safe whole-file replacement that keeps the file's identity, as VS Code and Vim (<c>backupcopy=auto</c>) do (lsp-core-8):
/// <list type="bullet">
/// <item><b>Normally</b>: write a sibling temp file, fsync it, give it the target's owner, group, mode and extended attributes (ACLs, SELinux label),
/// then rename it over the target. A reader (or a crash at any point) sees either the complete old content or the complete new content.</item>
/// <item><b>In place</b> when a rename would lose something: the target has other hard links (they would keep the old content), its owner or group
/// cannot be given to a new file (a colleague's file in a shared directory), an extended attribute cannot be copied, or no temp file can be created
/// next to it (a writable file in a read-only directory). The new content is written over the existing inode and fsynced; if that fails part way
/// (disk full, I/O error, cancellation) the old content is written back. Not crash-safe against a power cut mid-write — the price VS Code pays for
/// every save.</item>
/// </list>
/// Linux reads link count, owner and group with <c>statx</c>; on other systems only the mode is kept (the previous behaviour). Blocking by design:
/// call it only from the IO lane.
/// </summary>
internal static class AtomicFile
{
    public const int ChunkSize = 64 * 1024;

    /// <summary>The largest file whose old content is kept in memory so an in-place write that fails can be undone.</summary>
    private const long MaxRestoreBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>. Symlinks are written THROUGH (the link keeps pointing at its
    /// target), an existing file keeps its links, owner, group, mode and extended attributes, and parents are created when asked to.
    /// <paramref name="beforeCommit"/> runs before anything replaces the target: after the temp file is durable and before the rename, or before an
    /// in-place write starts (fault-injection point for tests).
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> content, bool createParents, CancellationToken ct, Action? beforeCommit = null, Action? onChunk = null)
    {
        var target = ResolveFinalTarget(path);
        var dir = Path.GetDirectoryName(target) ?? throw new ArgumentException("Path has no directory", nameof(path));
        if (createParents) Directory.CreateDirectory(dir);

        UnixFileMode? mode = null;
        UnixFileIdentity? identity = null;
        var exists = File.Exists(target);
        if (!OperatingSystem.IsWindows() && exists)
        {
            try { mode = File.GetUnixFileMode(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            identity = UnixNative.TryStat(target);
        }

        // Other names of the same inode would keep the old content after a rename.
        if (identity is { Links: > 1 })
        {
            WriteInPlace(target, content, ct, beforeCommit, onChunk);
            return;
        }

        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        FileStream fs;
        try { fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.None); }
        catch (UnauthorizedAccessException) when (exists)
        {
            // A writable file in a directory we may not create files in: VS Code saves it in place, and so do we.
            WriteInPlace(target, content, ct, beforeCommit, onChunk);
            return;
        }

        var committed = false;
        try
        {
            using (fs)
            {
                var offset = 0;
                while (offset < content.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = Math.Min(ChunkSize, content.Length - offset);
                    fs.Write(content.Slice(offset, n));
                    offset += n;
                    onChunk?.Invoke();
                }
                fs.Flush(flushToDisk: true); // fsync BEFORE the rename: otherwise a power cut can leave a renamed-but-empty file
            }

            // Owner first (chown clears set-id bits), then the mode, then the extended attributes (an ACL also rewrites the group bits).
            if (identity is { } id && !TakeIdentity(temp, id))
            {
                File.Delete(temp);
                WriteInPlace(target, content, ct, beforeCommit, onChunk);
                committed = true;
                return;
            }
            if (!OperatingSystem.IsWindows() && mode is { } m) File.SetUnixFileMode(temp, m);
            if (identity is not null && !UnixNative.TryCopyXattrs(target, temp))
            {
                File.Delete(temp);
                WriteInPlace(target, content, ct, beforeCommit, onChunk);
                committed = true;
                return;
            }
            ct.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            File.Move(temp, target, overwrite: true);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Gives the temp file the target's owner and group when they differ from what a new file gets; false when that is not allowed.</summary>
    private static bool TakeIdentity(string temp, UnixFileIdentity id)
    {
        if (UnixNative.TryStat(temp) is not { } fresh) return true; // cannot tell: keep the old behaviour (mode only)
        if (fresh.Uid == id.Uid && fresh.Gid == id.Gid) return true;
        return UnixNative.TryChown(temp, id.Uid, id.Gid);
    }

    /// <summary>
    /// Overwrites the existing file (same inode: links, owner, group, mode, xattrs untouched): write from offset 0, truncate, fsync. When that fails
    /// part way, the old content (kept in memory up to <see cref="MaxRestoreBytes"/>) is written back before the exception propagates.
    /// </summary>
    internal static void WriteInPlace(string target, ReadOnlySpan<byte> content, CancellationToken ct, Action? beforeCommit, Action? onChunk)
    {
        ct.ThrowIfCancellationRequested();
        FileStream fs;
        byte[]? original = null;
        try
        {
            fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1, FileOptions.None);
            if (fs.Length <= MaxRestoreBytes)
            {
                original = new byte[fs.Length];
                fs.ReadExactly(original);
            }
        }
        catch (UnauthorizedAccessException)
        {
            fs = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read, bufferSize: 1, FileOptions.None); // write-only file: no undo
        }

        using (fs)
        {
            beforeCommit?.Invoke();
            try
            {
                fs.Position = 0;
                var offset = 0;
                while (offset < content.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = Math.Min(ChunkSize, content.Length - offset);
                    fs.Write(content.Slice(offset, n));
                    offset += n;
                    onChunk?.Invoke();
                }
                fs.SetLength(content.Length);
                fs.Flush(flushToDisk: true);
            }
            catch when (original is not null)
            {
                try
                {
                    fs.Position = 0;
                    fs.Write(original);
                    fs.SetLength(original.Length);
                    fs.Flush(flushToDisk: true);
                }
                catch (IOException) { }
                throw;
            }
        }
    }

    /// <summary>Follows a (chain of) symlink(s) to the real file path; a dangling link resolves to its would-be target.</summary>
    public static string ResolveFinalTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null) return resolved.FullName;
                var parent = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/";
                return Path.GetFullPath(info.LinkTarget, parent);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.GetFullPath(path);
    }

    /// <summary>Reads a whole file in 64 KB chunks, observing cancellation between chunks.</summary>
    public static byte[] ReadAll(string path, CancellationToken ct, Action? onChunk = null)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        var length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength) throw new IOException($"File is too large to read into memory ({length:N0} bytes): {path}");

        if (length == 0)
        {
            // /proc-style files report 0 but have content: fall back to reading until EOF.
            return ReadUntilEof(handle, ct, onChunk);
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(handle, buffer.AsSpan(read, Math.Min(ChunkSize, buffer.Length - read)), read);
            if (n == 0) break; // truncated while reading
            read += n;
            onChunk?.Invoke();
        }
        if (read < buffer.Length) Array.Resize(ref buffer, read);
        else
        {
            // grown while reading: pick up the tail so a concurrent append is not silently cut at the stat length
            var tail = ReadTail(handle, read, ct);
            if (tail.Length > 0) { var all = new byte[buffer.Length + tail.Length]; buffer.CopyTo(all, 0); tail.CopyTo(all, buffer.Length); return all; }
        }
        return buffer;
    }

    private static byte[] ReadTail(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, CancellationToken ct)
    {
        var probe = new byte[1];
        if (RandomAccess.Read(handle, probe, offset) == 0) return [];
        using var ms = new MemoryStream();
        ms.Write(probe);
        var chunk = new byte[ChunkSize];
        offset++;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(handle, chunk, offset);
            if (n == 0) break;
            ms.Write(chunk, 0, n);
            offset += n;
        }
        return ms.ToArray();
    }

    private static byte[] ReadUntilEof(Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationToken ct, Action? onChunk)
    {
        using var ms = new MemoryStream();
        var chunk = new byte[ChunkSize];
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n;
            try { n = RandomAccess.Read(handle, chunk, offset); }
            catch (NotSupportedException) { break; }
            if (n == 0) break;
            ms.Write(chunk, 0, n);
            offset += n;
            onChunk?.Invoke();
            // Files that claim length 0 but have content (procfs) are tiny; an endless source (/dev/zero) must not eat all memory.
            if (offset > MaxUnknownLengthRead) throw new IOException($"File reports no size and is larger than {MaxUnknownLengthRead / (1024 * 1024)} MB; refusing to read it into memory");
        }
        return ms.ToArray();
    }

    private const long MaxUnknownLengthRead = 256L * 1024 * 1024;
}
