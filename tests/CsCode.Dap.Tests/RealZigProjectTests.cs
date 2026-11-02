using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Dap.Adapters;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Service;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// The "Done when" path of docs/ZIG.md end to end through the real services the app composes (task runner with the Problems service, launch
/// configurations, debug service with the built-in CodeLLDB) on a real <c>zig init</c> project: Create launch.json writes the Zig templates and
/// their tasks; Run Task <c>zig: build</c> puts a compile error in Problems; the <c>zig build</c> template builds through its preLaunchTask and
/// stops at a breakpoint in <c>zig-out/bin/hello</c> with readable slices and optionals. Skips without zig, CodeLLDB or (macOS) Developer Mode.
/// </summary>
[Trait("Category", RealAdapterSupport.Category)]
[Collection(SerialCollection.Name)]
public class RealZigProjectTests
{
    [Fact]
    public async Task A_zig_init_project_builds_through_its_task_with_errors_in_problems_and_debugs_under_codelldb()
    {
        var noZig = ZigTools.ZigSkipReason();
        Assert.SkipWhen(noZig is not null, noZig ?? "");
        var adapter = CodeLldbToolchain.FindAdapter();
        Assert.SkipWhen(adapter is null, $"CodeLLDB {CodeLldbToolchain.PinnedVersion} is not installed: run tools/fetch-codelldb.sh or set {CodeLldbToolchain.EnvironmentVariable}");
        var blocked = await RealLldbDapTests.MacDebuggingBlockedReasonAsync();
        Assert.SkipWhen(blocked is not null, blocked ?? "");
        var zig = ZigTools.FindZig()!;

        using var temp = new TempDirectory("zigproject-");
        var root = Path.Combine(TempDirectory.PhysicalPath(temp.Path), "hello"); // LLDB's debug info has the physical path (/private/var on macOS)
        Directory.CreateDirectory(root);
        using (var init = Process.Start(new ProcessStartInfo(zig, "init") { WorkingDirectory = root, RedirectStandardError = true, UseShellExecute = false })!)
        {
            await init.WaitForExitAsync(Ct);
            Assert.Equal(0, init.ExitCode);
        }
        var main = Path.Combine(root, "src", "main.zig");
        var original = File.ReadAllText(main);
        const string Entry = "pub fn main(init: std.process.Init) !void {";
        Assert.Contains(Entry, original, StringComparison.Ordinal);

        var folder = new Uri(root + "/");
        var settings = new TestConfigurationService();
        settings.Set(ZigToolchainLocator.SettingPath, zig);
        settings.Set(CodeLldbAdapterFactory.SettingPath, adapter!);
        var diagnostics = new DiagnosticService();
        var window = new RecordingWindowService();
        var output = new InMemoryOutputService();
        using var paths = new TestAppPaths();
        var platform = new DapPlatform
        {
            Workspace = new FakeWorkspace(folder), Configuration = settings, Window = window, Output = output, Diagnostics = diagnostics, Paths = paths,
            State = new FakeStateStorage(), Commands = new TestCommandService(), ContextKeys = new TestContextKeyService(),
        };
        using var lane = new DebugLane("zigproject");
        var breakpoints = new BreakpointManager(platform.State, platform.Workspace);
        var tasks = new TaskService(lane, platform);
        var configurations = new DebugConfigurationService(lane, platform, () => tasks);
        var codelldb = new CodeLldbAdapterFactory(platform);
        await using var service = new DebugService(lane, platform, breakpoints, configurations, tasks, CodeLldbAdapterFactory.Builtins(codelldb));

        // Create launch.json (what F5 offers in a build.zig folder): the Zig templates and the tasks their preLaunchTask names
        var launch = await configurations.CreateLaunchJsonAsync(CodeLldbAdapterFactory.PrimaryType, folder, Ct);
        TestMetrics.Report("[zig/project] launch.json: " + File.ReadAllText(launch.LocalPath).ReplaceLineEndings(" "));
        var taskList = await tasks.GetTasksAsync(folder, Ct);
        Assert.Contains(taskList, t => t.Label == ZigDebugging.BuildTask && t.IsDefaultBuild);

        // Run Task "zig: build" on a broken main.zig: it fails and the error is in Problems, on its line
        File.WriteAllText(main, original.Replace(Entry, Entry + "\n    const broken: u32 = \"text\";\n    _ = broken;", StringComparison.Ordinal));
        var failed = await tasks.RunAsync(ZigDebugging.BuildTask, folder, Ct);
        Assert.False(failed.Succeeded);
        var problems = diagnostics.GetAll().Where(d => d.Uri.LocalPath == main).ToList();
        TestMetrics.Report("[zig/project] problems: " + string.Join(" | ", problems.Select(p => $"{p.Diagnostic.Range.Start.Line + 1}:{p.Diagnostic.Range.Start.Character + 1} {p.Diagnostic.Severity} {p.Diagnostic.Message}")));
        var brokenLine = File.ReadAllLines(main).ToList().FindIndex(l => l.Contains("const broken", StringComparison.Ordinal));
        var error = Assert.Single(problems, p => p.Diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(brokenLine, error.Diagnostic.Range.Start.Line);
        Assert.Contains("expected type 'u32'", error.Diagnostic.Message, StringComparison.Ordinal);

        // fixed, with a slice and an optional to look at: the zig build template builds through its preLaunchTask and stops at the breakpoint
        File.WriteAllText(main, original.Replace(Entry, Entry + """

                const numbers = [_]u32{ 5, 6, 7 };
                const window: []const u32 = numbers[init.minimal.args.vector.len - 1 ..];
                const maybe: ?u32 = if (window.len > 2) window[2] else null;
                std.debug.print("{d} {any}\n", .{ window.len, maybe }); // @inspect
            """, StringComparison.Ordinal));
        var inspect = File.ReadAllLines(main).ToList().FindIndex(l => l.Contains("// @inspect", StringComparison.Ordinal));
        breakpoints.Add(new Uri(main), inspect);
        var config = (await configurations.GetLaunchConfigurationsAsync(folder, Ct)).Single(c => c.Name == "Zig: Debug hello (CodeLLDB, zig build)");
        var stops = new List<DebugStoppedEventArgs>();
        service.SessionStarted.Subscribe(s => s.Stopped.Subscribe(e => { lock (stops) stops.Add(e); }));
        var session = await service.StartAsync(config, Ct);
        Assert.True(session is not null, "the session did not start: " + string.Join(" | ", window.Messages.Select(m => m.Message)) + "\n" + output.Text(TaskService.ChannelName));
        await SessionHarness.Eventually(() => { lock (stops) return stops.Count > 0; }, 120_000, "the breakpoint in main.zig");
        await SessionHarness.Eventually(() => service.StopLocation is { Session.State: DebugSessionState.Stopped }, 30_000, "stop location");
        var loc = service.StopLocation!;
        Assert.Equal((main, inspect), (loc.Uri?.LocalPath, loc.Line));
        Assert.True(File.Exists(Path.Combine(root, "zig-out", "bin", "hello")), "zig build installed the program");
        var locals = new Dictionary<string, DebugVariable>(StringComparer.Ordinal);
        foreach (var scope in await loc.Session.GetScopesAsync(loc.FrameId, Ct))
            if (scope.Name is "Local" or "Locals" || scope.PresentationHint is "locals")
                foreach (var v in await loc.Session.GetVariablesAsync(scope.VariablesReference, ct: Ct)) locals.TryAdd(v.Name, v);
        TestMetrics.Report("[zig/project] locals: " + string.Join(" | ", locals.Values.Select(v => $"{v.Name}={v.Value} ({v.Type})")));
        Assert.Equal("len=3", locals["window"].Value);
        Assert.Equal("7", locals["maybe"].Value);
        await service.StopAllAsync(Ct);
        await SessionHarness.Eventually(() => service.Sessions.Count == 0, 30_000, "the session ended");
        await configurations.DisposeAsync();
        await breakpoints.DisposeAsync();
    }
}
