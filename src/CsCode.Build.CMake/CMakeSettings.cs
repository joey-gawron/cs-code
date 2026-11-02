using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Build.CMake;

/// <summary>
/// The settings the integration reads. Names and meanings follow VS Code's CMake Tools (<c>cmake.*</c>) so a workspace's existing settings
/// keep working; the CsCode-only ones are <c>cscode.cmake.*</c>. Programs (<c>cmake.cmakePath</c>, <c>cmake.ctestPath</c>) are machine-scoped:
/// a cloned repository's settings cannot choose what CsCode runs.
/// </summary>
internal static class CMakeSettings
{
    public const string SourceDirectory = "cmake.sourceDirectory";
    public const string BuildDirectory = "cmake.buildDirectory";
    public const string Generator = "cmake.generator";
    public const string ConfigureArgs = "cmake.configureArgs";
    public const string ConfigureSettings = "cmake.configureSettings";
    public const string BuildArgs = "cmake.buildArgs";
    public const string BuildToolArgs = "cmake.buildToolArgs";
    public const string ParallelJobs = "cmake.parallelJobs";
    public const string ConfigureOnOpen = "cmake.configureOnOpen";
    public const string ConfigureOnEdit = "cmake.configureOnEdit";
    public const string BuildBeforeRun = "cmake.buildBeforeRun";
    public const string UseCMakePresets = "cmake.useCMakePresets";
    public const string DebugConfig = "cmake.debugConfig";
    public const string CMakePath = "cmake.cmakePath";
    public const string CTestPath = "cmake.ctestPath";
    public const string PointClangd = "cscode.cmake.clangdCompileCommandsDir";
    public const string Debugger = "cscode.cmake.debugger";

    public const string DefaultBuildDirectory = "${workspaceFolder}/build";
    public const string DefaultBuildType = "Debug";

    /// <summary>The build types offered when no preset decides (CMake's standard four).</summary>
    public static readonly IReadOnlyList<string> BuildTypes = ["Debug", "Release", "RelWithDebInfo", "MinSizeRel"];

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IEnumerable<ConfigurationProperty> Properties() =>
    [
        new(SourceDirectory, J("\"${workspaceFolder}\""), "The directory that holds the root CMakeLists.txt."),
        new(BuildDirectory, J($"\"{DefaultBuildDirectory}\""), "Where the project is configured when no configure preset sets binaryDir. Supports ${workspaceFolder}, ${workspaceFolderBasename}, ${buildType}, ${generator}, ${userHome} and ${env:NAME}."),
        new(Generator, J("null"), "The CMake generator (-G) when no preset sets one. Null: Ninja when it is installed, else CMake's default."),
        new(ConfigureArgs, J("[]"), "Extra arguments for every configure."),
        new(ConfigureSettings, J("{}"), "Cache variables passed as -D<name>=<value> at every configure."),
        new(BuildArgs, J("[]"), "Extra arguments for cmake --build."),
        new(BuildToolArgs, J("[]"), "Arguments for the build tool, after --."),
        new(ParallelJobs, J("0"), "--parallel for builds; 0 lets the build tool decide."),
        new(ConfigureOnOpen, J("null"), "Configure CMake projects when a folder opens. Null: ask the first time (the answer is saved here)."),
        new(ConfigureOnEdit, J("true"), "Configure again when a CMakeLists.txt or *.cmake file of a configured project is saved."),
        new(BuildBeforeRun, J("true"), "Build the launch target before Run Target, Debug Target and ${command:cmake.launchTargetPath}, and a C++ test executable's target before the Test Explorer runs or debugs its tests. (CTest's runs always build the targets of the tests they run.)"),
        new(UseCMakePresets, J("\"auto\""), "\"auto\": use CMakePresets.json / CMakeUserPresets.json when present; \"always\" or \"never\"."),
        new(DebugConfig, J("{}"), "Properties merged into the configuration Debug Target starts (e.g. args, env, stopOnEntry)."),
        new(CMakePath, J("\"cmake\""), "The cmake program: a path, or a name looked up on PATH and in the usual install directories.") { Scope = ConfigurationScope.Machine },
        new(CTestPath, J("\"\""), "The ctest program; empty: the ctest next to cmake.") { Scope = ConfigurationScope.Machine },
        new(PointClangd, J("true"), "Start clangd with --compile-commands-dir set to the configured build directory (restarted when it changes)."),
        new(Debugger, J("null"), "The debugger for Debug Target and CTest debugging: \"lldb\" (CodeLLDB), \"lldb-dap\" or \"gdb\". Null: CodeLLDB on macOS, GDB elsewhere."),
    ];

    public static string? String(IConfigurationService? config, string key) => config?.Get<string>(key) is { Length: > 0 } s ? s : null;

    public static IReadOnlyList<string> Strings(IConfigurationService? config, string key)
    {
        if (config?.GetRaw(key) is not { ValueKind: JsonValueKind.Array } a) return [];
        return a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }

    /// <summary><c>cmake.configureSettings</c> as -D values: strings as written, booleans ON/OFF, numbers as written, arrays joined with ';'.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> CacheSettings(IConfigurationService? config)
    {
        if (config?.GetRaw(ConfigureSettings) is not { ValueKind: JsonValueKind.Object } o) return [];
        var list = new List<KeyValuePair<string, string>>();
        foreach (var p in o.EnumerateObject())
        {
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => "ON",
                JsonValueKind.False => "OFF",
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.Array => string.Join(';', p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                _ => null,
            };
            if (value is not null) list.Add(new(p.Name, value));
        }
        return list;
    }

    /// <summary>Substitutes the variables CMake Tools allows in its path settings.</summary>
    public static string Substitute(string value, string folder, string? buildType, string? generator, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = value
            .Replace("${workspaceFolder}", folder, StringComparison.Ordinal)
            .Replace("${workspaceRoot}", folder, StringComparison.Ordinal)
            .Replace("${workspaceFolderBasename}", Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), StringComparison.Ordinal)
            .Replace("${buildType}", buildType ?? DefaultBuildType, StringComparison.Ordinal)
            .Replace("${buildKit}", "default", StringComparison.Ordinal)
            .Replace("${generator}", generator ?? "", StringComparison.Ordinal)
            .Replace("${userHome}", home, StringComparison.Ordinal);
        var start = 0;
        while ((start = result.IndexOf("${env:", start, StringComparison.Ordinal)) >= 0)
        {
            var end = result.IndexOf('}', start);
            if (end < 0) break;
            var name = result[(start + 6)..end];
            var v = environment(name) ?? "";
            result = result[..start] + v + result[(end + 1)..];
            start += v.Length;
        }
        if (result.StartsWith("~/", StringComparison.Ordinal)) result = Path.Combine(home, result[2..]);
        return Path.TrimEndingDirectorySeparator(Path.IsPathRooted(result) ? Path.GetFullPath(result) : Path.GetFullPath(Path.Combine(folder, result)));
    }
}
