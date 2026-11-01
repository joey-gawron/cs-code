using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.Testing.Providers.JavaScript;
using CsCode.Testing.Providers.Rust;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

public sealed class XunitJsonReporterTests
{
    private static readonly TestProject Project = new("dotnet", "xs", "/work/xs/xs.csproj", "/work/xs", DotNetTestProvider.FrameworkXunitV3);

    [Fact]
    public void Reads_starts_results_messages_output_and_theory_cases_from_a_real_run()
    {
        var sink = new RecordingSink();
        var reporter = new XunitJsonReporter(Project, sink);
        foreach (var line in Fixture.Read("xunit-v3-reporter.jsonl").Split('\n')) reporter.OnLine(line.TrimEnd('\r'));

        Assert.True(reporter.SawMessages);
        Assert.Equal(["Sample.Ns.CalcTests.Adds", "Sample.Ns.CalcTests.Fails", "Sample.Ns.CalcTests.Skipped", "Sample.Ns.CalcTests.Th"], sink.StartedIds.Order());
        Assert.Equal(TestOutcome.Passed, sink.Result("Sample.Ns.CalcTests.Adds").Outcome);
        Assert.NotNull(sink.Result("Sample.Ns.CalcTests.Adds").Duration);

        var failed = sink.Result("Sample.Ns.CalcTests.Fails");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.StartsWith("Assert.Equal() Failure: Values differ", failed.Message);
        Assert.Contains("CalcTests.Fails()", failed.StackTrace);
        Assert.Equal("hello out", failed.Output?.Trim());
        Assert.Equal(new TestLocation("/work/xs/T.cs", 5), failed.FailureLocation); // the frame in the project, not xUnit's /_/src

        var skipped = sink.Result("Sample.Ns.CalcTests.Skipped");
        Assert.Equal((TestOutcome.Skipped, "later"), (skipped.Outcome, skipped.Message));

        // a theory without pre-enumeration: one test case, a result per data row
        Assert.Equal(TestOutcome.Passed, sink.Result("Sample.Ns.CalcTests.Th", "Th(x: 1)").Outcome);
        Assert.Equal(TestOutcome.Failed, sink.Result("Sample.Ns.CalcTests.Th", "Th(x: -2)").Outcome);
        // the test cases carry their source location: discovered with it
        Assert.Contains(sink.DiscoveredTests, d => d.Id == "Sample.Ns.CalcTests.Fails" && d.Location == new TestLocation("/work/xs/T.cs", 5));
    }

    [Fact]
    public void Lines_that_are_not_messages_go_to_the_output()
    {
        var sink = new RecordingSink();
        var reporter = new XunitJsonReporter(Project, sink);
        reporter.OnLine("plain console text");
        reporter.OnLine("{ not json");
        Assert.False(reporter.SawMessages);
        Assert.Equal(["plain console text", "{ not json"], sink.Lines);
    }

    [Fact]
    public void Groups_split_namespace_and_nested_class()
    {
        var groups = XunitJsonReporter.DotNetGroups("A.B.Outer+Inner");
        Assert.Equal([("A.B", TestNodeKind.Namespace), ("Outer+Inner", TestNodeKind.Class)], groups.Select(g => (g.Label, g.Kind)));
        Assert.Equal([("Plain", TestNodeKind.Class)], XunitJsonReporter.DotNetGroups("Plain").Select(g => (g.Label, g.Kind)));
    }
}

public sealed class TrxReaderTests
{
    [Fact]
    public void Reads_a_real_dotnet_test_trx_with_outcomes_durations_messages_and_data_rows()
    {
        var results = TrxReader.Read(Fixture.Read("dotnet-test.trx"), "/work/xs");
        Assert.Equal(5, results.Count);
        var adds = results.Single(r => r.TestId == "Sample.Ns.CalcTests.Adds");
        Assert.Equal(TestOutcome.Passed, adds.Outcome);
        Assert.Null(adds.CaseLabel);
        Assert.NotNull(adds.Duration);
        var fails = results.Single(r => r.TestId == "Sample.Ns.CalcTests.Fails");
        Assert.Equal(TestOutcome.Failed, fails.Outcome);
        Assert.Contains("Values differ", fails.Message);
        Assert.Contains("hello out", fails.Output);
        Assert.Equal(new TestLocation("/work/xs/T.cs", 5), fails.FailureLocation);
        Assert.Equal(TestOutcome.Skipped, results.Single(r => r.TestId == "Sample.Ns.CalcTests.Skipped").Outcome);
        var rows = results.Where(r => r.TestId == "Sample.Ns.CalcTests.Th").ToList();
        Assert.Equal(["Th(x: -2)", "Th(x: 1)"], rows.Select(r => r.CaseLabel).Order());
        Assert.Equal(TestOutcome.Failed, rows.Single(r => r.CaseLabel == "Th(x: -2)").Outcome);
    }
}

