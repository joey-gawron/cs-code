namespace CsCode.Lsp.Runtime;

/// <summary>
/// Resolves a server command to an executable file: absolute/relative path as given, else PATH (honouring a definition's PATH override),
/// else the managed tools directory <c>{ToolsDir}/lsp/**</c> (npm <c>.bin</c> folders, <c>bin</c> folders, the dir itself).
/// </summary>
public static class ToolResolver
{
    public static string? Resolve(string command, string? toolsDir, string? workingDirectory, IReadOnlyDictionary<string, string>? env)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains('/') || command.Contains('\\'))
        {
            var path = Path.IsPathRooted(command) || workingDirectory is null ? command : Path.GetFullPath(Path.Combine(workingDirectory, command));
            return IsExecutableFile(path) ? path : null;
        }

        string? pathVar = null;
        env?.TryGetValue("PATH", out pathVar);
        var onPath = FindOnPath(command, pathVar);
        if (onPath is not null) return onPath;

        return toolsDir is null ? null : FindInToolsDir(command, Path.Combine(toolsDir, "lsp"));
    }

    public static string? FindOnPath(string command, string? pathOverride)
    {
        var path = pathOverride ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in Candidates(dir, command))
                if (IsExecutableFile(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string dir, string command)
    {
        string baseDir;
        try { baseDir = Path.GetFullPath(dir); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { yield break; }
        yield return Path.Combine(baseDir, command);
        if (OperatingSystem.IsWindows())
            foreach (var ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(baseDir, command + ext);
    }

    private static string? FindInToolsDir(string command, string root)
    {
        if (!Directory.Exists(root)) return null;
        // Layout used by tools/fetch-lsp-tools.sh: root/node_modules/.bin/<cmd>, root/<tool>/bin/<cmd>, root/<cmd>.
        var dirs = new List<string> { Path.Combine(root, "node_modules", ".bin"), Path.Combine(root, "bin"), root };
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(sub);
                if (name == "node_modules") continue;
                dirs.Add(Path.Combine(sub, "node_modules", ".bin"));
                dirs.Add(Path.Combine(sub, "bin"));
                dirs.Add(sub);
            }
        }
        catch (IOException) { }
        foreach (var dir in dirs)
            foreach (var candidate in Candidates(dir, command))
                if (IsExecutableFile(candidate)) return candidate;
        return null;
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows()) return true;
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
