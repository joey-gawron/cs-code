using CsCode.Core.Projects;
using CsCode.Panels.NewProject.ViewModels;
using CsCode.ProjectTemplates.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.NewProject.Tests;

public sealed class NewProjectViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<NewProjectViewModel> ReadyAsync(NewProjectViewModel vm)
    {
        await vm.Initialized.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await vm.OptionsLoaded.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        return vm;
    }

    private static ProjectOption Vcs => ProjectOption.Choice(ProjectOption.VcsOptionId, "Version control", "git", [new("git", "Git"), new("hg", "Mercurial"), new("none", "None")]);

    [Fact]
    public async Task Lists_every_generator_and_disables_one_whose_tool_is_missing()
    {
        using var rig = new Rig();
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0), new FakeGenerator("zig", 60, available: false))));
        Assert.Equal(["empty", "zig"], vm.Generators.Select(g => g.Id));
        var zig = vm.Generators[1];
        Assert.False(zig.IsAvailable);
        Assert.False(zig.IsSelectable);
        Assert.Contains("brew install zig", zig.Detail, StringComparison.Ordinal);
        Assert.True(vm.Generators[0].IsAvailable);
    }

    [Fact]
    public async Task Proposes_a_free_name_in_the_home_folder_and_previews_the_target()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Temp.Combine("untitled"));
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        Assert.Equal(rig.Temp.Path, vm.Location);
        Assert.Equal("untitled1", vm.Name);
        Assert.Equal($"Project will be created in {rig.Temp.Combine("untitled1")}", vm.Preview);
        Assert.True(vm.CanCreate);
    }

    [Fact]
    public async Task The_default_location_setting_wins_and_expands_the_home_folder()
    {
        using var rig = new Rig();
        rig.Configuration.Set(NewProjectHost.DefaultLocationSetting, "~/code");
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        Assert.Equal(Path.Combine(rig.Temp.Path, "code"), vm.Location);
    }

    [Fact]
    public void Without_the_setting_the_parent_of_the_most_recent_folder_is_proposed()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("work", "last"));
        Assert.Equal(temp.Combine("work"), ProjectLocations.DefaultLocation(null, [temp.Combine("gone"), temp.Combine("work", "last") + "/"], "/home/me"));
        Assert.Equal("/home/me", ProjectLocations.DefaultLocation("", [], "/home/me"));
    }

    [Fact]
    public async Task Names_follow_the_generator_and_the_target_must_be_empty()
    {
        using var rig = new Rig();
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        vm.Name = "has space";
        Assert.Equal("No spaces.", vm.NameError);
        Assert.False(vm.CanCreate);
        rig.Temp.WriteFile("taken/file.txt", "x");
        vm.Name = "taken";
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(vm.NameError);
        Assert.Contains("not empty", vm.LocationError, StringComparison.Ordinal);
        Assert.False(vm.CanCreate);
        vm.Location = "relative";
        Assert.Contains("absolute", vm.LocationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_opens_the_folder_here_when_none_is_open_then_the_main_file_and_closes_the_page()
    {
        using var rig = new Rig();
        var generator = new FakeGenerator("empty", 0);
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(generator)));
        vm.Name = "proj";
        vm.CreateGitRepository = false;
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await vm.CreateAsync();
        var target = rig.Temp.Combine("proj");
        Assert.True(File.Exists(Path.Combine(target, "main.txt")));
        Assert.Equal([new Uri(target + Path.DirectorySeparatorChar)], rig.Workspace.Opened);
        Assert.Equal([new Uri(Path.Combine(target, "main.txt"))], rig.Navigation.Opened);
        Assert.Contains(NewProjectHost.ViewId, rig.Views.Closed);
        Assert.Empty(rig.Window.Messages); // nothing to ask: the window had no folder
        Assert.Contains("tool says hello", rig.Output.Text(NewProjectViewModel.OutputChannel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_folder_open_the_user_chooses_a_new_window()
    {
        using var rig = new Rig();
        rig.Workspace.Folders = [new Uri(rig.Temp.Path + "/")];
        IReadOnlyList<object?>? newWindowArgs = null;
        using var _ = rig.Commands.Register(NewProjectViewModel.NewWindowCommand, inv => { newWindowArgs = inv.Args; return ValueTask.FromResult<object?>(null); });
        rig.Window.MessageActionResponses.Enqueue(NewProjectViewModel.OpenInNewWindow);
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        vm.Name = "other";
        vm.CreateGitRepository = false;
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await vm.CreateAsync();
        Assert.Single(rig.Window.Messages);
        Assert.Empty(rig.Workspace.Opened);
        Assert.Equal([rig.Temp.Combine("other"), Path.Combine(rig.Temp.Combine("other"), "main.txt")], newWindowArgs);
        Assert.Empty(rig.Python.Picks); // the new window's own settings pick the interpreter
    }

    [Fact]
    public async Task A_project_opened_here_runs_its_follow_ups_after_the_page_closed()
    {
        using var rig = new Rig();
        var generator = new FakeGenerator(ProjectTemplates.Generators.PythonProjectGenerator.GeneratorId, 0)
        {
            ExtraFiles = [ProjectTemplates.Generators.PythonProjectGenerator.VenvInterpreter],
        };
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(generator)));
        vm.Name = "py";
        vm.CreateGitRepository = false;
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await vm.CreateAsync();
        await vm.FollowedUp.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var target = rig.Temp.Combine("py");
        Assert.Equal(Path.Combine(target, ProjectTemplates.Generators.PythonProjectGenerator.VenvInterpreter), rig.Python.Picks[new Uri(target + Path.DirectorySeparatorChar)]);
        Assert.Contains("== Selected the interpreter 3.14.0 ('.venv': venv)", rig.Output.Text(NewProjectViewModel.OutputChannel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dont_open_leaves_the_page_with_the_created_status()
    {
        using var rig = new Rig();
        rig.Workspace.Folders = [new Uri(rig.Temp.Path + "/")];
        rig.Window.MessageActionResponses.Enqueue(NewProjectViewModel.DoNotOpen);
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        vm.CreateGitRepository = false;
        await vm.CreateAsync();
        Assert.Empty(rig.Workspace.Opened);
        Assert.Empty(rig.Views.Closed);
        Assert.StartsWith("Created ", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_shows_the_error_and_the_output_and_removes_the_folder()
    {
        using var rig = new Rig();
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0) { Fail = true })));
        vm.Name = "broken";
        vm.CreateGitRepository = false;
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await vm.CreateAsync();
        Assert.True(vm.StatusIsError);
        Assert.Contains("code 3", vm.Status, StringComparison.Ordinal);
        Assert.Contains("output", rig.Views.Shown);
        Assert.False(Directory.Exists(rig.Temp.Combine("broken")));
        Assert.False(vm.IsBusy);
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the folder is checked again after the clean-up
        Assert.True(vm.CanCreate); // it can be tried again
    }

    [Fact]
    public async Task Cancel_stops_a_running_creation()
    {
        using var rig = new Rig();
        var generator = new FakeGenerator("empty", 0) { Hold = new TaskCompletionSource() };
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(generator)));
        vm.Name = "slow";
        vm.CreateGitRepository = false;
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var creating = vm.CreateAsync();
        while (generator.Requests.IsEmpty) await Task.Delay(10, Ct);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanCreate);
        vm.Cancel();
        await creating.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("Cancelled.", vm.Status);
        Assert.False(Directory.Exists(rig.Temp.Combine("slow")));
        Assert.Empty(rig.Views.Closed); // Cancel stopped the run; it did not close the page
    }

    [Fact]
    public async Task Cancel_when_idle_closes_the_page()
    {
        using var rig = new Rig();
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0))));
        vm.Cancel();
        Assert.Contains(NewProjectHost.ViewId, rig.Views.Closed);
    }

    [Fact]
    public async Task Remembers_the_generator_and_its_options_for_the_next_page()
    {
        using var rig = new Rig();
        var kind = ProjectOption.Choice("kind", "Type", "bin", [new("bin", "Binary"), new("lib", "Library")]);
        var tests = ProjectOption.Toggle("tests", "Tests", true);
        IProjectGenerator[] generators = [new FakeGenerator("empty", 0), new FakeGenerator("rust", 20, options: [kind, tests])];
        using (var first = await ReadyAsync(new NewProjectViewModel(rig.Services(generators))))
        {
            first.SelectedGenerator = first.Generators[1];
            await first.OptionsLoaded.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            ((ChoiceOptionItem)first.Options[0]).Value = "lib";
            ((ToggleOptionItem)first.Options[1]).IsChecked = false;
            first.CreateGitRepository = false;
            await first.CreateAsync();
        }
        using var second = await ReadyAsync(new NewProjectViewModel(rig.Services(generators)));
        Assert.Equal("rust", second.SelectedGenerator!.Id);
        Assert.Equal("lib", second.Options[0].Value);
        Assert.Equal(ProjectOption.False, second.Options[1].Value);
        Assert.False(second.CreateGitRepository);
    }

    [Fact]
    public async Task A_remembered_generator_whose_tool_is_gone_gives_way_to_one_that_works()
    {
        using var rig = new Rig();
        rig.State.Set(NewProjectViewModel.StateKey, new RememberedChoices { Generator = "zig" });
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("empty", 0), new FakeGenerator("zig", 60, available: false))));
        Assert.Equal("empty", vm.SelectedGenerator!.Id);
    }

    [Fact]
    public async Task The_git_box_and_the_generators_vcs_option_stay_in_step()
    {
        using var rig = new Rig();
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(new FakeGenerator("rust", 0, options: [Vcs]))));
        var vcs = (ChoiceOptionItem)vm.Options.Single();
        Assert.True(vm.CreateGitRepository);
        Assert.Equal("git", vcs.Value);
        vm.CreateGitRepository = false;
        Assert.Equal("none", vcs.Value);
        vcs.Value = "git";
        Assert.True(vm.CreateGitRepository);
        vcs.Value = "hg";
        Assert.False(vm.CreateGitRepository);
    }

    [Fact]
    public async Task The_request_carries_the_options_and_the_git_choice()
    {
        using var rig = new Rig();
        var generator = new FakeGenerator("rust", 0, options: [Vcs, ProjectOption.Toggle("tests", "Tests", false)]);
        using var vm = await ReadyAsync(new NewProjectViewModel(rig.Services(generator)));
        vm.CreateGitRepository = false;
        ((ToggleOptionItem)vm.Options[1]).IsChecked = true;
        vm.Name = "req";
        await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await vm.CreateAsync();
        var request = Assert.Single(generator.Requests);
        Assert.Equal("req", request.Name);
        Assert.False(request.CreateGitRepository);
        Assert.Equal("none", request.Options[ProjectOption.VcsOptionId]);
        Assert.Equal(ProjectOption.True, request.Options["tests"]);
    }

    [Fact]
    public async Task The_view_model_is_not_retained_after_dispose()
    {
        using var rig = new Rig();
        var services = rig.Services(new FakeGenerator("empty", 0), new FakeGenerator("rust", 20, options: [Vcs]));
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var vm = await ReadyAsync(new NewProjectViewModel(services));
            vm.SelectedGenerator = vm.Generators[1];
            await vm.OptionsLoaded.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            vm.Name = "leak-check";
            await vm.TargetChecked.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            vm.Dispose();
            return [vm, .. vm.Options, .. vm.Generators];
        });
    }
}