public sealed class LibtestOutputParserTests
{
    private static readonly TestProject Project = new("rust", "rs_sample", "/work/rs/Cargo.toml", "/work/rs", "cargo");

    [Fact]
    public void Reads_libtest_results_and_failure_sections()
    {
        var sink = new RecordingSink();
        var parser = new LibtestOutputParser(Project, sink, n => "lib/" + n);
        foreach (var line in Fixture.Read("libtest-lib.txt").Split('\n')) parser.OnLine(line.TrimEnd('\r'));
        Assert.Equal(3, sink.Results.Count); // the failure waits for its section
        parser.Complete();
        Assert.Equal(TestOutcome.Passed, sink.Result("lib/tests::adds").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("lib/tests::nested::deep").Outcome);
        Assert.Equal(TestOutcome.Skipped, sink.Result("lib/tests::ignored").Outcome);
        var failed = sink.Result("lib/tests::fails");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal("assertion `left == right` failed\n  left: 3\n right: 4", failed.Message!.Replace("\r", ""));
        Assert.Equal("some output", failed.Output);
        Assert.Equal(new TestLocation(Path.GetFullPath("/work/rs/src/lib.rs"), 8), failed.FailureLocation);
        Assert.Contains("running 4 tests", sink.Lines);
    }

    [Fact]
    public void An_ignore_reason_is_the_skip_message()
    {
        var sink = new RecordingSink();
        var parser = new LibtestOutputParser(Project, sink, n => n);
        parser.OnLine("test slow ... ignored, needs network");
        Assert.Equal("needs network", sink.Result("slow").Message);
    }

    [Fact]
    public void Cargo_artifacts_give_the_test_executables_with_their_targets()
    {
        string[] lines =
        [
            """{"reason":"compiler-artifact","target":{"kind":["lib"],"crate_types":["lib"],"name":"rs_sample","src_path":"/w/src/lib.rs","test":true},"profile":{"test":false},"executable":null}""",
            """{"reason":"compiler-artifact","target":{"kind":["test"],"crate_types":["bin"],"name":"integ","src_path":"/w/tests/integ.rs","test":true},"profile":{"test":true},"executable":"/w/target/debug/deps/integ-1"}""",
            """{"reason":"compiler-artifact","target":{"kind":["lib"],"crate_types":["lib"],"name":"rs_sample","src_path":"/w/src/lib.rs","test":true},"profile":{"test":true},"executable":"/w/target/debug/deps/rs_sample-2"}""",
            "not json",
            """{"reason":"build-finished","success":true}""",
        ];
        var targets = RustTestProvider.ParseArtifacts(lines);
        Assert.Equal([("test:integ", "/w/target/debug/deps/integ-1", "/w/tests/integ.rs"), ("lib", "/w/target/debug/deps/rs_sample-2", "/w/src/lib.rs")],
            targets.Select(t => (t.Key, t.Executable, t.SourcePath)));
        var d = RustTestProvider.Discovered(targets[1], "tests::nested::deep", null);
        Assert.Equal("lib/tests::nested::deep", d.Id);
        Assert.Equal("deep", d.Label);
        Assert.Equal(["rs_sample (lib)", "tests", "nested"], d.Groups.Select(g => g.Label));
    }
}

public sealed class JestJsonReaderTests
{
    [Fact]
    public void Reads_results_ids_locations_and_suites_that_failed_to_run()
    {
        var (results, failures) = JestJsonReader.Read(Fixture.Read("jest-report.json"), "/work/js");
        Assert.Equal(["src/math.test.js::math > add > adds two numbers", "src/math.test.js::math > subtracts", "src/math.test.js::later"], results.Select(r => r.Test.Id));
        var passed = results[0];
        Assert.Equal((TestOutcome.Passed, TimeSpan.FromMilliseconds(3)), (passed.Result.Outcome, passed.Result.Duration!.Value));
        Assert.Equal(new TestLocation("/work/js/src/math.test.js", 3), passed.Test.Location);
        Assert.Equal(["src/math.test.js", "math", "add"], passed.Test.Groups.Select(g => g.Label));
        var failed = results[1].Result;
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.False(failed.Message!.Contains('\u001b', StringComparison.Ordinal)); // ANSI colours stripped
        Assert.Equal(new TestLocation("/work/js/src/math.test.js", 8), failed.FailureLocation); // the test file's frame, not node_modules
        Assert.Equal(TestOutcome.Skipped, results[2].Result.Outcome);
        Assert.Equal("src/broken.test.js", Assert.Single(failures).FileId);
    }
}
