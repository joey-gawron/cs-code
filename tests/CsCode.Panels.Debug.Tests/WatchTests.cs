using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Variables;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

public class WatchTests
{
    private static VariableNodeViewModel Watch(WatchViewModel vm, string expression) =>
        vm.Tree.Rows.Single(r => r.Kind == VariableRowKind.Watch && r.Name == expression);

    private static async Task AddAsync(WatchViewModel vm, string expression)
    {
        vm.BeginAdd();
        Assert.True(vm.NewRow.IsEditingExpression);
        vm.NewRow.EditText = expression;
        await vm.Tree.CommitEditAsync(vm.NewRow);
    }

    [AvaloniaFact]
    public async Task Typing_in_the_empty_last_row_adds_a_watch_and_persists_it_per_workspace()
    {
        using var rig = new PanelRig(withSession: false);
        using var vm = new WatchViewModel(rig.Services);
        Assert.Equal([VariableRowKind.NewWatch], vm.Tree.Rows.Select(r => r.Kind));

        await AddAsync(vm, "x + 1");
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2, what: "watch row");
        Assert.Equal("x + 1", Assert.Single(rig.Debug.Watches).Expression);
        Assert.Equal(VariableRowKind.NewWatch, vm.Tree.Rows[^1].Kind); // the empty row stays last
        Assert.Equal(["x + 1"], rig.State.Get<List<string>>(WatchStore.StateKey, StorageScope.Workspace));
    }

    [AvaloniaFact]
    public void Persisted_watches_are_restored_into_the_debug_service_when_it_has_none()
    {
        using var rig = new PanelRig(withSession: false);
        rig.State.Set(WatchStore.StateKey, new List<string> { "a", "b.c" }, StorageScope.Workspace);
        using var vm = new WatchViewModel(rig.Services);
        Assert.Equal(["a", "b.c"], rig.Debug.Watches.Select(w => w.Expression));
        Assert.Equal(["a", "b.c"], vm.Tree.Rows.Where(r => r.Kind == VariableRowKind.Watch).Select(r => r.Name));
    }

    [AvaloniaFact]
    public void A_watch_list_that_already_lives_in_the_service_is_not_overwritten_by_the_persisted_one()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.AddWatch("live");
        rig.State.Set(WatchStore.StateKey, new List<string> { "old" }, StorageScope.Workspace);
        using var vm = new WatchViewModel(rig.Services);
        Assert.Equal(["live"], rig.Debug.Watches.Select(w => w.Expression));
    }

    private static List<string>? SavedWatches(PanelRig rig, Uri folder)
    {
        var current = rig.State.Workspace;
        rig.State.Workspace = folder.AbsoluteUri;
        try { return rig.State.Get<List<string>>(WatchStore.StateKey, StorageScope.Workspace); }
        finally { rig.State.Workspace = current; }
    }

    private static void SaveWatches(PanelRig rig, Uri folder, params string[] expressions)
    {
        var current = rig.State.Workspace;
        rig.State.Workspace = folder.AbsoluteUri;
        rig.State.Set(WatchStore.StateKey, expressions.ToList(), StorageScope.Workspace);
        rig.State.Workspace = current;
    }

    private static string[] WatchNames(WatchViewModel vm) => [.. vm.Tree.Rows.Where(r => r.Kind == VariableRowKind.Watch).Select(r => r.Name)];

    [AvaloniaFact]
    public async Task Opening_another_folder_shows_that_folders_watches_and_never_overwrites_its_saved_list()
    {
        // Regression (debug-ct-4 / dbgp-persist-1): the list was loaded once per app lifetime, so after Open Folder B the Watch window kept
        // A's expressions and the first edit wrote them into B's workspace state, losing B's own list.
        using var rig = new PanelRig(withSession: false);
        var a = new Uri("file:///work/a/");
        var b = new Uri("file:///work/b/");
        SaveWatches(rig, a, "a1", "a2");
        SaveWatches(rig, b, "b1");
        await rig.Workspace.OpenFolderAsync(a);
        using var vm = new WatchViewModel(rig.Services);
        Assert.Equal(["a1", "a2"], WatchNames(vm));

        await rig.Workspace.OpenFolderAsync(b);
        await PanelRig.Eventually(() => WatchNames(vm).SequenceEqual(["b1"]), what: "B's watches shown");
        Assert.Equal(["b1"], rig.Debug.Watches.Select(w => w.Expression));

        await AddAsync(vm, "x");
        await PanelRig.Eventually(() => rig.Debug.Watches.Count == 2, what: "added");
        Assert.Equal(["b1", "x"], SavedWatches(rig, b));
        Assert.Equal(["a1", "a2"], SavedWatches(rig, a)); // A's list is untouched

        await rig.Workspace.OpenFolderAsync(a); // and back
        await PanelRig.Eventually(() => WatchNames(vm).SequenceEqual(["a1", "a2"]), what: "A's watches shown again");
    }

    [AvaloniaFact]
    public async Task Adding_a_watch_from_locals_before_the_watch_window_was_ever_opened_keeps_the_folders_saved_watches()
    {
        // Regression (debug-ct-4 / dbgp-persist-1, "never save before the load"): only the Watch window loaded the saved list. An edit made
        // elsewhere first (Locals' "Add Watch") saved the service's list over it, and the later load saw a non-empty service and skipped it.
        using var rig = new PanelRig();
        var a = new Uri("file:///work/a/");
        SaveWatches(rig, a, "a1", "a2");
        await rig.Workspace.OpenFolderAsync(a);
        using var locals = new LocalsViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => locals.Tree.Rows.Any(r => r.Name == "x"), what: "locals");

        locals.Tree.AddToWatch(locals.Tree.Rows.Single(r => r.Name == "x"));
        Assert.Equal(["a1", "a2", "x"], rig.Debug.Watches.Select(w => w.Expression));
        Assert.Equal(["a1", "a2", "x"], SavedWatches(rig, a));
        using var vm = new WatchViewModel(rig.Services);
        Assert.Equal(["a1", "a2", "x"], WatchNames(vm));
    }

    [AvaloniaFact]
    public async Task A_folder_without_saved_watches_starts_empty_and_adding_a_second_root_keeps_the_list()
    {
        using var rig = new PanelRig(withSession: false);
        var a = new Uri("file:///work/a/");
        SaveWatches(rig, a, "a1");
        await rig.Workspace.OpenFolderAsync(a);
        using var vm = new WatchViewModel(rig.Services);
        var a1 = rig.Debug.Watches.Single();

        await rig.Workspace.AddFolderAsync(new Uri("file:///work/lib/")); // same primary folder, same workspace state
        await Task.Delay(50);
        Assert.Same(a1, rig.Debug.Watches.Single());

        await rig.Workspace.OpenFolderAsync(new Uri("file:///work/new/"));
        await PanelRig.Eventually(() => WatchNames(vm).Length == 0, what: "nothing carried over");
        Assert.Empty(rig.Debug.Watches);
        Assert.Equal(["a1"], SavedWatches(rig, a));

        Assert.Equal(1, rig.Workspace.SubscriberCount); // the app-lifetime store, released with the services
        rig.Services.Dispose();
        Assert.Equal(0, rig.Workspace.SubscriberCount);
    }

    [AvaloniaFact]
    public async Task Expressions_are_evaluated_in_the_focused_frame_with_context_watch_on_every_stop()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult(e == "x + 1" ? "42" : "?", "int");
        rig.Debug.AddWatch("x + 1");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "x + 1").DisplayValue == "42", what: "evaluated");
        var request = Assert.Single(rig.Session.EvaluateRequests);
        Assert.Equal(("x + 1", 102, "watch"), request);
        Assert.Equal("int", Watch(vm, "x + 1").Type);
    }

    [AvaloniaFact]
    public async Task Evaluation_errors_show_in_red_with_the_adapters_message()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => throw new InvalidOperationException("error CS0103: The name 'nope' does not exist");
        rig.Debug.AddWatch("nope");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "nope").IsError, what: "error row");
        Assert.Contains("CS0103", Watch(vm, "nope").DisplayValue);
        Assert.Contains("CS0103", Watch(vm, "nope").Tooltip);
    }

    [AvaloniaFact]
    public async Task Values_refresh_after_each_step_and_changed_values_are_highlighted()
    {
        using var rig = new PanelRig();
        var counter = 41;
        rig.Session.Evaluator = (_, _, _) => new DebugEvaluateResult((counter++).ToString(), "int");
        rig.Debug.AddWatch("x");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "x").DisplayValue == "41");
        Assert.False(Watch(vm, "x").IsChanged);

        rig.Resume();
        await PanelRig.Eventually(() => Watch(vm, "x").IsStale, what: "dimmed while running");
        Assert.Equal("41", Watch(vm, "x").DisplayValue); // the last value stays visible, dimmed

        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "x").DisplayValue == "42" && !Watch(vm, "x").IsStale);
        Assert.True(Watch(vm, "x").IsChanged);
    }

    [AvaloniaFact]
    public async Task Evaluation_results_with_children_are_expandable()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => new DebugEvaluateResult("{Sample.Item}", "Sample.Item", 2000, 2, 0);
        rig.Debug.AddWatch("o");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "o").HasChildren);
        await vm.Tree.ToggleAsync(Watch(vm, "o"));
        Assert.Equal(["o", "Name", "Count", ""], vm.Tree.Rows.Select(r => r.Name));
        Assert.Equal(1, vm.Tree.Rows[1].Depth);
    }

    [AvaloniaFact]
    public async Task Expanded_watch_results_are_re_expanded_after_the_next_stop()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => new DebugEvaluateResult("{Sample.Item}", "Sample.Item", 2000, 2, 0);
        rig.Debug.AddWatch("o");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "o").HasChildren);
        await vm.Tree.ToggleAsync(Watch(vm, "o"));
        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2, what: "children dropped on resume");
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 4, what: "re-expanded");
    }

    [AvaloniaFact]
    public async Task Editing_an_expression_replaces_it_and_re_evaluates()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult("v:" + e, "string");
        rig.Debug.AddWatch("x");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "x").DisplayValue == "\"v:x\"");

        var row = Watch(vm, "x");
        vm.BeginRename(row);
        Assert.Equal("x", row.EditText);
        row.EditText = "x * 2";
        await vm.Tree.CommitEditAsync(row);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "x * 2" && r.DisplayValue == "\"v:x * 2\""), what: "renamed + evaluated");
        Assert.Equal(["x * 2"], rig.Debug.Watches.Select(w => w.Expression));
        Assert.Equal(["x * 2"], rig.State.Get<List<string>>(WatchStore.StateKey, StorageScope.Workspace));
    }

    [AvaloniaFact]
    public async Task Clearing_the_text_of_an_expression_deletes_it_and_delete_removes_the_selected_row()
    {
        using var rig = new PanelRig(withSession: false);
        rig.Debug.AddWatch("a");
        rig.Debug.AddWatch("b");
        using var vm = new WatchViewModel(rig.Services);

        var a = Watch(vm, "a");
        vm.BeginRename(a);
        a.EditText = "   ";
        await vm.Tree.CommitEditAsync(a);
        await PanelRig.Eventually(() => rig.Debug.Watches.Count == 1);

        vm.Tree.SelectedRow = Watch(vm, "b");
        vm.RemoveSelected();
        await PanelRig.Eventually(() => rig.Debug.Watches.Count == 0);
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 1);
        Assert.Empty(rig.State.Get<List<string>>(WatchStore.StateKey, StorageScope.Workspace)!);
    }

    [AvaloniaFact]
    public async Task Watches_can_be_reordered_and_keep_their_values()
    {
        using var rig = new PanelRig();
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult("=" + e, "string");
        rig.Debug.AddWatch("a");
        rig.Debug.AddWatch("b");
        rig.Debug.AddWatch("c");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Where(r => r.Kind == VariableRowKind.Watch).All(r => r.DisplayValue.Length > 0));

        vm.Move(Watch(vm, "c"), -2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Select(r => r.Name).SequenceEqual(["c", "a", "b", ""]), what: "reordered");
        Assert.Equal(["c", "a", "b"], rig.Debug.Watches.Select(w => w.Expression));
        Assert.Equal(["c", "a", "b"], rig.State.Get<List<string>>(WatchStore.StateKey, StorageScope.Workspace));
        Assert.Equal("\"=c\"", Watch(vm, "c").DisplayValue);

        vm.MoveTo(Watch(vm, "b"), Watch(vm, "c"));
        await PanelRig.Eventually(() => rig.Debug.Watches.Select(w => w.Expression).SequenceEqual(["b", "c", "a"]));
    }

    [AvaloniaFact]
    public async Task Two_expanded_watches_re_expanded_concurrently_keep_their_children_under_the_right_parent()
    {
        // Regression (debug-ct-1): every watch is re-evaluated and re-expanded concurrently after a stop. Each full page is inserted in a
        // Background chunk whose position used to be computed before the yield, so b's children landed inside a's list.
        var ui = new GatedUiDispatcher();
        using var rig = new PanelRig(ui: ui);
        rig.Session.Evaluator = (e, _, _) => new DebugEvaluateResult("int[1000]", "int[]", e == "a" ? 5000 : 6000, null, 1000);
        rig.Session.Variables[5000] = Enumerable.Range(0, 1000).Select(i => new DebugVariable($"[{i}]", "1" + i, "int")).ToList();
        rig.Session.Variables[6000] = Enumerable.Range(0, 1000).Select(i => new DebugVariable($"[{i}]", "2" + i, "int")).ToList();
        rig.Debug.AddWatch("a");
        rig.Debug.AddWatch("b");
        using var vm = new WatchViewModel(rig.Services);
        rig.StopAt(2);
        await PanelRig.Eventually(() => Watch(vm, "a").HasChildren && Watch(vm, "b").HasChildren, what: "evaluated");
        await vm.Tree.ExpandAsync(Watch(vm, "a"));
        await vm.Tree.ExpandAsync(Watch(vm, "b"));
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2 * 102 + 1, what: "both expanded");
        string Layout() => string.Join("|", vm.Tree.Rows.Select(r => $"{r.Depth}:{r.Name}={r.FullValue}"));
        var expected = Layout();

        rig.Resume();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 3, what: "children dropped on resume");
        ui.HoldBackground = true;
        rig.StopAt(2);
        await PanelRig.Eventually(() => ui.HeldCount == 2, what: "both re-expansions created their nodes");
        ui.ReleaseAll();
        await PanelRig.Eventually(() => ui.HeldCount == 2, what: "both row inserts queued");
        ui.HoldBackground = false;
        ui.ReleaseAll();
        await PanelRig.Eventually(() => vm.Tree.Rows.Count == 2 * 102 + 1, what: "both re-expanded");
        Assert.Equal(expected, Layout());
    }

    [AvaloniaFact]
    public async Task Watches_added_straight_to_the_debug_service_show_up_on_the_next_stop()
    {
        using var rig = new PanelRig();
        using var vm = new WatchViewModel(rig.Services);
        rig.Debug.AddWatch("late");
        rig.StopAt(2);
        await PanelRig.Eventually(() => vm.Tree.Rows.Any(r => r.Name == "late" && r.DisplayValue.Length > 0), what: "late watch evaluated");
    }
}
