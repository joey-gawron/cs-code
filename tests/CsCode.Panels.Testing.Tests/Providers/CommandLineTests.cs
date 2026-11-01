using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>The command lines the providers build, and detection from project files (scripted processes, temp folders).</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void Xunit_v3_runs_with_the_json_reporter_and_one_method_filter_per_test()
    {
        Assert.Equal(["/o/x.dll", "-reporter", "json", "-noColor", "-noLogo", "-method", "N.C.A", "-method", "N.C.B"],
            DotNetTestProvider.XunitArguments("/o/x.dll", ["N.C.A", "N.C.B"]));
        Assert.Equal(["/o/x.dll", "-reporter", "json", "-noColor", "-noLogo"], DotNetTestProvider.XunitArguments("/o/x.dll", null));
        // beyond the limit the whole project runs
        var many = Enumerable.Range(0, DotNetTestProvider.MaxFilteredTests + 1).Select(i => "N.C.T" + i).ToList();
        Assert.DoesNotContain("-method", DotNetTestProvider.XunitArguments("/o/x.dll", many));
    }

    [Fact]
    public void VsTest_filter_names_each_test_and_escapes_the_filter_syntax()
    {
        Assert.Equal("FullyQualifiedName=N.C.A|FullyQualifiedName=N.C.B", DotNetTestProvider.VsTestFilter(["N.C.A", "N.C.B"]));
        Assert.Equal(@"FullyQualifiedName=N.C\(x\)\=1", DotNetTestProvider.VsTestFilter(["N.C(x)=1"]));
        Assert.Null(DotNetTestProvider.VsTestFilter(null));
    }

    [Fact]
    public void Msbuild_evaluation_is_read_for_test_projects_and_xunit_v3()
    {
        var json = """
            {
              "Properties": { "IsTestProject": "true", "TargetPath": "/p/bin/Debug/net10.0/p.dll", "TargetFramework": "", "TargetFrameworks": "net10.0;net8.0" },
              "Items": { "PackageReference": [ { "Identity": "Microsoft.NET.Test.Sdk" }, { "Identity": "xunit.v3" } ] }
            }
            """;
        var parsed = DotNetTestProvider.ParseEvaluation(json);
        Assert.Equal((true, "/p/bin/Debug/net10.0/p.dll", "net10.0", true), parsed);
        var lib = DotNetTestProvider.ParseEvaluation("""{"Properties":{"IsTestProject":"","TargetPath":"/l.dll","TargetFramework":"net10.0","TargetFrameworks":""},"Items":{"PackageReference":[]}}""");
        Assert.False(lib!.Value.IsTestProject);
        Assert.Null(DotNetTestProvider.ParseEvaluation("MSBUILD : error"));
    }

    [Fact]
    public async Task Dotnet_detection_evaluates_only_projects_that_mention_a_test_framework()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        dir.WriteFile("tests/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        dir.WriteFile("tests/App.Tests/bin/Debug/Copy.csproj", "<Project><PackageReference Include=\"xunit\" /></Project>"); // build output: never walked
        var processes = new ScriptedProcesses
        {
            Answer = spec => (0, ["{\"Properties\":{\"IsTestProject\":\"true\",\"TargetPath\":\"" + spec.WorkingDirectory + "/bin/x.dll\",\"TargetFramework\":\"net10.0\",\"TargetFrameworks\":\"\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"xunit.v3\"}]}}"], []),
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var projects = await provider.DetectAsync(dir.Path, TestContext.Current.CancellationToken);
        var p = Assert.Single(projects);
        Assert.Equal(("App.Tests", DotNetTestProvider.FrameworkXunitV3), (p.Name, p.Framework));
        var eval = Assert.Single(processes.Started);
        Assert.Contains("msbuild", eval.Arguments);
        Assert.Contains("-getProperty:IsTestProject", eval.Arguments);
    }

    [Fact]
    public async Task A_multi_targeted_xunit_v3_project_runs_the_assembly_of_its_first_framework()
    {
        // the outer (cross-targeting) evaluation of a <TargetFrameworks> project has no TargetPath; it is asked again for the first framework
        using var dir = new TempDirectory();
        dir.WriteFile("M/M.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        var inner = dir.Combine("M", "bin", "Debug", "net10.0", "M.dll");
        var processes = new ScriptedProcesses
        {
            Answer = spec => spec.Arguments.Contains("-p:TargetFramework=net10.0") ? (0, [inner], [])
                : spec.Arguments.Contains("msbuild") ? (0, ["{\"Properties\":{\"IsTestProject\":\"true\",\"TargetPath\":\"\",\"TargetFramework\":\"\",\"TargetFrameworks\":\"net10.0;net9.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"xunit.v3\"}]}}"], [])
                : (0, [], []),
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var ct = TestContext.Current.CancellationToken;
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        await provider.RunAsync(new TestRunRequest(project, ["N.C.A"]), new RecordingSink(), ct);
        var started = processes.Started.ToList();
        var build = Assert.Single(started, s => s.Arguments.Contains("build"));
        Assert.Equal(["-f", "net10.0"], build.Arguments.SkipWhile(a => a != "-f").Take(2));
        var run = started.Last();
        Assert.Equal(inner, run.Arguments[0]);
        Assert.Equal(["-method", "N.C.A"], run.Arguments.TakeLast(2));
        Assert.Equal(dir.Combine("M", "bin", "Debug", "net10.0"), run.WorkingDirectory);
    }

    [Fact]
    public async Task A_dotnet_project_without_a_target_path_is_not_run_with_an_empty_assembly()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("M/M.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        var processes = new ScriptedProcesses
        {
            Answer = spec => spec.Arguments.Contains("-p:TargetFramework=net10.0") ? (0, [""], [])
                : spec.Arguments.Contains("msbuild") ? (0, ["{\"Properties\":{\"IsTestProject\":\"true\",\"TargetPath\":\"\",\"TargetFramework\":\"\",\"TargetFrameworks\":\"net10.0\"},\"Items\":{\"PackageReference\":[{\"Identity\":\"xunit.v3\"}]}}"], [])
                : (0, [], []),
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var ct = TestContext.Current.CancellationToken;
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), new RecordingSink(), ct);
        Assert.Contains("no TargetPath", outcome.Error);
        Assert.DoesNotContain(processes.Started, s => s.Arguments.Count > 0 && s.Arguments[0].Length == 0);
    }

    [Fact]
    public void A_single_property_evaluation_answers_with_the_bare_value()
    {
        Assert.Equal("/p/bin/Debug/net10.0/p.dll", DotNetTestProvider.ParseTargetPath("/p/bin/Debug/net10.0/p.dll\n"));
        Assert.Equal("/p/x.dll", DotNetTestProvider.ParseTargetPath("""{"Properties":{"TargetPath":"/p/x.dll"}}"""));
        Assert.Equal("", DotNetTestProvider.ParseTargetPath("\n"));
    }

    [Fact]
    public async Task Dotnet_discovery_scans_the_project_sources_but_not_nested_projects_or_build_output()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("T/T.csproj", "<Project />");
        dir.WriteFile("T/CalcTests.cs", "namespace N;\npublic class CalcTests\n{\n    [Fact] public void Adds() { }\n    [Fact(Skip = \"x\")] public void Later() { }\n}\n");
        dir.WriteFile("T/obj/Generated.cs", "namespace N; public class G { [Fact] public void Gen() { } }");
        dir.WriteFile("T/Nested/Nested.csproj", "<Project />");
        dir.WriteFile("T/Nested/Other.cs", "namespace M; public class O { [Fact] public void Other() { } }");
        var provider = new DotNetTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var project = new TestProject("dotnet", "T", dir.Combine("T", "T.csproj"), dir.Combine("T"), DotNetTestProvider.FrameworkXunitV3);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), TestContext.Current.CancellationToken);
        Assert.Equal(["N.CalcTests.Adds", "N.CalcTests.Later"], tests.Select(t => t.Id));
        Assert.Equal(new TestLocation(dir.Combine("T", "CalcTests.cs"), 3), tests[0].Location);
        Assert.Equal("x", tests[1].SkipReason);
        Assert.Equal(["N", "CalcTests"], tests[0].Groups.Select(g => g.Label));
    }

    [Fact]
    public void Rust_runs_name_the_tests_exactly_and_a_single_test_includes_ignored()
    {
        Assert.Equal(["a::b", "--exact", "--include-ignored"], RustTestProvider.RunArguments(["a::b"]));
        Assert.Equal(["a", "b", "--exact"], RustTestProvider.RunArguments(["a", "b"]));
        Assert.Empty(RustTestProvider.RunArguments(null));
        var by = RustTestProvider.ByTarget(["lib/tests::a", "test:integ/x", "lib/tests::b"]);
        Assert.Equal(["tests::a", "tests::b"], by!["lib"]);
        Assert.Equal(["x"], by["test:integ"]);
        Assert.Null(RustTestProvider.ByTarget(null));
    }

    [Fact]
    public void Rust_debugger_follows_the_setting_and_the_platform_default()
    {
        Assert.Equal("lldb", RustTestProvider.DebugType(null, macOS: true));
        Assert.Equal("gdb", RustTestProvider.DebugType(null, macOS: false));
        Assert.Equal("lldb", RustTestProvider.DebugType("codelldb", macOS: false));
        Assert.Equal("gdb", RustTestProvider.DebugType("gdb", macOS: true));
    }

    [Fact]
    public async Task Rust_detection_takes_packages_not_workspace_roots()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Cargo.toml", "[workspace]\nmembers = [\"a\"]\n");
        dir.WriteFile("a/Cargo.toml", "[package]\nname = \"alpha\"\nversion = \"0.1.0\"\n");
        dir.WriteFile("target/debug/build/x/Cargo.toml", "[package]\nname = \"built\"\n");
        var provider = new RustTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var p = Assert.Single(await provider.DetectAsync(dir.Path, TestContext.Current.CancellationToken));
        Assert.Equal(("alpha", dir.Combine("a")), (p.Name, p.Directory));
    }

    [Fact]
    public void Jest_and_vitest_command_lines_limit_files_and_names()
    {
        var jest = new TestProject("javascript", "js", "/w/package.json", "/w", "jest");
        var args = JavaScriptTestProvider.RunnerArguments(jest, ["src/a.test.js::math > adds", "src/b.test.js::x"], "/t/r.json", debug: false);
        Assert.Equal(["--json", "--outputFile=/t/r.json", "--testLocationInResults", "--ci=false", "--testNamePattern=^(math adds|x)$", "src/a.test.js", "src/b.test.js"], args);
        var vitest = new TestProject("javascript", "js", "/w/package.json", "/w", "vitest");
        var vargs = JavaScriptTestProvider.RunnerArguments(vitest, null, "/t/r.json", debug: true);
        Assert.Equal(["run", "--reporter=json", "--outputFile=/t/r.json", "--no-file-parallelism", "--test-timeout=0"], vargs);
        Assert.Contains("--runInBand", JavaScriptTestProvider.RunnerArguments(jest, null, "/t/r.json", debug: true));
    }

    [Fact]
    public void Package_json_declares_jest_or_vitest()
    {
        Assert.Equal("jest", JavaScriptTestProvider.Framework("""{"devDependencies":{"jest":"^29"}}"""));
        Assert.Equal("vitest", JavaScriptTestProvider.Framework("""{"devDependencies":{"vitest":"^2","jest":"^29"}}"""));
        Assert.Equal("jest", JavaScriptTestProvider.Framework("""{"scripts":{"test":"jest --coverage"}}"""));
        Assert.Equal("jest", JavaScriptTestProvider.Framework("""{"jest":{"testEnvironment":"node"}}"""));
        Assert.Null(JavaScriptTestProvider.Framework("""{"dependencies":{"react":"^18"}}"""));
        Assert.Null(JavaScriptTestProvider.Framework("not json"));
    }

    [Fact]
    public void Test_ids_end_with_a_segment_only_at_a_separator()
    {
        Assert.True(TestIds.EndsWithSegment("lib/tests::adds", "tests::adds"));
        Assert.True(TestIds.EndsWithSegment("N.C.M", "C.M"));
        Assert.True(TestIds.EndsWithSegment("f.test.js::a > b", "a > b"));
        Assert.False(TestIds.EndsWithSegment("N.C.Madds", "adds"));
        Assert.True(TestIds.EndsWithSegment("x", "x"));
    }
}
