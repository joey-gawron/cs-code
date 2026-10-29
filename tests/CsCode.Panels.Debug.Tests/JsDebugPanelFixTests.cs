using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using CsCode.Panels.Debug.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// Regression tests (fakes shaped like js-debug v1.140.0's real answers) for what the TypeScript evaluation matrix found in the Debug panels,
/// and for the debugger follow-ups handed to this wave. Each fails without its fix (docs/DEBUGGING-RUST-TS.md).
/// </summary>
public class JsDebugPanelFixTests
{
    /// <summary>Answers like js-debug: <c>start</c> is ignored unless a filter is given; the filters page through indexed / list named children.</summary>
    internal sealed class JsLikeSession : FakeDebugSession, IDebugSessionVariablePaging
    {
        public List<(int Reference, string? Filter, int? Start, int? Count)> FilteredRequests { get; } = new();

        public JsLikeSession() : base("s1", "node") => WithDefaultScript();

        public override ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) =>
            base.GetVariablesAsync(variablesReference, null, count, ct); // js-debug: no filter, no paging (always from element 0)

        public async ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, string? filter, int? start, int? count, CancellationToken ct = default)
        {
            lock (FilteredRequests) FilteredRequests.Add((variablesReference, filter, start, count));
            var all = await base.GetVariablesAsync(variablesReference, null, null, ct);
            IEnumerable<DebugVariable> chosen = filter switch
            {
                "indexed" => all.Where(v => int.TryParse(v.Name, out _)),
                "named" => all.Where(v => !int.TryParse(v.Name, out _)),
                _ => all,
            };
            if (filter is null) return [.. chosen.Take(count ?? int.MaxValue)];
            chosen = chosen.Skip(start ?? 0);
            if (count is > 0) chosen = chosen.Take(count.Value);
            return [.. chosen];
        }
    }

    // ------------------------------------------------------------------ Call Stack

    [AvaloniaFact]
    public async Task Node_internals_the_adapter_deemphasizes_are_external_code_and_the_async_label_stays_visible()
    {
        using var rig = new PanelRig();
        var internals = new DebugSource("<node_internals>/internal/modules/cjs/loader", "<node_internals>/internal/modules/cjs/loader", 77, "deemphasize");
        rig.Session.Frames[1] =
        [
            new DebugStackFrame(100, "add", new DebugSource("src/util.ts", "/work/src/util.ts"), 1, 2),
            new DebugStackFrame(101, "work", new DebugSource("src/main.ts", "/work/src/main.ts"), 11, 12),
            new DebugStackFrame(102, "await", null, 0, 0, PresentationHint: "label"),
            new DebugStackFrame(103, "main", new DebugSource("src/main.ts", "/work/src/main.ts"), 53, 18),
            new DebugStackFrame(104, "Module._compile", internals, 1528, 13, PresentationHint: "deemphasize"),
            new DebugStackFrame(105, "Module.load", internals, 1274, 31, PresentationHint: "deemphasize"),
        ];
        using var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 5, what: "frames");
        // the skipped internals fold by default (JsDebugReviewFixTests); shown, they are greyed
        Assert.Equal(["add", "work", "await", "main", "[External Code]"], vm.Rows.Select(r => r.Text));
        await vm.ActivateAsync(vm.Rows[^1], takeFocus: false);
        Assert.Equal([false, false, true, false, true, true], vm.Rows.Select(r => r.IsExternal)); // greyed

        vm.ShowExternalCode = false;
        Assert.Equal(["add", "work", "await", "main", "[External Code]"], vm.Rows.Select(r => r.Text));
        Assert.Equal(2, vm.Rows[^1].HiddenFrames);

        var calls = rig.Debug.FocusCalls.Count;
        await vm.ActivateAsync(vm.Rows.Single(r => r.Text == "await"), takeFocus: false); // a separator, not a frame to switch to
        Assert.Equal(calls, rig.Debug.FocusCalls.Count);
        Assert.Empty(rig.Navigation.Opened);
    }

    // ------------------------------------------------------------------ values

    [Fact]
    public void Strings_the_adapter_already_quoted_with_single_quotes_are_not_quoted_again()
    {
        Assert.Equal("'hello'", ValueFormat.Display("'hello'", "string", NumberFormat.Decimal));         // js-debug
        Assert.Equal("'a long te…'", ValueFormat.Display("'a long te…'", "string", NumberFormat.Decimal)); // js-debug cuts long strings
        Assert.Equal("\"hello\"", ValueFormat.Display("hello", "string", NumberFormat.Decimal));
        Assert.Equal("\"it's\"", ValueFormat.Display("\"it's\"", "string", NumberFormat.Decimal));
    }

    [AvaloniaFact]
    public async Task A_lazy_getter_shows_a_placeholder_and_runs_only_when_activated_then_shows_its_value_in_place()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1000] =
        [
            new DebugVariable("x", "3", "number"),
            new DebugVariable("length", "ƒ length() { return Math.sqrt(this.x * this.x + this.y * this.y); }", "function", 3000) { IsLazy = true },
        ];
        rig.Session.Variables[3000] = [new DebugVariable("length", "5", "number")];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "length"), what: "locals");
        var getter = vm.Tree.Rows.Single(r => r.Name == "length");
        Assert.Equal(VariableNodeViewModel.LazyText, getter.DisplayValue);
        Assert.False(getter.HasChildren);
        Assert.DoesNotContain(rig.Session.VariableRequests, r => r.Reference == 3000); // the getter has not run

        await vm.Tree.ActivateAsync(getter);
        Assert.Equal("5", getter.DisplayValue);
        Assert.False(getter.IsLazy);
        Assert.Contains(rig.Session.VariableRequests, r => r.Reference == 3000);
    }

    [AvaloniaFact]
    public async Task Indexed_children_are_paged_with_the_indexed_filter_from_an_adapter_that_ignores_start_without_it()
    {
        var session = new JsLikeSession();
        using var rig = new PanelRig(session: session);
        var elements = Enumerable.Range(0, 250).Select(i => new DebugVariable(i.ToString(System.Globalization.CultureInfo.InvariantCulture), (i * 2).ToString(System.Globalization.CultureInfo.InvariantCulture), "number")).ToList();
        session.Variables[1000] = [new DebugVariable("big", "(250) [0, 2, 4, …]", "Array", 4000, "big", NamedVariables: 1, IndexedVariables: 250)];
        session.Variables[4000] = [.. elements, new DebugVariable("length", "250", "number")];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "big"), what: "locals");
        var big = vm.Tree.Rows.Single(r => r.Name == "big");
        await vm.Tree.ToggleAsync(big);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Parent == big && r.Kind == VariableRowKind.More), what: "first page");
        for (var i = 0; i < 5 && vm.Tree.Rows.FirstOrDefault(r => r.Parent == big && r.Kind == VariableRowKind.More) is { } more; i++)
            await vm.Tree.LoadMoreAsync(more);
        var children = vm.Tree.Rows.Where(r => r.Parent == big && r.Kind == VariableRowKind.Variable).Select(r => r.Name).ToList();
        Assert.Equal(["length", .. elements.Select(e => e.Name)], children);
        Assert.Contains(session.FilteredRequests, r => r is { Filter: "indexed", Start: 200, Count: 100 });
    }

    [AvaloniaFact]
    public async Task A_pseudo_variable_that_appears_after_a_step_does_not_hide_what_changed()
    {
        using var rig = new PanelRig();
        rig.Session.Variables[1000] = [new DebugVariable("a", "51", "number"), new DebugVariable("b", "1", "number"), new DebugVariable("sum", "undefined", "undefined")];
        using var vm = new LocalsViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "sum"), what: "first stop");
        rig.Resume();
        // js-debug after stepping over `const sum = a + b`: a "Return value" pseudo-variable appears among the locals
        rig.Session.Variables[1000] = [new DebugVariable("a", "51", "number"), new DebugVariable("b", "1", "number"), new DebugVariable("Return value", "52", "number"), new DebugVariable("sum", "52", "number")];
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "sum" && r.DisplayValue == "52"), what: "after the step");
        Assert.True(vm.Tree.Rows.Single(r => r.Name == "sum").IsChanged);
        Assert.False(vm.Tree.Rows.Single(r => r.Name == "a").IsChanged);
    }

    // ------------------------------------------------------------------ showing where the debugger stopped

    /// <summary>An editor document that only knows its URI (all the stop reveal looks at).</summary>
    public class UriOnlyDocument : System.Reflection.DispatchProxy
    {
        public Uri Uri { get; set; } = new("file:///none");
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method?.Name == "get_Uri" ? Uri : throw new NotSupportedException(method?.Name);

        public static CsCode.Core.Text.IEditorDocument For(Uri uri)
        {
            var doc = Create<CsCode.Core.Text.IEditorDocument, UriOnlyDocument>();
            ((UriOnlyDocument)(object)doc).Uri = uri;
            return doc;
        }
    }

    [AvaloniaFact]
    public async Task A_stop_in_a_file_that_is_not_the_active_editor_opens_it_at_the_stop_line()
    {
        using var sc = new Scenario(stopped: false);
        _ = sc.Provider.GetRequiredService<CsCode.Panels.Debug.ToolWindows.DebugPanelsRuntime>();
        var nav = sc.Rig.Navigation;
        nav.ActiveDocument = UriOnlyDocument.For(new Uri("file:///work/Sample/Other.cs"));

        sc.Rig.StopAt(0); // Program.cs, not the active editor
        var top = sc.Session.Frames[1][0];
        await PanelRig.Eventually(() => nav.Opened.Count == 1, what: "the stop file opened");
        Assert.Equal(top.Source!.ToUri(), nav.Opened[0].Uri);
        Assert.Equal(CsCode.Core.Text.TextRange.At(top.Line, top.Column), nav.Opened[0].Selection);

        // already the active editor: the editor reveals the line itself, nothing is opened again
        nav.ActiveDocument = UriOnlyDocument.For(top.Source.ToUri()!);
        sc.Rig.Resume();
        sc.Rig.StopAt(0);
        await Task.Delay(200);
        Assert.Single(nav.Opened);

        // a frame picked in the Call Stack opens its source there, not here
        nav.ActiveDocument = UriOnlyDocument.For(new Uri("file:///work/Sample/Other.cs"));
        sc.Rig.Debug.SetStopLocation(sc.Rig.Debug.StopLocation! with { Reason = "frame", FrameId = 101 });
        await Task.Delay(200);
        Assert.Single(nav.Opened);
    }

    // ------------------------------------------------------------------ follow-up (d): frame focus throws

    [Fact]
    public async Task The_test_fake_refuses_a_frame_it_cannot_focus_by_throwing_as_the_real_debug_service_does()
    {
        using var rig = new PanelRig();
        rig.StopAt(0);
        var fake = (IDebugFrameFocus)rig.Debug;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await fake.SetFocusedFrameAsync("s1", 1, 999, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await fake.SetFocusedFrameAsync("ended", 1, 100, TestContext.Current.CancellationToken));

        // the real service, same contract
        var services = new ServiceCollection();
        CsCode.Dap.ServiceCollectionExtensions.AddCsCodeDap(services);
        await using var provider = services.BuildServiceProvider();
        var real = provider.GetRequiredService<IDebugFrameFocus>();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await real.SetFocusedFrameAsync("ended", 1, 100, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task A_frame_the_service_refuses_keeps_the_arrow_and_the_status_bar_names_the_frame_and_the_reason()
    {
        using var rig = new PanelRig();
        using var vm = new CallStackViewModel(rig.Services);
        rig.StopAt(0);
        await PanelRig.Eventually(() => vm.Rows.Count == 3 && vm.Rows[0].IsCurrent, what: "frames");
        rig.Debug.FocusableFrameLimit = 1;
        await vm.ActivateAsync(vm.Rows[2], takeFocus: false);
        Assert.True(vm.Rows[0].IsCurrent);
        Assert.False(vm.Rows[2].IsCurrent);
        Assert.Contains(rig.Window.StatusMessages, m => m.Contains("Main()", StringComparison.Ordinal) && m.Contains("no longer valid", StringComparison.Ordinal));
        Assert.Empty(rig.Navigation.Opened);
    }

    // ------------------------------------------------------------------ follow-up (a): breakpoint kinds per adapter capability

    [AvaloniaFact]
    public async Task Hit_counts_and_logpoints_are_offered_only_when_the_active_adapter_supports_them()
    {
        using var rig = new PanelRig();
        rig.Session.Capabilities = new DebugCapabilities(SupportsConditionalBreakpoints: true); // netcoredbg 3.2: no hit counts, no logpoints
        rig.StopAt(0);
        var bp = rig.Debug.Breakpoints.Add(new Uri("file:///work/Program.cs"), 7);
        using var vm = new BreakpointsViewModel(rig.Services);
        await PanelRig.Eventually(() => vm.Rows.Any(r => r.Kind == BreakpointRowKind.Source), what: "rows");
        var row = vm.Rows.Single(r => r.Kind == BreakpointRowKind.Source);
        Assert.Equal("The 'fake' debug adapter does not support hit-count breakpoints.", vm.HitCountUnsupported);
        Assert.Equal("The 'fake' debug adapter does not support logpoints.", vm.LogMessageUnsupported);

        rig.Window.InputResponses.Enqueue("5");
        await vm.EditHitCountAsync(row);
        await vm.EditLogMessageAsync(row);
        Assert.Empty(rig.Window.InputPrompts); // nothing to type: the user is told why instead
        Assert.Equal(["The 'fake' debug adapter does not support hit-count breakpoints.", "The 'fake' debug adapter does not support logpoints."], rig.Window.Messages);
        Assert.Null(rig.Debug.Breakpoints.SourceBreakpoints.Single().HitCondition);

        var disabled = BreakpointsView.Gated("Hit Count…", vm.HitCountUnsupported, () => Task.CompletedTask);
        Assert.False(disabled.IsEnabled);
        Assert.Equal("Hit Count… (not supported)", disabled.Header);
        Assert.Equal(vm.HitCountUnsupported, Avalonia.Controls.ToolTip.GetTip(disabled));

        // js-debug has both
        rig.Session.Capabilities = new DebugCapabilities(SupportsConditionalBreakpoints: true, SupportsHitConditionalBreakpoints: true, SupportsLogPoints: true);
        Assert.Null(vm.HitCountUnsupported);
        Assert.Null(vm.LogMessageUnsupported);
        await vm.EditHitCountAsync(row);
        Assert.Equal("5", rig.Debug.Breakpoints.SourceBreakpoints.Single(b => b.Id == bp.Id).HitCondition);
        Assert.True(BreakpointsView.Gated("Hit Count…", vm.HitCountUnsupported, () => Task.CompletedTask).IsEnabled);
    }
}
