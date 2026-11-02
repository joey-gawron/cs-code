using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Cpp;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>The GoogleTest, Catch2 and doctest readers on output captured from real runs (Fixtures/cpp: GoogleTest 1.17.0, Catch2 3.8.1, doctest 2.4.12).</summary>
public sealed class CppParserTests
{
    private const string Gtest = "/work/cppsample/gtest_tests.cpp";
    private const string Catch = "/work/cppsample/catch_tests.cpp";
    private const string Doc = "/work/cppsample/doctest_tests.cpp";

    [Fact]
    public void GoogleTest_json_listing_gives_every_instance_with_its_file_line_and_parameters()
    {
        var tests = GoogleTestFormat.ParseListingJson(Fixture.Read("cpp/gtest-list.json"));
        Assert.Equal(
            ["MathTest.Adds", "MathTest.Fails", "MathTest.DISABLED_Later", "MathTest.Skips", "Fixture.UsesValue", "Typed/0.DefaultIsZero", "Typed/1.DefaultIsZero",
             "NsTest.Throws", "Numbers/Param.IsPositive/0", "Numbers/Param.IsPositive/1", "Numbers/Param.IsPositive/2"],
            tests.Select(t => t.Name));
        var adds = tests[0];
        Assert.Equal((Gtest, 6, "Adds"), (adds.File, adds.Line, adds.Label));
        Assert.Equal([new TestGroup("MathTest", TestNodeKind.Class)], adds.Groups);
        Assert.Equal("IsPositive/2 (GetParam() = -3)", tests.Single(t => t.Name == "Numbers/Param.IsPositive/2").Label);
        Assert.Equal("DefaultIsZero (TypeParam = long)", tests.Single(t => t.Name == "Typed/1.DefaultIsZero").Label);
        Assert.NotNull(tests.Single(t => t.Name == "MathTest.DISABLED_Later").SkipReason);
        Assert.Null(adds.SkipReason);
    }

    [Fact]
    public void GoogleTest_printed_listing_gives_the_same_names_without_places()
    {
        var printed = GoogleTestFormat.ParseListing(Fixture.Read("cpp/gtest-list.txt").Split('\n'));
        var json = GoogleTestFormat.ParseListingJson(Fixture.Read("cpp/gtest-list.json"));
        Assert.Equal(json.Select(t => t.Name).Order(), printed.Select(t => t.Name).Order());
        Assert.Equal(json.Select(t => t.Label).Order(), printed.Select(t => t.Label).Order());
        Assert.All(printed, t => Assert.Null(t.File));
    }

    [Fact]
    public void GoogleTest_console_reports_each_test_as_it_runs_with_failures_output_and_places()
    {
        var started = new List<string>();
        var results = new List<CppTestResult>();
        var parser = new GoogleTestFormat.ConsoleParser(started.Add, results.Add);
        foreach (var line in Fixture.Read("cpp/gtest-run.txt").Split('\n')) parser.OnLine(line.TrimEnd('\r'));

        Assert.Equal(["MathTest.Adds", "MathTest.Fails", "MathTest.Skips", "Fixture.UsesValue", "Typed/0.DefaultIsZero", "Typed/1.DefaultIsZero", "NsTest.Throws",
                      "Numbers/Param.IsPositive/0", "Numbers/Param.IsPositive/1", "Numbers/Param.IsPositive/2"], started);
        Assert.Null(parser.Running);
        CppTestResult R(string n) => results.Single(r => r.Name == n);
        Assert.Equal(TestOutcome.Passed, R("MathTest.Adds").Outcome);
        Assert.Equal(TimeSpan.Zero, R("MathTest.Adds").Duration);
        var fails = R("MathTest.Fails");
        Assert.Equal(TestOutcome.Failed, fails.Outcome);
        Assert.Equal("from the test", fails.Output);
        Assert.Equal((Gtest, 9), (fails.FailureFile, fails.FailureLine));
        Assert.StartsWith("gtest_tests.cpp:9: Expected equality of these values:", fails.Message); // the full path is the failure location
        Assert.EndsWith("two and two", fails.Message);
        Assert.Equal((TestOutcome.Skipped, "not yet"), (R("MathTest.Skips").Outcome, R("MathTest.Skips").Message));
        Assert.Equal(TestOutcome.Skipped, R("MathTest.DISABLED_Later").Outcome);
        var thrown = R("NsTest.Throws");
        Assert.Equal((TestOutcome.Failed, null), (thrown.Outcome, thrown.FailureFile)); // "unknown file": the test's own place is used
        Assert.Contains("C++ exception with description \"boom\"", thrown.Message);
        var param = R("Numbers/Param.IsPositive/2"); // "[  FAILED  ] Numbers/Param.IsPositive/2, where GetParam() = -3 (0 ms)"
        Assert.Equal((TestOutcome.Failed, 18), (param.Outcome, param.FailureLine));
        Assert.Equal(11, results.Count); // the summary's "[  FAILED  ] …" lines report nothing more
    }

