using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// Microsoft.Testing.Platform and F# support without a real tool: the listings, the uid and native filters, the console reporter, the F#
/// scanner and the detection of a project that sets only IsTestingPlatformApplication. The outputs are copied from the real runners
/// (MSTest 4.5.2, NUnit3TestAdapter 6.3.1, TUnit 1.78.0 on MTP 2.5.2; xunit.v3 3.2.2), 2026-11-01.
/// </summary>
public sealed class TestingPlatformTests
{
    private const string MtpListing = """
        warning: banner text before the document
        {"schemaVersion":1,"tests":[
         {"uid":"18c70ee5-cd4f-87ac-9389-370564e34593","displayName":"Adds","type":{"assemblyFullName":"","namespace":"Samples.MsTest","typeName":"CalculatorTests","methodName":"Adds","methodArity":0,"returnTypeFullName":"","parameterTypeFullNames":[]},"location":{"file":"/w/T/CalculatorTests.cs","lineStart":7,"lineEnd":7}},
         {"uid":"11ff1f8f-4aa7-815c-81d7-bb0860acefd3","displayName":"Sums (1,2,3)","type":{"namespace":"Samples.MsTest","typeName":"CalculatorTests","methodName":"Sums"},"location":null},
         {"uid":"1f4b4d37-9d83-8787-98b4-b635ad7bc37e","displayName":"Sums (2,2,4)","type":{"namespace":"Samples.MsTest","typeName":"CalculatorTests","methodName":"Sums"}}
        ]}
        """;

    [Fact]
    public void The_mtp_listing_gives_uids_ids_and_locations()
    {
        var listed = TestingPlatform.ParseMtpListing(MtpListing)!;
        Assert.Equal(3, listed.Count);
        Assert.Equal(new ListedTest("18c70ee5-cd4f-87ac-9389-370564e34593", "Samples.MsTest.CalculatorTests.Adds", "Adds", "Samples.MsTest.CalculatorTests", "Adds",
            new TestLocation("/w/T/CalculatorTests.cs", 6)), listed[0]);
        Assert.Equal("Samples.MsTest.CalculatorTests.Sums", listed[1].Id);
        Assert.Null(listed[1].Location);
        Assert.Null(TestingPlatform.ParseMtpListing("Unknown option '--list-tests json'"));
    }

    [Fact]
    public void A_method_selects_the_uids_of_all_its_data_rows_and_an_unknown_test_runs_everything()
    {
        var listed = TestingPlatform.ParseMtpListing(MtpListing)!;
        Assert.Equal(["11ff1f8f-4aa7-815c-81d7-bb0860acefd3", "1f4b4d37-9d83-8787-98b4-b635ad7bc37e"],
            TestingPlatform.UidsFor(["Samples.MsTest.CalculatorTests.Sums"], listed));
        Assert.Null(TestingPlatform.UidsFor(["Samples.MsTest.CalculatorTests.Renamed"], listed));
        Assert.Null(TestingPlatform.UidsFor(null, listed));
    }

    [Fact]
    public void Each_framework_gets_its_own_filter_when_uids_are_not_available()
    {
        Assert.Equal(["--filter", "FullyQualifiedName=N.C.A|FullyQualifiedName=N.C.B"], TestingPlatform.NativeFilter(MtpFramework.MSTest, ["N.C.A", "N.C.B"]));
        // NUnit's parser takes no escaped '(': "contains" also matches every case of a parameterized test
        Assert.Equal(["--filter", "FullyQualifiedName~N.C.Sums"], TestingPlatform.NativeFilter(MtpFramework.NUnit, ["N.C.Sums"]));
        Assert.Equal(["--treenode-filter", "/*/N/C/(A|B)"], TestingPlatform.NativeFilter(MtpFramework.TUnit, ["N.C.A", "N.C.B"]));
        Assert.Null(TestingPlatform.NativeFilter(MtpFramework.TUnit, ["N.C.A", "N.D.B"])); // two classes: everything runs
        Assert.Null(TestingPlatform.NativeFilter(MtpFramework.MSTest, ["N.C.Sums(1,2)"]));  // not expressible
    }

    [Fact]
    public void The_xunit_v3_listing_names_fsharp_tests_by_their_compiled_names()
    {
        var listed = TestingPlatform.ParseXunitListing("""
            [{"Assembly":"/w/FsXunit.dll","DisplayName":"Samples.FsTests.adds two numbers","ID":"2d48","Class":"Samples.FsTests","Method":"adds two numbers"},
             {"Assembly":"/w/FsXunit.dll","DisplayName":"Samples.FsTests+Nested.inner test","ID":"29bd","Class":"Samples.FsTests+Nested","Method":"inner test"}]
            [0m
            """)!;
        Assert.Equal(["Samples.FsTests.adds two numbers", "Samples.FsTests+Nested.inner test"], listed.Select(t => t.Id));
        Assert.Equal("inner test", listed[1].Method);
    }

