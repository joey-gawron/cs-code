using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Dap.Adapters;
using CsCode.Panels.Testing;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Cpp;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The Test Explorer in the REAL application (<see cref="RealAppHost"/>) on a CMake C++ project (<see cref="CppTestSample"/>: GoogleTest, Catch2
/// and doctest executables, built with CMake before the folder opens; building from the app is the CMake integration's, stage 3): showing the
/// Test Explorer lists the three executables' tests with their places; Run All runs them, failures land in the Error List at the failing
/// assertion; then a GoogleTest test and a Catch2 test are each debugged to a breakpoint in them under CodeLLDB, and after Continue their results
/// come back. Skips with the reason when CMake, a compiler, the frameworks (tools/fetch-cpp-test-frameworks.sh) or CodeLLDB is missing.
/// </summary>
public class CppTestExplorerRealAppTests
{
    private static void Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        frame.Save(Path.Combine(DevHost.ScreenshotDirectory(), name + ".png"), new PngBitmapEncoderOptions());
    }

    [AvaloniaFact(Timeout = 360_000)]
    public async Task Cpp_tests_are_listed_run_and_debugged_to_a_breakpoint_in_the_real_app()
    {
        var skip = CppTestSample.SkipReason()
            ?? (CodeLldbToolchain.FindAdapter() is null ? "CodeLLDB's adapter was not found (tools/fetch-codelldb.sh, or CSCODE_CODELLDB)" : null);
        Assert.SkipWhen(skip is not null, skip ?? "");
        var ct = TestContext.Current.CancellationToken;
        var codelldb = CodeLldbToolchain.FindAdapter()!;
        await using var host = RealAppHost.Create(h =>
        {
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[CppTestProvider.DebuggerSetting] = "codelldb";
            settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
            settings[TestingSettings.OpenTesting] = "neverOpen";
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        var failure = await CppTestSample.WriteAndBuildAsync(host.Files.Path, ct);
        Assert.True(failure is null, failure);
        await host.StartAsync([host.Files.Path]);
        var testing = host.Get<TestingHost>();
        var tree = testing.Controller.Tree;
        string Output() => host.Get<Core.Platform.IOutputService>().GetOrCreate(TestingHost.OutputChannelName).GetText();

        await host.Get<Core.Platform.ICommandService>().ExecuteAsync(TestingHost.FocusView);
        Assert.True(await RealAppHost.WaitFor(() => tree.FindTest("gtest_tests/MathTest.Adds") is not null && tree.FindTest("catch_tests/fails") is not null
            && tree.FindTest("doctest_tests/adds") is not null && !testing.Controller.IsDiscovering, 60_000),
            "discovered: " + string.Join(", ", tree.AllTests().Select(t => t.TestId)) + "; output: " + Output());
        var gtestSource = host.Files.Combine("gtest_tests.cpp");
        var catchSource = host.Files.Combine("catch_tests.cpp");
        Assert.Equal(new TestLocation(gtestSource, 5), tree.FindTest("gtest_tests/MathTest.Adds")!.Location);
        // the CTest provider lists the CMake project beside them (the sample registers no CTest tests, so none of these is left to it)
        Assert.Equal(["build/catch_tests", "build/doctest_tests", "build/gtest_tests"],
            tree.Projects.Where(p => p.Project.ProviderId == CppTestProvider.ProviderId).Select(p => p.Label).Order());

        // Run All: the failures are in the Error List at the failing assertions
        await testing.Controller.RunAllAsync();
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && tree.FindTest("catch_tests/fails")!.Outcome == TestOutcome.Failed, 60_000), "the run finished; output: " + Output());
        Assert.Equal(TestOutcome.Passed, tree.FindTest("gtest_tests/MathTest.Adds")!.Outcome);
        Assert.Equal(TestOutcome.Failed, tree.FindTest("gtest_tests/MathTest.Fails")!.Outcome);
        Assert.Equal(TestOutcome.Passed, tree.FindTest("catch_tests/adds numbers")!.Outcome);
        var diagnostics = host.Get<IDiagnosticService>();
        Assert.True(await RealAppHost.WaitFor(() => diagnostics.Get(new Uri(gtestSource)).Any(d => d.Source == "Test Explorer" && d.Range.Start.Line == 13)
            && diagnostics.Get(new Uri(catchSource)).Any(d => d.Source == "Test Explorer" && d.Range.Start.Line == 7), 10_000), "the failures in the Error List");

        // "Run All Tests" above the parameterized fixture class runs every instance of its TEST_P
        var gtestDocument = new InMemoryTextDocument(new Uri(gtestSource), "cpp", CppTestSample.GoogleTestSource);
        var lensProvider = Assert.Single(host.Get<ILanguageFeatureRegistry>().GetProviders<ICodeLensProvider>(gtestDocument), p => p is not null && p.GetType().Name == "TestCodeLensProvider");
        var lenses = await lensProvider.ProvideCodeLensesAsync(gtestDocument, TestContext.Current.CancellationToken);
        var classLine = CppTestSample.GoogleTestSource.Split('\n').ToList().FindIndex(l => l.StartsWith("class Param", StringComparison.Ordinal));
        var runAll = Assert.Single(lenses!, l => l.Command?.Title == "Run All Tests").Command!;
        Assert.Equal(classLine, Assert.Single(lenses!, l => l.Command?.Title == "Run All Tests").Range.Start.Line);
        await host.Get<Core.Platform.ICommandService>().ExecuteAsync(runAll.CommandId, [.. runAll.Arguments!.Select(a => (object?)a)]);
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && tree.FindTest("gtest_tests/Numbers/Param.IsPositive/1")?.Outcome == TestOutcome.Failed, 60_000),
            "the fixture's tests ran; output: " + Output());
        Assert.Equal(TestOutcome.Passed, tree.FindTest("gtest_tests/Numbers/Param.IsPositive/0")!.Outcome);

        // what the user sees: the explorer with the results, and the test file with its glyphs (failures marked) and lenses
        await host.Get<Core.Platform.INavigationService>().OpenAsync(new Uri(gtestSource), TextRange.At(11, 0));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "gtest_tests.cpp" && t.Controller is not null)), "gtest_tests.cpp opened");
        await DevScene.SettleAsync(20);
        Capture(host, "cpp-test-explorer");

        // Debug one GoogleTest and one Catch2 test: a breakpoint in each stops the executable there, then the result comes back
        var debug = host.Get<IDebugService>();
        foreach (var (id, file, line) in new[] { ("gtest_tests/MathTest.Adds", gtestSource, 7), ("catch_tests/adds numbers", catchSource, 5) })
        {
            // clang records the source path as CMake gave it: the folder as it was opened (/var/... on macOS, not /private/var/...)
            var source = new Uri(file);
            debug.Breakpoints.Add(source, line);
            var run = testing.Controller.RunAsync([tree.FindTest(id)!], debug: true);
            Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000),
                $"{id}: stopped (state {debug.State}); output: " + Output());
            Assert.Equal((TempDirectory.PhysicalPath(file), line), (TempDirectory.PhysicalPath(debug.StopLocation!.Uri!.LocalPath), debug.StopLocation.Line));
            Assert.Equal(TestOutcome.Running, tree.FindTest(id)!.Outcome);
            debug.Breakpoints.RemoveAll();
            await Task.Delay(300, ct);
            await debug.ActiveSession!.ContinueAsync();
            Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(60), ct));
            Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && debug.Sessions.Count == 0, 30_000), $"{id}: the debug run ended");
            Assert.Equal(TestOutcome.Passed, tree.FindTest(id)!.Outcome); // read from the results file the debugged executable wrote
        }
    }

    /// <summary>
    /// The sample with <c>gtest_discover_tests</c> and <c>catch_discover_tests</c> (<see cref="CppTestSample.CTestDiscovery"/>) in the real app:
    /// the CTest provider lists those tests and the C++ provider leaves their executables out, so each is listed once; the doctest executable,
    /// which CTest runs whole, keeps its tests in the C++ provider.
    /// </summary>
    [AvaloniaFact(Timeout = 360_000)]
    public async Task Tests_that_ctest_discovers_are_listed_once_in_the_real_app()
    {
        var skip = CppTestSample.SkipReason();
        Assert.SkipWhen(skip is not null, skip ?? "");
        var ct = TestContext.Current.CancellationToken;
        await using var host = RealAppHost.Create(h =>
        {
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[TestingSettings.OpenTesting] = "neverOpen";
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        var failure = await CppTestSample.WriteAndBuildAsync(host.Files.Path, ct, ctest: true);
        Assert.True(failure is null, failure);
        await host.StartAsync([host.Files.Path]);
        var testing = host.Get<TestingHost>();
        var tree = testing.Controller.Tree;
        string Output() => host.Get<Core.Platform.IOutputService>().GetOrCreate(TestingHost.OutputChannelName).GetText();
        IReadOnlyList<string> CppProjects() => [.. tree.Projects.Where(p => p.Project.ProviderId == CppTestProvider.ProviderId).Select(p => p.Label).Order()];

        await host.Get<Core.Platform.ICommandService>().ExecuteAsync(TestingHost.FocusView);
        Assert.True(await RealAppHost.WaitFor(() => tree.FindTest("MathTest.Adds") is not null && tree.FindTest("adds numbers") is not null
            && tree.FindTest("doctest_tests/adds") is not null && CppProjects() is ["build/doctest_tests"] && !testing.Controller.IsDiscovering, 120_000),
            "projects: " + string.Join(", ", tree.Projects.Select(p => $"{p.Project.ProviderId}:{p.Label}")) + "; tests: "
            + string.Join(", ", tree.AllTests().Select(t => t.TestId)) + "; output: " + Output());
        Assert.Single(tree.AllTests(), t => t.TestId?.EndsWith("MathTest.Adds", StringComparison.Ordinal) == true);
        Assert.Single(tree.AllTests(), t => t.TestId?.EndsWith("adds numbers", StringComparison.Ordinal) == true);
        Assert.Null(tree.FindTest("gtest_tests/MathTest.Adds"));
        Capture(host, "cpp-ctest-listed-once");
    }
}
