using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Panels.NewProject.ViewModels;
using CsCode.Panels.NewProject.Views;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.NewProject.Tests;

/// <summary>The real view in a headless window: keyboard-only use and its lifetime.</summary>
public sealed class NewProjectViewTests
{
    private static async Task SettleAsync(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_keyboard_starts_in_Name_and_Enter_creates_the_project()
    {
        using var rig = new Rig();
        var generator = new FakeGenerator("empty", 0);
        var vm = new NewProjectViewModel(rig.Services(generator));
        var view = new NewProjectView(vm);
        var window = new Window { Content = view, Width = 1100, Height = 700 };
        window.Show();
        await SettleAsync(() => vm.Initialized.IsCompleted && vm.CanCreate);
        Assert.True(vm.CanCreate);

        var focused = window.FocusManager?.GetFocusedElement() as TextBox;
        Assert.NotNull(focused);
        Assert.Equal(vm.Name, focused.Text); // the Name box, its text selected
        window.KeyTextInput("kbd");
        await SettleAsync(() => vm.Name == "kbd" && vm.CanCreate);
        Assert.Equal("kbd", vm.Name);
        vm.CreateGitRepository = false;

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await SettleAsync(() => !generator.Requests.IsEmpty && rig.Views.Closed.Count > 0);
        Assert.Equal("kbd", Assert.Single(generator.Requests).Name);
        Assert.True(File.Exists(rig.Temp.Combine("kbd", "main.txt")));
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task The_generator_list_lists_disabled_generators_and_the_arrows_choose()
    {
        using var rig = new Rig();
        var vm = new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0), new FakeGenerator("rust", 1), new FakeGenerator("zig", 2, available: false)));
        var view = new NewProjectView(vm);
        var window = new Window { Content = view, Width = 1100, Height = 700 };
        window.Show();
        await SettleAsync(() => vm.Initialized.IsCompleted);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        var rows = list.GetRealizedContainers().OfType<ListBoxItem>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.False(rows[2].IsEnabled); // zig: listed, disabled
        rows[0].Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        await SettleAsync(() => vm.SelectedGenerator?.Id == "rust");
        Assert.Equal("rust", vm.SelectedGenerator!.Id);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("rust", vm.SelectedGenerator!.Id); // the disabled one is skipped
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Escape_closes_the_page_when_nothing_runs()
    {
        using var rig = new Rig();
        var vm = new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0)));
        var view = new NewProjectView(vm);
        var window = new Window { Content = view, Width = 1100, Height = 700 };
        window.Show();
        await SettleAsync(() => vm.Initialized.IsCompleted);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(NewProjectHost.ViewId, rig.Views.Closed);
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task The_view_and_its_view_model_are_not_retained_after_the_tab_closes()
    {
        using var rig = new Rig();
        var services = rig.Services(new FakeGenerator("empty", 0));
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var vm = new NewProjectViewModel(services);
            var view = new NewProjectView(vm);
            var window = new Window { Content = view, Width = 1100, Height = 700 };
            window.Show();
            await SettleAsync(() => vm.Initialized.IsCompleted);
            window.Close();
            view.Dispose();
            // A closed headless window is let go at the next render tick (as the Settings page's leak test pumps).
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            return [vm, view];
        });
    }
}
