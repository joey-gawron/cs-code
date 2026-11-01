using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// The providers against real tools on small sample projects made in temp folders: dotnet (an xUnit v3 project: detection by msbuild
/// evaluation, discovery by scan, a build and a JSON-reporter run), cargo (a library with unit and integration tests: build, --list, locations,
/// run) and node (a package whose Jest is a stand-in script that writes a Jest report, so the provider's command line, the real node process
/// and the report reading are exercised without a download). A missing tool skips with the reason.
/// </summary>
public sealed class RealProjectTests
{
    private static TestProviderContext Context() => new(new ProcessRunner());

    [Fact]
    public async Task Dotnet_xunit_v3_project_is_detected_discovered_and_run()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-dotnet-tests-");
        dir.WriteFile("Calc.Tests/Calc.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><OutputType>Exe</OutputType><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
              </ItemGroup>
            </Project>
            """);
        var source = dir.WriteFile("Calc.Tests/MathTests.cs", """
            using Xunit;
            namespace Calc;

            public class MathTests(ITestOutputHelper output)
            {
                [Fact] public void Adds() => Assert.Equal(4, 2 + 2);
                [Fact] public void Fails() { output.WriteLine("from the test"); Assert.Equal(5, 2 + 2); }
                [Fact(Skip = "not yet")] public void Later() { }
                [Theory, InlineData(1), InlineData(-1)] public void Positive(int x) => Assert.True(x > 0);
            }
            """);
        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        Assert.Equal(DotNetTestProvider.FrameworkXunitV3, project.Framework);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(["Calc.MathTests.Adds", "Calc.MathTests.Fails", "Calc.MathTests.Later", "Calc.MathTests.Positive"], tests.Select(t => t.Id));

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("Calc.MathTests.Adds").Outcome);
        var failed = sink.Result("Calc.MathTests.Fails");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Contains("from the test", failed.Output);
        Assert.Equal(TestDirectory(source), TestDirectory(failed.FailureLocation!.FilePath));
        Assert.Equal(6, failed.FailureLocation.Line);
        Assert.Equal(TestOutcome.Skipped, sink.Result("Calc.MathTests.Later").Outcome);
        Assert.Equal(TestOutcome.Failed, sink.Result("Calc.MathTests.Positive", "Positive(x: -1)").Outcome);

        // one test: only it runs (the -method filter)
        var one = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["Calc.MathTests.Adds"]), one, ct);
        Assert.Equal(["Calc.MathTests.Adds"], one.Results.Select(r => r.TestId));
    }

    private static string TestDirectory(string path) => TempDirectory.PhysicalPath(path);

    [Fact]
    public async Task Cargo_package_is_built_listed_located_and_run()
    {
        var cargo = ProcessRunner.FindCargo();
        if (!Path.IsPathRooted(cargo)) Assert.Skip("cargo is not installed (not on PATH, not in ~/.cargo/bin)");
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cargo-tests-");
        dir.WriteFile("Cargo.toml", "[package]\nname = \"rs_sample\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        var lib = dir.WriteFile("src/lib.rs", """
            pub fn add(a: i32, b: i32) -> i32 { a + b }
            mod parser;

            #[cfg(test)]
            mod tests {
                use super::*;
                #[test]
                fn adds() { assert_eq!(add(1, 2), 3); }
                #[test]
                fn fails() { println!("some output"); assert_eq!(add(1, 2), 4); }
                #[test]
                #[ignore]
                fn ignored() {}
            }
            """);
        var parser = dir.WriteFile("src/parser.rs", "pub fn parse() {}\n\n#[test]\nfn parses() { parse(); }\n");
        dir.WriteFile("tests/integ.rs", "#[test]\nfn integration_works() { assert_eq!(rs_sample::add(2, 2), 4); }\n");

        var provider = new RustTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        var log = new RecordingSink();
        var tests = await provider.DiscoverAsync(project, log, ct);
        Assert.Equal(["lib/parser::parses", "lib/tests::adds", "lib/tests::fails", "lib/tests::ignored", "test:integ/integration_works"], tests.Select(t => t.Id).Order());
        Assert.Equal(new TestLocation(lib, 7), tests.Single(t => t.Id == "lib/tests::adds").Location);
        Assert.Equal(new TestLocation(parser, 3), tests.Single(t => t.Id == "lib/parser::parses").Location);

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.Equal(101, outcome.ExitCode); // a test failed
        Assert.Equal(TestOutcome.Passed, sink.Result("lib/tests::adds").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("test:integ/integration_works").Outcome);
        Assert.Equal(TestOutcome.Skipped, sink.Result("lib/tests::ignored").Outcome);
        var failed = sink.Result("lib/tests::fails");
        Assert.Equal((TestOutcome.Failed, "some output"), (failed.Outcome, failed.Output));
        Assert.Equal(9, failed.FailureLocation!.Line);

        // a single ignored test runs when it is asked for by itself
        var one = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["lib/tests::ignored"]), one, ct);
        Assert.Equal([("lib/tests::ignored", TestOutcome.Passed)], one.Results.Select(r => (r.TestId, r.Outcome)));
    }

    [Fact]
    public async Task Jest_package_runs_its_runner_with_node_and_reads_the_report()
    {
        if (ProcessRunner.FindOnPath("node") is null) Assert.Skip("node is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-jest-tests-");
        dir.WriteFile("package.json", """{ "name": "js-sample", "devDependencies": { "jest": "^29.0.0" } }""");
        var testFile = dir.WriteFile("src/math.test.js", "describe('math', () => {\n  it('adds', () => {});\n  it('fails', () => {});\n});\n");
        // the stand-in runner: writes a Jest report for what it was asked, after printing its arguments
        dir.WriteFile("node_modules/jest/bin/jest.js", """
            const fs = require('fs');
            const args = process.argv.slice(2);
            console.log('ARGS ' + JSON.stringify(args));
            const out = args.find(a => a.startsWith('--outputFile=')).slice('--outputFile='.length);
            const file = require('path').resolve('src/math.test.js');
            fs.writeFileSync(out, JSON.stringify({ testResults: [{ name: file, status: 'failed', message: '', assertionResults: [
              { ancestorTitles: ['math'], title: 'adds', status: 'passed', duration: 2, failureMessages: [], location: { line: 2, column: 3 } },
              { ancestorTitles: ['math'], title: 'fails', status: 'failed', duration: 4, failureMessages: ['Error: nope\n    at Object.<anonymous> (' + file + ':3:20)'], location: { line: 3, column: 3 } },
            ] }] }));
            process.exit(1);
            """);
        var provider = new JavaScriptTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        Assert.Equal("jest", project.Framework);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(["src/math.test.js::math > adds", "src/math.test.js::math > fails"], tests.Select(t => t.Id));

        var sink = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["src/math.test.js::math > fails"]), sink, ct);
        var args = sink.Lines.Single(l => l.StartsWith("ARGS ", StringComparison.Ordinal));
        Assert.Contains("--testNamePattern=^(math fails)$", args);
        Assert.Contains("\"src/math.test.js\"", args);
        Assert.Equal(TestOutcome.Passed, sink.Result("src/math.test.js::math > adds").Outcome);
        var failed = sink.Result("src/math.test.js::math > fails");
        Assert.Equal((TestOutcome.Failed, "Error: nope"), (failed.Outcome, failed.Message));
        // node reports resolved paths (macOS: /private/var/... for /var/...): they come back under the folder as opened
        Assert.Equal(new TestLocation(testFile, 2), failed.FailureLocation);
        Assert.Equal(new TestLocation(testFile, 1), sink.DiscoveredTests.Single(t => t.Id == "src/math.test.js::math > adds").Location);
    }

    [Fact]
    public async Task A_package_without_its_runner_installed_says_so()
    {
        using var dir = new TempDirectory("cscode-jest-tests-");
        dir.WriteFile("package.json", """{ "name": "js-sample", "devDependencies": { "vitest": "^2.0.0" } }""");
        var provider = new JavaScriptTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, TestContext.Current.CancellationToken));
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), new RecordingSink(), TestContext.Current.CancellationToken);
        Assert.Contains("Vitest is not installed", outcome.Error);
    }
}
