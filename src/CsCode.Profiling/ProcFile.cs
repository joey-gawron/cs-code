using Microsoft.Win32.SafeHandles;

namespace CsCode.Profiling;

/// <summary>
/// A <c>/proc</c> pseudo-file opened once and re-read in place: every <see cref="Read"/> is one <c>pread(fd, buf, 0)</c> into a caller-owned buffer,
/// which makes the kernel regenerate the text, so periodic sampling allocates nothing. A handle on <c>/proc/&lt;pid&gt;/…</c> stays bound to the
/// process it was opened for (reads fail with ESRCH once it exited, even if the pid is reused). Linux only.
/// </summary>
internal sealed class ProcFile : IDisposable
{
    private readonly SafeFileHandle _handle;

    private ProcFile(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens <paramref name="path"/> for reading, or returns null when it does not exist / cannot be read (process gone, not Linux).</summary>
    public static ProcFile? TryOpen(string path)
    {
        try { return new ProcFile(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>Reads the whole file (up to the buffer's size) into <paramref name="buffer"/>; -1 when the read failed (the process exited).</summary>
    public int Read(Span<byte> buffer)
    {
        try { return RandomAccess.Read(_handle, buffer, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { return -1; }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// The value of a <c>Key:   1234 kB</c> line of a status-style file, in bytes (the unit is always kB in /proc), or -1 when absent. With
    /// <paramref name="kilobytes"/> false the number is returned as is (e.g. <c>Threads:</c>).
    /// </summary>
    public static long Field(ReadOnlySpan<byte> text, ReadOnlySpan<byte> key, bool kilobytes = true)
    {
        var at = 0;
        while (at < text.Length)
        {
            var rest = text[at..];
            var eol = rest.IndexOf((byte)'\n');
            var line = eol < 0 ? rest : rest[..eol];
            if (line.StartsWith(key) && line.Length > key.Length && line[key.Length] == (byte)':')
            {
                var value = ParseFirstNumber(line[(key.Length + 1)..]);
                return value < 0 ? -1 : kilobytes ? value * 1024 : value;
            }
            if (eol < 0) break;
            at += eol + 1;
        }
        return -1;
    }

    /// <summary>The first unsigned decimal number in <paramref name="text"/> (leading blanks skipped), or -1.</summary>
    public static long ParseFirstNumber(ReadOnlySpan<byte> text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == (byte)' ' || text[i] == (byte)'\t')) i++;
        if (i >= text.Length || text[i] < (byte)'0' || text[i] > (byte)'9') return -1;
        long value = 0;
        for (; i < text.Length && text[i] >= (byte)'0' && text[i] <= (byte)'9'; i++) value = value * 10 + (text[i] - (byte)'0');
        return value;
    }
}
