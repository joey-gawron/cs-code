using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Build.CMake;
using CsCode.Core.Build;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Lsp.Extensions.Clangd;
using CsCode.Panels.Debug.Native;
using CsCode.Panels.Debug.Variables;
using CsCode.Panels.Testing;
using CsCode.Testing.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Stage 3 (C) in the REAL application (<see cref="RealAppHost"/>: every real module, a headless window) on the CMake C sample
/// (<see cref="CMakeCSample"/>), the checks of docs/C-CPP.md §7: the project configures from its preset on open and clangd finds its headers
/// through the exported compile commands (an <c>#include</c>'s link, the <c>#if 0</c> block faded, Switch Between Source/Header, clangd's file
/// status); Build in the status bar puts a compiler error in the Error List and builds once it is fixed; Debug Target stops at a breakpoint
/// with the Disassembly and Memory windows showing the program; and the CTest tests are listed, run and debugged in the Test Explorer.
/// Frames are saved to <c>.scratch/screenshots/c-*.png</c>. Skips with the reason when cmake, ninja, a C compiler, clangd or CodeLLDB is missing.
/// </summary>
public class CStageRealAppTests
{
    /// <summary>One visible configure preset: configure on open needs no choice.</summary>
    private const string Presets = """
        {
          "version": 6,
          "configurePresets": [
            { "name": "debug", "displayName": "Debug", "generator": "Ninja", "binaryDir": "${sourceDir}/build/debug", "cacheVariables": { "CMAKE_BUILD_TYPE": "Debug" } }
          ]
        }
        """;

