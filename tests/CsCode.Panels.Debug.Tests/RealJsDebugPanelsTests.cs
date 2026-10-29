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
/// The Debug panels over the REAL DAP module and the REAL js-debug v1.140.0 debugging a TypeScript program compiled by the real tsc: what the
/// Call Stack, Locals, Watch, Breakpoints, Threads, Exception and Debug Console windows show for Node.js (async frames, skipped internals,
/// JavaScript values, paged arrays, getters, the REPL). Evidence for docs/DEBUGGING-RUST-TS.md. Skips with a reason when node, tsc or js-debug
/// is missing (tools/fetch-js-debug.sh, CSCODE_JSDEBUG).
/// </summary>
[Trait("Category", "RealAdapter")]
public class RealJsDebugPanelsTests
{
    internal static void Note(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        if (Environment.GetEnvironmentVariable("CSCODE_DAP_TEST_METRICS") is { Length: > 0 } path)
            lock (typeof(RealJsDebugPanelsTests)) File.AppendAllText(path, "[panels/js-debug] " + text + "\n");
    }

    /// <summary>The application as the shell composes it: the real DAP module and the panels' DI registration over test platform services.</summary>
    internal sealed class App : IAsyncDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required JsDebugSample Sample { get; init; }
        public required RecordingNavigationService Navigation { get; init; }
        public required TestWindowService Window { get; init; }
        public required TestAppPaths Paths { get; init; }
        public IDebugService Debug => Provider.GetRequiredService<IDebugService>();
        public Dictionary<string, IToolWindow> Tools => Provider.GetServices<IToolWindow>().ToDictionary(t => t.Id);
        public T View<T>(string id) where T : class => (T)Tools[id].CreateView(Provider).DataContext!;
        public Uri MainUri => new(Sample.MainTs);
        public Uri UtilUri => new(Sample.UtilTs);

        public static async Task<App> CreateAsync()
        {
            Assert.SkipWhen(JsDebugToolchain.SkipReason() is not null, JsDebugToolchain.SkipReason() ?? "");
            var sample = await JsDebugSample.CreateAsync("jsdebug-panels-");
            var documents = new DocumentManager();
            var navigation = new RecordingNavigationService(documents);
            var window = new TestWindowService();
            var settings = new TestConfigurationService();
            settings.Set(JsDebugAdapterFactory.SettingPath, JsDebugToolchain.FindJsDebug());
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
            return new App { Provider = services.BuildServiceProvider(), Sample = sample, Navigation = navigation, Window = window, Paths = paths };
        }

