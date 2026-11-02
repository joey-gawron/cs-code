using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The .NET SDK's detected tasks (<see cref="DotnetTaskProvider"/>): what a folder's solution and projects give (no SDK needed), and a real
/// console project built, broken and run under <c>dotnet watch</c> through the task runner (skips without the SDK).
/// </summary>
public class DotnetTaskProviderTests
{
    private static JsonElement Def(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Program = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    private const string Library = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    private const string XunitTests = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" /><PackageReference Include="xunit.v3" Version="3.2.2" /></ItemGroup>
        </Project>
        """;
    private const string MSTestRunner = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableMSTestRunner>true</EnableMSTestRunner></PropertyGroup>
        </Project>
        """;

    private static DotnetTaskProvider Provider(TempDirectory dir) => new(new DapPlatform { Workspace = new FakeWorkspace(dir.Uri) });

    private static TempDirectory Solution()
    {
        var dir = new TempDirectory("dotnettasks-");
        dir.WriteFile("App.slnx", """
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Project Path="src/Core/Core.csproj" />
              <Project Path="tests/App.Tests/App.Tests.csproj" />
              <Project Path="tests/Runner.Tests/Runner.Tests.csproj" />
            </Solution>
            """);
        dir.WriteFile("src/App/App.csproj", Program);
        dir.WriteFile("src/Core/Core.csproj", Library);
        dir.WriteFile("tests/App.Tests/App.Tests.csproj", XunitTests);
        dir.WriteFile("tests/Runner.Tests/Runner.Tests.csproj", MSTestRunner);
        return dir;
    }

    [Fact]
    public async Task A_solution_gives_its_own_tasks_then_each_projects_and_each_programs_run_and_watch()
    {
        using var dir = Solution();
        var tasks = await Provider(dir).ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Equal(
        [
            "dotnet: build", "dotnet: test", "dotnet: clean",
            "dotnet: build - App", "dotnet: clean - App", "dotnet: build - Core", "dotnet: clean - Core",
            "dotnet: build - App.Tests", "dotnet: clean - App.Tests", "dotnet: test - App.Tests",
            "dotnet: build - Runner.Tests", "dotnet: clean - Runner.Tests", "dotnet: test - Runner.Tests",
            "dotnet: run", "dotnet: watch", // the one program (the MSTest runner project is an executable, but a test project)
        ], tasks.Select(t => t.Label));

        var build = tasks[0];
        Assert.Equal(("dotnet", "build"), (build.Command, build.Group));
        Assert.Equal(["build", "App.slnx", "/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary"], build.Args);
        Assert.Equal(["$msCompile"], build.ProblemMatchers);
        Assert.Equal("""{"type":"dotnet","command":"build","project":"App.slnx"}""", build.Definition!.Value.GetRawText());
        Assert.Equal(["test", "App.slnx"], tasks[1].Args);
        Assert.Equal("test", tasks[1].Group);
        Assert.Null(tasks[2].Group); // clean is no build task
        Assert.Equal(["test", "tests/App.Tests/App.Tests.csproj"], tasks.Single(t => t.Label == "dotnet: test - App.Tests").Args);

        var run = tasks.Single(t => t.Label == "dotnet: run");
        Assert.Equal(["run", "--project", "src/App/App.csproj"], run.Args);
        Assert.False(run.IsBackground);
        var watch = tasks.Single(t => t.Label == "dotnet: watch");
        Assert.Equal(["watch", "run", "--project", "src/App/App.csproj"], watch.Args);
        Assert.True(watch.IsBackground);
        Assert.Equal(["$dotnet-watch"], watch.ProblemMatchers);
        Assert.Equal("dotnet watch run --project src/App/App.csproj", watch.Detail);
    }

    [Fact]
    public async Task One_project_at_the_top_gives_one_set_and_none_per_project()
    {
        using var dir = new TempDirectory("dotnettasks-");
        dir.WriteFile("Tool.csproj", Program);
        var tasks = await Provider(dir).ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Equal(["dotnet: build", "dotnet: test", "dotnet: clean", "dotnet: run", "dotnet: watch"], tasks.Select(t => t.Label));
        Assert.Equal(["build", "Tool.csproj", "/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary"], tasks[0].Args);
    }

    [Fact]
    public async Task Several_programs_are_named_and_two_projects_of_one_name_by_their_paths()
    {
        using var dir = new TempDirectory("dotnettasks-");
        dir.WriteFile("src/Web/Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        dir.WriteFile("src/Cli/Cli.csproj", Program);
        dir.WriteFile("src/Shared/Shared.csproj", Library);
        dir.WriteFile("tools/Shared/Shared.csproj", Library);
        var labels = (await Provider(dir).ProvideTasksAsync(dir.Uri, TestToken.Ct)).Select(t => t.Label).ToList();
        Assert.DoesNotContain("dotnet: build", labels); // no solution and no project at the top: nothing builds the whole folder
        Assert.Contains("dotnet: run - Web", labels);
        Assert.Contains("dotnet: watch - Cli", labels);
        Assert.Contains("dotnet: build - src/Shared/Shared.csproj", labels);
        Assert.Contains("dotnet: build - tools/Shared/Shared.csproj", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("dotnet: run - Shared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Under_a_testing_platform_global_json_test_takes_its_project_option()
    {
        using var dir = Solution();
        dir.WriteFile("global.json", """
            {
              "sdk": { "version": "10.0.100" }, // comments are allowed
              "test": { "runner": "Microsoft.Testing.Platform" }
            }
            """);
        var tasks = await Provider(dir).ProvideTasksAsync(dir.Uri, TestToken.Ct);
        Assert.Equal(["test", "--project", "App.slnx"], tasks.Single(t => t.Label == "dotnet: test").Args);
        Assert.Equal(["test", "--project", "tests/App.Tests/App.Tests.csproj"], tasks.Single(t => t.Label == "dotnet: test - App.Tests").Args);
        Assert.False(DotnetTaskProvider.IsTestingPlatformRunner("""{"test":{"runner":"VSTest"}}"""u8));
        Assert.False(DotnetTaskProvider.IsTestingPlatformRunner("not json"u8));
    }

    [Fact]
    public async Task A_folder_without_dotnet_projects_has_no_dotnet_tasks()
    {
        using var dir = new TempDirectory("dotnettasks-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"x\"\n");
        Assert.Empty(await Provider(dir).ProvideTasksAsync(dir.Uri, TestToken.Ct));
    }

    [Fact]
    public async Task A_tasks_json_entry_names_a_detected_task_or_runs_its_own_command()
    {
        using var dir = Solution();
        var provider = Provider(dir);
        var detected = await ((ITaskProvider)provider).ResolveTaskAsync(dir.Uri, Def("""{"type":"dotnet","command":"build","project":"src/App/App.csproj","label":"b"}"""), TestToken.Ct);
        Assert.Equal("dotnet: build - App", detected!.Label);

        var release = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"dotnet","command":"build","project":"src/App/App.csproj","args":["-c","Release"]}"""), TestToken.Ct);
        Assert.Equal(["build", "src/App/App.csproj", "/property:GenerateFullPaths=true", "/consoleloggerparameters:NoSummary", "-c", "Release"], release!.Args);

        var publish = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"dotnet","command":"publish","project":"src/App/App.csproj","env":{"X":"1"},"cwd":"src"}"""), TestToken.Ct);
        Assert.Equal(["publish", "src/App/App.csproj"], publish!.Args);
        Assert.Equal(("dotnet: publish - src/App/App.csproj", "1", Path.Combine(dir.Path, "src")), (publish.Label, publish.Env!["X"], publish.Cwd));
        Assert.Equal(["$msCompile"], publish.ProblemMatchers);

        Assert.Null(await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"dotnet","project":"App.slnx"}"""), TestToken.Ct)); // no command
    }

    // ------------------------------------------------------------------ the real SDK

    private static string? Dotnet()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "dotnet"))) return Path.Combine(dir, "dotnet");
        return null;
    }

    private const string Sleeper = """
        System.Console.WriteLine("up");
        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        """;

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_real_project_builds_reports_its_error_and_runs_under_dotnet_watch_as_a_tracked_background_task()
    {
        Assert.SkipWhen(Dotnet() is null, "dotnet is not on PATH");
        using var dir = new TempDirectory("dotnettasks-real-");
        dir.WriteFile("W.csproj", Program);
        dir.WriteFile("Program.cs", Sleeper);
        using var lane = new DebugLane("dotnettasks-lane");
        var output = new InMemoryOutputService();
        var diagnostics = new DiagnosticService();
        ITaskProvider[] providers = [];
        var platform = new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Output = output, Diagnostics = diagnostics, TaskProviders = () => providers };
        providers = [new DotnetTaskProvider(platform)];
        using var service = new TaskService(lane, platform);

        Assert.Contains("dotnet: watch", (await service.GetTasksAsync(dir.Uri, TestToken.Ct)).Select(t => t.Label));
        var built = await service.RunAsync("dotnet: build", dir.Uri, TestToken.Ct);
        Assert.True(built.Succeeded, built.Error + "\n" + output.Text(TaskService.ChannelName));

        // a compile error: the build fails and $msCompile puts it on the file
        dir.WriteFile("Program.cs", "System.Console.WriteLine(\"up\")\n");
        var broken = await service.RunAsync("dotnet: build", dir.Uri, TestToken.Ct);
        Assert.False(broken.Succeeded);
        // once, though without a terminal MSBuild's summary repeats it (whatever /consoleloggerparameters says)
        Assert.Equal(2, Regex.Count(output.Text(TaskService.ChannelName), @"error CS1002"));
        var error = Assert.Single(diagnostics.GetAll(), d => d.Diagnostic.Code == "CS1002");
        Assert.EndsWith(Path.GetFileName(dir.Path) + "/Program.cs", error.Uri.LocalPath, StringComparison.Ordinal); // GenerateFullPaths
        Assert.Equal(0, error.Diagnostic.Range.Start.Line);

        // dotnet watch: ready once the program started (the run returns then), the error cleared by that build; still running until terminated
        dir.WriteFile("Program.cs", Sleeper);
        var watch = await service.RunAsync("dotnet: watch", dir.Uri, TestToken.Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(3), TestToken.Ct);
        Assert.True(watch.Succeeded, watch.Error + "\n" + output.Text(TaskService.ChannelName));
        Assert.True(service.IsActive("dotnet: watch", dir.Uri));
        Assert.DoesNotContain(diagnostics.GetAll(), d => d.Diagnostic.Code == "CS1002");
        await service.TerminateAsync("dotnet: watch", dir.Uri, TestToken.Ct);
        await SessionHarness.Eventually(() => !service.IsActive("dotnet: watch", dir.Uri), 30_000, "dotnet watch still running");
    }
}
