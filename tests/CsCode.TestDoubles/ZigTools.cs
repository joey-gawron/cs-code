namespace CsCode.TestDoubles;

/// <summary>
/// The real Zig toolchain for the Zig suites (docs/ZIG.md): <c>zig</c> and <c>zls</c> from <c>CSCODE_ZIG</c> / <c>CSCODE_ZLS</c>, else the
/// repo-local copies tools/fetch-zig.sh installs (<c>.scratch/tools/zig/&lt;version&gt;/zig/zig</c> and <c>…/zls/zls</c>, looked for upwards from
/// the test assembly, so a worktree finds its own), else PATH. Nothing is downloaded here.
/// </summary>
public static class ZigTools
{
    public const string PinnedVersion = "0.17.0";
    public const string ZigVariable = "CSCODE_ZIG";
    public const string ZlsVariable = "CSCODE_ZLS";

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string? FindZig() => Find(ZigVariable, Path.Combine("zig", Exe("zig")), Exe("zig"));

    public static string? FindZls() => Find(ZlsVariable, Path.Combine("zls", Exe("zls")), Exe("zls"));

    private static string? Find(string variable, string inFetch, string onPath)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env && File.Exists(env)) return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "zig", PinnedVersion, inFetch);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, onPath))) return Path.Combine(dir, onPath);
        return null;
    }

    /// <summary>Null when zig is there; otherwise the reason a test that needs it skips.</summary>
    public static string? ZigSkipReason() => FindZig() is null
        ? $"zig is not installed: run tools/fetch-zig.sh (Zig {PinnedVersion} into .scratch/tools/zig), set {ZigVariable}, or put zig on PATH"
        : null;

    /// <summary>Null when zig and zls are there; otherwise the reason a test that needs both skips.</summary>
    public static string? ZlsSkipReason() => ZigSkipReason() ?? (FindZls() is null
        ? $"zls is not installed: run tools/fetch-zig.sh (zls {PinnedVersion} into .scratch/tools/zig), set {ZlsVariable}, or put zls on PATH"
        : null);
}
