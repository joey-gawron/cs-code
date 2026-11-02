using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap;
using CsCode.Dap.Adapters;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Modules;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Threads;
using CsCode.Panels.Debug.Variables;
using CsCode.TestDoubles;
using CsCode.Ui;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// End to end with the REAL DAP module and the REAL Samsung netcoredbg: a tiny C# program is built, a session is started through
/// <c>IDebugService</c>, and the panels (created from the real DI registration, exactly as the shell would) must show what the real
/// adapter reports: real frames, real locals, changed values after a step, a watch that evaluates, verified breakpoints and the
/// program's stdout. Skips with an explicit reason when netcoredbg (tools/fetch-netcoredbg.sh) or <c>dotnet</c> is unavailable.
/// </summary>
[Trait("Category", "RealAdapter")]
public class RealDebuggerEndToEndTests
{
    private const string Program = """
        using System;

        namespace Sample;

        public static class Program
        {
            public static int Add(int a, int b)
            {
                var sum = a + b; // @add
                return sum;      // @ret
            }

            public static int Outer(int n)
            {
                var inner = Add(n, 1); // @outer
                return inner * 2;
            }

            public static int Main()
            {
                int x = 41;
                string s = "hello";
                for (int i = 0; i < 5; i++)
                {
                    x += i;
                    Console.WriteLine($"loop {i}");
                }
                var r = Outer(x); // @call
                Console.WriteLine($"done {s} {r}");
                return 7;
            }
        }
        """;

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <UseAppHost>false</UseAppHost>
            <Optimize>false</Optimize>
            <DebugType>portable</DebugType>
            <ImplicitUsings>disable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <InvariantGlobalization>true</InvariantGlobalization>
            <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
          </PropertyGroup>
        </Project>
        """;

    private static int LineOf(string marker) => Program.Split('\n').ToList().FindIndex(l => l.Contains("// @" + marker, StringComparison.Ordinal)); // 0-based

    private static string? FindNetCoreDbg()
    {
        var exe = NetCoreDbgAdapterFactory.ExecutableName;
        if (Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } fromEnv && File.Exists(fromEnv)) return fromEnv;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "netcoredbg", exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindDotnet() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "dotnet")).FirstOrDefault(File.Exists);

    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static (string Dir, string Dll, string Source)? s_built;

    private static async Task<(string Dir, string Dll, string Source)> BuildSampleAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (s_built is { } built) return built;
            var dir = new TempDirectory("panels-e2e-");
            dir.WriteFile("Sample.csproj", Project);
            var source = dir.WriteFile("Program.cs", Program);
            var psi = new ProcessStartInfo(FindDotnet()!) { WorkingDirectory = dir.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "build", "Sample.csproj", "-c", "Debug", "-o", "out", "--nologo", "-v", "q" }) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException($"building the sample failed:\n{await stdout}\n{await stderr}");
            s_built = (dir.Path, Path.Combine(dir.Path, "out", "Sample.dll"), source);
            return s_built.Value;
        }
        finally { BuildGate.Release(); }
    }

    private static void Note(string text) => TestContext.Current.SendDiagnosticMessage(text);

    [AvaloniaFact]
    public async Task The_panels_show_what_the_real_adapter_reports_through_a_whole_debug_session()
    {
        var exe = FindNetCoreDbg();
        Assert.SkipUnless(exe is not null, "netcoredbg is not available: run tools/fetch-netcoredbg.sh (expects .scratch/tools/netcoredbg/) or set CSCODE_NETCOREDBG");
        Assert.SkipUnless(FindDotnet() is not null, "`dotnet` is not on PATH, so the sample program cannot be built");
        var sample = await BuildSampleAsync();

        // ---- the application, as the shell composes it: real DAP module + the panels' DI registration over test platform services.
        var documents = new DocumentManager();
        var navigation = new RecordingNavigationService(documents);
        var window = new TestWindowService();
        var settings = new TestConfigurationService();
        settings.Set(NetCoreDbgAdapterFactory.SettingPath, exe);
        using var paths = new TestAppPaths();
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(settings);
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<ICommandService, TestCommandService>();
        services.AddSingleton<IContextKeyService, TestContextKeyService>();
        services.AddSingleton<IOutputService, InMemoryOutputService>();
        services.AddSingleton<INavigationService>(navigation);
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<IStateStorage, InMemoryStateStorage>();
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IUiDispatcher>(AvaloniaUiDispatcher.Instance);
        CsCode.Dap.ServiceCollectionExtensions.AddCsCodeDap(services);
        CsCode.Panels.Debug.ServiceCollectionExtensions.AddCsCodeDebugPanels(services);
        await using var provider = services.BuildServiceProvider();
        var debug = provider.GetRequiredService<IDebugService>();
        var tools = provider.GetServices<IToolWindow>().ToDictionary(t => t.Id);
        Assert.Equal(["debug.autos", "debug.breakpoints", "debug.callstack", "debug.console", "debug.disassembly", "debug.exception", "debug.locals", "debug.memory", "debug.modules", "debug.registers", "debug.threads", "debug.watch"], tools.Keys.Order());

        var sourceUri = new Uri(sample.Source);
        debug.Breakpoints.Toggle(sourceUri, LineOf("add"));
        debug.AddWatch("x + 1");
        debug.AddWatch("a + b");

        // ---- the windows exist before the session starts (they auto-show on inDebugMode; here we open them first)
        var breakpoints = (BreakpointsViewModel)tools["debug.breakpoints"].CreateView(provider).DataContext!;
        var callStack = (CallStackViewModel)tools["debug.callstack"].CreateView(provider).DataContext!;
        var locals = ((FrameVariablesViewModel)tools["debug.locals"].CreateView(provider).DataContext!).Tree;
        var autos = ((FrameVariablesViewModel)tools["debug.autos"].CreateView(provider).DataContext!).Tree;
        var watch = ((WatchViewModel)tools["debug.watch"].CreateView(provider).DataContext!).Tree;
        var threads = (ThreadsViewModel)tools["debug.threads"].CreateView(provider).DataContext!;
        var console = (DebugConsoleViewModel)tools["debug.console"].CreateView(provider).DataContext!;
        var modules = (ModulesViewModel)tools["debug.modules"].CreateView(provider).DataContext!;

        var stops = 0;
        IDebugSession? session = null;
        try
        {
            var config = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "coreclr", ["request"] = "launch", ["name"] = "e2e", ["program"] = sample.Dll, ["cwd"] = sample.Dir,
                ["args"] = Array.Empty<string>(), ["stopAtEntry"] = false, ["justMyCode"] = true, ["console"] = "internalConsole",
            }));
            session = await debug.StartAsync(config) ?? throw new InvalidOperationException("the session did not start: " + string.Join(" | ", window.StatusMessages));
            session.Stopped.Subscribe(_ => Interlocked.Increment(ref stops));
            var adapterPid = (session as CsCode.Dap.Session.DebugSession)?.AdapterProcessId;
            Assert.NotNull(adapterPid);

            // ============ stopped in Add(): the top frame, its locals, a verified breakpoint, the program's output
            await PanelRig.Eventually(() => stops >= 1 && callStack.Rows.Any(r => r.IsCurrent), 40_000, "stopped at the breakpoint");
            Note("call stack: " + string.Join(" | ", callStack.Rows.Select(r => $"{r.Kind}:{r.Text}@{r.Location}")));
            Assert.Contains("Add", callStack.Rows.First(r => r.Kind == CallStackRowKind.Frame).Text);
            Assert.Equal($"Program.cs : {LineOf("add") + 1}", callStack.Rows.First(r => r.Kind == CallStackRowKind.Frame).Location);
            Assert.Contains(callStack.Rows, r => r.Kind == CallStackRowKind.Frame && r.Text.Contains("Main"));

            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "a"), 20_000, "locals of Add");
            Note("locals: " + string.Join(" | ", locals.Rows.Select(r => $"{r.Name}={r.FullValue}")));
            Assert.Equal("51", locals.Rows.Single(r => r.Name == "a").FullValue);
            Assert.Equal("1", locals.Rows.Single(r => r.Name == "b").FullValue);
            await PanelRig.Eventually(() => autos.Rows.Count > 0, 20_000, "autos");

            await PanelRig.Eventually(() => breakpoints.Rows.Any(r => r.Kind == BreakpointRowKind.Source && r.Verification is BreakpointVerification.Verified or BreakpointVerification.Moved), 20_000, "breakpoint verified by the adapter");
            Assert.Contains($"Program.cs : {LineOf("add") + 1}", breakpoints.Rows.Select(r => r.Label));

            await PanelRig.Eventually(() => threads.Rows.Count >= 1 && threads.Rows.All(r => r.State == "Stopped"), 20_000, "threads");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text == "loop 4"), 20_000, "program stdout in the console");
            Assert.Equal(["loop 0", "loop 1", "loop 2", "loop 3", "loop 4"], console.Lines.Where(l => l.Kind == ConsoleLineKind.Output).Select(l => l.Text).Take(5));

            // modules: netcoredbg reports them with `module` events only (it does not implement the `modules` request)
            await PanelRig.Eventually(() => modules.Rows.Count > 0 || modules.StatusText != "Not debugging", 20_000, "modules window loaded");
            Note($"modules: {modules.Rows.Count} (status: {modules.StatusText}), e.g. " + string.Join(" | ", modules.Rows.Take(3).Select(r => $"{r.Name} {r.Version} user={r.UserCode} {r.SymbolStatus}")));
            Assert.Null(modules.StatusText);
            Assert.Contains(modules.Rows, r => r.Path is { } p && p.EndsWith("Sample.dll", StringComparison.Ordinal));

            // watch: `a + b` evaluates in Add (52), `x + 1` cannot (x lives in Main) and shows the adapter's error
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "a + b" && r.DisplayValue == "52"), 20_000, "watch a + b");
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "x + 1" && r.IsError), 20_000, "watch x + 1 fails in Add");
            Note("watch x + 1 in Add: " + watch.Rows.First(r => r.Name == "x + 1").DisplayValue);

            // ============ pick the Main frame in the Call Stack: Locals and Watch follow, the editor is told to show the source
            var mainRow = callStack.Rows.First(r => r.Kind == CallStackRowKind.Frame && r.Text.Contains("Main"));
            await callStack.ActivateAsync(mainRow, takeFocus: false);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "x"), 20_000, "locals of Main");
            Note("locals in Main: " + string.Join(" | ", locals.Rows.Select(r => $"{r.Name}={r.FullValue}")));
            Assert.Equal("51", locals.Rows.Single(r => r.Name == "x").FullValue);
            Assert.Contains("hello", locals.Rows.Single(r => r.Name == "s").FullValue);
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "x + 1" && r.DisplayValue == "52"), 20_000, "watch x + 1 in Main");
            Assert.Contains(navigation.Opened, o => o.Uri == sourceUri && o.Selection is { } sel && sel.Start.Line == mainRow.Frame!.Line);
            Assert.True(mainRow.IsCurrent || callStack.Rows.First(r => r.Kind == CallStackRowKind.Frame && r.Text.Contains("Main")).IsCurrent);

            // ============ the debug console evaluates in the focused frame (Main): x = 51
            console.Input = "x * 2";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text == "102"), 20_000, "REPL result");

            // ============ step over in Add: the frame resets to the top, sum changes 0 -> 52 and is highlighted
            var before = stops;
            await session.StepOverAsync();
            await PanelRig.Eventually(() => stops > before && locals.Rows.Any(r => r.Name == "sum"), 30_000, "stopped after step");
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "sum" && r.FullValue == "52"), 20_000, "sum assigned");
            Note("locals after step: " + string.Join(" | ", locals.Rows.Select(r => $"{r.Name}={r.FullValue}{(r.IsChanged ? " (changed)" : "")}")));
            Assert.True(locals.Rows.Single(r => r.Name == "sum").IsChanged, "the value that changed since the previous stop is highlighted; locals: " + string.Join(" | ", locals.Rows.Select(r => $"{r.Name}={r.FullValue}{(r.IsChanged ? " (changed)" : "")}")));
            Assert.False(locals.Rows.Single(r => r.Name == "a").IsChanged);
            Assert.Contains($"Program.cs : {LineOf("ret") + 1}", callStack.Rows.Where(r => r.Kind == CallStackRowKind.Frame).Select(r => r.Location));

            // ============ run to the end: panels go quiet, the console has the last output, nothing is left running
            var debuggeePid = (session as CsCode.Dap.Session.DebugSession)?.DebuggeeProcessId;
            await session.ContinueAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 40_000, "session ended");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text.StartsWith("done hello", StringComparison.Ordinal)), 20_000, "final output");
            await PanelRig.Eventually(() => locals.Rows.Count == 0 && locals.StatusText == "Not debugging", 10_000, "locals cleared");
            await PanelRig.Eventually(() => callStack.Rows.Count == 0, 10_000, "call stack cleared");

            await PanelRig.Eventually(() => !IsAlive(adapterPid!.Value), 20_000, "adapter process gone");
            if (debuggeePid is { } pid) await PanelRig.Eventually(() => !IsAlive((int)pid), 20_000, "debuggee process gone");
        }
        finally
        {
            await debug.StopAllAsync();
            foreach (var t in new[] { breakpoints as IDisposable, callStack, threads, console, modules }) t.Dispose();
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
