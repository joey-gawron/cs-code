using CsCode.App.QuickInput;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>Tab completion in an input box (the Open Remote Folder path box): candidates under the box, Tab to the common prefix, then through them.</summary>
public class InputBoxCompletionTests
{
    private static readonly string[] Folders = ["/home/me/scratch/", "/home/me/src/", "/home/me/srv/", "/home/me/docs/"];

    private static Task<IReadOnlyList<string>> Candidates(string text, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Folders.Where(f => f.StartsWith(text, StringComparison.Ordinal))]);

    private static void Settle(ManualUiDispatcher ui, QuickInputViewModel vm, Func<bool> done)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!done() && Environment.TickCount64 < deadline) { ui.RunPending(); Thread.Sleep(5); }
        Assert.True(done(), "completions did not arrive");
    }

    private static (QuickInputViewModel Vm, ManualUiDispatcher Ui, Task<string?> Result) Open(string initial, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? complete)
    {
        var ui = new ManualUiDispatcher();
        var vm = new QuickInputViewModel(ui);
        var result = vm.InputAsync(new InputBoxOptions("Open Remote Folder", Value: initial, Complete: complete));
        return (vm, ui, result);
    }

    [Fact]
    public void Candidates_are_listed_under_the_box_as_the_user_types_and_the_list_is_hidden_without_any()
    {
        var (vm, ui, _) = Open("/home/me/", Candidates);
        Settle(ui, vm, () => vm.Items.Count == 4);
        Assert.True(vm.ShowList);
        Assert.Equal(-1, vm.SelectedIndex); // nothing highlighted: Enter keeps the typed text
        vm.Text = "/home/me/sr";
        Settle(ui, vm, () => vm.Items.Count == 2);
        Assert.Equal(["src/", "srv/"], vm.Items.Select(i => i.Label));
        vm.Text = "/nowhere/";
        Settle(ui, vm, () => vm.Items.Count == 0);
        Assert.False(vm.ShowList);
    }

    [Fact]
    public void Tab_extends_to_the_common_prefix_then_highlights_and_takes_a_candidate()
    {
        var (vm, ui, _) = Open("/home/me/s", Candidates);
        Settle(ui, vm, () => vm.Items.Count == 3);
        Assert.True(vm.TryComplete());
        Assert.Equal("/home/me/s", vm.Text); // scratch, src, srv share only "s": nothing to add
        Assert.Equal(0, vm.SelectedIndex);   // so the first is highlighted
        vm.MoveSelection(+1);
        Assert.True(vm.TryComplete());
        Assert.Equal("/home/me/src/", vm.Text); // Tab takes the highlighted one
        Settle(ui, vm, () => vm.Items.Count == 1 && vm.SelectedIndex == -1);

        vm.Text = "/home/me/sr";
        Settle(ui, vm, () => vm.Items.Count == 2);
        Assert.True(vm.TryComplete());
        Assert.Equal("/home/me/sr", vm.Text); // src/ and srv/ only differ after "sr"
        vm.Text = "/home/me/d";
        Settle(ui, vm, () => vm.Items.Count == 1);
        Assert.True(vm.TryComplete());
        Assert.Equal("/home/me/docs/", vm.Text); // a single candidate completes fully
    }

    [Fact]
    public async Task Enter_accepts_the_typed_text_not_a_candidate_and_clicking_one_replaces_the_text()
    {
        var (vm, ui, result) = Open("/home/me/s", Candidates);
        Settle(ui, vm, () => vm.Items.Count == 3);
        vm.MoveSelection(+1);
        vm.Accept();
        Assert.Equal("/home/me/s", await result);

        var (vm2, ui2, result2) = Open("/home/me/s", Candidates);
        Settle(ui2, vm2, () => vm2.Items.Count == 3);
        vm2.ApplyCompletion(vm2.Items.Single(i => i.Label == "scratch/"));
        Assert.Equal("/home/me/scratch/", vm2.Text);
        vm2.Accept();
        Assert.Equal("/home/me/scratch/", await result2);
    }

    [Fact]
    public async Task A_box_without_completion_and_a_failing_source_behave_as_before()
    {
        var (plain, _, plainResult) = Open("x", null);
        Assert.False(plain.TryComplete()); // Tab keeps moving the highlight
        Assert.False(plain.ShowList);
        plain.Accept();
        Assert.Equal("x", await plainResult);

        var (broken, ui, brokenResult) = Open("/a", (_, _) => throw new IOException("host gone"));
        Thread.Sleep(300);
        ui.RunPending();
        Assert.Empty(broken.Items);
        Assert.True(broken.TryComplete()); // nothing to complete, still the box's Tab
        broken.Accept();
        Assert.Equal("/a", await brokenResult);
    }

    [Fact]
    public void The_common_prefix_of_candidates()
    {
        Assert.Equal("/home/me/s", QuickInputViewModel.CommonPrefix(["/home/me/scratch/", "/home/me/src/"]));
        Assert.Equal("/a/", QuickInputViewModel.CommonPrefix(["/a/"]));
        Assert.Equal("", QuickInputViewModel.CommonPrefix(["/a", "b"]));
    }
}
