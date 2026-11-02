using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A C# solution: <c>dotnet new sln</c> (<c>.slnx</c> or <c>.sln</c>), the chosen template of the installed SDK in <c>src/Name</c> added to it, and
/// optionally an xUnit project in <c>tests/Name.Tests</c> that references it. The templates come from <c>dotnet new list</c>, the target
/// frameworks and the solution formats from the templates' <c>--help</c>, all parsed off the UI thread and cached for a few minutes. F5:
/// netcoredbg's <c>coreclr</c> after the <c>build</c> task (<c>dotnet build</c>, <c>$msCompile</c>), as CsCode's own .NET template writes it.
/// </summary>
public sealed partial class DotnetProjectGenerator(IToolRunner runner, TimeProvider? time = null) : GeneratorBase(runner)
{
    public const string GeneratorId = "dotnet";
    public const string TemplateOption = "template";
    public const string FrameworkOption = "framework";
    public const string SolutionOption = "solution";
    public const string TestsOption = "tests";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IReadOnlyList<ProjectOption> Options, DateTimeOffset At)? _cached;

    public override string Id => GeneratorId;
    public override string DisplayName => "C#";
    public override string Description => "A .NET solution from the SDK's templates, with an xUnit test project";
    public override string? IconKey => "symbol-class";
    public override string? LanguageId => "csharp";
    public override int Order => 50;

    private static string? Dotnet => ToolLocator.FindDotnet();

    public override ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) => CheckToolAsync(Dotnet, "dotnet", Tools.Dotnet, ct);

    public override string? ValidateName(string name) => ProjectNames.CSharp(name);

    public override async ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } c && _time.GetUtcNow() - c.At < CacheFor) return c.Options;
            var options = await ReadOptionsAsync(ct).ConfigureAwait(false);
            _cached = (options, _time.GetUtcNow());
            return options;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<ProjectOption>> ReadOptionsAsync(CancellationToken ct)
    {
        IReadOnlyList<DotnetTemplate> templates = [];
        IReadOnlyList<string> frameworks = [];
        string? defaultFramework = null;
        var formats = new List<string> { "sln" };
        if (Dotnet is { } dotnet)
        {
            var temp = Path.GetTempPath();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60)); // the first run of a fresh SDK builds its template cache
            var list = await Runner.RunAsync(new ToolRun(dotnet, ["new", "list", "--type", "project", "--language", "C#"], temp), null, timeout.Token).ConfigureAwait(false);
            if (list.Succeeded && DotnetTemplates.ParseList(list.Output) is { Count: > 0 } parsed) templates = DotnetTemplates.Order(parsed);
            var console = await Runner.RunAsync(new ToolRun(dotnet, ["new", "console", "--help"], temp), null, timeout.Token).ConfigureAwait(false);
            if (console.Succeeded && DotnetTemplates.ParseChoices(console.Output, "--framework") is { } fw)
            {
                frameworks = fw.Values;
                defaultFramework = fw.Default;
            }
            var sln = await Runner.RunAsync(new ToolRun(dotnet, ["new", "sln", "--help"], temp), null, timeout.Token).ConfigureAwait(false);
            if (sln.Succeeded && DotnetTemplates.ParseChoices(sln.Output, "--format") is { Values.Count: > 0 } f)
                formats = [.. f.Values.OrderBy(v => v == f.Default ? 0 : 1)];
        }
        if (templates.Count == 0) // the list could not be read: the curated ones, which every SDK since 6 has
            templates = [new("Console App", "console", "C#", "Common/Console"), new("Class Library", "classlib", "C#", "Common/Library"),
                new("ASP.NET Core Web API", "webapi", "C#", "Web/Web API/API/Service"), new("xUnit Test Project", "xunit", "C#", "Test/xUnit")];
        List<ProjectOption> options =
        [
            ProjectOption.Choice(TemplateOption, "Template", templates[0].ShortName,
                [.. templates.Select(t => new ProjectOptionChoice(t.ShortName, $"{t.Name} ({t.ShortName})", t.Tags))]),
        ];
        if (frameworks.Count > 0)
            options.Add(ProjectOption.Choice(FrameworkOption, "Target framework", defaultFramework ?? frameworks[0], [.. frameworks.Select(f => new ProjectOptionChoice(f, f))]));
        options.Add(ProjectOption.Choice(SolutionOption, "Solution file", formats[0],
            [.. formats.Select(f => new ProjectOptionChoice(f, f == "slnx" ? ".slnx (XML solution)" : ".sln"))]));
        options.Add(ProjectOption.Toggle(TestsOption, "Add an xUnit test project", true, "tests/<Name>.Tests, referencing the project (not for a test template)"));
        return options;
    }

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var dotnet = Require(Dotnet, "dotnet", Tools.Dotnet);
        var name = request.Name;
        var dir = request.Directory;
        var template = request.Option(TemplateOption, "console");
        var framework = request.Options.TryGetValue(FrameworkOption, out var fw) && fw.Length > 0 ? fw : null;
        // An SDK whose sln template has --format (9.0.200 and later) lists both formats; an older one makes a .sln and takes no flag.
        var formats = (await GetOptionsAsync(ct).ConfigureAwait(false)).First(o => o.Id == SolutionOption).Choices;
        var format = formats.Count > 1 ? request.Option(SolutionOption, formats[0].Value) : "sln";
        var solution = $"{name}.{format}";
        var project = $"src/{name}/{name}.csproj";

        await StepAsync(progress, "Creating the solution", new ToolRun(dotnet, ["new", "sln", "--name", name, .. formats.Count > 1 ? new[] { "--format", format } : []], dir), ct).ConfigureAwait(false);
        // A template that has no --framework (some item-like project templates) must not be given one.
        var help = framework is null ? null : await Runner.RunAsync(new ToolRun(dotnet, ["new", template, "--help"], dir), null, ct).ConfigureAwait(false);
        string[] frameworkArgs = framework is not null && help is { Succeeded: true } h && DotnetTemplates.HasOption(h.Output, "--framework") ? ["--framework", framework] : [];
        await StepAsync(progress, $"Running dotnet new {template}", new ToolRun(dotnet, ["new", template, "--name", name, "--output", $"src/{name}", .. frameworkArgs], dir), ct).ConfigureAwait(false);
        await StepAsync(progress, "Adding the project to the solution", new ToolRun(dotnet, ["sln", solution, "add", project], dir), ct).ConfigureAwait(false);

        var isTestTemplate = await IsTestProjectAsync(Path.Combine(dir, "src", name, name + ".csproj"), ct).ConfigureAwait(false);
        var withTests = request.Flag(TestsOption, true) && !isTestTemplate;
        if (withTests)
        {
            var tests = $"tests/{name}.Tests/{name}.Tests.csproj";
            await StepAsync(progress, "Creating the xUnit test project", new ToolRun(dotnet, ["new", "xunit", "--name", name + ".Tests", "--output", $"tests/{name}.Tests", .. frameworkArgs], dir), ct).ConfigureAwait(false);
            await StepAsync(progress, "Referencing the project from the tests", new ToolRun(dotnet, ["add", tests, "reference", project], dir), ct).ConfigureAwait(false);
            await StepAsync(progress, "Adding the tests to the solution", new ToolRun(dotnet, ["sln", solution, "add", tests], dir), ct).ConfigureAwait(false);
        }
        await StepAsync(progress, "Writing .gitignore", new ToolRun(dotnet, ["new", "gitignore"], dir), ct).ConfigureAwait(false);

        var files = new ProjectWriter(dir);
        progress.Step("Writing launch.json, tasks.json, README.md and .editorconfig");
        var projectFile = files.PathOf(project);
        var csproj = await File.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false);
        var tfm = TargetFramework().Match(csproj) is { Success: true } m ? m.Groups[1].Value : framework ?? "net10.0";
        var runnable = !isTestTemplate && IsRunnable(csproj);
        var web = csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal);
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, VsCodeFiles.Tasks(
            VsCodeFiles.ProcessTask("build", "dotnet", ["build", "${workspaceFolder}/" + solution, "/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary"], "$msCompile", group: "build"),
            VsCodeFiles.ProcessTask("test", "dotnet", ["test", "${workspaceFolder}/" + solution], "$msCompile", group: "test")), ct).ConfigureAwait(false);
        if (runnable) await files.WriteJsonAsync(VsCodeFiles.LaunchPath, Launch(name, tfm, web), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig(EditorConfigCSharp), ct).ConfigureAwait(false);
        var commands = new List<(string, string)> { ("Build", $"dotnet build {solution}") };
        if (runnable) commands.Add(("Run", $"dotnet run --project {project}"));
        if (withTests || isTestTemplate) commands.Add(("Test", $"dotnet test {solution}"));
        await files.WriteAsync("README.md", CommonFiles.Readme(name, $"A .NET solution ({template}, {tfm}).", commands), ct).ConfigureAwait(false);

        string? main = new[] { "Program.cs", "Class1.cs", "UnitTest1.cs", "Test1.cs" }.Select(f => $"src/{name}/{f}").FirstOrDefault(files.Exists) ?? project;
        return new ProjectResult(dir, main);
    }

    private static async Task<bool> IsTestProjectAsync(string csproj, CancellationToken ct)
    {
        if (!File.Exists(csproj)) return false;
        var text = await File.ReadAllTextAsync(csproj, ct).ConfigureAwait(false);
        return text.Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"Include=""(xunit|xunit\.v3|NUnit|MSTest\.TestFramework|MSTest)""", RegexOptions.IgnoreCase)
            || text.Contains("Sdk=\"MSTest.Sdk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A program (console, web, worker): an <c>Exe</c>/<c>WinExe</c> output or a Web/Worker SDK.</summary>
    internal static bool IsRunnable(string csproj) =>
        Regex.IsMatch(csproj, @"<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase)
        || csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal)
        || csproj.Contains("Microsoft.NET.Sdk.Worker", StringComparison.Ordinal);

    /// <summary>netcoredbg's launch of the program the <c>build</c> task makes (CsCode's .NET template: <c>NetCoreDbgAdapterFactory.ProvideInitialAsync</c>).</summary>
    internal static JsonObject Launch(string name, string tfm, bool web)
    {
        var config = new JsonObject
        {
            ["name"] = $".NET: Launch {name}",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["preLaunchTask"] = "build",
            ["program"] = $"${{workspaceFolder}}/src/{name}/bin/Debug/{tfm}/{name}.dll",
            ["args"] = new JsonArray(),
            ["cwd"] = $"${{workspaceFolder}}/src/{name}",
            ["stopAtEntry"] = false,
            ["console"] = "internalConsole",
        };
        if (web) config["env"] = new JsonObject { ["ASPNETCORE_ENVIRONMENT"] = "Development" };
        return VsCodeFiles.Launch(config, new JsonObject
        {
            ["name"] = ".NET: Attach",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processId"] = "${command:pickProcess}",
        });
    }

    private const string EditorConfigCSharp = """
        [*.{csproj,props,targets,slnx,xml}]
        indent_size = 2

        [*.cs]
        csharp_style_namespace_declarations = file_scoped:suggestion
        csharp_new_line_before_open_brace = all
        dotnet_sort_system_directives_first = true
        """;

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^;<\s]+)")]
    private static partial Regex TargetFramework();
}
