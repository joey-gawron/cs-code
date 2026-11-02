using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>What the generators share: running a tool as a step (its output to the progress, a failure as <see cref="ProjectGenerationException"/>) and the tool check.</summary>
public abstract class GeneratorBase(IToolRunner runner) : IProjectGenerator
{
    /// <summary>
    /// The "Add sample code" option of the generators that write their own code (Python, Node.js, the static website): off, the project keeps its
    /// layout, entry point and one test, without the greeting example. Generators whose tool writes the code (cargo, dotnet, zig) and C/C++
    /// (whose program and tests are built around the sample library) do not offer it.
    /// </summary>
    public const string SampleOption = "sample";

    protected IToolRunner Runner { get; } = runner;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract string? IconKey { get; }
    public abstract string? LanguageId { get; }
    public abstract int Order { get; }

    public abstract ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct);
    public abstract ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct);
    public abstract string? ValidateName(string name);
    public abstract Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct);

    protected static ProjectOption SampleCode(string description) => ProjectOption.Toggle(SampleOption, "Add sample code", true, description);

    /// <summary>Runs <paramref name="run"/> as the step <paramref name="title"/>; a non-zero exit fails the generation with the output's last lines.</summary>
    protected async Task<ToolResult> StepAsync(IProjectGenerationProgress progress, string title, ToolRun run, CancellationToken ct)
    {
        progress.Step(title);
        progress.Output("> " + run);
        var result = await Runner.RunAsync(run, progress.Output, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var tail = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(4);
            throw new ProjectGenerationException($"{Path.GetFileName(run.FileName)} exited with code {result.ExitCode}: {string.Join(' ', tail)}");
        }
        return result;
    }

    /// <summary>The tool found and answering <c>--version</c>, else the install hint.</summary>
    protected async ValueTask<ProjectToolCheck> CheckToolAsync(string? path, string displayName, ToolInstallHint hint, CancellationToken ct, params string[] versionArgs)
    {
        if (path is null) return ProjectToolCheck.Missing($"{displayName} was not found", hint.ForCurrentPlatform);
        var version = await ToolLocator.VersionAsync(Runner, path, ct, versionArgs).ConfigureAwait(false);
        return version is null
            ? ProjectToolCheck.Missing($"{displayName} ({path}) did not run", hint.ForCurrentPlatform)
            : ProjectToolCheck.Available(version);
    }

    /// <summary>The tool's path, or a <see cref="ProjectGenerationException"/> naming the install hint (the page disables the generator first, so this is a race).</summary>
    protected static string Require(string? path, string displayName, ToolInstallHint hint) =>
        path ?? throw new ProjectGenerationException($"{displayName} was not found. {hint.ForCurrentPlatform}");
}
