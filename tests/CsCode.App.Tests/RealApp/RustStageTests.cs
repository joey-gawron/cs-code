using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Menus;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Lsp.Extensions.RustAnalyzer;
using CsCode.Panels.Debug.Native;
using CsCode.Panels.Debug.Variables;
using CsCode.TestDoubles;
using Avalonia.Headless.XUnit;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Stage 2 (Rust)'s "Done when", in the REAL application (<see cref="RealAppHost"/>: every real module, real cargo, rust-analyzer, CodeLLDB and
/// lldb-dap; headless window): a Rust debug session under CodeLLDB and under lldb-dap — F5 builds with the detected <c>cargo: build</c> task,
/// the Disassembly window shows the current instruction and a step goes by one instruction while it has the keyboard, a Vec's buffer shows in
/// the Memory window, the Registers window lists the register groups, and a data breakpoint from Locals stops when the value changes — and
/// rust-analyzer's server status, Expand Macro, the Run | Debug lens on <c>main</c> and the cargo tasks in Run Task. Rendered frames are saved to
/// <c>.scratch/screenshots/rust-*.png</c> (git-ignored) as evidence. Skips with the reason when a tool is missing or macOS Developer Mode is off.
/// </summary>
public class RealAppRustStageTests
{
    private static string LaunchJson(string type) => $$"""
        {
          "version": "0.2.0",
          "configurations": [
            {
              "name": "Rust: Debug sample ({{type}})",
              "type": "{{type}}",
              "request": "launch",
              "program": "${workspaceFolder}/target/debug/sample",
              "args": [],
              "cwd": "${workspaceFolder}",
              "preLaunchTask": "cargo: build"
            }
          ]
        }
        """;

    private static void Note(string text)
    {
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path) File.AppendAllText(path, "[app/rust-stage] " + text + Environment.NewLine);
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

    /// <summary>The sample as a cargo package; <paramref name="settings"/> edits the host's settings.json (rust-analyzer is off unless enabled).</summary>
    private static RealAppHost CreateHost(Action<JsonObject> settings, string? launchType = null) => RealAppHost.Create(h =>
    {
        h.Files.WriteFile("Cargo.toml", RustSample.CargoToml);
        h.Files.WriteFile("src/main.rs", RustSample.MainSource);
        h.Files.WriteFile("src/util.rs", RustSample.UtilSource);
        if (launchType is not null) h.Files.WriteFile(".vscode/launch.json", LaunchJson(launchType));
        var path = h.Home.Combine("config", "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        settings(json);
        File.WriteAllText(path, json.ToJsonString());
    });

    /// <summary>The folder as the debug info names it: on a Mac the temp folder (/var/folders/…) is a link into /private.</summary>
    private static string Folder(RealAppHost host) => TempDirectory.PhysicalPath(host.Files.Path);

    private static T? ViewModel<T>(RealAppHost host, string toolId) where T : class => host.Shell.Tools.Get(toolId)?.LiveView?.DataContext as T;

    public static TheoryData<string> Adapters => new() { CodeLldbAdapterFactory.PrimaryType, LldbDapAdapterFactory.PrimaryType };

    [AvaloniaTheory(Timeout = 300_000)]
    [MemberData(nameof(Adapters))]
    public async Task A_rust_session_steps_by_instruction_shows_a_Vec_buffer_and_the_registers_and_stops_on_a_data_breakpoint(string type)
    {
        Assert.SkipWhen(GdbToolchain.FindCargo() is null || GdbToolchain.FindRustc() is null, "the Rust toolchain ('cargo', 'rustc') is not on PATH");
        var adapter = type == CodeLldbAdapterFactory.PrimaryType
            ? CodeLldbToolchain.FindAdapter()
            : await new LldbDapAdapterFactory(new CsCode.Dap.Configuration.DapPlatform()).LocateAsync(TestContext.Current.CancellationToken);
        Assert.SkipWhen(adapter is null, $"the {type} debug adapter is not installed");
        if (OperatingSystem.IsMacOS())
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/DevToolsSecurity", "-status") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var status = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.SkipWhen(!status.Contains("enabled", StringComparison.Ordinal) || status.Contains("disabled", StringComparison.Ordinal),
                "macOS Developer Mode is off: starting a debuggee would wait for an administrator in a dialog ('sudo DevToolsSecurity -enable')");
        }
        await using var host = CreateHost(s => s[type == CodeLldbAdapterFactory.PrimaryType ? CodeLldbAdapterFactory.SettingPath : LldbDapAdapterFactory.SettingPath] = adapter, type);
        await host.StartAsync([Folder(host)]);
        var debug = host.Get<IDebugService>();
        var main = new Uri(Path.Combine(Folder(host), "src", "main.rs"));
        var line = RustSample.MainLine("loop");

