using System.Text.Json;
using CsCode.Core.Build;
using CsCode.Core.Tasks;

namespace CsCode.Build.CMake;

/// <summary>
/// CMake's detected tasks, as VS Code's CMake Tools contributes them: <c>"CMake: configure"</c>, <c>"CMake: build"</c>, <c>"CMake: clean"</c>,
/// <c>"CMake: clean rebuild"</c>, <c>"CMake: install"</c> and <c>"CMake: test"</c> for the CMake project of the folder, with the command lines
/// the status bar's commands run (the same source and build directories, presets, build type, generator and <c>cmake.*</c> settings; build
/// builds the selected default target). The build tasks are in the build group and use <c>$gcc</c>; test runs ctest in the build directory.
/// <para>Their definitions are CMake Tools' (<c>{"type": "cmake", "command": "build", "targets": ["app"], "preset": …, "args": […],
/// "options": {"cwd", "environment"}}</c>), so a tasks.json written for VS Code customises them, and an entry with targets, a preset, args or
/// options runs as written (a preset written as <c>${command:cmake.active…PresetName}</c> means the selected one).</para>
/// <para>Listing them writes CsCode's File API query into the build directory, as configuring does, so a configure run as a task leaves a reply;
/// <see cref="CMakeService"/> watches for it and reads the new targets.</para>
/// </summary>
internal sealed class CMakeTaskProvider(CMakeService service) : ITaskProvider
{
    public const string TaskType = "cmake";
    public const string Source = "CMake";
    private static readonly string[] Gcc = ["$gcc"];

    /// <summary>CMake Tools' <c>command</c> values CsCode runs, with the task names it lists them under.</summary>
    internal static readonly (string Command, string Name, string? Group)[] Commands =
        [("configure", "configure", null), ("build", "build", "build"), ("clean", "clean", null), ("cleanRebuild", "clean rebuild", "build"),
         ("install", "install", null), ("test", "test", "test")];

    public string Type => TaskType;

    public async ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct)
    {
        if (await ViewForAsync(folder, ct).ConfigureAwait(false) is not { } view) return [];
        var tasks = new List<ProvidedTask>(Commands.Length);
        foreach (var (command, _, _) in Commands)
            if (await TaskAsync(view, command, view.BuildTarget is { } t ? [t] : [], null, [], ct).ConfigureAwait(false) is { } task)
                tasks.Add(task);
        return tasks;
    }

    public async ValueTask<ProvidedTask?> ResolveTaskAsync(Uri folder, JsonElement definition, CancellationToken ct)
    {
        if (definition.ValueKind != JsonValueKind.Object || Str(definition, "command") is not { } command || Commands.All(c => c.Command != command)) return null;
        if (await ViewForAsync(folder, ct).ConfigureAwait(false) is not { } view) return null;
        var targets = Strings(definition, "targets");
        var preset = Str(definition, "preset") is { Length: > 0 } p && !p.StartsWith("${", StringComparison.Ordinal) ? p : null;
        var args = Strings(definition, "args");
        var task = await TaskAsync(view, command, targets.Count > 0 ? targets : view.BuildTarget is { } t ? [t] : [], preset, args, ct).ConfigureAwait(false);
        if (task is null) return null;
        if (definition.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            if (Str(options, "cwd") is { Length: > 0 } cwd) task = task with { Cwd = Path.IsPathRooted(cwd) ? cwd : Path.Combine(view.Project.SourceDirectory, cwd) };
            if (options.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object)
                task = task with { Env = env.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.String).ToDictionary(e => e.Name, e => e.Value.GetString()!) };
        }
        return targets.Count > 0 && command == "build" ? task with { Name = $"build {string.Join(' ', targets)}" } : task;
    }

    /// <summary>The project of <paramref name="folder"/>: the one whose source directory is the folder or below it (the deepest when several).</summary>
    private async Task<CMakeProjectView?> ViewForAsync(Uri folder, CancellationToken ct)
    {
        if (!folder.IsFile) return null;
        await service.Detected.WaitAsync(ct).ConfigureAwait(false);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.LocalPath));
        return service.Views.Where(v => IsUnder(v.Project.SourceDirectory, root)).OrderBy(v => v.Project.SourceDirectory.Length).FirstOrDefault();
    }

    private async Task<ProvidedTask?> TaskAsync(CMakeProjectView view, string command, IReadOnlyList<string> targets, string? preset, IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var project = view.Project;
        var (_, name, group) = Commands.First(c => c.Command == command);
        if (service.CMakeProgram is not { } cmake) return null;
        CMakeCommand? line = command switch
        {
            "configure" => preset is null ? await service.ConfigureCommandAsync(project.SourceDirectory).WaitAsync(ct).ConfigureAwait(false)
                : new CMakeCommand(cmake, ["-S", project.SourceDirectory, "--preset", preset], project.SourceDirectory),
            "build" => await BuildAsync(targets),
            "clean" => await BuildAsync(["clean"]),
            "cleanRebuild" => await BuildAsync(targets) is { } b ? b with { Arguments = [.. b.Arguments, "--clean-first"] } : null,
            "install" => new CMakeCommand(cmake, ["--install", project.BuildDirectory, .. Config(project)], project.SourceDirectory),
            "test" => project.CTestPath is { } ctest ? new CMakeCommand(ctest, [.. CTestConfig(project), "--output-on-failure"], project.BuildDirectory) : null,
            _ => null,
        };
        if (line is null) return null;
        if (command == "configure") TryWriteQuery(project.BuildDirectory);
        return new ProvidedTask(Source, name, line.FileName, [.. line.Arguments, .. args])
        {
            Group = group,
            Cwd = line.WorkingDirectory,
            ProblemMatchers = command is "build" or "clean" or "cleanRebuild" ? Gcc : [],
            Definition = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["type"] = TaskType, ["command"] = command }),
        };

        async Task<CMakeCommand?> BuildAsync(IReadOnlyList<string> buildTargets)
        {
            if (preset is null) return await service.BuildCommandAsync(project.SourceDirectory, buildTargets).WaitAsync(ct).ConfigureAwait(false);
            List<string> a = ["--build", "--preset", preset];
            if (buildTargets.Count > 0) a.AddRange(["--target", .. buildTargets]);
            return new CMakeCommand(cmake, a, project.SourceDirectory);
        }
    }

    // a multi-config generator needs the configuration; a single-config one ignores it
    private static string[] Config(CMakeProject p) => p.Configuration is { Length: > 0 } c ? ["--config", c] : [];
    private static string[] CTestConfig(CMakeProject p) => p.Configuration is { Length: > 0 } c ? ["-C", c] : [];

    private static void TryWriteQuery(string buildDir)
    {
        try { CMakeFileApi.WriteQuery(buildDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the configure itself will say what is wrong
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.Ordinal) && (path.Length == root.Length || path[root.Length] is '/' or '\\');
}
