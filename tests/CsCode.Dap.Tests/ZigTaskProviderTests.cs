using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Zig's detected tasks (docs/ZIG.md, "Tasks"): <c>zig build -l</c>'s steps as tasks, listed in the background with the fallback tasks until it
/// answers, cached until build.zig changes, tasks.json entries; and a real <c>zig init</c> project through the task runner (skips without zig).
/// </summary>
public class ZigTaskProviderTests
{
    private const string Listing017 = """
          install (default)            Copy build artifacts to prefix path
          uninstall                    Remove build artifacts from prefix path
          run                          Run the app
          test                         Run tests
          check                        Check if it compiles
        """;

    private static JsonElement Def(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A build.zig folder, a stand-in zig (configured through zig.path) and a provider whose <c>zig build -l</c> is scripted.</summary>
    private static (TempDirectory Dir, ZigTaskProvider Provider, List<string> Listings) Make(Func<string?> answer, TimeSpan? delay = null, TimeSpan? wait = null)
    {
        var dir = new TempDirectory("zigtasks-");
        dir.WriteFile("build.zig", "const std = @import(\"std\");\n");
        var zig = dir.WriteFile("bin/zig", "");
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, zig);
        var listings = new List<string>();
        var provider = new ZigTaskProvider(new DapPlatform { Configuration = settings })
        {
            Environment = _ => null,
            ListingWait = wait ?? TimeSpan.FromSeconds(10),
            ListSteps = async (exe, root, ct) =>
            {
                lock (listings) listings.Add(root);
                if (delay is { } d) await Task.Delay(d, ct);
                return answer();
            },
        };
        return (dir, provider, listings);
    }

    [Fact]
    public void Zig_build_l_lines_give_the_steps_with_the_default_and_their_descriptions()
    {
        var steps = ZigTaskProvider.ParseSteps(Listing017 + "\nerror: something else\n    ^~~\n");
        Assert.Equal(["install", "uninstall", "run", "test", "check"], steps.Select(s => s.Name));
        Assert.True(steps[0].IsDefault);
        Assert.False(steps[2].IsDefault);
        Assert.Equal("Run the app", steps[2].Description);
    }

    [Fact]
    public async Task A_folder_without_build_zig_has_no_zig_tasks()
    {
        using var dir = new TempDirectory("zigtasks-");
        var provider = new ZigTaskProvider(new DapPlatform());
        Assert.Empty(await provider.ProvideTasksAsync(dir.Uri, Ct));
    }

    [Fact]
    public async Task Without_zig_build_run_and_test_are_listed_and_run_plain_zig()
    {
        using var dir = new TempDirectory("zigtasks-");
        dir.WriteFile("build.zig", "");
        var provider = new ZigTaskProvider(new DapPlatform()) { Environment = _ => null };
        var tasks = await provider.ProvideTasksAsync(dir.Uri, Ct);
        Assert.Equal(["zig: build", "zig: build run", "zig: build test"], tasks.Select(t => t.Label));
        Assert.All(tasks, t => Assert.Equal("zig", t.Command));
        Assert.Equal([["build"], ["build", "run"], ["build", "test"]], tasks.Select(t => t.Args));
        Assert.Equal(["build", null, "test"], tasks.Select(t => t.Group));
        Assert.All(tasks, t => Assert.Equal(["$zig"], t.ProblemMatchers));
        Assert.Equal("""{"type":"zig"}""", tasks[0].Definition!.Value.GetRawText());
        Assert.Equal("""{"type":"zig","step":"run"}""", tasks[1].Definition!.Value.GetRawText());
    }

    [Fact]
    public async Task The_projects_steps_are_tasks_listed_once_until_build_zig_changes()
    {
        var (dir, provider, listings) = Make(() => Listing017);
        using var _ = dir;
        var tasks = await provider.ProvideTasksAsync(dir.Uri, Ct);
        Assert.Equal(["zig: build", "zig: build uninstall", "zig: build run", "zig: build test", "zig: build check"], tasks.Select(t => t.Label));
        Assert.Equal(dir.Combine("bin/zig"), tasks[0].Command);
        Assert.Equal("zig build: Copy build artifacts to prefix path", tasks[0].Detail);
        Assert.Equal("zig build run: Run the app", tasks[2].Detail);
        Assert.Equal(["build", null, null, "test", "build"], tasks.Select(t => t.Group));

        await provider.ProvideTasksAsync(dir.Uri, Ct);
        lock (listings) Assert.Single(listings);
        var buildZig = dir.Combine("build.zig");
        File.SetLastWriteTimeUtc(buildZig, DateTime.UtcNow.AddMinutes(1));
        await provider.ProvideTasksAsync(dir.Uri, Ct);
        lock (listings) Assert.Equal(2, listings.Count);
        dir.WriteFile("build.zig.zon", ".{}");
        await provider.ProvideTasksAsync(dir.Uri, Ct);
        lock (listings) Assert.Equal(3, listings.Count);
    }

    [Fact]
    public async Task A_slow_listing_gives_the_fallback_tasks_first_and_its_steps_once_it_answered()
    {
        var (dir, provider, listings) = Make(() => Listing017, delay: TimeSpan.FromSeconds(1), wait: TimeSpan.FromMilliseconds(50));
        using var _ = dir;
        var watch = Stopwatch.StartNew();
        var first = await provider.ProvideTasksAsync(dir.Uri, Ct);
        Assert.True(watch.ElapsedMilliseconds < 900, $"the caller waited {watch.ElapsedMilliseconds} ms");
        Assert.Equal(["zig: build", "zig: build run", "zig: build test"], first.Select(t => t.Label));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(await provider.ProvideTasksAsync(dir.Uri, Ct)).Any(t => t.Label == "zig: build check"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the listed steps never came");
            await Task.Delay(50, Ct);
        }
        lock (listings) Assert.Single(listings); // the first listing went on and filled the cache
    }

