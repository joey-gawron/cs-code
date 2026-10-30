using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Dap.Adapters;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Exceptions;
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
/// The Debug panels over the REAL DAP module and the REAL GDB (native DAP) debugging a Rust program built by the real cargo: what the Call Stack,
/// Locals, Autos, Watch, Breakpoints, Threads, Exception and Debug Console windows show for Rust (generic and closure frames, the standard
/// library folded as external code, Rust values, paged vectors, panics, the GDB-backed REPL). Evidence for docs/DEBUGGING-RUST-TS.md. Skips with a
/// reason when gdb (14+), cargo or rustc is missing.
/// </summary>
[Trait("Category", "RealAdapter")]
public class RealGdbPanelsTests
{
    internal static void Note(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path)
            lock (typeof(RealGdbPanelsTests)) File.AppendAllText(path, "[panels/gdb] " + text + "\n");
    }

    /// <summary>The application as the shell composes it: the real DAP module and the panels' DI registration over test platform services.</summary>
    internal sealed class App : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required RustSample Sample { get; init; }
        public required RecordingNavigationService Navigation { get; init; }
        public required TestWindowService Window { get; init; }
        public required TestAppPaths Paths { get; init; }
        public IDebugService Debug => Provider.GetRequiredService<IDebugService>();
        public Dictionary<string, IToolWindow> Tools => Provider.GetServices<IToolWindow>().ToDictionary(t => t.Id);
        public T View<T>(string id) where T : class => (T)Tools[id].CreateView(Provider).DataContext!;
        public Uri MainUri => new(Sample.MainRs);
        public Uri UtilUri => new(Sample.UtilRs);
        /// <summary>The debug type of <see cref="Config"/>: <c>gdb</c>, or <c>lldb</c> for the real CodeLLDB.</summary>
        public string Type { get; init; } = GdbAdapterFactory.PrimaryType;

        /// <param name="type"><c>gdb</c>, or <c>lldb</c>: CodeLLDB found by <see cref="CodeLldbToolchain"/> (never downloaded).</param>
        public static async Task<App> CreateAsync(string type = GdbAdapterFactory.PrimaryType)
        {
            var codeLldb = type == CodeLldbAdapterFactory.PrimaryType;
            var skip = codeLldb ? CodeLldbToolchain.SkipReason() : GdbToolchain.SkipReason();
            Assert.SkipWhen(skip is not null, skip ?? "");
            var sample = await RustSample.CreateAsync(codeLldb ? "rustlldb-panels-" : "rustgdb-panels-");
            var documents = new DocumentManager();
            var navigation = new RecordingNavigationService(documents);
            var window = new TestWindowService();
            var settings = new TestConfigurationService();
            settings.Set(GdbAdapterFactory.ArgsSettingPath, new[] { "-nx" }); // never the user's gdbinit
            if (codeLldb) settings.Set(CodeLldbAdapterFactory.SettingPath, CodeLldbToolchain.FindAdapter()!);
            var paths = new TestAppPaths();
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
            return new App { Provider = services.BuildServiceProvider(), Sample = sample, Navigation = navigation, Window = window, Paths = paths, Type = type };
        }

        public DebugConfiguration Config(params string[] args) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = Type, ["request"] = "launch", ["name"] = "sample", ["program"] = Sample.Binary, ["cwd"] = Sample.Dir.Path, ["args"] = args,
        }), Sample.Dir.Uri);

        public void PanicStops(bool enabled) => Debug.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting(GdbAdapterQuirks.RustPanicFilter, "Rust panics", enabled)]);

        public async ValueTask DisposeAsync()
        {
            try { await Debug.StopAllAsync(); } catch (ObjectDisposedException) { }
            await Provider.DisposeAsync();
            Paths.Dispose();
            Sample.Dispose();
        }
    }

    private static string Rows(CallStackViewModel cs) => string.Join(" | ", cs.Rows.Select(r => $"{r.Kind}:{r.Text}@{r.Location}{(r.IsExternal ? " (ext)" : "")}{(r.IsCurrent ? " <=" : "")}"));
    private static string Rows(VariablesTreeViewModel tree) => string.Join(" | ", tree.Rows.Select(r => $"{new string('.', Math.Max(0, r.Depth))}{r.Name}={r.DisplayValue}{(r.HasChildren ? "+" : "")}{(r.IsChanged ? "*" : "")}{(r.IsError ? "!" : "")}"));
    private static string Here(CallStackViewModel cs) => cs.Rows.FirstOrDefault(r => r.IsCurrent)?.Location ?? "";

    [AvaloniaFact(Timeout = 240_000)]
    public async Task The_panels_show_a_rust_session_frames_values_paging_console_repl_and_the_return_value()
    {
        await using var app = await App.CreateAsync();
        var debug = app.Debug;
        app.PanicStops(false);
        debug.Breakpoints.Add(app.MainUri, RustSample.MainLine("comment"));
        debug.Breakpoints.Add(app.MainUri, RustSample.MainLine("call"));
        debug.AddWatch("p.x + p.y");
        debug.AddWatch("nope");

        var breakpoints = app.View<BreakpointsViewModel>("debug.breakpoints");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        var locals = app.View<FrameVariablesViewModel>("debug.locals").Tree;
        var watch = app.View<WatchViewModel>("debug.watch").Tree;
        var autos = app.View<FrameVariablesViewModel>("debug.autos").Tree;
        var threads = app.View<ThreadsViewModel>("debug.threads");
        var console = app.View<DebugConsoleViewModel>("debug.console");
        var stops = 0;
        debug.SessionStarted.Subscribe(s => s.Stopped.Subscribe(_ => Interlocked.Increment(ref stops)));
        try
        {
            _ = await debug.StartAsync(app.Config()) ?? throw new InvalidOperationException("no session: " + string.Join(" | ", app.Window.StatusMessages));

            // ============ stop 1: the breakpoint on a comment line moved to the next statement
            await PanelRig.Eventually(() => stops >= 1 && callStack.Rows.Any(r => r.IsCurrent), 60_000, "stopped at the moved breakpoint");
            await PanelRig.Eventually(() => breakpoints.Rows.Any(r => r.Kind == BreakpointRowKind.Source && r.Verification == BreakpointVerification.Moved), 20_000, "moved verdict");
            Note($"breakpoints window: {string.Join(" | ", breakpoints.Rows.Select(r => $"{r.Kind} {r.Label} {r.Verification} note='{r.Note}'"))}");
            var movedRow = breakpoints.Rows.First(r => r.Kind == BreakpointRowKind.Source && r.Verification == BreakpointVerification.Moved);
            Assert.Equal(RustSample.MainLine("comment") + 2, movedRow.MovedToLine);
            Assert.Equal($"main.rs : {RustSample.MainLine("comment") + 2}", Here(callStack));
            Assert.Contains(breakpoints.Rows, r => r.Kind == BreakpointRowKind.Exception && r.Label == "Rust panics");
            await debug.StopLocation!.Session.ContinueAsync();

            // ============ stop 2: at util::add(x, 1): Rust values in Locals
            await PanelRig.Eventually(() => stops >= 2 && Here(callStack) == $"main.rs : {RustSample.MainLine("call") + 1}", 60_000, "stopped at @call");
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "big") && locals.Rows.Any(r => r.Name == "wrapper"), 20_000, "locals of main");
            Note("locals in main: " + Rows(locals));
            string Value(string name) => locals.Rows.First(r => r.Name == name && r.Depth <= 0).DisplayValue;
            Assert.Equal("51", Value("x"));
            Assert.Equal("\"hello\"", Value("s"));
            Assert.Equal("\"world\"", Value("text"));
            Assert.Equal("Some(5)", Value("some"));
            Assert.Equal("Err(\"invalid digit found in string\")", Value("err"));
            Assert.Equal("Point {x: 3, y: 4}", Value("p"));
            Assert.Equal("HashMap(size=2)", Value("map"));
            Assert.Matches("^0x[0-9a-f]+ Point \\{x: 5, y: 6\\}$", Value("boxed"));

            // a vector of 250: every element through the pager
            var big = locals.Rows.First(r => r.Name == "big");
            await locals.ToggleAsync(big);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == big), 20_000, "first page of big");
            for (var page = 0; page < 5 && locals.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More); page++)
                await locals.LoadMoreAsync(locals.Rows.First(r => r.Parent == big && r.Kind == VariableRowKind.More));
            var elements = locals.Rows.Where(r => r.Parent == big && r.Kind == VariableRowKind.Variable).ToList();
            Note($"big after paging: {elements.Count} elements, last {elements[^1].Name}={elements[^1].DisplayValue}");
            Assert.Equal(Enumerable.Range(0, 250).Select(i => $"[{i}]"), elements.Select(r => r.Name));
            Assert.Equal("498", elements[^1].DisplayValue);
            await locals.ToggleAsync(big);

            // an enum: the variant, then its field
            var some = locals.Rows.First(r => r.Name == "some");
            await locals.ToggleAsync(some);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == some), 20_000, "some expanded");
            Note("some expanded: " + string.Join(" | ", locals.Rows.Where(r => r.Parent == some).Select(r => $"{r.Name}={r.DisplayValue}")));

            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "p.x + p.y" && r.DisplayValue == "7") && watch.Rows.Any(r => r.Name == "nope" && r.IsError), 20_000, "watch");
            Note("watch: " + Rows(watch));
            await PanelRig.Eventually(() => autos.Rows.Count > 0, 20_000, "autos");
            Note("autos at util::add(x, 1): " + Rows(autos));

            // ============ Debug Console: program output, the REPL (expressions and GDB commands), completions
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text == "loop 4"), 20_000, "program stdout");
            Assert.Equal(["loop 0", "loop 1", "loop 2", "loop 3", "loop 4"], console.Lines.Where(l => l.Kind == ConsoleLineKind.Output && l.Text.StartsWith("loop", StringComparison.Ordinal)).Select(l => l.Text));
            Note("console before the REPL: " + string.Join(" | ", console.Lines.Select(l => $"{l.Kind}:{l.Text}")));
            Assert.DoesNotContain(console.Lines, l => l.Kind == ConsoleLineKind.Output && l.Text.Contains("pending", StringComparison.Ordinal)); // GDB's own messages
            console.Input = "x * 2";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text == "102"), 20_000, "REPL expression");
            console.Input = "-exec info args";
            await console.SubmitAsync();
            console.Input = "p s";
            await console.SubmitAsync();
            try { await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text.EndsWith("= \"hello\"", StringComparison.Ordinal)), 20_000, "REPL GDB command"); }
            catch (TimeoutException) { Note("REPL (timed out): " + string.Join(" | ", console.Lines.Select(l => $"{l.Kind}:{l.Text}"))); throw; }
            console.Input = "p.";
            await console.RequestCompletionsAsync(2);
            await PanelRig.Eventually(() => console.Completions.Count > 0, 20_000, "completions");
            Note("completions for p.: " + string.Join(", ", console.Completions.Take(5).Select(c => $"{c.Label}=>{c.Text}")));
            Assert.Contains(console.Completions, c => c.Label == "p.x");
            console.AcceptCompletion(console.Completions.First(c => c.Label == "p.x"));
            Assert.Equal("p.x", console.Input);
            console.Input = "nope + 1";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.ResultError), 20_000, "REPL error");
            Note("REPL: " + string.Join(" | ", console.Lines.Where(l => l.Kind is ConsoleLineKind.Input or ConsoleLineKind.Result or ConsoleLineKind.ResultError).Select(l => $"{l.Kind}:{l.Text}")));

            // ============ set a variable from Locals (inline edit → setVariable)
            VariableNodeViewModel LocalX() => locals.Rows.First(r => r.Name == "x" && r.Depth <= 0);
            Assert.True(locals.CanEditValue(LocalX()));
            locals.BeginEditValue(LocalX());
            LocalX().EditText = "100";
            await locals.CommitEditAsync(LocalX());
            Assert.Equal("100", LocalX().DisplayValue);

            // ============ step into add(), then out: the value it returned
            var before = stops;
            await debug.StopLocation!.Session.StepIntoAsync();
            await PanelRig.Eventually(() => stops > before && Here(callStack) == $"util.rs : {RustSample.UtilLine("add") + 1}", 30_000, "in add()");
            Note("call stack in add: " + Rows(callStack));
            Assert.Equal(["sample::util::add", "sample::main"], callStack.Rows.Where(r => r.Kind == CallStackRowKind.Frame).Select(r => r.Text).Take(2));
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "a"), 20_000, "arguments of add");
            Note("locals in add: " + Rows(locals));
            Assert.Equal("100", locals.Rows.First(r => r.Name == "a").DisplayValue);
            before = stops;
            await debug.StopLocation!.Session.StepOutAsync();
            await PanelRig.Eventually(() => stops > before && locals.Rows.Any(r => r.Name == "(return)"), 30_000, "the return value after step out");
            Note("locals after step out: " + Rows(locals));
            Assert.Equal("101", locals.Rows.First(r => r.Name == "(return)").DisplayValue);
            await PanelRig.Eventually(() => autos.Rows.FirstOrDefault()?.Name == "(return)", 20_000, "autos show the return value first");
            Note("autos after step out: " + Rows(autos));
            await PanelRig.Eventually(() => threads.Rows.Count >= 1, 20_000, "threads");
            Note("threads: " + string.Join(" | ", threads.Rows.Select(r => $"{r.Id} {r.Name} {r.State} {r.Location}")));

            // ============ run to the end
            debug.Breakpoints.RemoveAll();
            await Task.Delay(300);
            await debug.StopLocation!.Session.ContinueAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 60_000, "session ended");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text.StartsWith("done hello world", StringComparison.Ordinal)), 20_000, "final output");
            // GDB sends the program's stderr as stdout (one pipe): eprintln! is not red
            Assert.Equal(ConsoleLineKind.Output, console.Lines.Single(l => l.Text == "err 101").Kind);
            await PanelRig.Eventually(() => locals.Rows.Count == 0 && callStack.Rows.Count == 0, 10_000, "panels cleared");
        }
        finally
        {
            foreach (var t in new IDisposable[] { breakpoints, callStack, threads, console }) t.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_rust_panic_shows_in_the_exception_window_and_the_call_stack_folds_the_standard_library()
    {
        await using var app = await App.CreateAsync(); // "Rust panics" on by default
        var debug = app.Debug;
        var exception = app.View<ExceptionHelperViewModel>("debug.exception");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        try
        {
            await debug.StartAsync(app.Config("panic"));
            await PanelRig.Eventually(() => exception.HasException, 60_000, "exception shown");
            Note($"exception window: title '{exception.Title}', type '{exception.Details!.TypeName}', message '{exception.Details.Message}', stack: {exception.Details.StackTrace}");
            Assert.Equal(("panic", "boom at the bottom"), (exception.Details.TypeName, exception.Details.Message));
            Assert.Contains("panicked at src/util.rs:", exception.Details.StackTrace, StringComparison.Ordinal);
            await PanelRig.Eventually(() => callStack.Rows.FirstOrDefault(r => r.IsCurrent)?.Text == "sample::util::explode", 20_000, "the arrow on the user's frame");
            Note("call stack at the panic: " + Rows(callStack));
            Assert.Equal($"util.rs : {RustSample.UtilLine("panic") + 1}", Here(callStack));
            // std's panic machinery above the user's frames and its runtime below main() fold into [External Code] rows
            Assert.Equal(CallStackRowKind.ExternalCode, callStack.Rows[0].Kind);
            Assert.DoesNotContain(callStack.Rows, r => r.Kind == CallStackRowKind.Frame && r.Text.StartsWith("std::panicking", StringComparison.Ordinal));
            Assert.Equal(3, callStack.Rows.Count(r => r.Kind == CallStackRowKind.Frame && r.Text == "sample::util::explode"));
            Assert.Contains(callStack.Rows, r => r.Kind == CallStackRowKind.Frame && r.Text.StartsWith("sample::main::{closure#", StringComparison.Ordinal));
            await callStack.ActivateAsync(callStack.Rows[0], takeFocus: false);
            Note("call stack with std shown: " + Rows(callStack));
            Assert.Contains(callStack.Rows, r => r.Kind == CallStackRowKind.Frame && r.Text == "std::panicking::rust_panic" && r.IsExternal);
            await debug.StopAllAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 30_000, "stopped");
        }
        finally
        {
            exception.Dispose();
            callStack.Dispose();
        }
    }
}