    [Fact]
    public void GoogleTest_console_names_the_test_a_crash_interrupted()
    {
        var results = new List<CppTestResult>();
        var parser = new GoogleTestFormat.ConsoleParser(_ => { }, results.Add);
        foreach (var line in new[] { "[ RUN      ] A.Ok", "[       OK ] A.Ok (1 ms)", "[ RUN      ] A.Crashes", "about to crash", "/w/a.cpp:3: Failure", "Value of: x", "" })
            parser.OnLine(line);
        Assert.Equal("A.Crashes", parser.Running);
        Assert.Equal(("about to crash", "a.cpp:3: Value of: x"), parser.Pending());
        Assert.Single(results);
    }

    [Fact]
    public void GoogleTest_json_results_settle_outcomes_times_and_failure_places()
    {
        var results = GoogleTestFormat.ParseResults(Fixture.Read("cpp/gtest-results.json"));
        CppTestResult R(string n) => results.Single(r => r.Name == n);
        Assert.Equal(TestOutcome.Passed, R("MathTest.Adds").Outcome);
        Assert.Equal((TestOutcome.Failed, Gtest, 9), (R("MathTest.Fails").Outcome, R("MathTest.Fails").FailureFile, R("MathTest.Fails").FailureLine));
        Assert.StartsWith("gtest_tests.cpp:9: Expected equality", R("MathTest.Fails").Message);
        Assert.Equal((TestOutcome.Skipped, "not yet"), (R("MathTest.Skips").Outcome, R("MathTest.Skips").Message));
        Assert.Equal(TestOutcome.Skipped, R("MathTest.DISABLED_Later").Outcome);
        Assert.Equal((TestOutcome.Failed, null), (R("NsTest.Throws").Outcome, R("NsTest.Throws").FailureFile));
        Assert.Equal(TestOutcome.Failed, R("Numbers/Param.IsPositive/2").Outcome);
        Assert.Equal(TimeSpan.Zero, R("Fixture.UsesValue").Duration);
    }

    [Fact]
    public void Catch2_listing_gives_names_tags_places_and_marks_hidden_tests()
    {
        var tests = CatchXmlFormat.ParseListing(Fixture.Read("cpp/catch2-list.xml"));
        Assert.Equal(["adds numbers", "fails", "hidden test", "has sections", "skipped one", "throws", "Name, with comma [and brackets]", "fixture case", "Scenario: a scenario"],
            tests.Select(t => t.Name));
        Assert.Equal(("fails  [math][slow]", Catch, 8), (tests[1].Label, tests[1].File, tests[1].Line)); // tags in the label: the explorer's filter finds them
        Assert.NotNull(tests.Single(t => t.Name == "hidden test").SkipReason);
        Assert.Null(tests[0].SkipReason);
    }

    [Fact]
    public void Catch2_report_gives_outcomes_with_sections_exceptions_skips_and_captured_output()
    {
        var results = new List<CppTestResult>();
        Assert.Null(CatchXmlFormat.ParseResults(new StringReader(Fixture.Read("cpp/catch2-results.xml")), results.Add));
        CppTestResult R(string n) => results.Single(r => r.Name == n);
        Assert.Equal(TestOutcome.Passed, R("adds numbers").Outcome);
        Assert.True(R("adds numbers").Duration > TimeSpan.Zero);
        var fails = R("fails");
        Assert.Equal((TestOutcome.Failed, Catch, 11, "from catch"), (fails.Outcome, fails.FailureFile, fails.FailureLine, fails.Output));
        Assert.Equal("catch_tests.cpp:11: CHECK( Add(x, 2) == 5 ) with expansion: 4 == 5", fails.Message);
        var sections = R("has sections");
        Assert.Equal((TestOutcome.Failed, 16), (sections.Outcome, sections.FailureLine));
        Assert.StartsWith("In section \"second\":", sections.Message);
        Assert.Equal((TestOutcome.Skipped, "not yet"), (R("skipped one").Outcome, R("skipped one").Message));
        Assert.Equal((TestOutcome.Failed, "catch_tests.cpp:19: Unexpected exception: boom"), (R("throws").Outcome, R("throws").Message));
        Assert.Equal(TestOutcome.Passed, R("Scenario: a scenario").Outcome);
    }

