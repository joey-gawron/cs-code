namespace CsCode.Build.CMake;

/// <summary>
/// Finds the programs the integration runs. A setting's value (a path, or a bare name looked up like the others) wins; then PATH (the login
/// shell's on macOS, merged in at start-up), then the usual install directories: Homebrew (Apple Silicon and Intel), MacPorts, CMake.app,
/// and on Linux /usr/local/bin. Nothing is ever installed.
/// </summary>
internal static class CMakeTools
{
    internal static IReadOnlyList<string> WellKnownDirectories { get; } = OperatingSystem.IsMacOS()
        ? ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/Applications/CMake.app/Contents/bin"]
        : OperatingSystem.IsWindows() ? [] : ["/usr/local/bin", "/usr/bin"];

    /// <summary><paramref name="setting"/> (absolute, or a name looked up), else <paramref name="name"/> on PATH or in the well-known directories; null when absent.</summary>
    public static string? Find(string name, string? setting = null, Func<string, string?>? environment = null)
    {
        if (setting is { Length: > 0 } s)
        {
            if (Path.IsPathRooted(s)) return File.Exists(s) ? s : null;
            name = s;
        }
        var exe = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        var path = (environment ?? Environment.GetEnvironmentVariable)("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(WellKnownDirectories))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>True when <paramref name="program"/> lives in a directory on PATH (so cmake's own lookup finds it too).</summary>
    public static bool IsOnPath(string program, Func<string, string?>? environment = null)
    {
        var dir = Path.GetDirectoryName(program);
        var path = (environment ?? Environment.GetEnvironmentVariable)("PATH") ?? "";
        return dir is not null && path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));
    }

    /// <summary>The <c>ctest</c> beside <paramref name="cmake"/> (they ship together), else one found like cmake.</summary>
    public static string? FindCTest(string? cmake, string? setting = null)
    {
        if (setting is { Length: > 0 }) return Find("ctest", setting);
        if (cmake is not null && Path.GetDirectoryName(cmake) is { } dir)
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ctest.exe" : "ctest");
            if (File.Exists(sibling)) return sibling;
        }
        return Find("ctest");
    }
}
