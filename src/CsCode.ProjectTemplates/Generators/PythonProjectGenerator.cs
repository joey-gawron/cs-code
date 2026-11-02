using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Templates;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>
/// A Python project written by CsCode: <c>pyproject.toml</c> with a src layout, Ruff configured (lint and format) and pytest (with
/// <c>pythonpath = ["src"]</c>, so the tests import the package without installing it), the package, a test, then a <c>.venv</c> made by
/// <c>python -m venv</c> (<c>uv venv</c> when uv is installed) and pytest installed into it. debugpy's launch configuration runs the package as
/// a module (<c>python -m pkg</c>) with the venv's interpreter.
/// </summary>
public sealed partial class PythonProjectGenerator(IToolRunner runner) : GeneratorBase(runner)
{
    public const string GeneratorId = "python";
    public const string KindOption = "kind";
    public const string VenvOption = "venv";
    public const string TestsOption = "pytest";

    public override string Id => GeneratorId;
    public override string DisplayName => "Python";
    public override string Description => "pyproject.toml (src layout, Ruff), .venv and pytest";
    public override string? IconKey => "symbol-namespace";
    public override string? LanguageId => "python";
    public override int Order => 10;

    private static string? Python => OperatingSystem.IsWindows() ? ToolLocator.Find("python", "py") : ToolLocator.Find("python3", "python");

    public override ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ProjectOption>>(
    [
        ProjectOption.Choice(KindOption, "Type", "app",
        [
            new("app", "Application", "A runnable package: python -m <package>, and a console script"),
            new("lib", "Package (library)", "An importable package"),
        ]),
        ProjectOption.Toggle(VenvOption, "Create a virtual environment (.venv)", true, "python -m venv .venv (uv venv when uv is installed)"),
        ProjectOption.Toggle(TestsOption, "pytest, with a tests/ folder", true, "pytest is installed into .venv from PyPI"),
    ]);

