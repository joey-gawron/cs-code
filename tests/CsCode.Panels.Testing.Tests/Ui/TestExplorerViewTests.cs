using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Testing.Engine;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Ui;

public sealed class TestExplorerViewTests
{
    private static (HostRig Rig, TestExplorerView View, Window Window) Show()
    {
        var rig = new HostRig(AvaloniaUiDispatcher.Instance);
        var tool = new TestExplorerToolWindow(rig.Host);
        var view = (TestExplorerView)tool.CreateView(rig.Services);
        var window = new Window { Width = 420, Height = 520, Content = view };
        window.Show();
        return (rig, view, window);
    }

    internal static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task SettleAsync(HostRig rig)
    {
        for (var i = 0; i < 400; i++)
        {
            Pump();
            if (!rig.Host.Controller.IsDiscovering && !rig.Host.Controller.IsRunning && rig.Host.Controller.Tree.TestCount > 0) break;
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Key(TestExplorerView view, Key key) =>
        view.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = view.List });

    private static string[] Labels(TestExplorerView view) => [.. view.Model.Rows.Select(r => r.ToString().Trim())];

    [AvaloniaFact]
    public async Task Shows_the_tree_expands_classes_and_runs_from_the_toolbar()
    {
        var (rig, view, window) = Show();
        using var _ = rig;
        await SettleAsync(rig);
        Assert.Equal("testing", new TestExplorerToolWindow(rig.Host).Id);
        // the project, its only namespace, the classes (collapsed)
        Assert.Equal(["Calc.Tests [None]", "Calc [None]", "MathTests [None]", "Other [None]"], Labels(view));

        view.Model.Expand(view.Model.Rows[2]);
        Assert.Equal(["Calc.Tests [None]", "Calc [None]", "MathTests [None]", "Adds [None]", "Subtracts [None]", "Other [None]"], Labels(view));

        var runAll = view.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Run All Tests");
        Assert.True(runAll.IsEnabled);
        runAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(rig);
        Assert.Equal(TestOutcome.Failed, view.Model.Rows.Single(r => r.Label == "Subtracts").Outcome);
        Assert.Equal("error", view.Model.Rows.Single(r => r.Label == "Subtracts").StateIcon);
        Assert.Equal("pass-filled", view.Model.Rows.Single(r => r.Label == "Adds").StateIcon);
        Assert.Equal("4 ms", view.Model.Rows.Single(r => r.Label == "Subtracts").DurationText);
        Assert.StartsWith("3 tests: 2 passed, 1 failed", view.Model.Summary);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Filter_selects_by_name_and_state_and_details_show_the_failure()
    {
        var (rig, view, window) = Show();
        using var _ = rig;
        await SettleAsync(rig);
        await rig.Host.Controller.RunAllAsync();
        await SettleAsync(rig);

        view.Model.Filter = "@failed";
        // the groups keep their totals; only the failed test is listed
        Assert.Equal(["Calc.Tests [Failed] 8 ms", "Calc [Failed] 8 ms", "MathTests [Failed] 6 ms", "Subtracts [Failed] 4 ms"], Labels(view));
        view.Model.Filter = "later";
        Assert.Equal("Later [Passed] 2 ms", Labels(view).Last());
        view.Model.Filter = "";
        Assert.Equal(4, view.Model.Rows.Count); // back to the collapsed classes

        view.Model.Expand(view.Model.Rows.Single(r => r.Label == "MathTests"));
        var failed = view.Model.Rows.Single(r => r.Label == "Subtracts");
        view.Model.Selected = failed;
        Assert.Contains("Calc.MathTests.Subtracts  —  failed in 4 ms", view.Model.Details);
        Assert.Contains("Assert.Equal() Failure", view.Model.Details);
        Assert.Contains("Stack trace:", view.Model.Details);
        Assert.Contains("Output:\nprinted", view.Model.Details.Replace("\r", ""));

        var entries = view.MenuEntries(failed).Where(e => !e.IsSeparator).Select(e => e.Header).ToList();
        Assert.Equal(["Run", "Debug", "Go to Test", "Go to Failure", "Copy Name", "Copy Message"], entries);

        await view.Model.GoToAsync(failed, failure: true);
        Assert.Equal((new Uri(HostRig.MathFile), 7), (rig.Navigation.Opened[^1].Uri, rig.Navigation.Opened[^1].Selection!.Value.Start.Line));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keys_move_through_the_tree_and_enter_opens_the_test()
    {
        var (rig, view, window) = Show();
        using var _ = rig;
        await SettleAsync(rig);
        var math = view.Model.Rows.Single(r => r.Label == "MathTests");
        view.Model.Selected = math;
        Pump();
        Assert.True(view.FocusPreferred());
        Assert.Equal("MathTests", view.Model.Selected?.Label);
        Key(view, Avalonia.Input.Key.Right);
        Assert.True(view.Model.Rows.Single(r => r.Label == "MathTests").IsExpanded);
        view.Model.Selected = view.Model.Rows.Single(r => r.Label == "Adds");
        Pump();
        view.FocusPreferred();
        Key(view, Avalonia.Input.Key.Enter);
        Assert.Equal((new Uri(HostRig.MathFile), 4), (rig.Navigation.Opened[^1].Uri, rig.Navigation.Opened[^1].Selection!.Value.Start.Line));
        Key(view, Avalonia.Input.Key.Left); // a test: to its class
        Assert.Equal("MathTests", view.Model.Selected?.Label);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reveal_from_the_editor_expands_selects_and_the_view_releases_its_model()
    {
        var (rig, view, window) = Show();
        using var _ = rig;
        await SettleAsync(rig);
        await rig.Host.Gutter.RevealAsync("Calc.MathTests.Adds", TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Adds", view.Model.Selected?.Label);
        Assert.Contains("testing", rig.Views.Shown);
        Assert.Same(view.Model, rig.Host.LiveExplorer);
        window.Content = null;
        view.Dispose();
        Assert.Null(rig.Host.LiveExplorer);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_empty_workspace_says_what_the_explorer_looks_for()
    {
        var (rig, view, window) = Show();
        using var _ = rig;
        rig.Provider.Projects.Clear();
        await rig.Host.Controller.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Model.IsEmpty);
        Assert.Contains("No tests found", view.Model.EmptyText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_view_and_its_model_are_not_retained_after_the_window_hides()
    {
        var rig = new HostRig(AvaloniaUiDispatcher.Instance);
        using var _ = rig;
        await SettleAsync(rig);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var view = (TestExplorerView)new TestExplorerToolWindow(rig.Host).CreateView(rig.Services);
            var vm = view.Model;
            var window = new Window { Width = 400, Height = 400, Content = view };
            window.Show();
            Pump();
            await rig.Host.Controller.RunAllAsync();
            await SettleAsync(rig);
            vm.Expand(vm.Rows.Single(r => r.Label == "MathTests"));
            vm.Selected = vm.Rows.Single(r => r.Label == "Subtracts");
            Pump();
            window.Content = null;
            window.Close();
            view.Dispose();
            Pump();
            return [view, vm];
        });
    }
}
