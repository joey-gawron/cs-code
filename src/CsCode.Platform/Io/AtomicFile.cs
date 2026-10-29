namespace CsCode.Platform.Io;

/// <summary>
/// Crash-safe whole-file replacement: write a sibling temp file, fsync it, then rename over the target. A reader (or a crash at
/// any point) sees either the complete old content or the complete new content, never a torn file. Blocking by design: call it
/// only from the IO lane.
/// </summary>
internal static class AtomicFile
{
    public const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>. Symlinks are written THROUGH (the link keeps pointing at its
    /// target), an existing file's permission bits are preserved, and parents are created when asked to.
    /// <paramref name="beforeCommit"/> runs after the temp file is durable and before the rename (fault-injection point for tests).
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> content, bool createParents, CancellationToken ct, Action? beforeCommit = null)
    {
        var target = ResolveFinalTarget(path);
        var dir = Path.GetDirectoryName(target) ?? throw new ArgumentException("Path has no directory", nameof(path));
        if (createParents) Directory.CreateDirectory(dir);

        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(target))
        {
            try { mode = File.GetUnixFileMode(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.None))
            {
                var offset = 0;
                while (offset < content.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = Math.Min(ChunkSize, content.Length - offset);
                    fs.Write(content.Slice(offset, n));
                    offset += n;
                }
                fs.Flush(flushToDisk: true); // fsync BEFORE the rename: otherwise a power cut can leave a renamed-but-empty file
            }

            if (!OperatingSystem.IsWindows() && mode is { } m) File.SetUnixFileMode(temp, m);
            ct.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
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
    public static byte[] ReadAll(string path, CancellationToken ct)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        var length = RandomAccess.GetLength(handle);
        if (length > Array.MaxLength) throw new IOException($"File is too large to read into memory ({length:N0} bytes): {path}");

        if (length == 0)
        {
            // /proc-style files report 0 but have content: fall back to reading until EOF.
            return ReadUntilEof(handle, ct);
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var n = RandomAccess.Read(handle, buffer.AsSpan(read, Math.Min(ChunkSize, buffer.Length - read)), read);
            if (n == 0) break; // truncated while reading
            read += n;
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

    private static byte[] ReadUntilEof(Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationToken ct)
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
        }
        return ms.ToArray();
    }
}
