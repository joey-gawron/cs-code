using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.DotNet;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// The .NET provider against real Microsoft.Testing.Platform projects (MSTest runner, NUnit runner, TUnit) and an F# xUnit v3 project, made in
/// temp folders and built with the real SDK: detection (an MSTest or NUnit runner project sets IsTestingPlatformApplication, not IsTestProject),
/// discovery (scan for C#, the runner's listing for F#), runs with the uid filter and the TRX file, and a debug run under the real netcoredbg that
/// stops at a breakpoint in a test and reports results from the console. The packages come from nuget.org (MSTest 4.5.2, NUnit 4.6.1 +
/// NUnit3TestAdapter 6.3.1, TUnit 1.78.0, Microsoft.Testing.Extensions.TrxReport 2.5.2; the user agreed to the download on 2026-11-01): a
/// restore that cannot reach them skips with the reason.
/// </summary>
[Trait("Category", "RealTools")]
public sealed class RealTestingPlatformTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TestProviderContext Context() => new(new ProcessRunner());

    private const string MsTestProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings>
            <EnableMSTestRunner>true</EnableMSTestRunner>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="MSTest.TestFramework" Version="4.5.2" />
            <PackageReference Include="MSTest.TestAdapter" Version="4.5.2" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.5.2" />
          </ItemGroup>
        </Project>
        """;

    private const string MsTestSource = """
        namespace Samples.MsTest;

        [TestClass]
        public class CalculatorTests
        {
            [TestMethod]
            public void Adds()
            {
                var sum = 1 + 2; // @break
                Assert.AreEqual(3, sum);
            }

            [TestMethod]
            public void AddsMany() => Assert.AreEqual(6, 1 + 2 + 3);

            [TestMethod]
            public void Fails() => Assert.AreEqual(4, 2 + 3, "two and three");

            [TestMethod]
            [DataRow(1, 2, 3)]
            [DataRow(2, 2, 4)]
            public void Sums(int a, int b, int sum) => Assert.AreEqual(sum, a + b);
        }
        """;

    private const string NUnitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings>
            <EnableNUnitRunner>true</EnableNUnitRunner>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="NUnit" Version="4.6.1" />
            <PackageReference Include="NUnit3TestAdapter" Version="6.3.1" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.5.2" />
          </ItemGroup>
        </Project>
        """;

    private const string NUnitSource = """
        using NUnit.Framework;

        namespace Samples.NUnitTests;

        public class CalculatorTests
        {
            [Test]
            public void Adds()
            {
                var sum = 1 + 2; // @break
                Assert.That(sum, Is.EqualTo(3));
            }

            [Test]
            public void AddsMany() => Assert.That(1 + 2 + 3, Is.EqualTo(6));

            [Test]
            public void Fails() => Assert.That(2 + 3, Is.EqualTo(4), "two and three");

            [TestCase(1, 2, 3)]
            [TestCase(2, 2, 4)]
            public void Sums(int a, int b, int sum) => Assert.That(a + b, Is.EqualTo(sum));
        }
        """;

    private const string TUnitProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="TUnit.Engine" Version="1.78.0" />
            <PackageReference Include="TUnit.Assertions" Version="1.78.0" />
            <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.5.2" />
          </ItemGroup>
        </Project>
        """;

    private const string TUnitSource = """
        namespace Samples.TUnitTests;

        public class CalculatorTests
        {
            [Test]
            public async Task Adds() => await Assert.That(1 + 2).IsEqualTo(3);

            [Test]
            public async Task AddsMany() => await Assert.That(1 + 2 + 3).IsEqualTo(6);

            [Test]
            public async Task Fails() => await Assert.That(2 + 3).IsEqualTo(4);
        }
        """;

    private const string FSharpProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
          <ItemGroup><Compile Include="Tests.fs" /></ItemGroup>
          <ItemGroup><PackageReference Include="xunit.v3" Version="3.2.2" /></ItemGroup>
        </Project>
        """;

    private const string FSharpSource = """
        module Samples.FsTests

        open Xunit

        [<Fact>]
        let ``adds two numbers`` () = Assert.Equal(3, 1 + 2)

        [<Fact>]
        let ``fails on purpose`` () = Assert.Equal(4, 2 + 3)

        module Nested =
            [<Fact>]
            let ``inner test`` () = Assert.True(true)
        """;

    /// <summary>The project written and built (restore included); skips when the packages cannot be restored.</summary>
    private static async Task<(TempDirectory Dir, string Source)> FixtureAsync(string name, string projectFile, string project, string sourceFile, string source)
    {
        var dotnet = ProcessRunner.FindDotnet();
        if (!Path.IsPathRooted(dotnet) && !File.Exists(dotnet)) Assert.Skip("dotnet is not installed");
        var dir = new TempDirectory("cscode-mtp-" + name + "-");
        dir.WriteFile(name + "/" + projectFile, project);
        var src = dir.WriteFile(name + "/" + sourceFile, source);
        var psi = new ProcessStartInfo(dotnet) { WorkingDirectory = dir.Combine(name), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "build", "-v", "q", "-nologo" }) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(Ct);
        var stderr = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        var output = await stdout + await stderr;
        if (p.ExitCode != 0)
        {
            dir.Dispose();
            if (output.Contains("NU1101", StringComparison.Ordinal) || output.Contains("NU1301", StringComparison.Ordinal) || output.Contains("NU1102", StringComparison.Ordinal))
                Assert.Skip("the test framework packages could not be restored from nuget.org (offline?): " + output.Split('\n').FirstOrDefault(l => l.Contains("NU1")));
            Assert.Fail($"building {name} failed:\n{output}");
        }
        return (dir, src);
    }

    [Fact]
    public async Task An_MSTest_runner_project_is_detected_run_by_uid_and_read_from_its_trx()
    {
        var (dir, source) = await FixtureAsync("MsTestRunner", "MsTestRunner.csproj", MsTestProject, "CalculatorTests.cs", MsTestSource);
        using var _ = dir;
        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal(DotNetTestProvider.FrameworkMtp + ".mstest", project.Framework);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), Ct);
        Assert.Equal(["Samples.MsTest.CalculatorTests.Adds", "Samples.MsTest.CalculatorTests.AddsMany", "Samples.MsTest.CalculatorTests.Fails", "Samples.MsTest.CalculatorTests.Sums"],
            tests.Select(t => t.Id));

        var all = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), all, Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", all.Lines));
        Assert.Contains(all.Lines, l => l.Contains("--report-trx", StringComparison.Ordinal));
        Assert.Equal(TestOutcome.Passed, all.Result("Samples.MsTest.CalculatorTests.Adds").Outcome);
        var failed = all.Result("Samples.MsTest.CalculatorTests.Fails");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Contains("two and three", failed.Message);
        Assert.Equal(source, failed.FailureLocation?.FilePath);
        Assert.Equal(TestOutcome.Passed, all.Result("Samples.MsTest.CalculatorTests.Sums", "Sums (1,2,3)").Outcome);
        Assert.Equal(TestOutcome.Passed, all.Result("Samples.MsTest.CalculatorTests.Sums", "Sums (2,2,4)").Outcome);

        // one method with data rows, and one test: exactly those run (the uid filter), both rows of Sums
        var some = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(project, ["Samples.MsTest.CalculatorTests.Sums", "Samples.MsTest.CalculatorTests.Adds"]), some, Ct);
        Assert.Contains(some.Lines, l => l.Contains("--filter-uid", StringComparison.Ordinal));
        Assert.Equal(["Samples.MsTest.CalculatorTests.Adds", "Samples.MsTest.CalculatorTests.Sums", "Samples.MsTest.CalculatorTests.Sums"],
            some.Results.Select(r => r.TestId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_NUnit_runner_project_runs_a_parameterized_test_by_the_uids_of_its_cases()
    {
        var (dir, _) = await FixtureAsync("NUnitRunner", "NUnitRunner.csproj", NUnitProject, "CalculatorTests.cs", NUnitSource);
        using var __ = dir;
        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal(DotNetTestProvider.FrameworkMtp + ".nunit", project.Framework);

        // NUnit's own --filter parser rejects the escaped '(' a parameterized case needs ("Unexpected Eof"): the uids select them
        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["Samples.NUnitTests.CalculatorTests.Sums", "Samples.NUnitTests.CalculatorTests.Fails"]), sink, Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("Samples.NUnitTests.CalculatorTests.Sums", "Sums(1,2,3)").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("Samples.NUnitTests.CalculatorTests.Sums", "Sums(2,2,4)").Outcome);
        Assert.Equal(TestOutcome.Failed, sink.Result("Samples.NUnitTests.CalculatorTests.Fails").Outcome);
        Assert.Equal(3, sink.Results.Count);
    }

    [Fact]
    public async Task A_TUnit_project_is_detected_and_one_test_runs()
    {
        var (dir, _) = await FixtureAsync("TUnitTests", "TUnitTests.csproj", TUnitProject, "CalculatorTests.cs", TUnitSource);
        using var __ = dir;
        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal(DotNetTestProvider.FrameworkMtp + ".tunit", project.Framework);
        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["Samples.TUnitTests.CalculatorTests.Fails"]), sink, Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        var only = Assert.Single(sink.Results);
        Assert.Equal(("Samples.TUnitTests.CalculatorTests.Fails", TestOutcome.Failed), (only.TestId, only.Outcome));
    }

    [Fact]
    public async Task An_FSharp_project_is_discovered_through_its_runner_with_locations_and_run()
    {
        var (dir, source) = await FixtureAsync("FsXunit", "FsXunit.fsproj", FSharpProject, "Tests.fs", FSharpSource);
        using var _ = dir;
        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal(DotNetTestProvider.FrameworkXunitV3, project.Framework);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), Ct);
        Assert.Equal(["Samples.FsTests.adds two numbers", "Samples.FsTests.fails on purpose", "Samples.FsTests+Nested.inner test"], tests.Select(t => t.Id));
        Assert.Equal(new TestLocation(source, 5), tests[0].Location);
        Assert.Equal(new TestLocation(source, 12), tests[2].Location);
        // the editor's glyphs: the scan of the open file matches the discovered tests
        var scanned = provider.ScanSource(source, FSharpSource);
        Assert.True(provider.Matches(tests[2], source, Assert.Single(scanned, s => s.Line == 12)));

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["Samples.FsTests.fails on purpose"]), sink, Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Failed, Assert.Single(sink.Results).Outcome);
    }

    public static TheoryData<string> DebuggedFrameworks => new() { "mstest", "nunit" };

    [Theory]
    [MemberData(nameof(DebuggedFrameworks))]
    public async Task Debugging_a_testing_platform_test_stops_at_its_breakpoint_and_reports_the_result(string framework)
    {
        var netcoredbg = FindNetCoreDbg();
        Assert.SkipUnless(netcoredbg is not null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/)");
        var (dir, source) = framework == "mstest"
            ? await FixtureAsync("MsTestRunner", "MsTestRunner.csproj", MsTestProject, "CalculatorTests.cs", MsTestSource)
            : await FixtureAsync("NUnitRunner", "NUnitRunner.csproj", NUnitProject, "CalculatorTests.cs", NUnitSource);
        using var fixture = dir;
        var id = framework == "mstest" ? "Samples.MsTest.CalculatorTests.Adds" : "Samples.NUnitTests.CalculatorTests.Adds";
        var text = framework == "mstest" ? MsTestSource : NUnitSource;
        var line = text.Split('\n').ToList().FindIndex(l => l.Contains("// @break", StringComparison.Ordinal));

        await using var debug = RealDebugger.Create(netcoredbg!);
        debug.Service.Breakpoints.Toggle(new Uri(source), line);
        var stops = new List<DebugStopLocation>();
        using var sub = debug.Service.StopLocationChanged.Subscribe(location =>
        {
            if (location is null) return;
            lock (stops) stops.Add(location);
            _ = location.Session.ContinueAsync().AsTask();
        });

        var provider = new DotNetTestProvider(Context());
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        var sink = new RecordingSink();
        var outcome = await provider.DebugAsync(new TestRunRequest(project, [id]), sink, debug, Ct).WaitAsync(TimeSpan.FromMinutes(3), Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join("\n", sink.Lines));
        lock (stops)
        {
            var stop = Assert.Single(stops);
            Assert.Equal(new Uri(source), stop.Uri);
            Assert.Equal(line, stop.Line);
        }
        Assert.Equal(TestOutcome.Passed, Assert.Single(sink.Results).Outcome);
        Assert.DoesNotContain(sink.Lines, l => l.Contains("--report-trx", StringComparison.Ordinal));
    }

    private static string? FindNetCoreDbg()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } fromEnv && File.Exists(fromEnv)) return fromEnv;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Path.Combine(d.FullName, ".scratch", "tools", "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName) is var c && File.Exists(c)) return c;
        return null;
    }

    /// <summary>The real debug service (CsCode.Dap with netcoredbg) over test platform services, as a test debug launcher.</summary>
    private sealed class RealDebugger : ITestDebugLauncher, IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TestAppPaths _paths;

        private RealDebugger(ServiceProvider provider, TestAppPaths paths)
        {
            _provider = provider;
            _paths = paths;
            Service = provider.GetRequiredService<IDebugService>();
        }

        public IDebugService Service { get; }

        public static RealDebugger Create(string netcoredbg)
        {
            var settings = new TestConfigurationService();
            settings.Set(NetCoreDbgAdapterFactory.SettingPath, netcoredbg);
            var paths = new TestAppPaths();
            var documents = new DocumentManager();
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(settings);
            services.AddSingleton<IWindowService>(new RecordingWindowService());
            services.AddSingleton<ICommandService, TestCommandService>();
            services.AddSingleton<IContextKeyService, TestContextKeyService>();
            services.AddSingleton<IOutputService, InMemoryOutputService>();
            services.AddSingleton<INavigationService>(new RecordingNavigationService(documents));
            services.AddSingleton<IDocumentManager>(documents);
            services.AddSingleton<IStateStorage, MemoryState>();
            services.AddSingleton<IAppPaths>(paths);
            CsCode.Dap.ServiceCollectionExtensions.AddCsCodeDap(services);
            return new RealDebugger(services.BuildServiceProvider(), paths);
        }

        public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct) => Service.StartAsync(configuration, ct);

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            _paths.Dispose();
        }
    }

    private sealed class MemoryState : IStateStorage
    {
        private readonly Dictionary<(string, StorageScope), object?> _values = new();
        public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => _values.TryGetValue((key, scope), out var v) && v is T t ? t : default;
        public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _values[(key, scope)] = value;
        public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove((key, scope));
        public Task FlushAsync() => Task.CompletedTask;
    }
}