    public override async ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct)
    {
        var check = await CheckToolAsync(Python, "python3", Tools.Python, ct).ConfigureAwait(false);
        if (check.IsAvailable && ParseVersion(check.Summary) is { Major: < 3 })
            return ProjectToolCheck.Missing($"{check.Summary} is too old: Python 3 is needed", Tools.Python.ForCurrentPlatform);
        return check;
    }

    public override string? ValidateName(string name) => ProjectNames.Python(name);

    public override async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        var python = Require(Python, "python3", Tools.Python);
        var app = request.Option(KindOption, "app") == "app";
        var venv = request.Flag(VenvOption, true);
        var tests = request.Flag(TestsOption, true);
        var package = ProjectNames.PythonPackage(request.Name);
        var version = ParseVersion(await ToolLocator.VersionAsync(Runner, python, ct).ConfigureAwait(false)) ?? new Version(3, 10);
        var warnings = new List<string>();

        var files = new ProjectWriter(request.Directory);
        progress.Step("Writing pyproject.toml and the package");
        await files.WriteAsync("pyproject.toml", PyProject(request.Name, package, app, tests, version), ct).ConfigureAwait(false);
        await files.WriteAsync($"src/{package}/__init__.py", InitPy(request.Name), ct).ConfigureAwait(false);
        if (app) await files.WriteAsync($"src/{package}/__main__.py", MainPy(package), ct).ConfigureAwait(false);
        if (tests) await files.WriteAsync($"tests/test_{package}.py", TestPy(package), ct).ConfigureAwait(false);
        await files.WriteAsync(".gitignore", CommonFiles.GitIgnore(CommonFiles.PythonIgnore), ct).ConfigureAwait(false);
        await files.WriteAsync(".editorconfig", CommonFiles.EditorConfig(), ct).ConfigureAwait(false);
        var venvPython = OperatingSystem.IsWindows() ? ".venv\\Scripts\\python.exe" : ".venv/bin/python";
        var shellPython = venv ? venvPython : Path.GetFileName(python);
        await files.WriteAsync("README.md", CommonFiles.Readme(request.Name, app ? "A Python application." : "A Python package.",
        [
            .. venv ? new[] { ("Activate the environment", OperatingSystem.IsWindows() ? ".venv\\Scripts\\activate" : "source .venv/bin/activate") } : [],
            .. app ? new[] { ("Run", $"{shellPython} -m {package}") } : [],
            .. tests ? new[] { ("Test", $"{shellPython} -m pytest") } : [],
        ]), ct).ConfigureAwait(false);
        await WriteVsCodeAsync(files, request.Name, package, app, venv, tests, python, ct).ConfigureAwait(false);

        if (venv)
        {
            var uv = ToolLocator.Find("uv");
            await StepAsync(progress, "Creating the virtual environment (.venv)", uv is not null
                ? new ToolRun(uv, ["venv", ".venv"], request.Directory)
                : new ToolRun(python, ["-m", "venv", ".venv"], request.Directory), ct).ConfigureAwait(false);
            if (tests)
            {
                // From PyPI: offline, the project is still made and the README says how to install it later.
                var install = uv is not null
                    ? new ToolRun(uv, ["pip", "install", "--python", files.PathOf(venvPython), "pytest"], request.Directory)
                    : new ToolRun(files.PathOf(venvPython), ["-m", "pip", "install", "--disable-pip-version-check", "pytest"], request.Directory);
                progress.Step("Installing pytest into .venv");
                progress.Output("> " + install);
                var result = await Runner.RunAsync(install, progress.Output, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    var message = $"pytest could not be installed (offline?). Install it later with: {venvPython} -m pip install pytest";
                    warnings.Add(message);
                    progress.Warning(message);
                }
            }
        }
        return new ProjectResult(request.Directory, app ? $"src/{package}/__main__.py" : $"src/{package}/__init__.py", warnings);
    }

    private static async Task WriteVsCodeAsync(ProjectWriter files, string name, string package, bool app, bool venv, bool tests, string python, CancellationToken ct)
    {
        JsonObject Config(string title, Action<JsonObject> fill)
        {
            var c = new JsonObject { ["name"] = title, ["type"] = "debugpy", ["request"] = "launch" };
            fill(c);
            c["cwd"] = "${workspaceFolder}";
            c["env"] = new JsonObject { ["PYTHONPATH"] = "${workspaceFolder}/src" };
            if (venv) c["python"] = VsCodeFiles.VenvPython;
            c["console"] = "internalConsole";
            c["justMyCode"] = true;
            return c;
        }
        var configs = new List<JsonObject>();
        if (app) configs.Add(Config($"Python: {name}", c => c["module"] = package));
        if (tests) configs.Add(Config("Python: pytest", c => { c["module"] = "pytest"; c["args"] = new JsonArray("-q"); }));
        configs.Add(Config("Python: Current File", c => c["program"] = "${file}"));
        await files.WriteJsonAsync(VsCodeFiles.LaunchPath, VsCodeFiles.Launch([.. configs]), ct).ConfigureAwait(false);

        var interpreter = venv ? VsCodeFiles.VenvPython : python;
        var tasks = new List<JsonObject>();
        if (tests) tasks.Add(VsCodeFiles.ProcessTask("pytest", interpreter, ["-m", "pytest"], null, group: "test"));
        if (app) tasks.Add(VsCodeFiles.ProcessTask($"run {package}", interpreter, ["-m", package], null));
        await files.WriteJsonAsync(VsCodeFiles.TasksPath, VsCodeFiles.Tasks([.. tasks]), ct).ConfigureAwait(false);

        // VS Code's keys, which the Python stage's interpreter selection reads; harmless before it is on main.
        var settings = new JsonObject();
        if (venv) settings["python.defaultInterpreterPath"] = VsCodeFiles.VenvPython;
        if (tests)
        {
            settings["python.testing.pytestEnabled"] = true;
            settings["python.testing.pytestArgs"] = new JsonArray("tests");
        }
        if (settings.Count > 0) await files.WriteJsonAsync(VsCodeFiles.SettingsPath, settings, ct).ConfigureAwait(false);
    }

    internal static string PyProject(string name, string package, bool app, bool tests, Version python)
    {
        var dev = tests ? "\"pytest>=8\", \"ruff>=0.6\"" : "\"ruff>=0.6\"";
        var scripts = app ? $"""

            [project.scripts]
            {name} = "{package}.__main__:main"

            """ : "\n";
        var pytest = tests ? """

            [tool.pytest.ini_options]
            testpaths = ["tests"]
            pythonpath = ["src"]
            addopts = "-ra"

            """ : "";
        return $"""
            [project]
            name = "{name}"
            version = "0.1.0"
            description = ""
            readme = "README.md"
            requires-python = ">={python.Major}.{python.Minor}"
            dependencies = []
            {scripts}
            [dependency-groups]
            dev = [{dev}]

            [build-system]
            requires = ["hatchling"]
            build-backend = "hatchling.build"

            [tool.hatch.build.targets.wheel]
            packages = ["src/{package}"]

            [tool.ruff]
            line-length = 100
            src = ["src", "tests"]

            [tool.ruff.lint]
            select = ["E", "F", "W", "I", "B", "UP"]

            [tool.ruff.format]
            quote-style = "double"
            {pytest}
            """;
    }

    private static string InitPy(string name) => $$""""
        """{{name}}."""

        __version__ = "0.1.0"


        def greet(name: str) -> str:
            """Return a greeting for *name*."""
            return f"Hello, {name}!"

        """";

    private static string MainPy(string package) => $$""""
        """Entry point: python -m {{package}}."""

        from {{package}} import greet


        def main() -> None:
            print(greet("world"))


        if __name__ == "__main__":
            main()

        """";

    private static string TestPy(string package) => $$"""
        from {{package}} import greet


        def test_greet() -> None:
            assert greet("world") == "Hello, world!"

        """;

    /// <summary>The version in "Python 3.14.7"; null when there is none.</summary>
    internal static Version? ParseVersion(string? text) =>
        text is not null && PythonVersion().Match(text) is { Success: true } m ? new Version(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex PythonVersion();
}
