using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.App.Menus;
using CsCode.Panels.Debug.Exceptions;
using CsCode.Panels.Debug.Native;
using CsCode.Panels.Debug.Variables;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// C++ in the REAL application (<see cref="RealAppHost"/>): a C++20 program built with clang++ and a launch.json of type <c>lldb</c> (CodeLLDB)
/// as a user would write it. F9 and F5 stop at a breakpoint where the Locals window shows libc++ containers readably (a string's text, a
/// vector's and a map's size and elements, an optional's value, a smart pointer's target, a <c>Shape*</c> as the <c>Circle</c> it is); with
/// CodeLLDB's "C++: on throw" filter (on by default) the program then stops at the throw as an exception, shown in the Exception window. Then
/// Objective-C++ (<c>.mm</c>) and CUDA (<c>.cu</c>) files get clangd's diagnostics. Rendered frames are saved to
/// <c>.scratch/screenshots/cpp-*.png</c>. A second program shows the native windows (stage 2) on C++: a <c>std::vector</c>'s buffer in the
/// Memory window (View Binary Data on its first element), and an inlined function in the Disassembly window with its source line above the
/// current instruction. Skips without clang++ or CodeLLDB (and, for the clangd part, clangd).
/// </summary>
public class CppDebugRealAppTests
{
    private const string Program = """
        #include <cstdio>
        #include <map>
        #include <memory>
        #include <optional>
        #include <stdexcept>
        #include <string>
        #include <vector>

        struct Shape { virtual ~Shape() = default; virtual double area() const = 0; };
        struct Circle : Shape { double radius = 2; double area() const override { return 3 * radius * radius; } };

        static int thrower(int n) {
          if (n > 0) throw std::runtime_error("boom");
          return n;
        }

        int main() {
          std::string text = "hello world";
          std::vector<int> numbers{1, 2, 3};
          std::map<std::string, int> ages{{"ann", 31}, {"bob", 42}};
          std::optional<int> some = 5;
          auto shared = std::make_shared<std::string>("shared");
          std::unique_ptr<Shape> shape = std::make_unique<Circle>();
          Shape* raw = shape.get();
          int total = (int)numbers.size(); // @values
          try { total += thrower(1); } catch (const std::exception& e) { std::printf("caught %s\n", e.what()); }
          std::printf("done %d %d %s %d\n", total, ages["bob"], text.c_str(), (int)raw->area());
          return some.value_or(0) - 5;
        }
        """;

    private const string LaunchJson = """
        {
          "version": "0.2.0",
          "configurations": [
            { "name": "C++: matrix (CodeLLDB)", "type": "lldb", "request": "launch", "program": "${workspaceFolder}/matrix", "cwd": "${workspaceFolder}", "sourceLanguages": ["cpp"] }
          ]
        }
        """;

    private const string NativeProgram = """
        #include <cstdio>
        #include <vector>

        __attribute__((always_inline)) inline int square(int x) {
          return x * x; // @inlined
        }

        int main() {
          std::vector<int> numbers{1, 2, 3};
          int count = (int)numbers.size(); // @vector
          int squared = square(count);
          std::printf("squared %d\n", squared);
          return 0;
        }
        """;

    private static string Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(DevHost.ScreenshotDirectory(), name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    private static int Line(string marker, string program = Program) => program.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains("// " + marker, StringComparison.Ordinal)).i;

    private static T? ViewModel<T>(RealAppHost host, string toolId) where T : class => host.Shell.Tools.Get(toolId)?.LiveView?.DataContext as T;

