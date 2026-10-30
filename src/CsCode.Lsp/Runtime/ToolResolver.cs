namespace CsCode.Lsp.Runtime;

/// <summary>
/// Resolves a server command to an executable file: absolute/relative path as given, else PATH (honouring a definition's PATH override),
/// else the per-user install directories (<c>cscode.lsp.searchDirs</c>: <c>~/.nix-profile/bin</c>, <c>~/.cargo/bin</c>, <c>~/go/bin</c>, …),
/// else the managed tools directory <c>{ToolsDir}/lsp/**</c> (npm <c>.bin</c> folders, <c>bin</c> folders, the dir itself).
/// The install directories matter for an editor started from a desktop launcher, whose PATH rarely contains them. Nothing is ever installed.
/// </summary>
public static class ToolResolver
{
    /// <summary>
    /// The usual per-user install locations of language servers, unexpanded (<c>~</c> = home, <c>%VAR%</c> = environment variable):
    /// the default of <c>cscode.lsp.searchDirs</c>. Package managers put binaries here without necessarily adding them to the PATH a GUI sees.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchDirs { get; } = OperatingSystem.IsWindows()
        ?
        [
            "~/.cargo/bin", "~/go/bin", "~/.dotnet/tools", "%APPDATA%/npm", "~/scoop/shims", "%LOCALAPPDATA%/Coursier/data/bin", "~/.ghcup/bin",
        ]
        : OperatingSystem.IsMacOS()
            ?
            [
                "/opt/homebrew/bin", "/usr/local/bin", "~/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "~/.cargo/bin", "~/.local/bin",
                "~/go/bin", "~/.dotnet/tools", "~/Library/Application Support/Coursier/bin", "~/.ghcup/bin",
            ]
            :
            [
                "~/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "~/.cargo/bin", "~/.local/bin", "~/go/bin", "~/.dotnet/tools",
                "~/.local/share/coursier/bin", "~/.ghcup/bin",
            ];

    /// <summary>Expands a leading <c>~</c> to the user's home directory and <c>%VAR%</c> environment references; returns null for an empty entry.</summary>
    public static string? ExpandDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var d = Environment.ExpandEnvironmentVariables(dir.Trim());
        if (d == "~" || d.StartsWith("~/", StringComparison.Ordinal) || d.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            d = d.Length == 1 ? home : Path.Combine(home, d[2..]);
        }
        return d.Contains('%', StringComparison.Ordinal) ? null : d; // an unset %VAR% stays literal: not a real directory
    }

    /// <param name="searchDirs">Already expanded directories searched after PATH; null = none (the manager passes <c>cscode.lsp.searchDirs</c>).</param>
    public static string? Resolve(string command, string? toolsDir, string? workingDirectory, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string>? searchDirs = null)
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

        if (searchDirs is not null)
            foreach (var dir in searchDirs)
                foreach (var candidate in Candidates(dir, command))
                    if (IsExecutableFile(candidate)) return candidate;

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

    internal static bool IsExecutableFile(string path)
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
