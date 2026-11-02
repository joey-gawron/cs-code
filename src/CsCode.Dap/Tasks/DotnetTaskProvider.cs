using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Configuration;

namespace CsCode.Dap.Tasks;

/// <summary>
/// The .NET SDK's detected tasks, type <c>dotnet</c>, for a folder with .NET projects (<see cref="DotnetProjects"/>: its own project, else what
/// its solution lists, else the projects two levels down):
/// <list type="bullet">
/// <item><c>"dotnet: build"</c>, <c>"dotnet: test"</c> and <c>"dotnet: clean"</c> of the folder's one solution, or of its one project when it
/// has no solution (several solutions: one set each, <c>"dotnet: build - App.sln"</c>);</item>
/// <item>with more than one project, per project: <c>"dotnet: build - App"</c> and <c>"dotnet: clean - App"</c>, and <c>"dotnet: test -
/// App.Tests"</c> for a test project;</item>
/// <item>per program (OutputType Exe, the Web and Worker SDKs; not a test project): <c>"dotnet: run - App"</c> (<c>dotnet run --project</c>)
/// and <c>"dotnet: watch - App"</c> (<c>dotnet watch run --project</c>, hot reload, a background task followed through
/// <c>$dotnet-watch</c>); without the name when the folder has one program.</item>
/// </list>
/// Build, test, clean and run use <c>$msCompile</c>; build and clean pass <c>GenerateFullPaths</c> and no summary, as the tasks.json template
/// does. Paths are relative to the folder, the working directory, so MSBuild sees the physical paths a terminal's build sees. Under a
/// global.json that selects Microsoft.Testing.Platform for <c>dotnet test</c> (<c>"test": {"runner": "Microsoft.Testing.Platform"}</c>), test
/// takes <c>--project</c>, which that mode needs. The definitions are <c>{"type": "dotnet", "command": "build", "project": "src/App/App.csproj"}</c>;
/// a tasks.json entry with other <c>args</c>, an <c>env</c> or a <c>cwd</c>, or another SDK command (<c>publish</c>, <c>pack</c>), runs as
/// written. Nothing is cached: finding the projects reads a few files and runs no tool.
/// </summary>
internal sealed class DotnetTaskProvider(DapPlatform platform) : ITaskProvider
{
    public const string TaskType = "dotnet";

    private static readonly string[] BuildFlags = ["/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary"];

    public string Type => TaskType;

