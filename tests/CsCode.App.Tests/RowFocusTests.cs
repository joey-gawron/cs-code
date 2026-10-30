using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.ToolWindows.FindResults;
using CsCode.App.ToolWindows.Output;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using Xunit;
using Location = CsCode.Core.Languages.Location;

namespace CsCode.App.Tests;

/// <summary>
/// The tool windows whose rows are replaced while one of them has the keyboard (a diagnostics update, a new search, a batch of output) keep it
/// on their rows: Avalonia recycles the focused row with the rest and leaves the keyboard on nothing, where the arrows, Enter and Ctrl+C went
/// nowhere.
/// </summary>
public class RowFocusTests
{
    private static Window Host(Control view)
    {
        var window = new Window { Width = 700, Height = 400, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ListBox List(Control view) => view.GetVisualDescendants().OfType<ListBox>().Single();

    private static ListBoxItem? Row(Control view, Func<object?, bool> match) =>
        view.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(r => r.IsEffectivelyVisible && match(r.DataContext));

    private static string Focused(Window window) => window.FocusManager?.GetFocusedElement() is { } e ? $"{e} ({(e as Control)?.DataContext})" : "nothing";

    private static Diagnostic D(int line, string msg) => new(TextRange.At(line, 2), msg, DiagnosticSeverity.Error, null, "src");

    private static async Task Settle(ManualUiDispatcher ui, ErrorListViewModel vm)
    {
        await vm.WaitIdleAsync();
        ui.RunPending();
        Dispatcher.UIThread.RunJobs();
    }

    // A diagnostics update swaps the Error List's rows for a new array: the keyboard stays on the row of the diagnostic it was on, still selected.
    [AvaloniaFact]
    public async Task Error_List_keeps_the_keyboard_and_the_selection_on_its_row_when_the_diagnostics_change()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        var a = new Uri("file:///proj/a.cs");
        diags.Set(a, "t", [D(1, "one"), D(2, "two"), D(3, "three")]);
        using var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 5);
        await Settle(ui, vm);
        var view = new ErrorListView(vm);
        var window = Host(view);
        using var close = CsCode.Core.Lifecycle.Disposable.Create(window.Close);
        List(view).SelectedItem = vm.Rows.Single(r => r.Message == "two");
        Dispatcher.UIThread.RunJobs();
        Assert.True(Row(view, d => d is ErrorRow { Message: "two" })?.Focus());

        diags.Set(new Uri("file:///proj/b.cs"), "t", [D(7, "elsewhere")]);
        await Settle(ui, vm);

        Assert.Contains(vm.Rows, r => r.Message == "elsewhere");
        Assert.True(window.FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: ErrorRow { Message: "two" } }, $"the keyboard is on {Focused(window)}");
        Assert.Equal("two", (List(view).SelectedItem as ErrorRow)?.Message);
    }

    private static Location L(string file, int line) => new(new Uri("file:///p/" + file), TextRange.At(line, 0));

    // Each search replaces Find Results' rows (ReplaceAll), and Clear All empties them: the keyboard stays in the window, on a result while
    // there is one.
    [AvaloniaFact]
    public void Find_Results_keeps_the_keyboard_on_its_rows_when_a_search_replaces_them_and_in_the_window_when_they_are_cleared()
    {
        var vm = new FindResultsViewModel();
        vm.Show("first", [L("a.cs", 1), L("a.cs", 5), L("b.cs", 2)]);
        var view = new FindResultsView(vm);
        var window = Host(view);
        using var close = CsCode.Core.Lifecycle.Disposable.Create(window.Close);
        var hit = vm.Rows.First(r => !r.IsHeader);
        List(view).SelectedItem = hit;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Row(view, d => ReferenceEquals(d, hit))?.Focus());

        vm.Show("second", [L("c.cs", 3), L("c.cs", 4)]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: FindResultRow } row && view.IsVisualAncestorOf(row), $"the keyboard is on {Focused(window)}");

        vm.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FocusManager?.GetFocusedElement() is Control c && view.IsVisualAncestorOf(c), $"the keyboard is on {Focused(window)}");
    }

    // A batch of more than a few lines arrives as one bulk change (a Reset), as does trimming the oldest lines: the keyboard on a line of a
    // running build's output stays on a line.
    [AvaloniaFact]
    public void Output_keeps_the_keyboard_on_its_lines_while_output_arrives()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        var ch = svc.GetOrCreate("Build");
        for (var i = 0; i < 5; i++) ch.AppendLine("line " + i);
        ui.RunPending();
        using var vm = new OutputViewModel(svc);
        var view = new OutputView(vm);
        var window = Host(view);
        using var close = CsCode.Core.Lifecycle.Disposable.Create(window.Close);
        Assert.True(Row(view, d => d is "line 2")?.Focus());

        ch.Append(string.Concat(Enumerable.Range(5, 40).Select(i => $"line {i}\n")));
        ui.RunPending();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("line 44", vm.Lines);
        Assert.True(window.FocusManager?.GetFocusedElement() is ListBoxItem { DataContext: string } row && view.IsVisualAncestorOf(row), $"the keyboard is on {Focused(window)}");
    }
}
