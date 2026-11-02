using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Cpp;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>Builds <see cref="CppTestSample"/> once (CMake, the frameworks from tools/fetch-cpp-test-frameworks.sh) for the real-tool tests.</summary>
public sealed class CppSampleBuild : IAsyncLifetime
{
    private TempDirectory? _dir;

    public string? SkipReason { get; private set; }
    public string Folder => _dir!.Path;

    public async ValueTask InitializeAsync()
    {
        SkipReason = CppTestSample.SkipReason();
        if (SkipReason is not null) return;
        _dir = new TempDirectory("cscode-cpp-sample-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var failure = await CppTestSample.WriteAndBuildAsync(_dir.Path, timeout.Token);
        if (failure is not null) throw new InvalidOperationException(failure);
    }

    public ValueTask DisposeAsync()
    {
        _dir?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The C++ provider on real GoogleTest 1.17, Catch2 3.8 and doctest 2.4 executables: detection, listing with places, runs, filters.</summary>
[Trait("Category", CppTestSample.Category)]
public sealed class RealCppTests(CppSampleBuild sample) : IClassFixture<CppSampleBuild>
{
    private static async Task<(CppTestProvider Provider, IReadOnlyList<TestProject> Projects)> DetectAsync(string folder)
    {
        var provider = new CppTestProvider(new TestProviderContext(new ProcessRunner()));
        return (provider, await provider.DetectAsync(folder, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GoogleTest_executable_is_listed_with_places_and_run_with_results_and_failure_locations()
    {
        Assert.SkipWhen(sample.SkipReason is not null, sample.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var (provider, projects) = await DetectAsync(sample.Folder);
        Assert.Equal(["build/catch_tests", "build/doctest_tests", "build/gtest_tests"], projects.Select(p => p.Name).Order());
        var project = projects.Single(p => p.Framework == "GoogleTest");
        var source = Path.Combine(sample.Folder, "gtest_tests.cpp");

        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(["gtest_tests/MathTest.Adds", "gtest_tests/MathTest.DISABLED_Later", "gtest_tests/MathTest.Fails", "gtest_tests/Numbers/Param.IsPositive/0", "gtest_tests/Numbers/Param.IsPositive/1"],
            tests.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(new TestLocation(source, 5), tests.Single(t => t.Id == "gtest_tests/MathTest.Adds").Location);

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("gtest_tests/MathTest.Adds").Outcome);
        Assert.Equal(TestOutcome.Skipped, sink.Result("gtest_tests/MathTest.DISABLED_Later").Outcome);
        Assert.Equal(TestOutcome.Failed, sink.Result("gtest_tests/Numbers/Param.IsPositive/1").Outcome);
        var failed = sink.Result("gtest_tests/MathTest.Fails");
        Assert.Equal((TestOutcome.Failed, new TestLocation(source, 13), "from the test"), (failed.Outcome, failed.FailureLocation, failed.Output));
        Assert.Contains("two and two", failed.Message);

        // one test; a disabled one asked for by itself runs
        var one = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["gtest_tests/MathTest.Adds"]), one, ct);
        Assert.Equal([("gtest_tests/MathTest.Adds", TestOutcome.Passed)], one.Results.Select(r => (r.TestId, r.Outcome)));
        var disabled = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["gtest_tests/MathTest.DISABLED_Later"]), disabled, ct);
        Assert.Equal(TestOutcome.Passed, disabled.Result("gtest_tests/MathTest.DISABLED_Later").Outcome);
    }

    [Fact]
    public async Task Catch2_executable_is_listed_with_tags_and_hidden_tests_and_run_by_escaped_names()
    {
        Assert.SkipWhen(sample.SkipReason is not null, sample.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var (provider, projects) = await DetectAsync(sample.Folder);
        var project = projects.Single(p => p.Framework == "Catch2");
        var source = Path.Combine(sample.Folder, "catch_tests.cpp");

        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(["catch_tests/Name, with comma [and brackets]", "catch_tests/adds numbers", "catch_tests/fails", "catch_tests/has sections", "catch_tests/hidden test"],
            tests.Select(t => t.Id).Order(StringComparer.Ordinal));
        var fails = tests.Single(t => t.Id == "catch_tests/fails");
        Assert.Equal((new TestLocation(source, 6), "fails  [math][slow]", "catch_tests.cpp"), (fails.Location, fails.Label, fails.Groups[0].Label));
        Assert.NotNull(tests.Single(t => t.Id == "catch_tests/hidden test").SkipReason);

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("catch_tests/adds numbers").Outcome);
        Assert.Equal((TestOutcome.Failed, new TestLocation(source, 7)), (sink.Result("catch_tests/fails").Outcome, sink.Result("catch_tests/fails").FailureLocation));
        Assert.StartsWith("In section \"second\"", sink.Result("catch_tests/has sections").Message);
        Assert.DoesNotContain(sink.Results, r => r.TestId == "catch_tests/hidden test"); // hidden: not part of a full run

        var chosen = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["catch_tests/Name, with comma [and brackets]", "catch_tests/hidden test"]), chosen, ct);
        Assert.Equal([("catch_tests/Name, with comma [and brackets]", TestOutcome.Passed), ("catch_tests/hidden test", TestOutcome.Passed)],
            chosen.Results.Select(r => (r.TestId, r.Outcome)).OrderBy(r => r.TestId, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Doctest_executable_is_listed_with_suites_and_run_by_name()
    {
        Assert.SkipWhen(sample.SkipReason is not null, sample.SkipReason ?? "");
        var ct = TestContext.Current.CancellationToken;
        var (provider, projects) = await DetectAsync(sample.Folder);
        var project = projects.Single(p => p.Framework == "doctest");
        var source = Path.Combine(sample.Folder, "doctest_tests.cpp");

        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(["doctest_tests/adds", "doctest_tests/fails", "doctest_tests/skipped", "doctest_tests/suite::in suite"], tests.Select(t => t.Id).Order(StringComparer.Ordinal));
        Assert.Equal(new TestLocation(source, 6), tests.Single(t => t.Id == "doctest_tests/suite::in suite").Location);

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("doctest_tests/adds").Outcome);
        Assert.Equal((TestOutcome.Failed, new TestLocation(source, 4)), (sink.Result("doctest_tests/fails").Outcome, sink.Result("doctest_tests/fails").FailureLocation));
        Assert.Equal(TestOutcome.Passed, sink.Result("doctest_tests/suite::in suite").Outcome);
        Assert.Equal(TestOutcome.Skipped, sink.Result("doctest_tests/skipped").Outcome);

        var one = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["doctest_tests/suite::in suite"]), one, ct);
        Assert.Equal([("doctest_tests/suite::in suite", TestOutcome.Passed)], one.Results.Select(r => (r.TestId, r.Outcome)));
        var skipped = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["doctest_tests/skipped"]), skipped, ct);
        Assert.Equal(TestOutcome.Passed, skipped.Result("doctest_tests/skipped").Outcome);
    }
}
