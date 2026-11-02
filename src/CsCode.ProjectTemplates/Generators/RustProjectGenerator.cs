using System.Text.Json.Nodes;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A cargo package: <c>cargo new</c> (<c>cargo init</c> in an empty folder that exists) with <c>--bin</c>/<c>--lib</c>, <c>--edition</c>,
/// <c>--vcs</c> and <c>--name</c> (checked against cargo 1.96.1), then CodeLLDB's launch configuration over the <c>cargo: build</c> task, as
/// CsCode's own Rust template writes it (<c>CodeLldbAdapterFactory.ProvideInitialAsync</c>).
/// </summary>
public sealed class RustProjectGenerator(IToolRunner runner) : GeneratorBase(runner)
{
    public const string GeneratorId = "rust";
    public const string KindOption = "kind";
    public const string EditionOption = "edition";

    public override string Id => GeneratorId;
    public override string DisplayName => "Rust";
    public override string Description => "A cargo package: binary or library";
    public override string? IconKey => "symbol-misc";
    public override string? LanguageId => "rust";
    public override int Order => 20;

    private static string? Cargo => ToolLocator.Find("cargo");

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>(
    [
        ProjectOption.Choice(KindOption, "Type", "bin",
        [
            new("bin", "Binary (application)", "src/main.rs: cargo new --bin"),
            new("lib", "Library", "src/lib.rs with a unit test: cargo new --lib"),
        ]),
        ProjectOption.Choice(EditionOption, "Edition", "2024",
            [new("2024", "2024"), new("2021", "2021"), new("2018", "2018"), new("2015", "2015")]),
        ProjectOption.Choice(ProjectOption.VcsOptionId, "Version control", "git",
        [
            new("git", "Git", "cargo new --vcs git (also: the Create Git repository box)"),
            new("hg", "Mercurial"),
            new("pijul", "Pijul"),
            new("fossil", "Fossil"),
            new("none", "None"),
        ]),
    ]);

    public override ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => CheckToolAsync(Cargo, "cargo", Tools.Cargo, ct);

    public override string? ValidateName(string name) => ProjectNames.Rust(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var cargo = Require(Cargo, "cargo", Tools.Cargo);
        var lib = request.Option(KindOption, "bin") == "lib";
        var vcs = request.Option(ProjectOption.VcsOptionId, "git");
        if (vcs == "git" && !request.CreateGitRepository) vcs = "none"; // the page keeps the two in step; the box wins if they ever disagree
        // The page has made the folder (it may also be an empty one the user picked): cargo new refuses an existing path, cargo init takes it.
        string[] args = ["init", lib ? "--lib" : "--bin", "--edition", request.Option(EditionOption, "2024"), "--vcs", vcs, "--name", request.Name, request.Directory];
        await StepAsync(progress, "Running cargo init", new ToolRun(cargo, args, request.Directory), ct).ConfigureAwait(false);

        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing launch.json, tasks.json, README.md and .editorconfig");
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, VsCodeFiles.Tasks(
            VsCodeFiles.ProcessTask("cargo: build", "cargo", ["build"], "$rustc", group: "build"),
            VsCodeFiles.ProcessTask("cargo: test", "cargo", ["test"], "$rustc", group: "test"),
            VsCodeFiles.ProcessTask("cargo: clippy", "cargo", ["clippy"], "$rustc")), ct).ConfigureAwait(false);
        await files.WriteJsonAsync(VsCodeFiles.LaunchPath, Launch(request.Name, lib), ct).ConfigureAwait(false);
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name, lib ? "A Rust library." : "A Rust application.",
            lib ? [("Build", "cargo build"), ("Test", "cargo test")] : [("Build", "cargo build"), ("Run", "cargo run"), ("Test", "cargo test")]), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig(), ct).ConfigureAwait(false);
        // cargo writes "/target" into .gitignore only for --vcs git; the editors' files are added either way.
        await files.AppendMissingLinesAsync(".gitignore", CommonFiles.GitIgnore(CommonFiles.RustIgnore), ct).ConfigureAwait(false);
        return new ProjectResult(request.Directory, lib ? "src/lib.rs" : "src/main.rs");
    }

    /// <summary>
    /// A binary: its executable from <c>target/debug</c> after the <c>cargo: build</c> task, as CsCode's CodeLLDB template does. A library: its unit
    /// tests through CodeLLDB's <c>cargo</c> key (<c>cargo test --no-run</c>, filtered to the lib target), which the adapter factory resolves.
    /// </summary>
    internal static JsonObject Launch(string package, bool lib)
    {
        if (!lib)
            return VsCodeFiles.Launch(new JsonObject
            {
                ["name"] = $"Debug {package}",
                ["type"] = "lldb",
                ["request"] = "launch",
                ["program"] = "${workspaceFolder}/target/debug/" + VsCodeFiles.Executable(package),
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["preLaunchTask"] = "cargo: build",
                ["sourceLanguages"] = new JsonArray("rust"),
                ["terminal"] = "console",
            });
        return VsCodeFiles.Launch(new JsonObject
        {
            ["name"] = $"Debug unit tests of {package}",
            ["type"] = "lldb",
            ["request"] = "launch",
            ["cargo"] = new JsonObject
            {
                ["args"] = new JsonArray("test", "--no-run"),
                ["filter"] = new JsonObject { ["kind"] = "lib" },
            },
            ["args"] = new JsonArray(),
            ["cwd"] = "${workspaceFolder}",
            ["sourceLanguages"] = new JsonArray("rust"),
            ["terminal"] = "console",
        });
    }
}