    [AvaloniaFact(Timeout = 300_000)]
    public async Task A_vectors_buffer_shows_in_the_memory_window_and_an_inlined_function_in_the_disassembly_window()
    {
        var cxx = new[] { "/usr/bin/clang++", "/usr/bin/c++" }.FirstOrDefault(File.Exists);
        var codelldb = CodeLldbToolchain.FindAdapter();
        Assert.SkipWhen(cxx is null, "no clang++ / c++ in /usr/bin");
        Assert.SkipWhen(codelldb is null, "CodeLLDB's adapter was not found (tools/fetch-codelldb.sh, or CSCODE_CODELLDB)");
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("matrix.cpp", NativeProgram);
            h.Files.WriteFile(".vscode/launch.json", LaunchJson);
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        using (var build = Process.Start(new ProcessStartInfo(cxx!, ["-g", "-O0", "-std=c++20", "-o", host.Files.Combine("matrix"), host.Files.Combine("matrix.cpp")]) { UseShellExecute = false })!)
        {
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, build.ExitCode);
        }
        await host.StartAsync([host.Files.Path]);
        var debug = host.Get<IDebugService>();
        var commands = host.Get<ICommandService>();
        var source = new Uri(host.Files.Combine("matrix.cpp"));
        debug.Breakpoints.Add(source, Line("@vector", NativeProgram));
        debug.Breakpoints.Add(source, Line("@inlined", NativeProgram));
        await host.Get<INavigationService>().OpenAsync(source, TextRange.At(Line("@vector", NativeProgram), 2));
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "C++: matrix (CodeLLDB)", 10_000), "launch.json read");
        var start = commands.ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000), $"stopped (state {debug.State})");
        await start;
        Assert.Equal(Line("@vector", NativeProgram), debug.StopLocation!.Line);

        // ---- Memory: View Binary Data on numbers[0] shows the vector's buffer
        Assert.True(await host.Get<IViewActivator>().ShowViewAsync("debug.locals"));
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<LocalsViewModel>(host, "debug.locals")?.Tree.Rows.Any(r => r.Name == "numbers") == true, 30_000), "Locals lists numbers");
        var locals = ViewModel<LocalsViewModel>(host, "debug.locals")!;
        await locals.Tree.ExpandAsync(locals.Tree.Rows.First(r => r.Name == "numbers"));
        Assert.True(await RealAppHost.WaitFor(() => locals.Tree.Rows.Any(r => r.Name == "[0]"), 20_000), "numbers' elements");
        var first = locals.Tree.Rows.First(r => r.Name == "[0]");
        Assert.True(locals.Tree.CanViewBinaryData(first));
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.MemoryViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId) is not null, 10_000), "the Memory window");
        var memory = ViewModel<MemoryViewModel>(host, NativeViewRequests.MemoryViewId)!;
        locals.Tree.ViewBinaryData(first);
        memory.SetVisibleRows(MemoryViewModel.RowCount / 2, MemoryViewModel.RowCount / 2 + 16);
        Assert.True(await RealAppHost.WaitFor(() => memory.TargetRow.Hex.StartsWith("01 00 00 00 02 00 00 00  03 00 00 00", StringComparison.Ordinal), 30_000),
            $"the buffer of std::vector<int>{{1, 2, 3}} (row: {memory.TargetRow.AddressText} {memory.TargetRow.Hex}; {memory.StatusText})");
        await DevScene.SettleAsync(10);
        Capture(host, "cpp-memory-vector");

        // ---- Disassembly: at the breakpoint in the inlined function, the current instruction is in main's code under square's source line
        await commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation?.Line == Line("@inlined", NativeProgram), 30_000),
            $"stopped in the inlined function (line {debug.StopLocation?.Line})");
        await host.RunAsync(ShellMenus.ShowToolWindow, NativeViewRequests.DisassemblyViewId);
        Assert.True(await RealAppHost.WaitFor(() => ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)?.Rows.Any(r => r.IsCurrent) == true, 30_000),
            "the Disassembly window marks the current instruction");
        var disassembly = ViewModel<DisassemblyViewModel>(host, NativeViewRequests.DisassemblyViewId)!;
        var rows = disassembly.Rows.ToList();
        var current = rows.FindIndex(r => r.IsCurrent);
        var sourceAbove = rows.Take(current).LastOrDefault(r => r.IsSource);
        var symbolAbove = rows.Take(current).LastOrDefault(r => r.IsSymbol);
        Assert.True(sourceAbove?.Text.Contains("return x * x", StringComparison.Ordinal) == true, $"square's line above the current instruction (got '{sourceAbove?.Text}')");
        Assert.Contains("main", symbolAbove?.Text ?? rows[current].Symbol ?? "", StringComparison.Ordinal);
        await DevScene.SettleAsync(10);
        Capture(host, "cpp-disassembly-inlined");
        debug.Breakpoints.RemoveAll();
        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.Sessions.Count == 0, 30_000), "the session ended");
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task A_cpp_program_shows_libcxx_values_and_stops_at_a_throw_and_mm_and_cu_files_get_clangd()
    {
        var cxx = new[] { "/usr/bin/clang++", "/usr/bin/c++" }.FirstOrDefault(File.Exists);
        var codelldb = CodeLldbToolchain.FindAdapter();
        Assert.SkipWhen(cxx is null, "no clang++ / c++ in /usr/bin");
        Assert.SkipWhen(codelldb is null, "CodeLLDB's adapter was not found (tools/fetch-codelldb.sh, or CSCODE_CODELLDB)");
        await using var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("matrix.cpp", Program);
            h.Files.WriteFile(".vscode/launch.json", LaunchJson);
            h.Files.WriteFile("compile_flags.txt", "-std=c++20\n");
            var settingsPath = h.Home.Combine("config", "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings[CodeLldbAdapterFactory.SettingPath] = codelldb;
            settings["cscode.languageServers"]!["clangd"] = new JsonObject { ["enabled"] = true }; // the host turns every server off
            File.WriteAllText(settingsPath, settings.ToJsonString());
        });
        using (var build = Process.Start(new ProcessStartInfo(cxx!, ["-g", "-O0", "-std=c++20", "-o", host.Files.Combine("matrix"), host.Files.Combine("matrix.cpp")]) { UseShellExecute = false })!)
        {
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, build.ExitCode);
        }
        await host.StartAsync([host.Files.Path]);
        var debug = host.Get<IDebugService>();
        var commands = host.Get<ICommandService>();
        var source = new Uri(host.Files.Combine("matrix.cpp"));
        var line = Line("@values");

        await host.Get<INavigationService>().OpenAsync(source, TextRange.At(line, 2));
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Any(t => t.Title == "matrix.cpp" && t.Controller is not null)), "matrix.cpp opened");
        await host.RunAsync(KnownCommands.DebugToggleBreakpoint);
        Assert.True(await RealAppHost.WaitFor(() => debug.Configurations.Selected?.Name == "C++: matrix (CodeLLDB)", 10_000), "launch.json read");

        var start = commands.ExecuteAsync(KnownCommands.DebugStart).AsTask();
        Assert.True(await RealAppHost.WaitFor(() => debug.State == DebugState.Stopped && debug.StopLocation is not null, 120_000), $"stopped (state {debug.State})");
        await start;
        Assert.Equal((source, line), (debug.StopLocation!.Uri, debug.StopLocation.Line));

        // the Locals window: libc++ values as a user reads them (LLDB's formatters through CodeLLDB)
        Assert.True(await host.Get<IViewActivator>().ShowViewAsync("debug.locals"));
        LocalsViewModel? locals = null;
        Assert.True(await RealAppHost.WaitFor(() => (locals = host.Shell.Tools.Get("debug.locals")?.LiveView?.DataContext as LocalsViewModel) is { } vm
            && vm.Tree.Rows.Any(r => r.Name == "raw"), 30_000), "the Locals window shows the frame's variables");
        string Value(string name) => locals!.Tree.Rows.First(r => r.Name == name).DisplayValue;
        Assert.Contains("hello world", Value("text"), StringComparison.Ordinal);
        Assert.Contains("size=3", Value("numbers"), StringComparison.Ordinal);
        Assert.Contains("size=2", Value("ages"), StringComparison.Ordinal);
        Assert.Contains("\"shared\"", Value("shared"), StringComparison.Ordinal);
        Assert.Contains("Circle", locals!.Tree.Rows.First(r => r.Name == "raw").Type ?? "", StringComparison.Ordinal); // the dynamic type
        foreach (var name in new[] { "numbers", "ages", "some" }) await locals.Tree.ExpandAsync(locals.Tree.Rows.First(r => r.Name == name));
        Assert.True(await RealAppHost.WaitFor(() => locals.Tree.Rows.Any(r => r.Name == "[1]" && r.DisplayValue.Contains("bob", StringComparison.Ordinal)), 20_000), "the map's elements");
        await DevScene.SettleAsync(10);
        Capture(host, "cpp-debug-locals");

        // continue: CodeLLDB's "C++: on throw" (on by default) stops at the throw, reported as an exception
        debug.Breakpoints.RemoveAll();
        await Task.Delay(300);
        await commands.ExecuteAsync(KnownCommands.DebugContinue);
        Assert.True(await RealAppHost.WaitFor(() => debug.StopLocation is { Reason: "exception" }, 30_000), $"stopped at the throw (stop: {debug.StopLocation?.Reason})");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("debug.exception")?.LiveView?.DataContext is ExceptionHelperViewModel { HasException: true }, 20_000),
            "the Exception window shows the throw");
        await DevScene.SettleAsync(10);
        Capture(host, "cpp-debug-throw");
        await host.RunAsync(KnownCommands.DebugStop);
        Assert.True(await RealAppHost.WaitFor(() => debug.Sessions.Count == 0, 30_000), "the session ended");

        // Objective-C++ and CUDA files get clangd
        if (CsCode.Testing.Engine.ProcessRunner.FindOnPath("clangd") is null) return;
        host.Files.WriteFile("view.mm", "#include <vector>\n__attribute__((objc_root_class))\n@interface Greeter\n- (int)count;\n@end\nint main() { std::vector<int> v; undefined_call(); }\n");
        host.Files.WriteFile("kernel.cu", "__global__ void add(int n, float *x, float *y) {\n  int i = blockIdx.x * blockDim.x + threadIdx.x;\n  if (i < n) y[i] = x[i] + y[i];\n}\n");
        var diagnostics = host.Get<IDiagnosticService>();
        var mm = new Uri(host.Files.Combine("view.mm"));
        var cu = new Uri(host.Files.Combine("kernel.cu"));
        await host.Get<INavigationService>().OpenAsync(mm, TextRange.At(0, 0));
        Assert.True(await RealAppHost.WaitFor(() => diagnostics.Get(mm).Any(d => d.Message.Contains("undefined_call", StringComparison.Ordinal)), 90_000), "clangd's diagnostic in view.mm");
        await host.Get<INavigationService>().OpenAsync(cu, TextRange.At(0, 0));
        Assert.True(await RealAppHost.WaitFor(() => diagnostics.Get(cu).Any(d => d.Message.Contains("CUDA", StringComparison.Ordinal)), 90_000), "clangd's CUDA diagnostic in kernel.cu");
        Assert.True(await host.Get<IViewActivator>().ShowViewAsync("errorlist"));
        await DevScene.SettleAsync(15);
        Capture(host, "cpp-clangd-cu");
    }
}
