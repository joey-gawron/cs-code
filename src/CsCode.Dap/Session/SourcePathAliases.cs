namespace CsCode.Dap.Session;

/// <summary>
/// For an adapter that binds breakpoints by the physical path the compiler recorded (<see cref="DebugAdapterQuirks.BindsPhysicalSourcePaths"/>):
/// a path the editor opened (<c>/var/folders/…/Program.cs</c>) goes to the adapter with every symbolic link among its directories resolved
/// (<c>/private/var/folders/…/Program.cs</c>), and the adapter's paths come back under the directory the editor knows. Each translated
/// directory is remembered as a pair, so a frame in a file that never had a breakpoint (a step into another file of the project) still maps
/// back. One per session; the lookups touch the file system only for a directory not seen before. Thread-safe.
/// </summary>
internal sealed class SourcePathAliases
{
    /// <summary>Directories remembered at most (a session touches a handful; the cap only bounds a pathological one).</summary>
    internal const int MaxDirectories = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _physicalOf = new(StringComparer.Ordinal);
    // physical directory → the directory as the editor opened it; only pairs that differ
    private readonly List<(string Physical, string Logical)> _aliases = [];
    private readonly Func<string, string> _resolve;

    public SourcePathAliases() : this(PhysicalDirectory) { }

    /// <param name="resolve">A directory → the same directory with every link resolved (tests pass their own).</param>
    internal SourcePathAliases(Func<string, string> resolve) => _resolve = resolve;

    /// <summary>The path to send the adapter for <paramref name="path"/> (unchanged when no link is involved).</summary>
    public string ToAdapter(string path)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 0 } dir) return path;
        string physical;
        lock (_gate)
        {
            if (!_physicalOf.TryGetValue(dir, out physical!))
            {
                if (_physicalOf.Count >= MaxDirectories) return path;
                physical = _resolve(dir);
                _physicalOf[dir] = physical;
                if (!string.Equals(physical, dir, StringComparison.Ordinal) && !_aliases.Contains((physical, dir))) _aliases.Add((physical, dir));
            }
        }
        return ReferenceEquals(physical, dir) || string.Equals(physical, dir, StringComparison.Ordinal) ? path : Path.Combine(physical, Path.GetFileName(path));
    }

    /// <summary>The editor's path for a path the adapter reported, or null when it lies under no translated directory.</summary>
    public string? FromAdapter(string path)
    {
        lock (_gate)
        {
            // the longest physical directory first: /private/var/x/sub before /private/var/x
            string? best = null, logical = null;
            foreach (var (p, l) in _aliases)
                if ((best is null || p.Length > best.Length) && IsUnder(path, p)) { best = p; logical = l; }
            return best is null ? null : logical + path[best.Length..];
        }
    }

    private static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir, StringComparison.Ordinal) && (path.Length == dir.Length || path[dir.Length] == Path.DirectorySeparatorChar);

    /// <summary><paramref name="dir"/> with every symbolic link along it resolved (realpath); the directory itself when that fails.</summary>
    internal static string PhysicalDirectory(string dir) => PhysicalDirectory(dir, depth: 0);

    private static string PhysicalDirectory(string dir, int depth)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var current = Path.GetPathRoot(full)!;
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                // a link's target is returned as it was written, which may itself run through links (/var/... on macOS): resolve it again,
                // at most 40 links deep as the kernel does (ELOOP)
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = depth < 40 ? PhysicalDirectory(Path.TrimEndingDirectorySeparator(target.FullName), depth + 1) : Path.TrimEndingDirectorySeparator(target.FullName);
            }
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return dir; }
    }
}