    [Fact]
    public void Vstest_listing_reads_the_names_after_its_header()
    {
        var listed = DotNetTestProvider.ParseVsTestListing("""
            Build started...
            The following Tests are available:
                Samples.FsTests.adds two numbers
                Samples.FsTests.Sums(1, 2)
            """);
        Assert.Equal(["Samples.FsTests.adds two numbers", "Samples.FsTests.Sums"], listed.Select(t => t.Id));
    }

    [Fact]
    public void The_console_reporter_reads_results_failures_and_data_rows()
    {
        var project = new TestProject("dotnet", "T", "/w/T/T.csproj", "/w/T", DotNetTestProvider.FrameworkMtp + ".mstest");
        var sink = new RecordingSink();
        var reporter = new TestingPlatform.ConsoleReporter(project, sink, TestingPlatform.ParseMtpListing(MtpListing)!);
        foreach (var line in """
            passed Adds (2ms)
            failed Sums (1,2,3) (1s 080ms)
              Assertion failed. Expected values to be equal.
              expected: 4
              actual:   5
                at Samples.MsTest.CalculatorTests.Sums() in /w/T/CalculatorTests.cs:13
            skipped Sums (2,2,4) (0ms)

            Test run summary: Failed! - /w/T/bin/Debug/net10.0/T.dll (net10.0|arm64)
              total: 3
            """.Split('\n')) reporter.OnLine(line);
        reporter.Complete();
        Assert.True(reporter.SawResults);
        Assert.Equal(TestOutcome.Passed, sink.Result("Samples.MsTest.CalculatorTests.Adds").Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(2), sink.Result("Samples.MsTest.CalculatorTests.Adds").Duration);
        var failed = sink.Result("Samples.MsTest.CalculatorTests.Sums", "Sums (1,2,3)");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(1080), failed.Duration);
        Assert.StartsWith("Assertion failed.", failed.Message);
        Assert.Contains("CalculatorTests.cs:13", failed.StackTrace);
        Assert.Equal(TestOutcome.Skipped, sink.Result("Samples.MsTest.CalculatorTests.Sums", "Sums (2,2,4)").Outcome);
        Assert.Equal(3, sink.Results.Count);
    }

    [Fact]
    public void The_fsharp_scanner_finds_test_functions_and_members()
    {
        var found = FSharpTestScanner.Scan("""
            module Samples.FsTests

            open Xunit

            [<Fact>]
            let ``adds two numbers`` () = Assert.Equal(3, 1 + 2)

            // [<Fact>] let commented () = ()
            (* [<Fact>]
               let blockCommented () = () *)
            let helper x = x * 2

            [<Theory; InlineData(1)>]
            let positive (x: int) = Assert.True(x > 0)

            type Tests() =
                [<Test>]
                [<Category("slow")>]
                member _.``slow one`` () = ()

                [<NUnit.Framework.TestCase(1, 2)>] member this.Case (a: int, b: int) = ()

            module Nested =
                [<Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute>]
                let ``inner test`` () = ()
            """);
        Assert.Equal([("adds two numbers", 5), ("positive", 13), ("slow one", 18), ("Case", 20), ("inner test", 24)], found.Select(f => (f.Method, f.Line)));
    }

    [Fact]
    public async Task A_project_that_only_sets_IsTestingPlatformApplication_is_a_test_project()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Nu/Nu.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner></PropertyGroup></Project>");
        dir.WriteFile("Fs/Fs.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        var processes = new ScriptedProcesses
        {
            Answer = spec => spec.Arguments[1].EndsWith("Nu.csproj", StringComparison.Ordinal)
                ? (0, ["""{"Properties":{"IsTestProject":"","IsTestingPlatformApplication":"true","TargetPath":"/n/Nu.dll","TargetFramework":"net10.0","TargetFrameworks":"","Language":"C#"},"Items":{"PackageReference":[{"Identity":"NUnit"},{"Identity":"NUnit3TestAdapter"}]}}"""], [])
                : (0, ["""{"Properties":{"IsTestProject":"","IsTestingPlatformApplication":"true","TargetPath":"/f/Fs.dll","TargetFramework":"net10.0","TargetFrameworks":"","Language":"F#"},"Items":{"PackageReference":[{"Identity":"xunit.v3"}]}}"""], []),
        };
        var provider = new DotNetTestProvider(new TestProviderContext(processes));
        var projects = await provider.DetectAsync(dir.Path, TestContext.Current.CancellationToken);
        Assert.Equal([("Fs", DotNetTestProvider.FrameworkXunitV3), ("Nu", DotNetTestProvider.FrameworkMtp + ".nunit")], projects.Select(p => (p.Name, p.Framework)));
        Assert.All(processes.Started, s => Assert.Contains("-getProperty:IsTestingPlatformApplication", s.Arguments));
    }
}