    [Fact]
    public void A_report_cut_short_by_a_crash_gives_the_finished_tests_and_names_the_open_one()
    {
        var full = Fixture.Read("cpp/catch2-results.xml");
        var cut = full[..(full.IndexOf("<TestCase name=\"has sections\"", StringComparison.Ordinal) + 120)];
        var results = new List<CppTestResult>();
        Assert.Equal("has sections", CatchXmlFormat.ParseResults(new StringReader(cut), results.Add));
        Assert.Equal(["adds numbers", "fails"], results.Select(r => r.Name));
    }

    [Fact]
    public void Doctest_listing_and_report_give_suites_skips_subcases_and_exceptions()
    {
        var tests = DoctestXmlFormat.ParseListing(Fixture.Read("cpp/doctest-list.xml"));
        Assert.Equal(["adds", "fails", "suite::in suite", "skipped", "with subcases", "throws"], tests.Select(t => t.Name));
        var inSuite = tests[2];
        Assert.Equal(("in suite", Doc, 8), (inSuite.Label, inSuite.File, inSuite.Line));
        Assert.Equal([new TestGroup("suite", TestNodeKind.Suite)], inSuite.Groups);
        Assert.NotNull(tests.Single(t => t.Name == "skipped").SkipReason);

        var results = new List<CppTestResult>();
        Assert.Null(DoctestXmlFormat.ParseResults(new StringReader(Fixture.Read("cpp/doctest-results.xml")), results.Add));
        CppTestResult R(string n) => results.Single(r => r.Name == n);
        Assert.Equal(TestOutcome.Passed, R("adds").Outcome);
        Assert.Equal((TestOutcome.Failed, Doc, 6), (R("fails").Outcome, R("fails").FailureFile, R("fails").FailureLine));
        Assert.Equal(TestOutcome.Passed, R("suite::in suite").Outcome);
        Assert.Equal(TestOutcome.Skipped, R("skipped").Outcome);
        Assert.StartsWith("In subcase \"b\":", R("with subcases").Message);
        Assert.Equal(13, R("with subcases").FailureLine);
        Assert.Equal((TestOutcome.Failed, "Unexpected exception: boom", null), (R("throws").Outcome, R("throws").Message, R("throws").FailureFile));
    }
}

public sealed class CppScannerTests
{
    [Fact]
    public void Finds_googletest_catch2_and_doctest_macros_with_their_lines()
    {
        var text = """
            #include <gtest/gtest.h>
            // TEST(Commented, Out) {}
            /* TEST_CASE("in a block comment") */
            #define TEST_MY(x) TEST(Macro, x)
            TEST(MathTest, Adds) { }
            TEST_F(Fixture, UsesValue) {}
            TEST_P(Param, IsPositive) {}
            TYPED_TEST(Typed, DefaultIsZero) {}
              TEST_CASE("adds numbers", "[math]") {}
            TEST_CASE( "a \"quoted\" name, with comma" ) {}
            TEST_CASE("joined " "literals") {}
            SCENARIO("a scenario") {}
            TEST_CASE_METHOD(Fx, "fixture case", "[fx]") {}
            TEST_CASE_FIXTURE(Fx, "doctest fixture") {}
            TEST_CASE("decorated" * doctest::skip()) {}
            TEMPLATE_TEST_CASE("templated", "", int, float) {}
            DOCTEST_TEST_CASE("prefixed") {}
            auto s = "TEST(InString, No)";
            TEST_CASE() {}
            """;
        var found = CppTestScanner.Scan(text).Select(t => (t.Name, t.Line)).ToList();
        Assert.Equal(
        [
            ("MathTest.Adds", 4), ("Fixture.UsesValue", 5), ("Param.IsPositive", 6), ("Typed.DefaultIsZero", 7), ("adds numbers", 8),
            ("a \"quoted\" name, with comma", 9), ("joined literals", 10), ("Scenario: a scenario", 11), ("fixture case", 12), ("doctest fixture", 13),
            ("decorated", 14), ("templated", 15), ("prefixed", 16),
        ], found);
    }

    [Theory]
    [InlineData("MathTest.Adds", "MathTest.Adds", true)]
    [InlineData("Numbers/Param.IsPositive/2", "Param.IsPositive", true)]
    [InlineData("Typed/1.DefaultIsZero", "Typed.DefaultIsZero", true)]
    [InlineData("Prefix/TypedP/0.Works", "TypedP.Works", true)]
    [InlineData("MathTest.AddsMore", "MathTest.Adds", false)]
    [InlineData("templated - int", "templated", true)]
    [InlineData("templated<int>", "templated", true)]
    [InlineData("templatedx", "templated", false)]
    [InlineData("  Scenario: a scenario", "Scenario: a scenario", true)]
    [InlineData("suite::in suite", "in suite", true)]
    [InlineData("fails", "fails", true)]
    public void Runner_names_match_the_declaration_they_come_from(string runner, string source, bool matches) =>
        Assert.Equal(matches, CppTestScanner.NameMatches(runner, source));
}