    private static void Note(string text)
    {
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path) File.AppendAllText(path, "[app/c-stage] " + text + Environment.NewLine);
    }

    private static string Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(DevHost.ScreenshotDirectory(), name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        Note($"screenshot {path}");
        return path;
    }

    private static T? ViewModel<T>(RealAppHost host, string toolId) where T : class => host.Shell.Tools.Get(toolId)?.LiveView?.DataContext as T;

    /// <summary>The sample with one preset, configure on open, clangd on (with a private HOME) and CodeLLDB as the C debugger.</summary>
    private static RealAppHost CreateHost(string? codelldb) => RealAppHost.Create(h =>
    {
        CMakeCSample.Write(h.Files);
        h.Files.WriteFile("CMakePresets.json", Presets);
        var path = h.Home.Combine("config", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        settings["cmake.configureOnOpen"] = true;
        settings["cscode.languageServers"]!["clangd"] = new JsonObject
        {
            ["enabled"] = true,
            ["env"] = new JsonObject { ["HOME"] = h.Home.Path, ["XDG_CONFIG_HOME"] = h.Home.Combine("config"), ["XDG_CACHE_HOME"] = h.Home.Combine("cache") },
        };
        if (codelldb is not null) settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
        settings[TestingSettings.OpenTesting] = "neverOpen";
        File.WriteAllText(path, settings.ToJsonString());
    });

    private static string? Skip(bool needsClangd = false, bool needsDebugger = false) =>
        CMakeCSample.SkipReason()
        ?? (needsClangd && ProcessRunner.FindOnPath("clangd") is null ? "clangd is not installed" : null)
        ?? (needsDebugger && CodeLldbToolchain.FindAdapter() is null ? "CodeLLDB's adapter was not found (tools/fetch-codelldb.sh, or CSCODE_CODELLDB)" : null);

    private static async Task<CMakeProject> ConfiguredOnOpenAsync(RealAppHost host)
    {
        var cmake = host.Get<ICMakeService>();
        string Output() => host.Get<IOutputService>().GetOrCreate(CMakeService.OutputChannelName).GetText();
        Assert.True(await RealAppHost.WaitFor(() => cmake.Projects is [{ State: CMakeProjectState.Configured }], 120_000), "configured on open; CMake output: " + Output());
        return cmake.Projects[0];
    }

    private static async Task OpenAsync(RealAppHost host, string path, int line = 0)
    {
        await host.Get<INavigationService>().OpenAsync(new Uri(path), TextRange.At(line, 0));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == Path.GetFileName(path) && t.Controller is not null)), Path.GetFileName(path) + " opened");
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task The_project_configures_from_its_preset_and_clangd_and_the_status_bar_build_follow_it()
    {
        var skip = Skip(needsClangd: true);
        Assert.SkipWhen(skip is not null, skip ?? "");
        await using var host = CreateHost(null);
        await host.StartAsync([host.Files.Path]);

        // ---- configure on open, from the only preset, exporting compile commands for clangd
        var project = await ConfiguredOnOpenAsync(host);
        var build = host.Files.Combine("build", "debug");
        Assert.Equal(("debug", build), (project.ConfigurePreset, project.BuildDirectory));
        Assert.True(File.Exists(Path.Combine(build, "compile_commands.json")), "compile_commands.json exported");
        var clangdDef = host.Get<ILanguageServerManager>().Definitions.Single(d => d.Id == "clangd");
        Assert.Contains("--compile-commands-dir=" + build, clangdDef.Args);

        // ---- clangd on main.c: the include resolves through the compile commands (include/ comes from target_include_directories)
        var main = host.Files.Combine("src", "main.c");
        await OpenAsync(host, main, 8);
        var bar = host.Get<IStatusBarService>();
        Assert.True(await RealAppHost.WaitFor(() => bar.Items.Any(i => i.Id == "clangd.fileStatus" && i.Text == "clangd: idle"), 120_000),
            "clangd's file status: " + string.Join(", ", bar.Items.Where(i => i.Id.StartsWith("clangd", StringComparison.Ordinal)).Select(i => i.Text)));
        var doc = host.Get<IDocumentManager>().Get(new Uri(main))!;
        var registry = host.Get<ILanguageFeatureRegistry>();
        IReadOnlyList<DocumentLink>? links = null;
        Assert.True(await RealAppHost.WaitFor(() =>
        {
            links = registry.GetProviders<IDocumentLinkProvider>(doc).FirstOrDefault()?.ProvideDocumentLinksAsync(doc, CancellationToken.None).AsTask().Result;
            return links?.Any(l => l.Target?.LocalPath.EndsWith(Path.Combine("include", "mathlib.h"), StringComparison.Ordinal) == true) == true;
        }, 60_000), "the #include \"mathlib.h\" link names include/mathlib.h: " + string.Join(", ", links?.Select(l => l.Target?.LocalPath) ?? []));
        Assert.DoesNotContain(host.Get<IDiagnosticService>().Get(new Uri(main)), d => d.Message.Contains("not found", StringComparison.Ordinal));

        // ---- the #if 0 block is faded
        Assert.True(await RealAppHost.WaitFor(() => registry.GetProviders<IInactiveRegionsProvider>(doc).SelectMany(p => p.GetInactiveRegions(doc.Uri)).Any(r => r.Start.Line == 4), 30_000),
            "the #if 0 block reported inactive");
        await DevScene.SettleAsync(20);
        Capture(host, "c-clangd-main");

        // ---- Switch Between Source/Header: mathlib.c <-> include/mathlib.h (clangd matches them through its index)
        var library = host.Files.Combine("src", "mathlib.c");
        await OpenAsync(host, library);
        Assert.True(await RealAppHost.WaitFor(() =>
        {
            host.Get<ICommandService>().ExecuteAsync(ClangdExtension.SwitchHeaderSourceCommand).AsTask().Wait(TimeSpan.FromSeconds(10));
            return host.Get<INavigationService>().ActiveDocument?.Uri.LocalPath.EndsWith(Path.Combine("include", "mathlib.h"), StringComparison.Ordinal) == true;
        }, 60_000), "Switch Between Source/Header opened include/mathlib.h; active: " + host.Get<INavigationService>().ActiveDocument?.Uri.LocalPath);
        Note("Switch Between Source/Header: src/mathlib.c -> include/mathlib.h");

        // ---- Build from the status bar: a compiler error lands in the Error List; fixed, it builds
        File.WriteAllText(library, CMakeCSample.Library.Replace("return a + b;", "return a + missing_value;", StringComparison.Ordinal));
        var buildItem = bar.Items.Single(i => i.Id == "cmake.build");
        Assert.Equal(CMakeHost.Build, buildItem.CommandId);
        var failed = await host.Get<ICommandService>().ExecuteAsync(buildItem.CommandId!);
        Assert.False(Assert.IsType<CMakeBuildResult>(failed).Succeeded);
        var diagnostics = host.Get<IDiagnosticService>();
        Diagnostic? error = null;
        Assert.True(await RealAppHost.WaitFor(() => (error = diagnostics.Get(new Uri(library)).Concat(diagnostics.Get(new Uri(TempDirectory.PhysicalPath(library))))
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error && d.Source == "cmake")) is not null, 30_000), "the build error in the Error List");
        Assert.Contains("missing_value", error!.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Range.Start.Line);
        Assert.True(await host.Get<IViewActivator>().ShowViewAsync("errorlist"));
        await DevScene.SettleAsync(15);
        Capture(host, "c-build-error");

        File.WriteAllText(library, CMakeCSample.Library);
        var built = await host.Get<ICommandService>().ExecuteAsync(CMakeHost.Build);
        Assert.True(Assert.IsType<CMakeBuildResult>(built).Succeeded, host.Get<IOutputService>().GetOrCreate(CMakeService.OutputChannelName).GetText());
        Assert.True(await RealAppHost.WaitFor(() => !diagnostics.Get(new Uri(library)).Concat(diagnostics.Get(new Uri(TempDirectory.PhysicalPath(library))))
            .Any(d => d.Source == "cmake"), 10_000), "the next build cleared the error");
        Assert.True(File.Exists(Path.Combine(build, "app")));
        Note($"status bar: {string.Join(" | ", bar.Items.Where(i => i.Id.StartsWith("cmake.", StringComparison.Ordinal) || i.Id.StartsWith("clangd", StringComparison.Ordinal)).Select(i => i.Text))}");
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Debug_target_stops_at_a_breakpoint_with_the_disassembly_and_memory_windows()
    {
        var skip = Skip(needsDebugger: true);
        Assert.SkipWhen(skip is not null, skip ?? "");
        await using var host = CreateHost(CodeLldbToolchain.FindAdapter());
        await host.StartAsync([host.Files.Path]);
        await ConfiguredOnOpenAsync(host);
        var debug = host.Get<IDebugService>();

        // the sample has two executables: choose app as the launch target, as from the status bar's [Select Launch Target]
        var picking = await host.OpenPaletteAsync(CMakeHost.SelectLaunchTarget);
        var quick = host.Get<QuickInputViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Any(i => i.Label == "app")), "the launch targets: " + string.Join(", ", quick.Items.Select(i => i.Label)));
        quick.SelectedIndex = quick.Items.ToList().FindIndex(i => i.Label == "app");
        quick.Accept();
        await picking;

        // F9 on the printf line of main.c, then the status bar's Debug: it builds app and starts CodeLLDB
        var main = host.Files.Combine("src", "main.c");
        await OpenAsync(host, main, 9);
        await host.RunAsync(KnownCommands.DebugToggleBreakpoint);
        var start = host.Get<ICommandService>().ExecuteAsync(CMakeHost.DebugTarget).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000), $"stopped (state {debug.State})");
        await start;
        Assert.Equal((TempDirectory.PhysicalPath(main), 9), (TempDirectory.PhysicalPath(debug.StopLocation!.Uri!.LocalPath), debug.StopLocation.Line));

        // ---- Disassembly: the current instruction, on main.c's line
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.DisassemblyViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)?.Rows.Any(r => r.IsCurrent) == true, 30_000),
            "the Disassembly window marks the current instruction");
        var disassembly = ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)!;
        var current = disassembly.Rows.Single(r => r.IsCurrent);
        Note($"Disassembly at {current.AddressText} '{current.Text}' ({disassembly.Rows.Count} rows, {disassembly.Rows.Count(r => r.IsSource)} source lines)");
        Assert.Contains(disassembly.Rows, r => r.IsSource);
        await DevScene.SettleAsync(15);
        Capture(host, "c-debug-disassembly");

        // ---- Memory: View Binary Data on total (5) from the Locals window
        Assert.True(await host.Get<IViewActivator>().ShowViewAsync("debug.locals"));
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<LocalsViewModel>(host, "debug.locals")?.Tree.Rows.Any(r => r.Name == "total") == true, 60_000), "Locals lists total");
        var locals = ViewModel<LocalsViewModel>(host, "debug.locals")!;
        var total = locals.Tree.Rows.First(r => r.Name == "total");
        Assert.Equal("5", total.DisplayValue);
        Assert.True(locals.Tree.CanViewBinaryData(total), "View Binary Data is offered on total");
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.MemoryViewId); // the window open first, as in stage 2's real-app test
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId) is not null, 10_000), "the Memory window");
        locals.Tree.ViewBinaryData(total);
        var memory = ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId)!;
        memory.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 16);
        Assert.True(await RealAppHost.WaitFor(() => memory.TargetRow.Hex.StartsWith("05 00 00 00", StringComparison.Ordinal), 30_000),
            $"total's four bytes (row: {memory.TargetRow.AddressText} {memory.TargetRow.Hex}; {memory.StatusText})");
        Note($"Memory {memory.Title}: {memory.TargetRow.AddressText}  {memory.TargetRow.Hex}");
        await DevScene.SettleAsync(15);
        Capture(host, "c-debug-memory");

        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.Sessions.Count == 0, 30_000), "the session ended");
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Ctest_tests_are_listed_run_and_debugged_in_the_test_explorer()
    {
        var skip = Skip(needsDebugger: true);
        Assert.SkipWhen(skip is not null, skip ?? "");
        await using var host = CreateHost(CodeLldbToolchain.FindAdapter());
        await host.StartAsync([host.Files.Path]);
        await ConfiguredOnOpenAsync(host);
        var testing = host.Get<TestingHost>();
        var tree = testing.Controller.Tree;
        string Output() => host.Get<IOutputService>().GetOrCreate(TestingHost.OutputChannelName).GetText();

        await host.Get<ICommandService>().ExecuteAsync(TestingHost.FocusView);
        Assert.True(await RealAppHost.WaitFor(() => new[] { "adds", "subtracts", "fails", "in_dir" }.All(id => tree.FindTest(id) is not null) && !testing.Controller.IsDiscovering, 120_000),
            "discovered: " + string.Join(", ", tree.AllTests().Select(t => t.TestId)) + "; output: " + Output());

        await testing.Controller.RunAllAsync();
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && tree.FindTest("fails")!.Outcome == TestOutcome.Failed, 120_000), "the run finished; output: " + Output());
        Assert.Equal([TestOutcome.Passed, TestOutcome.Passed, TestOutcome.Passed], new[] { "adds", "subtracts", "in_dir" }.Select(id => tree.FindTest(id)!.Outcome));
        await DevScene.SettleAsync(15);
        Capture(host, "c-ctest-explorer");

        // Debug "adds": a breakpoint on its line in tests/math_tests.c stops the test's command there under CodeLLDB
        var debug = host.Get<IDebugService>();
        var source = host.Files.Combine("tests", "math_tests.c");
        var line = CMakeCSample.Tests.Split('\n').ToList().FindIndex(l => l.Contains("\"adds\"", StringComparison.Ordinal));
        debug.Breakpoints.Add(new Uri(source), line);
        var run = testing.Controller.RunAsync([tree.FindTest("adds")!], debug: true);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000), $"stopped (state {debug.State}); output: " + Output());
        Assert.Equal((TempDirectory.PhysicalPath(source), line), (TempDirectory.PhysicalPath(debug.StopLocation!.Uri!.LocalPath), debug.StopLocation.Line));
        await DevScene.SettleAsync(10);
        Capture(host, "c-ctest-debug");
        debug.Breakpoints.RemoveAll();
        await debug.ActiveSession!.ContinueAsync();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(await RealAppHost.WaitFor(() => !testing.Controller.IsRunning && debug.Sessions.Count == 0, 30_000), "the debug run ended");
        Assert.Equal(TestOutcome.Passed, tree.FindTest("adds")!.Outcome);
    }
}
