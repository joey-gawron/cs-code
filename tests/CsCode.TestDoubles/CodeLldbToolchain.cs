namespace CsCode.TestDoubles;

/// <summary>
/// What the real CodeLLDB tests need: CodeLLDB's standalone adapter (<c>extension/adapter/codelldb</c> of the pinned package, fetched by
/// <c>tools/fetch-codelldb.sh</c>) and the Rust toolchain to build <see cref="RustSample"/>. Tests skip with <see cref="SkipReason"/> when
/// something is missing; nothing is ever downloaded by a test. Shared by the DAP and Debug panels tests (new file in the shared test doubles).
/// </summary>
public static class CodeLldbToolchain
{
    public const string Category = "RealAdapter";
    public const string PinnedVersion = "v1.12.3";
    public const string EnvironmentVariable = "CSCODE_CODELLDB";

    /// <summary>
    /// The adapter: <c>CSCODE_CODELLDB</c> (the adapter, CodeLLDB's <c>extension/</c> directory or an install directory containing it), else the
    /// repo-local fetch location <c>.scratch/tools/codelldb/&lt;version&gt;/</c>.
    /// </summary>
    public static string? FindAdapter()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env) return Resolve(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Resolve(Path.Combine(dir.FullName, ".scratch", "tools", "codelldb", PinnedVersion)) is { } found) return found;
        return null;
    }

    private static string? Resolve(string path)
    {
        var name = OperatingSystem.IsWindows() ? "codelldb.exe" : "codelldb";
        if (File.Exists(path)) return Path.GetFullPath(path);
        foreach (var candidate in new[] { Path.Combine(path, name), Path.Combine(path, "adapter", name), Path.Combine(path, "extension", "adapter", name) })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    /// <summary>Null when everything the real CodeLLDB tests need is present; otherwise an explicit reason to skip.</summary>
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux()) return "the CodeLLDB/Rust tests run on Linux only (ptrace, /proc)";
        if (GdbToolchain.FindCargo() is null || GdbToolchain.FindRustc() is null) return "the Rust toolchain ('cargo', 'rustc') is not on PATH, so the Rust sample cannot be built";
        if (FindAdapter() is null)
            return $"CodeLLDB {PinnedVersion} is not installed: run tools/fetch-codelldb.sh or point {EnvironmentVariable} at its extension/adapter/codelldb";
        return null;
    }
}
