using System.Text.Json.Nodes;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.ProjectTemplates.Generators;
using CsCode.ProjectTemplates.Templates;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.ProjectTemplates.Tests;

/// <summary>
/// Each generator makes a project in a temp folder with the real tool, then the project is built and its tests run with the same commands its
/// tasks.json names, and the program its launch.json debugs is checked to exist and run. Each test skips with its reason when the tool is
/// missing; steps that download (pytest, GoogleTest, Catch2) also need <see cref="Network.Variable"/>=1.
/// </summary>
[Trait("Category", "RealTools")]
public sealed class RealGeneratorTests
{
    private static readonly ToolRunner Runner = new();
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(8);

    private static async Task<(string Dir, ProjectResult Result, RecordingProgress Progress)> CreateAsync(
        IProjectGenerator generator, TempDirectory temp, string name, Dictionary<string, string>? options = null, bool git = false)
    {
        var check = await generator.CheckToolsAsync(TestContext.Current.CancellationToken);
        if (!check.IsAvailable) Assert.Skip($"{generator.DisplayName}: {check.Summary}. {check.InstallHint}");
        if (git) TestGit.Configure();
        var dir = temp.Combine(name);
        var progress = new RecordingProgress();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Long);
        var outcome = await new ProjectCreator(Runner).CreateAsync(generator, new ProjectRequest(name, dir, options ?? [], git), progress, cts.Token);
        Assert.True(outcome.Succeeded, $"{outcome.Error}\n{progress.Log}");
        Assert.True(File.Exists(Path.Combine(dir, outcome.Result!.MainFile!)), "main file " + outcome.Result.MainFile);
        foreach (var common in new[] { ".gitignore", "README.md", ".editorconfig" }) Assert.True(File.Exists(Path.Combine(dir, common)), common);
        return (dir, outcome.Result, progress);
    }

    private static async Task<string> RunAsync(string dir, string tool, params string[] args)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(Long);
        var result = await Runner.RunAsync(new ToolRun(tool, args, dir), null, cts.Token);
        Assert.True(result.Succeeded, $"{Path.GetFileName(tool)} {string.Join(' ', args)} exited with {result.ExitCode}:\n{result.Output}");
        return result.Output;
    }

    /// <summary>The <c>program</c> of the first launch configuration, with <c>${workspaceFolder}</c> replaced.</summary>
    private static string LaunchProgram(string dir, string? type = null)
    {
        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".vscode", "launch.json")))!;
        var config = launch["configurations"]!.AsArray().First()!;
        if (type is not null) Assert.Equal(type, config["type"]!.GetValue<string>());
        return config["program"]!.GetValue<string>().Replace("${workspaceFolder}", dir, StringComparison.Ordinal);
    }

    private static JsonNode Tasks(string dir) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".vscode", "tasks.json")))!;

    [Fact]
    public async Task Empty_writes_the_good_start_files_and_commits_them()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new EmptyProjectGenerator(Runner), temp, "notes", git: ToolLocator.Find("git") is not null);
        Assert.Equal("README.md", result.MainFile);
        if (ToolLocator.Find("git") is { } git) Assert.Contains(ProjectCreator.FirstCommitMessage, await RunAsync(dir, git, "log", "--format=%s"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rust_binary_builds_tests_and_runs_what_launch_json_debugs()
    {
        var cargo = ToolLocator.Find("cargo");
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new RustProjectGenerator(Runner), temp, "hello-rs",
            new() { [RustProjectGenerator.KindOption] = "bin", [RustProjectGenerator.EditionOption] = "2021", [ProjectOption.VcsOptionId] = "git" }, git: true);
        Assert.Equal("src/main.rs", result.MainFile);
        Assert.Contains("edition = \"2021\"", File.ReadAllText(Path.Combine(dir, "Cargo.toml")), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Contains("/target", File.ReadAllText(Path.Combine(dir, ".gitignore")), StringComparison.Ordinal);
        Assert.Equal("cargo: build", Tasks(dir)["tasks"]![0]!["label"]!.GetValue<string>());
        await RunAsync(dir, cargo!, "build");
        await RunAsync(dir, cargo!, "test");
        var program = LaunchProgram(dir, "lldb");
        Assert.Contains("Hello, world!", await RunAsync(dir, program), StringComparison.Ordinal);
        var git = ToolLocator.Find("git")!;
        Assert.Contains("src/main.rs", await RunAsync(dir, git, "show", "--name-only", "--format="), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rust_library_without_vcs_builds_and_tests()
    {
        var cargo = ToolLocator.Find("cargo");
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new RustProjectGenerator(Runner), temp, "mylib",
            new() { [RustProjectGenerator.KindOption] = "lib", [ProjectOption.VcsOptionId] = "none" });
        Assert.Equal("src/lib.rs", result.MainFile);
        Assert.False(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Contains("edition = \"2024\"", File.ReadAllText(Path.Combine(dir, "Cargo.toml")), StringComparison.Ordinal);
        await RunAsync(dir, cargo!, "test");
        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".vscode", "launch.json")))!["configurations"]![0]!;
        Assert.Equal("lib", launch["cargo"]!["filter"]!["kind"]!.GetValue<string>());
    }

    public static TheoryData<string, string, string> CMakeCases => new()
    {
        { "c", "11", "executable" }, { "c", "17", "library" }, { "c", "23", "executable" },
        { "cpp", "17", "executable" }, { "cpp", "20", "library" }, { "cpp", "23", "executable" },
    };

    /// <summary>Configure, build and ctest with the presets the tasks use (C's CTest and C++'s plain CTest option: nothing is downloaded).</summary>
    [Theory]
    [MemberData(nameof(CMakeCases))]
    public async Task CMake_project_configures_builds_and_passes_ctest(string language, string standard, string kind)
    {
        var cpp = language == "cpp";
        using var temp = new TempDirectory();
        var options = new Dictionary<string, string> { [CMakeProjectGenerator.StandardOption] = standard, [CMakeProjectGenerator.KindOption] = kind };
        options[CMakeProjectGenerator.TestsOption] = cpp ? "ctest" : ProjectOption.True;
        var (dir, result, _) = await CreateAsync(new CMakeProjectGenerator(Runner, cpp), temp, $"hello-{language}{standard}", options);
        var cmake = ToolLocator.Find("cmake")!;
        var ctest = ToolLocator.Find("ctest");
        Assert.NotNull(ctest);
        await RunAsync(dir, cmake, "--preset", "debug");
        await RunAsync(dir, cmake, "--build", "--preset", "debug");
        Assert.Contains("100% tests passed", await RunAsync(dir, ctest, "--preset", "debug"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "build", "debug", "compile_commands.json")));
        if (kind == "executable")
        {
            Assert.Equal($"src/main.{(cpp ? "cpp" : "c")}", result.MainFile);
            Assert.Contains("Hello, world!", await RunAsync(dir, LaunchProgram(dir, "lldb")), StringComparison.Ordinal);
        }
        else Assert.True(File.Exists(LaunchProgram(dir, "lldb")), "the test program launch.json debugs");
    }

    /// <summary>
    /// The framework's source archive the C++ stage fetched with the user's go-ahead (<c>.scratch/tools/cpp-test-frameworks</c>, found from the
    /// test's directory up, as the CodeLLDB tests find theirs), so FetchContent takes it from disk; without it the test downloads only when
    /// <see cref="Network.Variable"/> allows.
    /// </summary>
    private static string? LocalFrameworkArchive(string framework)
    {
        var (dir, archive) = framework == "gtest" ? ("googletest-1.17.0", "googletest-v1.17.0.tar.gz") : ("Catch2-3.8.1", "Catch2-v3.8.1.tar.gz");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Path.Combine(d.FullName, ".scratch", "tools", "cpp-test-frameworks", dir, archive) is var path && File.Exists(path)) return path;
        return null;
    }

    [Theory]
    [InlineData("gtest")]
    [InlineData("catch2")]
    public async Task Cpp_with_a_fetched_test_framework_builds_and_passes_ctest(string framework)
    {
        var archive = LocalFrameworkArchive(framework);
        if (archive is null) Network.RequireOrSkip(framework == "gtest" ? "GoogleTest (FetchContent)" : "Catch2 (FetchContent)");
        using var temp = new TempDirectory();
        var (dir, _, _) = await CreateAsync(new CMakeProjectGenerator(Runner, cpp: true), temp, "fetched-" + framework,
            new() { [CMakeProjectGenerator.TestsOption] = framework });
        var cmake = ToolLocator.Find("cmake")!;
        string[] configure = ["--preset", "debug"];
        if (archive is not null)
        {
            var sources = temp.Combine("sources");
            Directory.CreateDirectory(sources);
            await RunAsync(sources, "/usr/bin/tar", "xzf", archive);
            var extracted = Directory.GetDirectories(sources).Single();
            configure = [.. configure, $"-DFETCHCONTENT_SOURCE_DIR_{(framework == "gtest" ? "GOOGLETEST" : "CATCH2")}={extracted}"];
        }
        await RunAsync(dir, cmake, configure);
        await RunAsync(dir, cmake, "--build", "--preset", "debug");
        var ctest = await RunAsync(dir, ToolLocator.Find("ctest")!, "--preset", "debug");
        Assert.Contains("100% tests passed", ctest, StringComparison.Ordinal);
        Assert.Contains(framework == "gtest" ? "Greets" : "greet greets", ctest, StringComparison.Ordinal); // the framework's own tests were discovered
    }

    [Fact]
    public async Task CSharp_console_with_tests_builds_tests_and_runs_what_launch_json_debugs()
    {
        var generator = new DotnetProjectGenerator(Runner);
        var options = await generator.GetOptionsAsync(TestContext.Current.CancellationToken);
        var templates = options.Single(o => o.Id == DotnetProjectGenerator.TemplateOption).Choices.Select(c => c.Value).ToArray();
        Assert.Equal(["console", "classlib", "webapi", "xunit"], templates.Take(4)); // the curated list first, read from the real SDK
        Assert.True(templates.Length > 4, "the installed SDK's other templates follow");
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(generator, temp, "Hello.Console",
            new() { [DotnetProjectGenerator.TemplateOption] = "console", [DotnetProjectGenerator.TestsOption] = ProjectOption.True });
        Assert.Equal("src/Hello.Console/Program.cs", result.MainFile);
        var dotnet = ToolLocator.FindDotnet()!;
        var solution = Directory.GetFiles(dir, "Hello.Console.sln*").Single();
        Assert.Contains("Hello.Console.Tests", await RunAsync(dir, dotnet, "sln", solution, "list"), StringComparison.Ordinal);
        await RunAsync(dir, dotnet, "build", solution);
        await RunAsync(dir, dotnet, "test", solution);
        var program = LaunchProgram(dir, "coreclr");
        Assert.True(File.Exists(program), program);
        Assert.Contains("Hello, World!", await RunAsync(dir, dotnet, program), StringComparison.Ordinal);
        Assert.Equal("$msCompile", Tasks(dir)["tasks"]![0]!["problemMatcher"]!.GetValue<string>());
    }

    [Fact]
    public async Task CSharp_classlib_in_an_sln_builds_and_has_no_launch_configuration()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new DotnetProjectGenerator(Runner), temp, "MyLib",
            new() { [DotnetProjectGenerator.TemplateOption] = "classlib", [DotnetProjectGenerator.SolutionOption] = "sln", [DotnetProjectGenerator.TestsOption] = ProjectOption.False });
        Assert.Equal("src/MyLib/Class1.cs", result.MainFile);
        Assert.True(File.Exists(Path.Combine(dir, "MyLib.sln")));
        Assert.False(File.Exists(Path.Combine(dir, ".vscode", "launch.json")));
        await RunAsync(dir, ToolLocator.FindDotnet()!, "build", Path.Combine(dir, "MyLib.sln"));
    }

    [Fact]
    public async Task Python_app_venv_runs_the_package_as_launch_json_does()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new PythonProjectGenerator(Runner), temp, "hello-py",
            new() { [PythonProjectGenerator.KindOption] = "app", [PythonProjectGenerator.VenvOption] = ProjectOption.True, [PythonProjectGenerator.TestsOption] = ProjectOption.False });
        Assert.Equal("src/hello_py/__main__.py", result.MainFile);
        var python = Path.Combine(dir, OperatingSystem.IsWindows() ? @".venv\Scripts\python.exe" : ".venv/bin/python");
        Assert.True(File.Exists(python), "the venv's interpreter");
        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".vscode", "launch.json")))!["configurations"]![0]!;
        Assert.Equal("debugpy", launch["type"]!.GetValue<string>());
        Assert.Equal("hello_py", launch["module"]!.GetValue<string>());
        // The configuration's module run with its PYTHONPATH, as debugpy will.
        var run = await Runner.RunAsync(new ToolRun(python, ["-m", "hello_py"], dir, new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(dir, "src") }), null, TestContext.Current.CancellationToken);
        Assert.True(run.Succeeded, run.Output);
        Assert.Contains("Hello, world!", run.Output, StringComparison.Ordinal);
        Assert.Contains("[tool.ruff]", File.ReadAllText(Path.Combine(dir, "pyproject.toml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Python_package_with_pytest_installs_it_and_the_tests_pass()
    {
        Network.RequireOrSkip("pytest (from PyPI)");
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new PythonProjectGenerator(Runner), temp, "mypkg",
            new() { [PythonProjectGenerator.KindOption] = "lib", [PythonProjectGenerator.TestsOption] = ProjectOption.True });
        Assert.Empty(result.Warnings);
        var python = Path.Combine(dir, OperatingSystem.IsWindows() ? @".venv\Scripts\python.exe" : ".venv/bin/python");
        Assert.Contains("1 passed", await RunAsync(dir, python, "-m", "pytest"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zig_init_project_builds_and_tests()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new ZigProjectGenerator(Runner), temp, "hello_zig");
        Assert.Equal("src/main.zig", result.MainFile);
        var zig = ToolLocator.Find("zig")!;
        await RunAsync(dir, zig, "build");
        await RunAsync(dir, zig, "build", "test");
        Assert.True(File.Exists(LaunchProgram(dir, "lldb")));
    }

    [Fact]
    public async Task JavaScript_package_runs_and_passes_node_test()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new NodeProjectGenerator(Runner, typeScript: false), temp, "hello-js");
        Assert.Equal("src/index.js", result.MainFile);
        var package = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "package.json")))!;
        Assert.Equal("hello-js", package["name"]!.GetValue<string>());
        Assert.Equal("module", package["type"]!.GetValue<string>());
        var node = ToolLocator.Find("node")!;
        Assert.Contains("Hello, world!", await RunAsync(dir, node, LaunchProgram(dir, "pwa-node")), StringComparison.Ordinal);
        Assert.Contains("pass 1", await RunAsync(dir, node, "--test"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeScript_package_builds_with_tsc_and_its_test_passes()
    {
        Network.RequireOrSkip("typescript and @types/node (from the npm registry)");
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new NodeProjectGenerator(Runner, typeScript: true), temp, "hello-ts");
        Assert.Equal("src/index.ts", result.MainFile);
        Assert.True(File.Exists(Path.Combine(dir, "tsconfig.json")));
        var npm = ToolLocator.Find("npm")!;
        Assert.Contains("pass 1", await RunAsync(dir, npm, "test"), StringComparison.Ordinal);
        Assert.Contains("Hello, world!", await RunAsync(dir, ToolLocator.Find("node")!, Path.Combine(dir, "dist", "index.js")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Static_site_writes_its_page()
    {
        using var temp = new TempDirectory();
        var (dir, result, _) = await CreateAsync(new StaticSiteGenerator(Runner), temp, "My Site");
        Assert.Equal("index.html", result.MainFile);
        Assert.Contains("<title>My Site</title>", File.ReadAllText(Path.Combine(dir, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_launch_configurations_use_the_adapters_types()
    {
        Assert.Equal("lldb", RustProjectGenerator.Launch("x", lib: false)["configurations"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("coreclr", DotnetProjectGenerator.Launch("X", "net10.0", web: true)["configurations"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Development", DotnetProjectGenerator.Launch("X", "net10.0", web: true)["configurations"]![0]!["env"]!["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Equal("cmake: build (debug)", VsCodeFiles.LldbLaunch("d", "p", CMakeProjectGenerator.BuildTask, "c")["preLaunchTask"]!.GetValue<string>());
    }
}