        public DebugConfiguration Config(params string[] args) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "pwa-node", ["request"] = "launch", ["name"] = "ts", ["program"] = Sample.MainJs, ["cwd"] = Sample.Dir.Path,
            ["outFiles"] = new[] { Sample.OutDir + "/**/*.js" }, ["sourceMaps"] = true, ["skipFiles"] = new[] { "<node_internals>/**" },
            ["console"] = "internalConsole", ["args"] = args, ["outputCapture"] = args.Contains("flood") ? "std" : "console",
        }), Sample.Dir.Uri);

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

    [AvaloniaFact(Timeout = 180_000)]
    public async Task The_panels_show_a_typescript_session_frames_values_paging_console_and_repl()
    {
        await using var app = await App.CreateAsync();
        var debug = app.Debug;
        debug.Breakpoints.Add(app.MainUri, JsDebugSample.MainLine("comment"));
        debug.Breakpoints.Add(app.MainUri, JsDebugSample.MainLine("call"));
        debug.Breakpoints.Add(app.UtilUri, JsDebugSample.UtilLine("add"));
        debug.AddWatch("a + b");
        debug.AddWatch("x");

        var breakpoints = app.View<BreakpointsViewModel>("debug.breakpoints");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        var locals = app.View<FrameVariablesViewModel>("debug.locals").Tree;
        var watch = app.View<WatchViewModel>("debug.watch").Tree;
        var threads = app.View<ThreadsViewModel>("debug.threads");
        var console = app.View<DebugConsoleViewModel>("debug.console");
        var stops = 0;
        debug.SessionStarted.Subscribe(s => s.Stopped.Subscribe(_ => Interlocked.Increment(ref stops)));
        try
        {
            _ = await debug.StartAsync(app.Config()) ?? throw new InvalidOperationException("no session: " + string.Join(" | ", app.Window.StatusMessages));

            // ============ stop 1: the breakpoint on a comment line moved to the next statement (Breakpoints window: "moved")
            await PanelRig.Eventually(() => stops >= 1 && callStack.Rows.Any(r => r.IsCurrent), 40_000, "stopped at the moved breakpoint");
            await PanelRig.Eventually(() => breakpoints.Rows.Any(r => r.Kind == BreakpointRowKind.Source && r.Verification == BreakpointVerification.Moved), 20_000, "moved verdict");
            var movedRow = breakpoints.Rows.First(r => r.Kind == BreakpointRowKind.Source && r.Verification == BreakpointVerification.Moved);
            Note($"breakpoints window: {string.Join(" | ", breakpoints.Rows.Where(r => r.Kind == BreakpointRowKind.Source).Select(r => $"{r.Label} {r.Verification} note='{r.Note}'"))}");
            Assert.Equal(JsDebugSample.MainLine("comment") + 2, movedRow.MovedToLine); // 1-based, the next line
            Assert.Equal($"src/main.ts : {JsDebugSample.MainLine("comment") + 2}", Here(callStack)); // js-debug names sources relative to the workspace
            await debug.StopLocation!.Session.ContinueAsync();

            // ============ stop 2: in main() at the awaiting call: JavaScript values in Locals
            await PanelRig.Eventually(() => stops >= 2 && Here(callStack) == $"src/main.ts : {JsDebugSample.MainLine("call") + 1}", 40_000, "stopped at @call");
            try { await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "big"), 20_000, "locals of main"); }
            catch (TimeoutException) { Note("locals in main (timed out): " + Rows(locals) + " status=" + locals.StatusText); throw; }
            Note("locals in main: " + Rows(locals));
            Assert.Equal("'hello'", locals.Rows.First(r => r.Name == "s").DisplayValue);
            Assert.Equal("51", locals.Rows.First(r => r.Name == "x").DisplayValue);

            // class instance with a getter
            var p = locals.Rows.First(r => r.Name == "p");
            await locals.ToggleAsync(p);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == p && r.Name == "length"), 20_000, "p expanded");
            Note($"p expanded: {string.Join(" | ", locals.Rows.Where(r => r.Parent == p).Select(r => $"{r.Name}={r.DisplayValue}{(r.HasChildren ? "+" : "")}"))}");
            await locals.ActivateAsync(locals.Rows.First(r => r.Parent == p && r.Name == "length"));
            await PanelRig.Eventually(() => locals.Rows.First(r => r.Parent == p && r.Name == "length").DisplayValue == "5", 20_000, "the getter evaluated in place");

            // an array of 250: first page, then the next pages through the pager row
            var big = locals.Rows.First(r => r.Name == "big");
            await locals.ToggleAsync(big);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More), 20_000, "first page of big");
            Note("big after the first page: " + string.Join(" | ", locals.Rows.Where(r => r.Parent == big).Select(r => $"{r.Name}={r.DisplayValue}").Take(4)) + " … " + locals.Rows.Last(r => r.Parent == big).Name);
            for (var page = 0; page < 4 && locals.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More); page++)
                await locals.LoadMoreAsync(locals.Rows.First(r => r.Parent == big && r.Kind == VariableRowKind.More));
            var children = locals.Rows.Where(r => r.Parent == big && r.Kind == VariableRowKind.Variable).ToList();
            Note($"big after paging: {children.Count} children (named: {string.Join(",", children.Where(r => !int.TryParse(r.Name, out _)).Select(r => r.Name))}), last {children[^1].Name}={children[^1].DisplayValue}, pager left: {locals.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More)}");
            Assert.Equal(Enumerable.Range(0, 250).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), children.Where(r => int.TryParse(r.Name, out _)).Select(r => r.Name));
            Assert.Equal("498", children.First(r => r.Name == "249").DisplayValue);
            Assert.Contains(children, r => r.Name == "length");
            await locals.ToggleAsync(big);

            // Map, Set
            var m = locals.Rows.First(r => r.Name == "m");
            Assert.StartsWith("Map(2)", m.DisplayValue);
            await locals.ToggleAsync(m);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == m), 20_000, "m expanded");
            Note("m expanded: " + string.Join(" | ", locals.Rows.Where(r => r.Parent == m).Select(r => $"{r.Name}={r.DisplayValue}")));
            Assert.StartsWith("Set(3)", locals.Rows.First(r => r.Name == "set").DisplayValue);

            // ============ Debug Console: program output, REPL, completions (in main())
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text == "loop 4"), 20_000, "program stdout");
            Assert.Equal(["loop 0", "loop 1", "loop 2", "loop 3", "loop 4"], console.Lines.Where(l => l.Kind == ConsoleLineKind.Output && l.Text.StartsWith("loop", StringComparison.Ordinal)).Select(l => l.Text));
            console.Input = "x * 2";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text == "102"), 20_000, "REPL in main()");
            console.Input = "s.toUpp";
            await console.RequestCompletionsAsync(7);
            await PanelRig.Eventually(() => console.Completions.Count > 0, 20_000, "completions");
            Note("completions for s.toUpp: " + string.Join(", ", console.Completions.Take(5).Select(c => c.Label)));
            Assert.Contains(console.Completions, c => c.Label == "toUpperCase");
            console.CloseCompletions();
            console.Input = "nope.nope";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.ResultError), 20_000, "REPL error");
            Note("REPL error line: " + console.Lines.Last(l => l.Kind == ConsoleLineKind.ResultError).Text);
            await debug.StopLocation!.Session.ContinueAsync();

            // ============ stop 3: in add() (util.ts), reached from the async work() — the async stack and the skipped node internals
            await PanelRig.Eventually(() => stops >= 3 && callStack.Rows.FirstOrDefault(r => r.IsCurrent)?.Text == "add", 40_000, "stopped in add()");
            await PanelRig.Eventually(() => callStack.Rows.Any(r => r.Text == "main"), 20_000, "whole stack");
            Note("call stack (Show External Code on, the default): " + Rows(callStack));
            Assert.Equal($"src/util.ts : {JsDebugSample.UtilLine("add") + 1}", Here(callStack));
            var frames = callStack.Rows.Where(r => r.Kind == CallStackRowKind.Frame).ToList();
            Assert.Equal(["add", "work"], frames.Take(2).Select(r => r.Text));
            Assert.Equal($"src/main.ts : {JsDebugSample.MainLine("async") + 1}", frames[1].Location);
            Assert.False(frames[0].IsExternal || frames[1].IsExternal);
            // node internals (skipped through skipFiles, hinted "deemphasize") are greyed like other external code
            var internals = callStack.Rows.Where(r => r.Location.StartsWith("<node_internals>", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(internals);
            Assert.All(internals, r => Assert.True(r.IsExternal, r.Text + " is greyed"));
            // folding external code keeps the async boundary between work() and main() visible and folds the node internals into one row
            callStack.ShowExternalCode = false;
            Note("call stack (Show External Code off): " + Rows(callStack));
            var label = callStack.Rows.Single(r => r.Text == "await");
            Assert.True(callStack.Rows.IndexOf(label) > callStack.Rows.IndexOf(callStack.Rows.First(r => r.Text == "work")) && callStack.Rows.IndexOf(label) < callStack.Rows.IndexOf(callStack.Rows.First(r => r.Text == "main")));
            Assert.DoesNotContain(callStack.Rows, r => r.Location.StartsWith("<node_internals>", StringComparison.Ordinal));
            Assert.Equal(CallStackRowKind.ExternalCode, callStack.Rows[^1].Kind);
            Assert.Equal(internals.Count, callStack.Rows[^1].HiddenFrames);
            callStack.ShowExternalCode = true;

            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "a"), 20_000, "locals of add");
            Note("locals in add: " + Rows(locals));
            Assert.Equal("51", locals.Rows.First(r => r.Name == "a").DisplayValue);
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "a + b" && r.DisplayValue == "52") && watch.Rows.Any(r => r.Name == "x" && r.IsError), 20_000, "watch");
            Note("watch in add: " + Rows(watch));
            await PanelRig.Eventually(() => threads.Rows.Count >= 1, 20_000, "threads");
            Note("threads: " + string.Join(" | ", threads.Rows.Select(r => $"{r.Id} {r.Name} {r.State} {r.Location}")));

            // the async parent frame (main, below the await label) can be selected, but js-debug has no variables for async frames
            await callStack.ActivateAsync(callStack.Rows.First(r => r.Text == "main"), takeFocus: false);
            await PanelRig.Eventually(() => locals.StatusText is { Length: > 0 } && locals.Rows.Count == 0, 20_000, "async frame: no variables");
            Note("Locals for the async main() frame: " + locals.StatusText);
            Assert.Contains(app.Navigation.Opened, o => o.Uri == app.MainUri && o.Selection is { } sel && sel.Start.Line == JsDebugSample.MainLine("call"));
            await callStack.ActivateAsync(callStack.Rows.First(r => r.Text == "add"), takeFocus: false);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "a"), 20_000, "locals of add again");

            // ============ step over in add(): sum changes and is highlighted
            var before = stops;
            await debug.StopLocation!.Session.StepOverAsync();
            await PanelRig.Eventually(() => stops > before && locals.Rows.Any(r => r.Name == "sum" && r.DisplayValue == "52"), 30_000, "sum assigned");
            Note("locals after step: " + Rows(locals));
            Assert.True(locals.Rows.First(r => r.Name == "sum").IsChanged);
            Assert.False(locals.Rows.First(r => r.Name == "a").IsChanged);

            // ============ run to the end: stderr in red, panels quiet
            await debug.StopLocation!.Session.ContinueAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 40_000, "session ended");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text.StartsWith("done hello", StringComparison.Ordinal)), 20_000, "final output");
            Assert.Equal(ConsoleLineKind.Error, console.Lines.Single(l => l.Text == "err 52").Kind);
            await PanelRig.Eventually(() => locals.Rows.Count == 0 && callStack.Rows.Count == 0, 10_000, "panels cleared");
        }
        finally
        {
            foreach (var t in new IDisposable[] { breakpoints, callStack, threads, console }) t.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_exception_window_shows_a_javascript_exception()
    {
        await using var app = await App.CreateAsync();
        var debug = app.Debug;
        debug.Breakpoints.SetExceptionFilters([new ExceptionFilterSetting("all", "Caught Exceptions", true), new ExceptionFilterSetting("uncaught", "Uncaught Exceptions", true)]);
        var exception = app.View<ExceptionHelperViewModel>("debug.exception");
        var callStack = app.View<CallStackViewModel>("debug.callstack");
        try
        {
            await debug.StartAsync(app.Config());
            await PanelRig.Eventually(() => exception.HasException, 40_000, "exception shown");
            Note($"exception window: title '{exception.Title}', type '{exception.Details!.TypeName}', message '{exception.Details.Message}', stack: {exception.Details.StackTrace?.Split('\n').FirstOrDefault()}");
            Assert.Contains("boom", exception.Details.TypeName + " " + exception.Details.Message);
            await PanelRig.Eventually(() => callStack.Rows.FirstOrDefault(r => r.IsCurrent)?.Text == "fails", 20_000, "stopped in fails()");
            Assert.Equal($"src/main.ts : {JsDebugSample.MainLine("throw") + 1}", Here(callStack));
        }
        finally
        {
            exception.Dispose();
            callStack.Dispose();
        }
    }
}
