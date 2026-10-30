using System.Text;

namespace CsCode.Dap.Transport;

/// <summary>
/// A startup file a <see cref="DebugAdapterStdioProcess"/> reads that must not be on its command line, which every local user can read through
/// <c>/proc/&lt;pid&gt;/cmdline</c>: GDB's launch environment (<c>-ix</c> commands; the values are often secrets). It is written afresh EACH time
/// an adapter is started from the descriptor — a session whose adapter has no <c>restart</c> request starts a new adapter from the same
/// descriptor — as a 0600 file in a new private 0700 directory (<see cref="PrivateSocket"/>: under <c>$XDG_RUNTIME_DIR</c> when set; directories
/// of CsCode processes that are gone are swept first). That directory belongs to that one adapter process: it is deleted when the adapter exits,
/// is closed, or fails to start. The argument equal to <see cref="Placeholder"/> becomes the file's path. Unix only (a Windows command line is not
/// readable by other users: the factory passes the commands as arguments there).
/// </summary>
public sealed class PrivateStartupFile
{
    /// <summary>Stands for the file's path among the descriptor's arguments (a NUL can never be part of a real argument).</summary>
    public const string Placeholder = "\0cscode-private-startup-file";

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public PrivateStartupFile(string directoryPrefix, string fileName, IReadOnlyList<string> lines, string description)
    {
        DirectoryPrefix = directoryPrefix;
        FileName = fileName;
        Lines = [.. lines];
        Description = description;
    }

    /// <summary>The private directories' name prefix (<c>&lt;prefix&gt;-&lt;pid&gt;-&lt;start&gt;-&lt;random&gt;</c>, see <see cref="PrivateSocket"/>).</summary>
    public string DirectoryPrefix { get; }

    public string FileName { get; }

    /// <summary>What the file passes, for messages ("the launch environment").</summary>
    public string Description { get; }

    /// <summary>The file's lines (never logged: see <see cref="ToString"/>).</summary>
    internal IReadOnlyList<string> Lines { get; }

    /// <summary>Where the private directories are made (null: <see cref="PrivateSocket.DefaultRoot"/>); a seam for tests.</summary>
    public string? Root { get; init; }

    /// <summary>
    /// Writes the file to a new private directory and returns both paths. Throws <see cref="IOException"/> (nothing left behind) when no private
    /// directory can be made or the file cannot be written.
    /// </summary>
    internal (string Path, string Directory) Write()
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Private startup files are not used on Windows.");
        var dir = PrivateSocket.Create(DirectoryPrefix, Root);
        var path = System.IO.Path.Combine(dir.Directory!, FileName);
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerReadWrite });
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var line in Lines) writer.Write(line + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dir.Delete();
            throw new IOException(ex.Message, ex);
        }
        return (path, dir.Directory!);
    }

    /// <summary>Never the contents (they may be secrets).</summary>
    public override string ToString() => $"{FileName} ({Description}, written privately at each start)";
}
