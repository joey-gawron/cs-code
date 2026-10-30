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
            // Homebrew (Apple Silicon, Intel), MacPorts, Nix; Go's own bin; the dotnet installer's root (lsp-3); keg-only Homebrew llvm for clangd
            // (lsp-4); Node, Ruby and Python version managers' shims (lsp-5)
            [
                "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "~/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "~/.cargo/bin",
                "~/.local/bin", "~/go/bin", "/usr/local/go/bin", "~/.dotnet/tools", "/usr/local/share/dotnet", "/opt/homebrew/opt/llvm/bin",
                "/usr/local/opt/llvm/bin", "~/.volta/bin", "~/Library/pnpm", "~/.bun/bin", "~/.asdf/shims", "~/.local/share/mise/shims", "~/.rbenv/shims",
                "~/.pyenv/shims", "~/Library/Application Support/Coursier/bin", "~/.ghcup/bin",
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
    /// <param name="workingDirectory">
    /// Not used to resolve the command any more (lsp-core-1): the working directory is the server's root inside the WORKSPACE, so a relative command
    /// would run whatever the opened repository keeps at that path. A command with a directory part must be absolute or start with <c>~/</c>; a
    /// relative one (<c>./x</c>, <c>bin/x</c>) is not found. Kept for the signature.
    /// </param>
    public static string? Resolve(string command, string? toolsDir, string? workingDirectory, IReadOnlyDictionary<string, string>? env, IReadOnlyList<string>? searchDirs = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains('/') || command.Contains('\\'))
        {
            var path = command.StartsWith("~/", StringComparison.Ordinal) || command.StartsWith("~\\", StringComparison.Ordinal)
                ? ExpandDirectory(command)
                : Path.IsPathRooted(command) ? command : null;
            return path is not null && IsExecutableFile(path) ? path : null;
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

    /// <summary>
    /// lsp-2: the PATH of a server started on macOS (where an app started from the Dock may still have launchd's short PATH): the executable's own
    /// directory first when <paramref name="path"/> lacks it (an npm, Volta or pnpm launcher runs the <c>node</c> installed beside it), then
    /// <paramref name="path"/>, then each search directory it lacks (what the server itself starts: <c>go</c> for gopls, <c>cargo</c> for
    /// rust-analyzer). Directories are compared as written, trailing separators aside; <paramref name="path"/> is returned as is when nothing is missing.
    /// </summary>
    public static string ExtendPath(string? path, string executable, IReadOnlyList<string>? searchDirs)
    {
        var entries = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = new HashSet<string>(entries.Select(Normalize), StringComparer.Ordinal);
        var front = Path.GetDirectoryName(executable) is { Length: > 0 } dir && known.Add(Normalize(dir)) ? dir : null;
        var back = (searchDirs ?? []).Where(d => d.Length > 0 && known.Add(Normalize(d))).ToList();
        if (front is null && back.Count == 0) return path ?? "";
        if (front is not null) entries.Insert(0, front);
        entries.AddRange(back);
        return string.Join(Path.PathSeparator, entries);

        static string Normalize(string d) => d.Length > 1 ? Path.TrimEndingDirectorySeparator(d) : d;
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
