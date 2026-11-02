using Xunit;
using CsCode.Core.Build;
using CsCode.Core.Languages;
using CsCode.TestDoubles;

namespace CsCode.Build.CMake.Tests;

/// <summary>The real cmake, ninja and C compiler on a sample project (skipped with the reason when one is missing).</summary>
[Trait("Category", CMakeCSample.Category)]
public sealed class RealCMakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CMakeService Real(string folder, TestConfigurationService? config = null, IDiagnosticService? diagnostics = null, InMemoryOutputService? output = null) =>
        new(new TestWorkspace(folder), config ?? new TestConfigurationService(), new MemoryState(), diagnostics ?? new DiagnosticService(),
            output ?? new InMemoryOutputService(), null, new CMakeProcesses(), null, null);

    [Fact]
    public async Task Configures_builds_and_lists_targets_from_the_file_api()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var output = new InMemoryOutputService();
        using var service = Real(dir.Path, output: output);
        await service.Detected.WaitAsync(Ct);

        var configured = await service.ConfigureAsync(service.Projects[0], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);

        Assert.True(configured is not null, output.Text(CMakeService.OutputChannelName));
        Assert.Equal("Ninja", configured.Generator);
        Assert.Equal(["app", "math_tests", "mathlib"], configured.Targets.Select(t => t.Name));
        var app = configured.Targets[0];
        Assert.Equal(Path.Combine(configured.BuildDirectory, "app"), app.Executable);
        Assert.Contains("mathlib", app.Dependencies);
        Assert.Contains(app.Sources, s => s.EndsWith(Path.Combine("src", "main.c"), StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(configured.BuildDirectory, "compile_commands.json")), "compile commands are exported for clangd");
        Assert.NotNull(configured.CTestPath);

        var built = await service.BuildAsync(configured, null, Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(built.Succeeded, output.Text(CMakeService.OutputChannelName));
        Assert.True(File.Exists(app.Executable));
    }

    [Fact]
    public async Task Compiler_errors_of_a_real_build_reach_the_error_list()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var main = dir.WriteFile("src/main.c", "int main(void) {\n    return missing_symbol;\n}\n");
        var diagnostics = new DiagnosticService();
        var output = new InMemoryOutputService();
        using var service = Real(dir.Path, diagnostics: diagnostics, output: output);
        await service.Detected.WaitAsync(Ct);

        var result = await service.BuildAsync(service.Projects[0], ["app"], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);

        Assert.False(result.Succeeded);
        Assert.True(result.Errors >= 1, output.Text(CMakeService.OutputChannelName));
        var problem = Assert.Single(diagnostics.Get(new Uri(TempDirectory.PhysicalPath(main))).Concat(diagnostics.Get(new Uri(main))).Where(d => d.Severity == DiagnosticSeverity.Error).DistinctBy(d => d.Message));
        Assert.Contains("missing_symbol", problem.Message, StringComparison.Ordinal);
        Assert.Equal(1, problem.Range.Start.Line);
    }

    [Fact]
    public async Task Configures_from_a_preset()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir, presets: true);
        var output = new InMemoryOutputService();
        using var service = Real(dir.Path, output: output);
        await service.Detected.WaitAsync(Ct);
        await service.SelectConfigurePresetAsync(dir.Path, "release");

        var configured = await service.ConfigureAsync(service.Projects[0], Ct).AsTask().WaitAsync(TimeSpan.FromMinutes(2), Ct);

        Assert.True(configured is not null, output.Text(CMakeService.OutputChannelName));
        Assert.Equal(dir.Combine("build", "release"), configured.BuildDirectory);
        Assert.Equal("Release", configured.Configuration);
        Assert.Contains("CMAKE_BUILD_TYPE:STRING=Release", File.ReadAllText(dir.Combine("build", "release", "CMakeCache.txt")), StringComparison.Ordinal);
        Assert.True(File.Exists(dir.Combine("build", "release", "compile_commands.json")));
    }
}