    public async ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct)
    {
        if (!folder.IsFile || platform.Workspace is not { } ws) return [];
        var found = await DotnetProjects.FindAsync(ws.FileSystem, folder, ct).ConfigureAwait(false);
        if (found.Projects.Count == 0 && found.Solutions.Count == 0) return [];
        var mtp = await UsesTestingPlatformAsync(ws.FileSystem, folder, ct).ConfigureAwait(false);
        var tasks = new List<ProvidedTask>();

        // the folder as a whole: its solution(s), else its one project at the top
        var wholes = found.Solutions.Count > 0 ? found.Solutions
            : found.Projects is [{ RelativePath: var only }] && !only.Contains('/') ? [only] : [];
        foreach (var target in wholes)
        {
            var suffix = wholes.Count > 1 ? " - " + target : "";
            foreach (var command in new[] { "build", "test", "clean" }) tasks.Add(Make(command, command + suffix, target, mtp));
        }

        var names = Names(found.Projects);
        if (found.Projects.Count > 1 || wholes.Count == 0)
            foreach (var project in found.Projects)
            {
                tasks.Add(Make("build", "build - " + names[project], project.RelativePath, mtp));
                tasks.Add(Make("clean", "clean - " + names[project], project.RelativePath, mtp));
                if (project.IsTest) tasks.Add(Make("test", "test - " + names[project], project.RelativePath, mtp));
            }
        var programs = found.Projects.Where(p => p.IsExecutable).ToList();
        foreach (var program in programs)
        {
            var suffix = programs.Count > 1 ? " - " + names[program] : "";
            tasks.Add(Make("run", "run" + suffix, program.RelativePath, mtp));
            tasks.Add(Make("watch", "watch" + suffix, program.RelativePath, mtp));
        }
        return tasks;
    }

    /// <summary>
    /// A tasks.json entry: <c>command</c> is the SDK command (<c>build</c>, <c>test</c>, <c>clean</c>, <c>run</c>, <c>watch</c>, or any other,
    /// such as <c>publish</c>), <c>project</c> the project or solution (relative to the folder), <c>args</c> come after them, <c>env</c> and
    /// <c>cwd</c> are optional. A detected task when the entry names one and adds nothing; else <c>dotnet &lt;command&gt; …</c> as written.
    /// </summary>
    public async ValueTask<ProvidedTask?> ResolveTaskAsync(Uri folder, JsonElement definition, CancellationToken ct)
    {
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String
            || c.GetString() is not { Length: > 0 } command)
            return null;
        var project = definition.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } named ? named : null;
        var args = definition.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var env = definition.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object
            ? e.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).ToDictionary(x => x.Name, x => x.Value.GetString()!)
            : null;
        var cwd = definition.TryGetProperty("cwd", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } dir && folder.IsFile
            ? (Path.IsPathRooted(dir) ? dir : Path.Combine(folder.LocalPath, dir))
            : null;
        if (args.Count == 0 && env is null && cwd is null)
        {
            var detected = await ProvideTasksAsync(folder, ct).ConfigureAwait(false);
            if (detected.FirstOrDefault(t => t.Definition is { } d && TaskDefinitionMatch.Matches(d, definition)) is { } match) return match;
        }
        var mtp = folder.IsFile && platform.Workspace is { } ws && await UsesTestingPlatformAsync(ws.FileSystem, folder, ct).ConfigureAwait(false);
        var task = Make(command, project is null ? command : $"{command} - {project}", project, mtp);
        return task with { Args = [.. task.Args, .. args], Detail = "dotnet " + string.Join(' ', task.Args.Concat(args)), Env = env, Cwd = cwd };
    }

    private ProvidedTask Make(string command, string name, string? target, bool testingPlatform)
    {
        IReadOnlyList<string> args = command switch
        {
            "build" or "clean" => target is null ? [command, .. BuildFlags] : [command, target, .. BuildFlags],
            "test" when testingPlatform => target is null ? [command] : [command, "--project", target],
            "run" => target is null ? [command] : [command, "--project", target],
            "watch" => target is null ? [command, "run"] : [command, "run", "--project", target],
            _ => target is null ? [command] : [command, target],
        };
        var members = new Dictionary<string, string> { ["type"] = TaskType, ["command"] = command };
        if (target is not null) members["project"] = target;
        using var definition = JsonDocument.Parse(JsonSerializer.Serialize(members));
        return new ProvidedTask(TaskType, name, "dotnet", args)
        {
            Group = command switch { "build" => "build", "test" => "test", _ => null },
            Detail = "dotnet " + string.Join(' ', args),
            ProblemMatchers = [command == "watch" ? "$dotnet-watch" : "$msCompile"],
            IsBackground = command == "watch",
            Definition = definition.RootElement.Clone(),
        };
    }

    /// <summary>Each project's name in task labels: its file name, or its path when two projects share one.</summary>
    private static Dictionary<DotnetProject, string> Names(IReadOnlyList<DotnetProject> projects)
    {
        var shared = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return projects.ToDictionary(p => p, p => shared.Contains(p.Name) ? p.RelativePath : p.Name);
    }

    /// <summary>
    /// Whether the global.json that applies to <paramref name="folder"/> (its own, else the nearest one above it, as the SDK looks) makes
    /// <c>dotnet test</c> run Microsoft.Testing.Platform.
    /// </summary>
    internal static async Task<bool> UsesTestingPlatformAsync(IFileSystem fs, Uri folder, CancellationToken ct)
    {
        for (var dir = folder.LocalPath.TrimEnd('/'); dir.Length > 0; dir = Path.GetDirectoryName(dir) ?? "")
        {
            var file = new Uri(Path.Combine(dir, "global.json"));
            byte[] bytes;
            try
            {
                if (await fs.StatAsync(file, ct).ConfigureAwait(false) is not { Type: FileType.File }) continue;
                bytes = await fs.ReadFileAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            return IsTestingPlatformRunner(bytes);
        }
        return false;
    }

    internal static bool IsTestingPlatformRunner(ReadOnlySpan<byte> globalJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(globalJson.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }
}
