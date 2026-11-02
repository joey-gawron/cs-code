using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A Zig package: <c>zig init</c> in the project folder (it takes the folder's name as the package's: <c>build.zig</c>, <c>build.zig.zon</c>,
/// <c>src/main.zig</c>, <c>src/root.zig</c>), then the <c>zig build</c> tasks and CodeLLDB's launch of <c>zig-out/bin/name</c>. Only the
/// flag-free <c>zig init</c> is used (it exists in every version since 0.12; zig was not installed where this was written, so nothing newer
/// was checked).
/// </summary>
public sealed class ZigProjectGenerator(IToolRunner runner) : GeneratorBase(runner)
{
    public const string GeneratorId = "zig";
    public const string BuildTask = "zig: build";

    public override string Id => GeneratorId;
    public override string DisplayName => "Zig";
    public override string Description => "zig init: an executable and a library module";
    public override string? IconKey => "flame";
    public override string? LanguageId => "zig";
    public override int Order => 60;

    private static string? Zig => ToolLocator.Find("zig");

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>([]);

    public override ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => CheckToolAsync(Zig, "zig", Tools.Zig, ct, "version");

    public override string? ValidateName(string name) => ProjectNames.Zig(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var zig = Require(Zig, "zig", Tools.Zig);
        await StepAsync(progress, "Running zig init", new ToolRun(zig, ["init"], request.Directory), ct).ConfigureAwait(false);

        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing launch.json, tasks.json, README.md and .editorconfig");
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, VsCodeFiles.Tasks(
            VsCodeFiles.ProcessTask(BuildTask, "zig", ["build"], "$gcc", group: "build"),
            VsCodeFiles.ProcessTask("zig: test", "zig", ["build", "test"], "$gcc", group: "test"),
            VsCodeFiles.ProcessTask("zig: run", "zig", ["build", "run"], "$gcc")), ct).ConfigureAwait(false);
        // CodeLLDB lists no "zig" source language yet (the Zig stage adds it), so none is named.
        await files.WriteJsonAsync(VsCodeFiles.LaunchPath, VsCodeFiles.Launch(
            VsCodeFiles.LldbLaunch($"Debug {request.Name}", "${workspaceFolder}/zig-out/bin/" + VsCodeFiles.Executable(request.Name), BuildTask, null)), ct).ConfigureAwait(false);
        await files.WriteIfMissingAsync("README.md", CommonFiles.Readme(request.Name, "A Zig program.",
            [("Build", "zig build"), ("Run", "zig build run"), ("Test", "zig build test")]), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig("[*.{zig,zon}]\nindent_size = 4"), ct).ConfigureAwait(false);
        await files.AppendMissingLinesAsync(".gitignore", CommonFiles.GitIgnore(CommonFiles.ZigIgnore), ct).ConfigureAwait(false);
        return new ProjectResult(request.Directory, files.Exists("src/main.zig") ? "src/main.zig" : "build.zig");
    }
}
