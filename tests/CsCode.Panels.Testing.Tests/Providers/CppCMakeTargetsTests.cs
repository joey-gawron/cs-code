using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Build;
using CsCode.Core.Lifecycle;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Cpp;
using CsCode.Testing.Providers.Rust;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// The C++ provider beside the CMake integration (stage 3's <see cref="ICMakeService"/>): test executables from the File API's targets, the
/// build before a run, and the change notices that make the explorer look for the executables again.
/// </summary>
public sealed class CppCMakeTargetsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A CMake integration with scripted projects and builds; builds fire <see cref="ICMakeService.BuildFinished"/> as the real one does.</summary>
    internal sealed class ScriptedCMake(params CMakeProject[] projects) : ICMakeService
    {
        private readonly Emitter<IReadOnlyList<CMakeProject>> _changed = new();
        private readonly Emitter<CMakeBuildFinished> _built = new();
        public IReadOnlyList<CMakeProject> Projects { get; private set; } = projects;
        public IEventSource<IReadOnlyList<CMakeProject>> ProjectsChanged => _changed;
        public IEventSource<CMakeBuildFinished> BuildFinished => _built;
        public ConcurrentQueue<IReadOnlyList<string>> Builds { get; } = new();
        public Func<IReadOnlyList<string>, CMakeBuildResult> Build { get; set; } = _ => new CMakeBuildResult(0);
        public CMakeProject? ProjectFor(string path) => Projects.FirstOrDefault();
        public ValueTask<CMakeProject?> ConfigureAsync(CMakeProject project, CancellationToken ct = default) => ValueTask.FromResult<CMakeProject?>(project);

        public ValueTask<CMakeBuildResult> BuildAsync(CMakeProject project, IReadOnlyList<string>? targets = null, CancellationToken ct = default)
        {
            Builds.Enqueue(targets ?? []);
            var result = Build(targets ?? []);
            _built.Fire(new CMakeBuildFinished(project, targets ?? [], result));
            return ValueTask.FromResult(result);
        }

        public void Configured(params CMakeProject[] now)
        {
            Projects = now;
            _changed.Fire(now);
        }
    }

    private static CMakeTarget Executable(TempDirectory dir, string name, string[]? dependencies = null, string[]? libraries = null) =>
        new(name, CMakeTargetType.Executable, [dir.Combine("build", name)], dir.Path, dependencies ?? [], libraries ?? [], [dir.Combine(name + ".cpp")]);

    private static CMakeProject Project(TempDirectory dir, long generation, params CMakeTarget[] targets) =>
        new(dir.Path, dir.Combine("build"), CMakeProjectState.Configured, null, null, "Debug", "Ninja", targets, "/usr/bin/ctest", generation);

    [Theory]
    [InlineData("/opt/gtest/lib/libgtest_main.a", "gtest_main")]
    [InlineData("lib/libgtest.so.1.17.0", "gtest")]
    [InlineData(@"C:\gtest\lib\gtestd.lib", "gtestd")]
    [InlineData("-lgmock", "gmock")]
    [InlineData("_deps/catch2-build/src/libCatch2Maind.a", "Catch2Maind")]
    [InlineData("-Wl,-rpath,/opt/lib", "")]
    [InlineData("\"/a b/libdoctest_with_main.a\"", "doctest_with_main")]
    public void Link_line_fragments_name_their_library(string fragment, string name) => Assert.Equal(name, CppExecutables.LibraryName(fragment));

    [Fact]
    public void A_target_links_a_framework_by_its_own_targets_or_its_link_line()
    {
        CMakeTarget T(string[] deps, string[] libs) => new("t", CMakeTargetType.Executable, ["/b/t"], "/s", deps, libs, []);
        Assert.Equal(CppFramework.GoogleTest, CppExecutables.FromTarget(T(["gtest_main", "gtest"], [])));            // FetchContent
        Assert.Equal(CppFramework.GoogleTest, CppExecutables.FromTarget(T([], ["/p/lib/libgmock_maind.a"])));        // imported, debug postfix
        Assert.Equal(CppFramework.Catch2, CppExecutables.FromTarget(T(["Catch2WithMain"], [])));
        Assert.Equal(CppFramework.Catch2, CppExecutables.FromTarget(T([], ["/p/lib/libCatch2Maind.a", "/p/lib/libCatch2d.a"])));
        Assert.Equal(CppFramework.Doctest, CppExecutables.FromTarget(T(["doctest_with_main"], [])));
        Assert.Equal(CppFramework.None, CppExecutables.FromTarget(T(["mylib"], ["-lpthread", "/p/lib/libfmt.a", "/p/lib/libgtestutils.a"])));
    }

    [Theory]
    [InlineData("gtest_tests_NOT_BUILT", "gtest_tests")]
    [InlineData("catch_tests_NOT_BUILT-b12d07c", "catch_tests")]
    [InlineData("MathTest.Adds", null)]
    [InlineData("_NOT_BUILT", null)]
    [InlineData("x_NOT_BUILTx", null)]
    public void Discovery_placeholders_name_their_target(string test, string? target) => Assert.Equal(target, CppTestProvider.NotBuiltTarget(test));

    [Fact]
    public async Task Targets_that_link_a_framework_are_listed_before_they_are_built_and_by_their_signature_after()
    {
        using var dir = new TempDirectory("cscode-cpp-targets-");
        CppProviderTests.FakeExecutable(dir, "build/unit_runner", "list-test-cases");  // doctest: nothing on its link line, its signature once built
        CppProviderTests.FakeExecutable(dir, "build/app", "nothing to see");
        CppProviderTests.FakeExecutable(dir, "build/math_tests", "gtest_list_tests");  // the globs find it too: listed once
        var cmake = new ScriptedCMake(Project(dir, 1,
            Executable(dir, "gtest_tests", libraries: ["/p/lib/libgtest_main.a", "/p/lib/libgtest.a"]),
            Executable(dir, "catch_tests", dependencies: ["Catch2WithMain", "Catch2"]),
            Executable(dir, "doctest_tests"),
            Executable(dir, "unit_runner"),
            Executable(dir, "app", libraries: ["-lpthread"]),
            Executable(dir, "math_tests", libraries: ["/p/lib/libgtest_main.a"]),
            new CMakeTarget("helpers", CMakeTargetType.StaticLibrary, [dir.Combine("build", "libhelpers.a")], dir.Path, ["gtest"], [], []),
            // googletest's own tests, from FetchContent (gtest_build_tests): its sources are under the build directory
            new CMakeTarget("googletest-unit", CMakeTargetType.Executable, [dir.Combine("build", "_deps", "googletest-build", "googletest-unit")],
                dir.Combine("build", "_deps", "googletest-src"), ["gtest_main"], [], [])));
        var provider = new CppTestProvider(new TestProviderContext(new ScriptedProcesses()), cmake: cmake) { CTestEnabled = () => false };

        var projects = await provider.DetectAsync(dir.Path, Ct);
        Assert.Equal([("build/catch_tests", "Catch2"), ("build/gtest_tests", "GoogleTest"), ("build/math_tests", "GoogleTest"), ("build/unit_runner", "doctest")],
            projects.Select(p => (p.Name, p.Framework)).Order());
        var unbuilt = projects.Single(p => p.Name == "build/gtest_tests");
        var error = await Assert.ThrowsAsync<TestDiscoveryException>(() => provider.DiscoverAsync(unbuilt, new RecordingSink(), Ct));
        Assert.Equal("Not built yet: running its tests builds gtest_tests.", error.Message);
        Assert.Equal("gtest_tests", provider.TargetFor(unbuilt.FilePath)?.Target.Name);
        Assert.Null(provider.TargetFor(dir.Combine("elsewhere", "gtest_tests")));
    }

    [Fact]
    public async Task Targets_ctest_lists_through_placeholders_before_a_build_are_left_to_the_ctest_provider()
    {
        using var dir = new TempDirectory("cscode-cpp-placeholders-");
        dir.WriteFile("build/CTestTestfile.cmake", "");
        var listing = JsonSerializer.Serialize(new
        {
            kind = "ctestInfo",
            tests = new object[] { new { name = "gtest_tests_NOT_BUILT" }, new { name = "catch_tests_NOT_BUILT-b12d07c" }, new { name = "other_NOT_BUILT" } },
            version = new { major = 1, minor = 0 },
        });
        var processes = new ScriptedProcesses { Answer = spec => spec.Arguments.Contains("--show-only=json-v1") ? (0, [listing], []) : (1, [], ["unexpected"]) };
        var cmake = new ScriptedCMake(Project(dir, 1,
            Executable(dir, "gtest_tests", libraries: ["/p/lib/libgtest_main.a"]),
            Executable(dir, "catch_tests", dependencies: ["Catch2WithMain"]),
            Executable(dir, "more_tests", dependencies: ["gtest_main"])));
        var provider = new CppTestProvider(new TestProviderContext(processes), cmake: cmake);
        Assert.Equal(["build/more_tests"], (await provider.DetectAsync(dir.Path, Ct)).Select(p => p.Name));
    }

    private static ScriptedProcesses GoogleTestProcesses(TempDirectory dir) => new()
    {
        Answer = spec =>
        {
            var source = dir.Combine("gtest_tests.cpp").Replace("/", "\\/", StringComparison.Ordinal);
            if (spec.Arguments.Contains("--gtest_list_tests"))
            {
                File.WriteAllText(spec.Arguments.First(a => a.StartsWith("--gtest_output=json:", StringComparison.Ordinal))["--gtest_output=json:".Length..],
                    Fixture.Read("cpp/gtest-list.json").Replace("\\/work\\/cppsample\\/gtest_tests.cpp", source, StringComparison.Ordinal));
                return (0, [], []);
            }
            File.WriteAllText(spec.Arguments.First(a => a.StartsWith("--gtest_output=json:", StringComparison.Ordinal))["--gtest_output=json:".Length..],
                Fixture.Read("cpp/gtest-results.json").Replace("\\/work\\/cppsample\\/gtest_tests.cpp", source, StringComparison.Ordinal));
            return (1, Fixture.Read("cpp/gtest-run.txt").Split('\n'), []);
        },
    };

    [Fact]
    public async Task A_run_builds_the_target_first_and_lists_what_the_build_changed()
    {
        using var dir = new TempDirectory("cscode-cpp-build-run-");
        var target = Executable(dir, "gtest_tests", dependencies: ["gtest_main"]);
        var cmake = new ScriptedCMake(Project(dir, 1, target));
        var processes = GoogleTestProcesses(dir);
        var settings = new Dictionary<string, JsonElement>();
        var provider = new CppTestProvider(new TestProviderContext(processes), k => settings.TryGetValue(k, out var v) ? v : null, cmake) { CTestEnabled = () => false };
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));

        // the first run builds the executable, lists it (announcing its tests: the explorer had none) and runs it
        cmake.Build = targets =>
        {
            CppProviderTests.FakeExecutable(dir, "build/gtest_tests", "gtest_list_tests");
            return new CMakeBuildResult(0);
        };
        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, Ct);
        Assert.Null(outcome.Error);
        Assert.Equal(["gtest_tests"], Assert.Single(cmake.Builds));
        Assert.Equal(11, sink.DiscoveredTests.Count);
        Assert.Equal(2, processes.Started.Count); // the listing, then the run
        Assert.Equal(TestOutcome.Passed, sink.Result("gtest_tests/MathTest.Adds").Outcome);

        // a build that changed nothing: no listing
        cmake.Build = _ => new CMakeBuildResult(0);
        await provider.RunAsync(new TestRunRequest(project, ["gtest_tests/MathTest.Adds"]), new RecordingSink(), Ct);
        Assert.Equal(2, cmake.Builds.Count);
        Assert.Equal(3, processes.Started.Count);

        // a failed build: nothing runs
        cmake.Build = _ => new CMakeBuildResult(1, Errors: 2);
        var failed = await provider.RunAsync(new TestRunRequest(project, null), new RecordingSink(), Ct);
        Assert.Equal("The build of gtest_tests failed with 2 error(s) (see the Error List).", failed.Error);
        Assert.Equal(3, processes.Started.Count);
        var debugged = await provider.DebugAsync(new TestRunRequest(project, null), new RecordingSink(), new RecordingLauncher(), Ct);
        Assert.Equal(failed.Error, debugged.Error);

        // cmake.buildBeforeRun: false runs the executable as it is
        settings[CppTestProvider.BuildBeforeRunSetting] = JsonSerializer.SerializeToElement(false);
        await provider.RunAsync(new TestRunRequest(project, null), new RecordingSink(), Ct);
        Assert.Equal(4, cmake.Builds.Count);
        Assert.Equal(4, processes.Started.Count);
    }

    [Fact]
    public async Task New_targets_and_finished_builds_say_the_projects_changed()
    {
        using var dir = new TempDirectory("cscode-cpp-changes-");
        var cmake = new ScriptedCMake(Project(dir, 1, Executable(dir, "gtest_tests", dependencies: ["gtest_main"])));
        var provider = new CppTestProvider(new TestProviderContext(new ScriptedProcesses()), cmake: cmake);
        var fired = 0;
        var subscription = provider.ProjectsChanged.Subscribe(_ => Interlocked.Increment(ref fired));

        cmake.Configured(cmake.Projects[0] with { State = CMakeProjectState.Configuring });
        cmake.Configured(cmake.Projects[0] with { State = CMakeProjectState.Configured });
        Assert.Equal(0, fired); // the same targets
        cmake.Configured(cmake.Projects[0] with { Generation = 2 });
        Assert.Equal(1, fired);
        await cmake.BuildAsync(cmake.Projects[0], ["gtest_tests"], Ct);
        Assert.Equal(2, fired);
        cmake.Configured();
        Assert.Equal(3, fired); // the project went

        subscription.Dispose();
        await cmake.BuildAsync(Project(dir, 2), null, Ct);
        Assert.Equal(3, fired);
        Assert.Same(EventSource.Never<bool>(), new CppTestProvider(new TestProviderContext(new ScriptedProcesses())).ProjectsChanged);
    }
}
