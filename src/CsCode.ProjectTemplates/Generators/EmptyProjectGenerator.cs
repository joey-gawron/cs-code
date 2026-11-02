using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>An empty folder with a README, an .editorconfig and a .gitignore: for anything the other generators do not cover.</summary>
public sealed class EmptyProjectGenerator(IToolRunner runner) : GeneratorBase(runner)
{
    public const string GeneratorId = "empty";

    public override string Id => GeneratorId;
    public override string DisplayName => "Empty Project";
    public override string Description => "A folder with a README, .editorconfig and .gitignore";
    public override string? IconKey => "folder";
    public override string? LanguageId => null;
    public override int Order => 0;

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>([]);

    public override ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => ValueTask.FromResult(ProjectToolCheck.Available("no tools needed"));

    public override string? ValidateName(string name) => ProjectNames.Folder(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing README.md, .editorconfig and .gitignore");
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name, "A new project.", []), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig(), ct).ConfigureAwait(false);
        await files.WriteAsync(".gitignore", CommonFiles.GitIgnore(null), ct).ConfigureAwait(false);
        return new ProjectResult(request.Directory, "README.md");
    }
}
