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

        public DebugConfiguration Config(params string[] args) => ConfigFor(Sample.MainJs, args.Contains("flood") ? "std" : "console", args);

        /// <summary>A launch of <paramref name="program"/> (the sample's own, or a JavaScript file written next to it).</summary>
        public DebugConfiguration ConfigFor(string program, string outputCapture, params string[] args) => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "pwa-node", ["request"] = "launch", ["name"] = Path.GetFileNameWithoutExtension(program), ["program"] = program, ["cwd"] = Sample.Dir.Path,
            ["outFiles"] = new[] { Sample.OutDir + "/**/*.js" }, ["sourceMaps"] = true, ["skipFiles"] = new[] { "<node_internals>/**" },
            ["console"] = "internalConsole", ["args"] = args, ["outputCapture"] = outputCapture,
        }), Sample.Dir.Uri);

        public string WriteProgram(string name, string source)
        {
            var path = Sample.Dir.Combine(name);
            File.WriteAllText(path, source);
            return path;
        }

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
        var autos = app.View<FrameVariablesViewModel>("debug.autos").Tree;
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

            // ============ set a variable from Locals (inline edit → setVariable): the program continues with the new value
            VariableNodeViewModel LocalX() => locals.Rows.First(r => r.Name == "x" && r.Parent?.Kind == VariableRowKind.Scope); // not p.x
            var xRow = LocalX();
            Assert.True(locals.CanEditValue(xRow));
            locals.BeginEditValue(xRow);
            xRow.EditText = "100";
            await locals.CommitEditAsync(xRow);
            Assert.Equal("100", LocalX().DisplayValue);
            console.Input = "x + 1";
            await console.SubmitAsync();
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Kind == ConsoleLineKind.Result && l.Text == "101"), 20_000, "REPL sees the new value");
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
            // node internals (skipped through skipFiles, hinted "deemphasize") are folded into one row by default, as VS Code does; a click
            // shows them, greyed like other external code
            Assert.DoesNotContain(callStack.Rows, r => r.Location.StartsWith("<node_internals>", StringComparison.Ordinal));
            var skipped = callStack.Rows.Single(r => r.Kind == CallStackRowKind.ExternalCode);
            Assert.True(skipped.HiddenFrames >= 2, $"{skipped.HiddenFrames} folded");
            await callStack.ActivateAsync(skipped, takeFocus: false);
            Note("call stack (skipped frames shown): " + Rows(callStack));
            var internals = callStack.Rows.Where(r => r.Location.StartsWith("<node_internals>", StringComparison.Ordinal)).ToList();
            Assert.Equal(skipped.HiddenFrames, internals.Count);
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
            Assert.Equal("100", locals.Rows.First(r => r.Name == "a").DisplayValue); // x was set to 100 at the previous stop
            await PanelRig.Eventually(() => watch.Rows.Any(r => r.Name == "a + b" && r.DisplayValue == "101") && watch.Rows.Any(r => r.Name == "x" && r.IsError), 20_000, "watch");
            Note("watch in add: " + Rows(watch));
            await PanelRig.Eventually(() => autos.Rows.Any(r => r.Name == "a"), 20_000, "autos");
            Note("autos in add: " + Rows(autos));
            await PanelRig.Eventually(() => threads.Rows.Count >= 1, 20_000, "threads");
            Note("threads: " + string.Join(" | ", threads.Rows.Select(r => $"{r.Id} {r.Name} {r.State} {r.Location}")));

            // the async parent frame (main, below the await label) can be selected, but js-debug has no variables for async frames
            await callStack.ActivateAsync(callStack.Rows.First(r => r.Text == "main"), takeFocus: false);
            await PanelRig.Eventually(() => locals.StatusText?.Contains("not available in async stacks", StringComparison.Ordinal) == true && locals.Rows.Count == 0, 20_000, "async frame: the adapter has no variables for it");
            Note("Locals for the async main() frame: " + locals.StatusText);
            Assert.Contains(app.Navigation.Opened, o => o.Uri == app.MainUri && o.Selection is { } sel && sel.Start.Line == JsDebugSample.MainLine("call"));
            await callStack.ActivateAsync(callStack.Rows.First(r => r.Text == "add"), takeFocus: false);
            await PanelRig.Eventually(() => locals.Rows.Any(r => r.Name == "a"), 20_000, "locals of add again");

            // ============ step over in add(): sum changes and is highlighted
            var before = stops;
            await debug.StopLocation!.Session.StepOverAsync();
            await PanelRig.Eventually(() => stops > before && locals.Rows.Any(r => r.Name == "sum" && r.DisplayValue == "101"), 30_000, "sum assigned");
            Note("locals after step: " + Rows(locals));
            Assert.True(locals.Rows.First(r => r.Name == "sum").IsChanged);
            Assert.False(locals.Rows.First(r => r.Name == "a").IsChanged);

            // ============ run to the end: stderr in red, panels quiet
            await debug.StopLocation!.Session.ContinueAsync();
            await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 40_000, "session ended");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text.StartsWith("done hello", StringComparison.Ordinal)), 20_000, "final output");
            Assert.Equal(ConsoleLineKind.Error, console.Lines.Single(l => l.Text == "err 101").Kind);
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

    /// <summary>A large array, a getter that throws, a getter that works, and two stops in the same frame (review findings, for real).</summary>
    private const string ExtrasProgram = """
        class Shape {
          get boom() { throw new Error("getter failed"); }
          get fine() { return 7; }
        }
        const huge = Array.from({ length: 10000 }, (_, i) => i * 3);
        const shape = new Shape();
        for (let round = 0; round < 2; round++) {
          debugger;
        }
        console.log("extras done " + huge.length);

        """;

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Locals_page_a_ten_thousand_element_array_to_its_end_and_show_getters_that_throw_or_were_evaluated_truthfully()
    {
        await using var app = await App.CreateAsync();
        var debug = app.Debug;
        var locals = app.View<FrameVariablesViewModel>("debug.locals").Tree;
        var stops = 0;
        debug.SessionStarted.Subscribe(s => s.Stopped.Subscribe(_ => Interlocked.Increment(ref stops)));
        _ = await debug.StartAsync(app.ConfigFor(app.WriteProgram("extras.js", ExtrasProgram), "console")) ?? throw new InvalidOperationException("no session");
        await PanelRig.Eventually(() => stops >= 1 && locals.Rows.Any(r => r.Name == "huge") && locals.Rows.Any(r => r.Name == "shape"), 40_000, "stopped at the debugger statement");
        Note("extras locals: " + Rows(locals));

        // 10,000 elements: every page down to the last element, and no cap notice (it used to stop at 9,997 and claim "10,000 of 10,000")
        var huge = locals.Rows.First(r => r.Name == "huge");
        await locals.ToggleAsync(huge);
        await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == huge && r.Kind == VariableRowKind.More), 20_000, "first page of huge");
        var pages = 1;
        for (; pages < 150 && locals.Rows.FirstOrDefault(r => r.Parent == huge && r.Kind == VariableRowKind.More) is { } more; pages++) await locals.LoadMoreAsync(more);
        var children = locals.Rows.Where(r => r.Parent == huge).ToList();
        var elements = children.Where(r => r.Kind == VariableRowKind.Variable && int.TryParse(r.Name, out _)).ToList();
        Note($"huge after {pages} pages: {elements.Count} elements, last {elements[^1].Name}={elements[^1].DisplayValue}, named: {string.Join(",", children.Where(r => r.Kind == VariableRowKind.Variable && !int.TryParse(r.Name, out _)).Select(r => r.Name))}, messages: {string.Join(" | ", children.Where(r => r.Kind == VariableRowKind.Message).Select(r => r.Name))}");
        Assert.Equal(10_000, elements.Count);
        Assert.Equal(("9999", "29997"), (elements[^1].Name, elements[^1].DisplayValue));
        Assert.DoesNotContain(children, r => r.Kind == VariableRowKind.Message);
        await locals.ToggleAsync(huge);

        // getters: one throws, one works
        var shape = locals.Rows.First(r => r.Name == "shape");
        await locals.ToggleAsync(shape);
        await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent == shape && r.Name == "boom") && locals.Rows.Any(r => r.Parent == shape && r.Name == "fine"), 20_000, "shape expanded");
        var boom = locals.Rows.First(r => r.Parent == shape && r.Name == "boom");
        await locals.ActivateAsync(boom);
        await PanelRig.Eventually(() => !boom.IsLoading && !boom.IsLazy || boom.IsError, 20_000, "the throwing getter ran");
        Note($"throwing getter: '{boom.DisplayValue}' error={boom.IsError} children={boom.HasChildren}");
        Assert.True(boom.IsError);
        Assert.Equal(VariablesTreeViewModel.LazyFailedText, boom.DisplayValue);
        var fine = locals.Rows.First(r => r.Parent == shape && r.Name == "fine");
        Note($"getter before evaluation: '{fine.DisplayValue}' type '{fine.Type}' lazy={fine.IsLazy}");
        Assert.Null(fine.Type); // js-debug types the placeholder "function" (the accessor), not its value
        await locals.ActivateAsync(fine);
        await PanelRig.Eventually(() => fine.DisplayValue == "7", 20_000, "the working getter evaluated in place");
        Assert.Equal("number", fine.Type);

        // the next stop in the same frame: the evaluated getter is a placeholder again and NOT marked changed; the loop variable is
        await debug.StopLocation!.Session.ContinueAsync();
        await PanelRig.Eventually(() => stops >= 2 && locals.Rows.Any(r => r.Name == "round" && r.DisplayValue == "1"), 40_000, "second stop");
        await PanelRig.Eventually(() => locals.Rows.Any(r => r.Parent?.Name == "shape" && r.Name == "fine"), 20_000, "shape expanded again");
        var fineAgain = locals.Rows.First(r => r.Parent?.Name == "shape" && r.Name == "fine");
        Note("second stop: " + Rows(locals));
        Assert.True(fineAgain.IsLazy);
        Assert.False(fineAgain.IsChanged);
        Assert.True(locals.Rows.First(r => r.Name == "round").IsChanged);
        await debug.StopAllAsync();
        await PanelRig.Eventually(() => debug.State == DebugState.Inactive, 20_000, "ended");
    }

}
