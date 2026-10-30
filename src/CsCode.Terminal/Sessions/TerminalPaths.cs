namespace CsCode.Terminal.Sessions;

/// <summary>File-system lookups for terminal links (blocking I/O: call off the UI thread).</summary>
public static class TerminalPaths
{
    /// <summary>
    /// A path printed in a terminal, as an existing file: absolute or <c>~/</c> as is, relative against the terminal's working directory
    /// (OSC 7) first and then each workspace folder. Null when nothing matches.
    /// </summary>
    public static string? ResolveFile(string path, string? cwd, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        try
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? Path.GetFullPath(path) : null;
            foreach (var root in cwd is null ? folders : folders.Prepend(cwd))
            {
                var candidate = Path.GetFullPath(Path.Combine(root, path));
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }
}