        // F9 at the loop, F5: the detected "cargo: build" task builds (no tasks.json), then the adapter stops there
        await host.Get<INavigationService>().OpenAsync(main, TextRange.At(line, 8));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "main.rs" && t.Controller is not null)), "main.rs opened");
        await host.RunAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Type == type, 10_000), "launch.json read");
        var start = host.Get<ICommandService>().ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 180_000),
            $"stopped at the breakpoint (state {debug.State}; tasks output: {host.Get<IOutputService>().GetOrCreate("Tasks").GetText()[^Math.Min(400, host.Get<IOutputService>().GetOrCreate("Tasks").GetText().Length)..]})");
        await start;
        Assert.Equal((main.LocalPath, line), (debug.StopLocation!.Uri?.LocalPath, debug.StopLocation.Line));
        Note($"{type}: F5 built with the detected cargo: build and stopped at main.rs:{line + 1}");

        // ---- Disassembly: the current instruction; a step with the keyboard in the window goes by one instruction
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.DisassemblyViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)?.Rows.Any(r => r.IsCurrent) == true, 30_000),
            "the Disassembly window marks the current instruction");
        var disassembly = ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)!;
        var before = disassembly.Rows.Single(r => r.IsCurrent);
        var keys = host.Get<IContextKeyService>();
        var focused = keys.Get(DisassemblyViewModel.FocusKey) is true;
        Note($"{type}: Disassembly at {before.AddressText} '{before.Text}' ({disassembly.Rows.Count} rows, {disassembly.Rows.Count(r => r.IsSource)} source lines); disassemblyViewFocus after Show Tool Window: {focused}");
        if (!focused) disassembly.SetKeyboardFocus(true); // the headless window may not move the keyboard: what the view does on focus
        Capture(host, $"rust-{type}-disassembly");
        await host.RunAsync(KnownCommands.DebugStepOver);
        Assert.True(await RealAppHost.WaitFor(() => disassembly.Rows.SingleOrDefault(r => r.IsCurrent) is { } now && now.Address != before.Address, 30_000), "the marker moved");
        var after = disassembly.Rows.Single(r => r.IsCurrent);
        Note($"{type}: Step Over by instruction: {before.AddressText} -> {after.AddressText}; still main.rs:{debug.StopLocation!.Line + 1}");
        Assert.Equal(before.Address + 4, after.Address); // arm64: the next instruction, not the next line
        Assert.Equal(line, debug.StopLocation!.Line);
        Capture(host, $"rust-{type}-disassembly-stepped");
        disassembly.SetKeyboardFocus(false);

        // ---- Memory: the Vec's buffer
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<LocalsViewModel>(host, "debug.locals")?.Tree.Rows.Any(r => r.Name == "v") == true, 60_000), "Locals lists v");
        var locals = ViewModel<LocalsViewModel>(host, "debug.locals")!;
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.MemoryViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId) is not null, 10_000), "the Memory window");
        var memory = ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId)!;
        if (type == CodeLldbAdapterFactory.PrimaryType)
        {
            var v = locals.Tree.Rows.First(r => r.Name == "v");
            await locals.Tree.ToggleAsync(v);
            Assert.True(await RealAppHost.WaitFor(() => locals.Tree.Rows.Any(r => r.Name == "[0]"), 20_000), "v's elements");
            var first = locals.Tree.Rows.First(r => r.Name == "[0]");
            Assert.True(locals.Tree.CanViewBinaryData(first));
            locals.Tree.ViewBinaryData(first); // View Binary Data on v[0]
        }
        else
        {
            // lldb-dap has no Rust formatters on a Mac (LldbDapAdapterFactory.IsAppleLldbDap): the buffer pointer, in the address box. Right after
            // the instruction step lldb-dap may still call the process running: the user would press Enter again
            Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped, 20_000), "stopped after the step");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await memory.GoToAsync("v.buf.inner.ptr.pointer.pointer");
                if (memory.StatusText?.StartsWith("Cannot evaluate", StringComparison.Ordinal) != true) break;
                Note($"{type}: attempt {attempt + 1}: {memory.StatusText}");
                await Task.Delay(500);
            }
        }
        memory.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 16);
        Assert.True(await RealAppHost.WaitFor(() => memory.TargetRow.Hex.StartsWith("01 00 00 00 02 00 00 00  03 00 00 00", StringComparison.Ordinal), 30_000),
            $"the buffer of vec![1, 2, 3] (row: {memory.TargetRow.AddressText} {memory.TargetRow.Hex}; {memory.StatusText})");
        Note($"{type}: Memory {memory.Title}: {memory.TargetRow.AddressText}  {memory.TargetRow.Hex}");
        Capture(host, $"rust-{type}-memory");

        // ---- Registers
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.RegistersViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<RegistersViewModel>(host, NativeViewRequests.RegistersViewId)?.Tree.Rows.Any(r => r.Name == "General Purpose Registers") == true, 30_000),
            "the Registers window lists the register groups");
        var registers = ViewModel<RegistersViewModel>(host, NativeViewRequests.RegistersViewId)!;
        await registers.Tree.ToggleAsync(registers.Tree.Rows.First(r => r.Name == "General Purpose Registers"));
        Assert.True(await RealAppHost.WaitFor(() => registers.Tree.Rows.Any(r => r.Name == "pc"), 20_000), "pc");
        Note($"{type}: Registers: {string.Join(", ", registers.Tree.Rows.Take(8).Select(r => $"{r.Name}={r.DisplayValue}"))}");
        Capture(host, $"rust-{type}-registers");

        // ---- a data breakpoint from Locals stops when x changes
        await host.RunAsync(ShellMenus.ShowToolWindow, "debug.locals");
        var x = locals.Tree.Rows.First(r => r.Name == "x");
        var xBefore = x.DisplayValue;
        Assert.True(locals.Tree.CanBreakWhenValueChanges(x));
        await locals.Tree.BreakWhenValueChangesAsync(x);
        var native = (INativeBreakpointManager)debug.Breakpoints;
        var data = Assert.Single(native.DataBreakpoints);
        Assert.True(await RealAppHost.WaitFor(() => debug.Breakpoints.GetStatus(data.Id) is { Verified: true }, 20_000), "the data breakpoint verified");
        debug.Breakpoints.RemoveAll(); // the line breakpoint goes; the data breakpoint must be what stops next (RemoveAll removes it too: add it again)
        Assert.True(await RealAppHost.WaitFor(() => native.DataBreakpoints.Count == 0));
        await locals.Tree.BreakWhenValueChangesAsync(locals.Tree.Rows.First(r => r.Name == "x"));
        data = Assert.Single(native.DataBreakpoints);
        Assert.True(await RealAppHost.WaitFor(() => debug.Breakpoints.GetStatus(data.Id) is { Verified: true }, 20_000), "the data breakpoint verified again");
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<CsCode.Panels.Debug.Breakpoints.BreakpointsViewModel>(host, "debug.breakpoints")?.Rows
            .Any(r => r.Kind == CsCode.Panels.Debug.Breakpoints.BreakpointRowKind.Data) == true, 10_000), "the Breakpoints window lists it");
        var stopBefore = debug.StopLocation;
        await host.RunAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is { } l && !ReferenceEquals(l, stopBefore), 30_000), "stopped by the data breakpoint");
        Assert.True(await RealAppHost.WaitFor(() => locals.Tree.Rows.FirstOrDefault(r => r.Name == "x") is { } now && now.DisplayValue != xBefore, 20_000), "x changed");
        Note($"{type}: data breakpoint stop ({debug.StopLocation!.Reason}) at main.rs:{debug.StopLocation.Line + 1}: x {xBefore} -> {locals.Tree.Rows.First(r => r.Name == "x").DisplayValue}");
        Capture(host, $"rust-{type}-data-breakpoint");

        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Inactive, 20_000), "debugging stopped");
        Assert.Empty(native.DataBreakpoints); // a data breakpoint that cannot persist ends with its session
    }

    [AvaloniaFact(Timeout = 240_000)]
    public async Task Rust_analyzer_status_Expand_Macro_the_main_lens_and_the_cargo_tasks_show_in_the_app()
    {
        Assert.SkipWhen(GdbToolchain.FindCargo() is null || JsDebugToolchain.FindOnPath("rust-analyzer") is null, "cargo or rust-analyzer is not on PATH");
        await using var host = CreateHost(s => s["cscode.languageServers"]!["rust-analyzer"] = new JsonObject { ["enabled"] = true });
        await host.StartAsync([Folder(host)]);
        var main = new Uri(Path.Combine(Folder(host), "src", "main.rs"));
        var vecLine = RustSample.MainSource.Split('\n').ToList().FindIndex(l => l.Contains("let v = vec![1, 2, 3];", StringComparison.Ordinal));
        var mainLine = RustSample.MainSource.Split('\n').ToList().FindIndex(l => l.Contains("fn main() {", StringComparison.Ordinal));
        var column = RustSample.MainSource.Split('\n')[vecLine].IndexOf("vec!", StringComparison.Ordinal) + 1;
        await host.Get<INavigationService>().OpenAsync(main, TextRange.At(vecLine, column));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "main.rs" && t.Controller is not null)), "main.rs opened");

        // the server status in the status bar
        var bar = host.Get<IStatusBarService>();
        Assert.True(await RealAppHost.WaitFor(() => bar.Items.Any(i => i.Id == "rust-analyzer.status" && i.Text == "✓ rust-analyzer"), 180_000),
            "rust-analyzer quiescent in the status bar: " + string.Join(", ", bar.Items.Where(i => i.Id.StartsWith("rust", StringComparison.Ordinal)).Select(i => i.Text)));
        Capture(host, "rust-analyzer-status");

        // the Run | Debug lens on fn main (the editor's lens providers; the editor draws them above the line)
        var doc = host.Get<IDocumentManager>().Get(main)!;
        var lenses = new List<CodeLens>();
        foreach (var provider in host.Get<ILanguageFeatureRegistry>().GetProviders<ICodeLensProvider>(doc))
            lenses.AddRange(await provider.ProvideCodeLensesAsync(doc, TestContext.Current.CancellationToken) ?? []);
        Note($"lenses: {string.Join(" | ", lenses.Select(l => $"{l.Range.Start.Line + 1}:{l.Command?.Title}"))}");
        Assert.Contains(lenses, l => l.Range.Start.Line == mainLine && l.Command?.CommandId == RustAnalyzerExtension.RunSingleCommand);
        Assert.Contains(lenses, l => l.Range.Start.Line == mainLine && l.Command?.CommandId == RustAnalyzerExtension.DebugSingleCommand);
        Capture(host, "rust-main-lens");

        // Expand Macro at the caret: a read-only Rust document
        await host.RunAsync(RustAnalyzerExtension.ExpandMacroCommand);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "vec!.rs [expanded]"), 30_000),
            "the expansion opened: " + string.Join(", ", host.Shell.Well.EditorTabs.Select(t => t.Title)));
        var expansion = host.Get<IDocumentManager>().Documents.OfType<IEditorDocument>().First(d => VirtualDocumentUri.IsVirtual(d.Uri));
        Note($"Expand Macro: {expansion.Uri} read-only={expansion.IsReadOnly} language={expansion.LanguageId}: {expansion.GetText().Split('\n').Skip(3).FirstOrDefault()}");
        Assert.True(expansion.IsReadOnly);
        Assert.Equal("rust", expansion.LanguageId);
        Capture(host, "rust-expand-macro");

        // the cargo tasks in Run Task (detected: there is no tasks.json)
        var running = await host.OpenPaletteAsync("workbench.action.tasks.runTask");
        var input = host.Get<QuickInputViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => input.Items.Any(i => i.Label == "cargo: build"), 20_000), "cargo tasks listed: " + string.Join(", ", input.Items.Select(i => i.Label)));
        Note("Run Task: " + string.Join(", ", input.Items.Select(i => i.Label)));
        Assert.Contains(input.Items, i => i.Label == "cargo: clippy");
        Assert.Contains(input.Items, i => i.Label == "cargo: test");
        Capture(host, "rust-run-task");
        await host.ClosePaletteAsync(running);
    }

    /// <summary>Records the kinds a code-action request asks for; offers nothing.</summary>
    private sealed class AskedCodeActions : ICodeActionProvider
    {
        private readonly List<string> _asked = [];
        public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }

        public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct)
        {
            lock (_asked) _asked.Add($"{string.Join(",", context.Only ?? [])} ({context.TriggerKind})");
            return ValueTask.FromResult<IReadOnlyList<CodeAction>?>(null);
        }
    }

    /// <summary>Unsorted imports, two of them unused, and a line rustfmt reflows.</summary>
    private const string ImportsSource = "use std::fmt::Display;\nuse std::collections::HashMap;\nuse std::collections::BTreeMap;\n\npub fn count(m:&HashMap<i32,i32>)->usize{m.len()}\n";
    private const string ImportsFormatted = "use std::collections::BTreeMap;\nuse std::collections::HashMap;\nuse std::fmt::Display;\n\npub fn count(m: &HashMap<i32, i32>) -> usize {\n    m.len()\n}\n";

    [AvaloniaFact(Timeout = 240_000)]
    public async Task Organize_imports_on_save_is_rustfmts_import_order_and_applies_none_of_rust_analyzers_quick_fixes()
    {
        Assert.SkipWhen(GdbToolchain.FindCargo() is null || JsDebugToolchain.FindOnPath("rust-analyzer") is null, "cargo or rust-analyzer is not on PATH");
        await using var host = CreateHost(s =>
        {
            s["cscode.languageServers"]!["rust-analyzer"] = new JsonObject { ["enabled"] = true };
            s["[rust]"] = new JsonObject
            {
                ["editor.formatOnSave"] = true,
                ["editor.codeActionsOnSave"] = new JsonObject { ["source.organizeImports"] = "explicit" },
                ["editor.codeActionsOnSaveTimeout"] = 10_000, // rust-analyzer answers within it: what the save skips, it skipped by kind
            };
        });
        host.Files.WriteFile("src/lib.rs", ImportsSource);
        await host.StartAsync([Folder(host)]);
        var lib = new Uri(Path.Combine(Folder(host), "src", "lib.rs"));
        await host.Get<INavigationService>().OpenAsync(lib, TextRange.At(0, 0));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "lib.rs" && t.Controller is not null)), "lib.rs opened");
        var bar = host.Get<IStatusBarService>();
        Assert.True(await RealAppHost.WaitFor(() => bar.Items.Any(i => i.Id == "rust-analyzer.status" && i.Text == "✓ rust-analyzer"), 180_000), "rust-analyzer quiescent");

        // asked for source.organizeImports, rust-analyzer (which advertises no such kind) answers with its unused-import quick fixes
        var doc = host.Get<IDocumentManager>().Get(lib)!;
        var asked = new CodeActionContext([], ["source.organizeImports"], CodeActionTriggerKind.Automatic);
        var offered = new List<CodeAction>();
        for (var deadline = DateTime.UtcNow.AddSeconds(60); offered.Count == 0 && DateTime.UtcNow < deadline;)
        {
            foreach (var provider in host.Get<ILanguageFeatureRegistry>().GetProviders<ICodeActionProvider>(doc))
                offered.AddRange(await provider.ProvideCodeActionsAsync(doc, TextRange.FromLines(0, 0, 3, 0), asked, TestContext.Current.CancellationToken) ?? []);
            Dispatcher.UIThread.RunJobs();
            if (offered.Count == 0) await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Note("asked for source.organizeImports, rust-analyzer offered: " + string.Join(", ", offered.Select(a => $"{a.Title} ({a.Kind})")));
        Assert.DoesNotContain(offered, a => a.Kind is "source.organizeImports" || a.Kind?.StartsWith("source.organizeImports.", StringComparison.Ordinal) == true); // else: update docs/RUST.md
        Assert.Contains(offered, a => a.Kind == "quickfix");

        // the shell's Save: code actions on save keep only actions of the asked kind, so none of those; format on save (rustfmt) sorts the imports
        var probe = new AskedCodeActions(); // asked in turn with rust-analyzer: shows the save did ask for organize imports
        using (host.Get<ILanguageFeatureRegistry>().Register<ICodeActionProvider>(DocumentSelector.ForLanguage("rust"), probe))
            await host.RunAsync(KnownCommands.Save);
        var path = lib.LocalPath;
        Assert.True(await RealAppHost.WaitFor(() => File.ReadAllText(path) != ImportsSource, 30_000), "the save wrote the file");
        Assert.Contains("source.organizeImports (Automatic)", probe.Asked);
        Assert.Equal(ImportsFormatted, File.ReadAllText(path));
        Note("saved with organize imports + format on save:\n" + File.ReadAllText(path));
        Capture(host, "rust-organize-imports-on-save");
    }
}