    [Fact]
    public async Task A_build_zig_that_does_not_list_gives_the_fallback_tasks_until_it_changes()
    {
        var answer = (string?)null;
        var (dir, provider, listings) = Make(() => answer);
        using var _ = dir;
        Assert.Equal(["zig: build", "zig: build run", "zig: build test"], (await provider.ProvideTasksAsync(dir.Uri, Ct)).Select(t => t.Label));
        await provider.ProvideTasksAsync(dir.Uri, Ct);
        lock (listings) Assert.Single(listings);
        answer = Listing017;
        File.SetLastWriteTimeUtc(dir.Combine("build.zig"), DateTime.UtcNow.AddMinutes(1));
        Assert.Contains(await provider.ProvideTasksAsync(dir.Uri, Ct), t => t.Label == "zig: build check");
    }

    [Fact]
    public async Task A_tasks_json_entry_names_a_step_or_runs_zig_build_with_its_own_arguments()
    {
        var (dir, provider, _) = Make(() => Listing017);
        using var __ = dir;
        var run = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"zig","step":"run","label":"go"}"""), Ct);
        Assert.Equal("zig: build run", run!.Label);
        Assert.Equal(["build", "run"], run.Args);
        var build = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"zig"}"""), Ct);
        Assert.Equal("zig: build", build!.Label);
        var release = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"zig","step":"run","args":["-Doptimize=ReleaseFast"],"env":{"A":"1"}}"""), Ct);
        Assert.Equal(["build", "run", "-Doptimize=ReleaseFast"], release!.Args);
        Assert.Equal("1", release.Env!["A"]);
        Assert.Equal(["$zig"], release.ProblemMatchers);
        var unlisted = await provider.ResolveTaskAsync(dir.Uri, Def("""{"type":"zig","step":"docs"}"""), Ct);
        Assert.Equal(["build", "docs"], unlisted!.Args);
    }

    [Fact]
    public void The_task_provider_is_registered_beside_cargos()
    {
        var services = new ServiceCollection();
        services.AddCsCodeDap();
        using var provider = services.BuildServiceProvider();
        var providers = provider.GetServices<Core.Tasks.ITaskProvider>().ToList();
        Assert.Single(providers.OfType<CargoTaskProvider>());
        Assert.Single(providers.OfType<ZigTaskProvider>());
    }

    // ------------------------------------------------------------------ real zig

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_zig_init_project_lists_its_steps_runs_its_tests_and_reports_a_compile_error_through_the_task_runner()
    {
        var noZig = ZigTools.ZigSkipReason();
        Assert.SkipWhen(noZig is not null, noZig ?? "");
        var zig = ZigTools.FindZig()!;
        using var temp = new TempDirectory("zigtasks-real-");
        var root = Path.Combine(TempDirectory.PhysicalPath(temp.Path), "hello");
        Directory.CreateDirectory(root);
        using (var init = Process.Start(new ProcessStartInfo(zig, "init") { WorkingDirectory = root, RedirectStandardError = true, UseShellExecute = false })!)
        {
            await init.WaitForExitAsync(Ct);
            Assert.Equal(0, init.ExitCode);
        }
        var folder = new Uri(root + "/");
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, zig);
        var diagnostics = new DiagnosticService();
        var output = new InMemoryOutputService();
        var provider = new ZigTaskProvider(new DapPlatform { Configuration = settings }) { ListingWait = TimeSpan.FromMinutes(3) };
        // the first listing compiles the build script (up to a minute on a cold cache): listed here, then the runner's 10 s bound is plenty
        var direct = await provider.ProvideTasksAsync(folder, Ct);
        TestMetrics.Report("[zig/tasks] " + string.Join(" | ", direct.Select(t => $"{t.Label} ({t.Detail})")));
        using var lane = new DebugLane("zigtasks");
        using var service = new TaskService(lane, new DapPlatform
        {
            Workspace = new FakeWorkspace(folder), Configuration = settings, Output = output, Diagnostics = diagnostics, TaskProviders = () => [provider],
        });
        var tasks = await service.GetTasksAsync(folder, Ct);
        Assert.Equal(["zig: build", "zig: build uninstall", "zig: build run", "zig: build test"], tasks.Where(t => t.Source == "zig").Select(t => t.Label));

        var test = await service.RunAsync("zig: build test", folder, Ct);
        Assert.True(test.Succeeded, test.Error + "\n" + output.Text(TaskService.ChannelName));

        var main = Path.Combine(root, "src", "main.zig");
        const string Entry = "pub fn main(init: std.process.Init) !void {";
        File.WriteAllText(main, File.ReadAllText(main).Replace(Entry, Entry + "\n    const broken: u32 = \"text\";\n    _ = broken;", StringComparison.Ordinal));
        var build = await service.RunAsync("zig: build", folder, Ct);
        Assert.False(build.Succeeded);
        var line = File.ReadAllLines(main).ToList().FindIndex(l => l.Contains("const broken", StringComparison.Ordinal));
        var error = Assert.Single(diagnostics.GetAll(), d => d.Uri.LocalPath == main && d.Diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(line, error.Diagnostic.Range.Start.Line);
    }
}
