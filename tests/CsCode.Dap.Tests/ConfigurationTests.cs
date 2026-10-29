using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

public class LaunchJsonParsingTests
{
    private static readonly Uri Folder = new("file:///w/");

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        var file = LaunchJson.Parse("""
            {
              // line comment
              "version": "0.2.0",
              /* block
                 comment */
              "configurations": [
                { "name": "A", "type": "coreclr", "request": "launch", "program": "x.dll", /* inline */ },
                { "name": "B", "type": "node", "request": "attach", "port": 9229, },
              ],
            }
            """, Folder, "launch.json");
        Assert.Equal(["A", "B"], file.Configurations.Select(c => c.Name));
        Assert.Equal("x.dll", file.Configurations[0].GetString("program"));
        Assert.True(file.Configurations[1].IsAttach);
        Assert.Equal(Folder, file.Configurations[0].WorkspaceFolder);
    }

    [Fact]
    public void Whole_raw_object_is_preserved_in_Properties()
    {
        var file = LaunchJson.Parse("""{"configurations":[{"name":"A","type":"t","request":"launch","env":{"X":"1"},"args":["a","b"],"nested":{"deep":[1,2,{"k":true}]}}]}""", Folder, "l");
        var p = file.Configurations[0].Properties;
        Assert.Equal("1", p["env"].GetProperty("X").GetString());
        Assert.Equal(2, p["args"].GetArrayLength());
        Assert.True(p["nested"].GetProperty("deep")[2].GetProperty("k").GetBoolean());
    }

    [Fact]
    public void Platform_blocks_are_merged_for_the_current_os_and_removed()
    {
        var file = LaunchJson.Parse("""
            {"configurations":[{
              "name":"A","type":"t","request":"launch","program":"base","env":{"A":"1","B":"2"},
              "windows":{"program":"win","env":{"B":"w"}},
              "linux":{"program":"lin","env":{"B":"l"}},
              "osx":{"program":"mac","env":{"B":"m"}}
            }]}
            """, Folder, "l");
        var c = file.Configurations[0];
        var (expectedProgram, expectedB) = OperatingSystem.IsWindows() ? ("win", "w") : OperatingSystem.IsMacOS() ? ("mac", "m") : ("lin", "l");
        Assert.Equal(expectedProgram, c.GetString("program"));
        Assert.Equal(expectedB, c.Properties["env"].GetProperty("B").GetString());
        Assert.Equal("1", c.Properties["env"].GetProperty("A").GetString()); // deep merge keeps untouched keys
        Assert.DoesNotContain("windows", c.Properties.Keys);
        Assert.DoesNotContain("linux", c.Properties.Keys);
        Assert.DoesNotContain("osx", c.Properties.Keys);
    }

    [Fact]
    public void Compounds_are_parsed_with_members_stopAll_and_preLaunchTask()
    {
        var file = LaunchJson.Parse("""
            {"configurations":[{"name":"Server","type":"t","request":"launch"},{"name":"Client","type":"t","request":"launch"}],
             "compounds":[{"name":"Both","configurations":["Server",{"name":"Client","folder":"${workspaceFolder}"}],"stopAll":true,"preLaunchTask":"build"}]}
            """, Folder, "l");
        var compound = Assert.Single(file.Compounds);
        Assert.Equal(LaunchJson.CompoundType, compound.Type);
        Assert.Equal(["Server", "Client"], LaunchJson.CompoundMembers(compound));
        Assert.True(LaunchJson.CompoundStopsAll(compound));
        Assert.Equal("build", compound.PreLaunchTask);
        Assert.Equal(["Server", "Client", "Both"], file.All.Select(c => c.Name));
    }

    [Fact]
    public void Inputs_are_collected_by_id()
    {
        var file = LaunchJson.Parse("""{"inputs":[{"id":"pid","type":"promptString","description":"Which?"},{"id":"pick","type":"pickString","options":["a","b"]}],"configurations":[]}""", Folder, "l");
        Assert.Equal(["pick", "pid"], file.Inputs.Keys.Order());
        Assert.Equal("Which?", file.Inputs["pid"].GetProperty("description").GetString());
    }

    [Fact]
    public void Configuration_defaults_follow_FromJson_rules()
    {
        var file = LaunchJson.Parse("""{"configurations":[{"type":"coreclr"},{"name":"N","type":"coreclr","request":"attach","preLaunchTask":"b","postDebugTask":"p"}]}""", Folder, "l");
        Assert.Equal(("coreclr", "launch", "coreclr"), (file.Configurations[0].Type, file.Configurations[0].Request, file.Configurations[0].Name));
        Assert.Equal(("b", "p"), (file.Configurations[1].PreLaunchTask, file.Configurations[1].PostDebugTask));
    }

    [Fact]
    public void Invalid_json_reports_the_file_and_line()
    {
        var ex = Assert.Throws<LaunchJsonException>(() => LaunchJson.Parse("{\n  \"configurations\": [\n    { oops }\n  ]\n}", Folder, "/w/.cscode/launch.json"));
        Assert.Contains("/w/.cscode/launch.json", ex.Message);
        Assert.Contains("line", ex.Message);
    }

    [Fact]
    public void A_non_object_root_is_rejected()
    {
        Assert.Throws<LaunchJsonException>(() => LaunchJson.Parse("[1,2,3]", Folder, "l"));
    }

    [Fact]
    public void Missing_sections_are_just_empty()
    {
        var file = LaunchJson.Parse("""{"version":"0.2.0"}""", Folder, "l");
        Assert.Empty(file.Configurations);
        Assert.Empty(file.Compounds);
        Assert.Empty(file.Inputs);
    }

    [Fact]
    public void Rendered_template_has_the_vscode_layout_and_parses_back()
    {
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { name = "Launch", type = "coreclr", request = "launch", program = "${workspaceFolder}/bin/x.dll", args = new string[0] }), Folder);
        var text = LaunchJson.Render([cfg]);
        Assert.StartsWith("{\n    // Use IntelliSense to learn about possible attributes.", text);
        Assert.Contains("\"version\": \"0.2.0\",", text);
        Assert.Contains("linkid=830387", text);
        var back = LaunchJson.Parse(text, Folder, "l");
        Assert.Equal("Launch", Assert.Single(back.Configurations).Name);
        Assert.Equal("${workspaceFolder}/bin/x.dll", back.Configurations[0].GetString("program"));
    }
}

public class VariableResolverTests
{
    private static VariableContext Ctx(Action<VariableContextBuilder>? tweak = null)
    {
        var b = new VariableContextBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    private sealed class VariableContextBuilder
    {
        public string? Folder = "/home/u/proj", File = "/home/u/proj/src/Program.cs";
        public int? Line = 42;
        public Dictionary<string, string> Env = new() { ["HOME_X"] = "/home/x", ["EMPTY"] = "" };
        public Dictionary<string, string> Config = new() { ["my.key"] = "cfg-value", ["my.path"] = "${workspaceFolder}/out" };
        public Func<string, CancellationToken, ValueTask<string?>>? Command;
        public Func<string, CancellationToken, ValueTask<string?>>? Input;
        public Func<string?>? Selected = () => "sel";
        public Func<CancellationToken, ValueTask<string?>>? DefaultBuild;

        public VariableContext Build() => new()
        {
            WorkspaceFolder = Folder,
            ActiveFile = File,
            ActiveLine = Line,
            SelectedText = Selected,
            Cwd = "/cwd",
            UserHome = "/home/u",
            Env = n => Env.GetValueOrDefault(n),
            Config = k => Config.GetValueOrDefault(k),
            Command = Command,
            Input = Input,
            DefaultBuildTask = DefaultBuild,
            WorkspaceFolderByName = n => n == "other" ? "/home/u/other" : null,
        };
    }

    private static async Task<string> R(string text, VariableContext? ctx = null) => await new VariableResolver(ctx ?? Ctx()).ResolveAsync(text, Ct);

    [Theory]
    [InlineData("${workspaceFolder}", "/home/u/proj")]
    [InlineData("${workspaceFolderBasename}", "proj")]
    [InlineData("${file}", "/home/u/proj/src/Program.cs")]
    [InlineData("${fileBasename}", "Program.cs")]
    [InlineData("${fileBasenameNoExtension}", "Program")]
    [InlineData("${fileDirname}", "/home/u/proj/src")]
    [InlineData("${fileExtname}", ".cs")]
    [InlineData("${relativeFile}", "src/Program.cs")]
    [InlineData("${relativeFileDirname}", "src")]
    [InlineData("${cwd}", "/cwd")]
    [InlineData("${lineNumber}", "42")]
    [InlineData("${selectedText}", "sel")]
    [InlineData("${userHome}", "/home/u")]
    [InlineData("${workspaceFolder:other}", "/home/u/other")]
    [InlineData("${fileDirnameBasename}", "src")]
    public async Task Predefined_variables(string input, string expected)
    {
        Assert.Equal(expected, await R(input));
    }

    [Fact]
    public async Task Path_separator_variables()
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        Assert.Equal($"a{sep}b", await R("a${pathSeparator}b"));
        Assert.Equal($"a{sep}b", await R("a${/}b"));
    }

    [Fact]
    public async Task Env_variables_resolve_and_unset_ones_are_empty()
    {
        Assert.Equal("/home/x/bin", await R("${env:HOME_X}/bin"));
        Assert.Equal("[]", await R("[${env:NOT_SET_ANYWHERE}]"));
        Assert.Equal("[]", await R("[${env:EMPTY}]"));
    }

    [Fact]
    public async Task Config_variables_resolve_and_may_contain_variables_themselves()
    {
        Assert.Equal("cfg-value", await R("${config:my.key}"));
        Assert.Equal("/home/u/proj/out", await R("${config:my.path}"));
    }

    [Fact]
    public async Task Undefined_config_is_an_error_naming_the_setting()
    {
        var ex = await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${config:nope.key}"));
        Assert.Contains("nope.key", ex.Message);
    }

    [Fact]
    public async Task Self_referencing_config_terminates()
    {
        var ctx = Ctx(b => b.Config["loop"] = "${config:loop}");
        var result = await R("${config:loop}", ctx);
        Assert.Contains("${config:loop}", result); // depth-limited, not infinite
    }

    [Fact]
    public async Task Several_variables_in_one_string_and_nested_in_text()
    {
        Assert.Equal("/home/u/proj/bin/Debug/Program.dll:42", await R("${workspaceFolder}/bin/Debug/${fileBasenameNoExtension}.dll:${lineNumber}"));
    }

    [Fact]
    public async Task Unknown_variables_are_left_verbatim()
    {
        Assert.Equal("${somethingElse} and ${3} and ${a:b}", await R("${somethingElse} and ${3} and ${a:b}"));
    }

    [Fact]
    public async Task Malformed_references_are_literals()
    {
        Assert.Equal("cost: $5 ${unterminated", await R("cost: $5 ${unterminated"));
        Assert.Equal("$", await R("$"));
        Assert.Equal("plain", await R("plain"));
    }

    [Fact]
    public async Task File_variables_without_an_open_file_are_errors_not_silent_blanks()
    {
        var ctx = Ctx(b => b.File = null);
        foreach (var v in new[] { "${file}", "${fileBasename}", "${relativeFile}", "${fileDirname}" })
        {
            var ex = await Assert.ThrowsAsync<VariableResolutionException>(async () => await R(v, ctx));
            Assert.Contains("no file is open", ex.Message);
        }
    }

    [Fact]
    public async Task Workspace_variables_without_a_folder_are_errors()
    {
        var ctx = Ctx(b => b.Folder = null);
        await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${workspaceFolder}", ctx));
        await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${workspaceFolder:missing}"));
    }

    [Fact]
    public async Task LineNumber_without_an_editor_is_an_error_but_selectedText_is_empty()
    {
        var ctx = Ctx(b => { b.Line = null; b.Selected = null; });
        await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${lineNumber}", ctx));
        Assert.Equal("", await R("${selectedText}", ctx));
    }

    [Fact]
    public async Task Command_variables_run_the_command_and_unknown_commands_fail_clearly()
    {
        var ctx = Ctx(b => b.Command = (id, _) => id == "pickProcess" ? ValueTask.FromResult<string?>("4242") : throw new CommandNotFoundException(id));
        Assert.Equal("pid=4242", await R("pid=${command:pickProcess}", ctx));
        var ex = await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${command:ghost}", ctx));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task A_command_that_returns_nothing_cancels_the_resolution()
    {
        var ctx = Ctx(b => b.Command = (_, _) => ValueTask.FromResult<string?>(null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await R("${command:x}", ctx));
    }

    [Fact]
    public async Task Inputs_are_asked_once_per_pass_and_cancel_aborts()
    {
        var asked = 0;
        var ctx = Ctx(b => b.Input = (id, _) => { asked++; return ValueTask.FromResult<string?>("v-" + id); });
        var resolver = new VariableResolver(ctx);
        Assert.Equal("v-a v-a v-b", await resolver.ResolveAsync("${input:a} ${input:a} ${input:b}", Ct));
        Assert.Equal(2, asked);

        var cancelled = Ctx(b => b.Input = (_, _) => ValueTask.FromResult<string?>(null));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await R("${input:a}", cancelled));
    }

    [Fact]
    public async Task DefaultBuildTask_resolves_or_fails_clearly()
    {
        var ok = Ctx(b => b.DefaultBuild = _ => ValueTask.FromResult<string?>("build"));
        Assert.Equal("build", await R("${defaultBuildTask}", ok));
        await Assert.ThrowsAsync<VariableResolutionException>(async () => await R("${defaultBuildTask}"));
    }

    [Fact]
    public async Task Resolution_recurses_through_objects_and_arrays_but_leaves_other_values_alone()
    {
        var node = JsonNode.Parse("""
            {"program":"${workspaceFolder}/a.dll","args":["${fileBasename}","literal",5,true,null],
             "env":{"P":"${env:HOME_X}","deep":{"list":[{"x":"${lineNumber}"}]}},"${workspaceFolder}":"keys are not substituted"}
            """);
        var resolved = (JsonObject)(await new VariableResolver(Ctx()).ResolveAsync(node, Ct))!;
        Assert.Equal("/home/u/proj/a.dll", (string?)resolved["program"]);
        Assert.Equal("Program.cs", (string?)resolved["args"]![0]);
        Assert.Equal(5, (int?)resolved["args"]![2]);
        Assert.True((bool?)resolved["args"]![3]);
        Assert.Null(resolved["args"]![4]);
        Assert.Equal("/home/x", (string?)resolved["env"]!["P"]);
        Assert.Equal("42", (string?)resolved["env"]!["deep"]!["list"]![0]!["x"]);
        Assert.Equal("keys are not substituted", (string?)resolved["${workspaceFolder}"]);
        Assert.NotSame(node, resolved); // input untouched
        Assert.Equal("${workspaceFolder}/a.dll", (string?)node!["program"]);
    }

    [Fact]
    public void Platform_merge_is_a_deep_merge_that_replaces_scalars_and_arrays()
    {
        var json = """{"a":1,"o":{"x":1,"y":2},"arr":[1,2],"PLATFORM":{"a":2,"o":{"y":9},"arr":[3]},"other":{"a":99}}""".Replace("PLATFORM", PlatformName());
        var merged = VariableResolver.MergePlatformBlock((JsonObject)JsonNode.Parse(json)!);
        Assert.Equal(2, (int?)merged["a"]);
        Assert.Equal(1, (int?)merged["o"]!["x"]);
        Assert.Equal(9, (int?)merged["o"]!["y"]);
        Assert.Single(merged["arr"]!.AsArray());
        Assert.False(merged.ContainsKey(PlatformName()));
    }

    private static string PlatformName() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
}

public sealed class ConfigServiceFixture : IAsyncDisposable
{
    public TempDirectory Dir { get; } = new("dapcfg-");
    public DebugLane Lane { get; } = new("cfg-lane");
    public FakeWorkspace Workspace { get; }
    public FakeStateStorage State { get; } = new();
    public TestConfigurationService Settings { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public TestCommandService Commands { get; } = new();
    public DebugConfigurationService Service { get; private set; } = null!;
    public Uri Folder => Dir.Uri;

    public ConfigServiceFixture() => Workspace = new FakeWorkspace(Dir.Uri);

    public DapPlatformBuilder Platform => new(this);

    public sealed class DapPlatformBuilder(ConfigServiceFixture f)
    {
        public DapPlatform Build(INavigationService? nav = null, IActiveSelectionProvider? selection = null) => new()
        {
            Workspace = f.Workspace, Configuration = f.Settings, State = f.State, Window = f.Window, Commands = f.Commands, Navigation = nav, Selection = selection,
        };
    }

    public DebugConfigurationService Create(INavigationService? nav = null, IActiveSelectionProvider? selection = null, Func<CsCode.Core.Tasks.ITaskService?>? tasks = null)
    {
        Service = new DebugConfigurationService(Lane, Platform.Build(nav, selection), tasks);
        return Service;
    }

    public async ValueTask DisposeAsync()
    {
        if (Service is not null) await Service.DisposeAsync();
        Lane.Dispose();
        Dir.Dispose();
    }
}

public class DebugConfigurationServiceTests
{
    private const string Launch = """
        {"version":"0.2.0","configurations":[
          {"name":"A","type":"coreclr","request":"launch","program":"${workspaceFolder}/a.dll"},
          {"name":"B","type":"coreclr","request":"attach","processId":"${input:pid}"}],
         "compounds":[{"name":"Both","configurations":["A","B"]}],
         "inputs":[{"id":"pid","type":"promptString","description":"PID?"}]}
        """;

    [Fact]
    public async Task Cscode_launch_json_is_preferred_over_vscode()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".vscode/launch.json", """{"configurations":[{"name":"FromVscode","type":"t","request":"launch"}]}""");
        f.Dir.WriteFile(".cscode/launch.json", """{"configurations":[{"name":"FromCscode","type":"t","request":"launch"}]}""");
        var svc = f.Create();
        Assert.Equal(["FromCscode"], (await svc.GetLaunchConfigurationsAsync(null, Ct)).Select(c => c.Name));
    }

    [Fact]
    public async Task Falls_back_to_vscode_launch_json()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".vscode/launch.json", """{"configurations":[{"name":"FromVscode","type":"t","request":"launch"}]}""");
        Assert.Equal(["FromVscode"], (await f.Create().GetLaunchConfigurationsAsync(null, Ct)).Select(c => c.Name));
    }

    [Fact]
    public async Task No_launch_json_means_no_configurations()
    {
        await using var f = new ConfigServiceFixture();
        Assert.Empty(await f.Create().GetLaunchConfigurationsAsync(null, Ct));
    }

    [Fact]
    public async Task Configurations_and_compounds_are_listed_with_their_folder()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        var all = await f.Create().GetLaunchConfigurationsAsync(null, Ct);
        Assert.Equal(["A", "B", "Both"], all.Select(c => c.Name));
        Assert.All(all, c => Assert.Equal(f.Folder, c.WorkspaceFolder));
    }

    [Fact]
    public async Task A_broken_launch_json_yields_no_configurations_and_one_error_message()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", "{ this is not json");
        var svc = f.Create();
        Assert.Empty(await svc.GetLaunchConfigurationsAsync(null, Ct));
        Assert.Empty(await svc.GetLaunchConfigurationsAsync(null, Ct)); // cached: not re-reported
        Assert.NotNull(svc.LastError);
        await SessionHarness.Eventually(() => f.Window.Messages.Count >= 1);
        Assert.Single(f.Window.Messages);
        Assert.Equal(MessageSeverity.Error, f.Window.Messages[0].Severity);
    }

    [Fact]
    public async Task Changes_to_launch_json_are_picked_up_via_the_watcher_and_announced()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", """{"configurations":[{"name":"One","type":"t","request":"launch"}]}""");
        var svc = f.Create();
        Assert.Single(await svc.GetLaunchConfigurationsAsync(null, Ct));
        var changed = 0;
        svc.ConfigurationsChanged.Subscribe(_ => Interlocked.Increment(ref changed));

        f.Dir.WriteFile(".cscode/launch.json", """{"configurations":[{"name":"One","type":"t","request":"launch"},{"name":"Two","type":"t","request":"launch"}]}""");
        f.Workspace.FakeWatcher.Raise(new FileChange(new Uri(f.Folder, ".cscode/launch.json"), FileChangeKind.Changed));
        await SessionHarness.Eventually(() => changed == 1);
        Assert.Equal(["One", "Two"], (await svc.GetLaunchConfigurationsAsync(null, Ct)).Select(c => c.Name));
    }

    [Fact]
    public async Task Unrelated_file_changes_do_not_invalidate()
    {
        await using var f = new ConfigServiceFixture();
        var svc = f.Create();
        var changed = 0;
        svc.ConfigurationsChanged.Subscribe(_ => Interlocked.Increment(ref changed));
        f.Workspace.FakeWatcher.Raise(new FileChange(new Uri(f.Folder, "src/Program.cs"), FileChangeKind.Changed));
        await Task.Delay(150, Ct);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Watches_are_released_on_dispose()
    {
        var f = new ConfigServiceFixture();
        f.Create();
        await SessionHarness.Eventually(() => f.Workspace.FakeWatcher.ActiveWatches > 0);
        await f.DisposeAsync();
        Assert.Equal(0, f.Workspace.FakeWatcher.ActiveWatches);
    }

    [Fact]
    public async Task The_first_configuration_is_selected_by_default_and_changes_are_persisted_and_announced()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        var svc = f.Create();
        var seen = new List<string?>();
        svc.SelectedChanged.Subscribe(c => { lock (seen) seen.Add(c?.Name); });
        var all = await svc.GetLaunchConfigurationsAsync(null, Ct);
        Assert.Equal("A", svc.Selected!.Name);
        svc.Selected = all[1];
        Assert.Equal("B", svc.Selected!.Name);
        lock (seen) Assert.Equal(["A", "B"], seen);
        Assert.Contains("\"B\"", f.State.Raw("debug.selectedConfiguration"));
    }

    [Fact]
    public async Task Selection_is_restored_from_the_workspace_state()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        f.State.Set("debug.selectedConfiguration", new PersistedSelectionProbe("B", null), StorageScope.Workspace);
        var svc = f.Create();
        await svc.GetLaunchConfigurationsAsync(null, Ct);
        Assert.Equal("B", svc.Selected!.Name);
    }

    [Fact]
    public async Task A_selection_that_no_longer_exists_falls_back_to_the_first()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        var svc = f.Create();
        var all = await svc.GetLaunchConfigurationsAsync(null, Ct);
        svc.Selected = all[1];
        f.Dir.WriteFile(".cscode/launch.json", """{"configurations":[{"name":"Only","type":"t","request":"launch"}]}""");
        f.Workspace.FakeWatcher.Raise(new FileChange(new Uri(f.Folder, ".cscode/launch.json"), FileChangeKind.Changed));
        await SessionHarness.Eventually(() => true);
        await Task.Delay(100, Ct);
        await svc.GetLaunchConfigurationsAsync(null, Ct);
        Assert.Equal("Only", svc.Selected!.Name);
    }

    [Fact]
    public async Task Resolving_variables_substitutes_everything_recursively_and_keeps_the_rest()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        f.Settings.Set("my.key", "from-settings");
        var svc = f.Create();
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new
        {
            name = "X", type = "coreclr", request = "launch", program = "${workspaceFolder}/bin/x.dll",
            args = new[] { "${config:my.key}", "plain" }, env = new { A = "${workspaceFolder}" }, justMyCode = false,
        }), f.Folder) with { NoDebug = true };
        var resolved = await svc.ResolveVariablesAsync(cfg, Ct);
        var root = f.Folder.LocalPath.TrimEnd('/');
        Assert.Equal($"{root}/bin/x.dll", resolved.GetString("program"));
        Assert.Equal("from-settings", resolved.Properties["args"][0].GetString());
        Assert.Equal(root, resolved.Properties["env"].GetProperty("A").GetString());
        Assert.False(resolved.Properties["justMyCode"].GetBoolean());
        Assert.True(resolved.NoDebug);
        Assert.Equal(f.Folder, resolved.WorkspaceFolder);
        Assert.Contains("${workspaceFolder}", cfg.GetString("program")); // original untouched
    }

    [Fact]
    public async Task Inputs_defined_in_launch_json_prompt_through_the_window()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", Launch);
        f.Window.InputResponses.Enqueue("1234");
        var svc = f.Create();
        var cfg = (await svc.GetLaunchConfigurationsAsync(null, Ct)).First(c => c.Name == "B");
        var resolved = await svc.ResolveVariablesAsync(cfg, Ct);
        Assert.Equal("1234", resolved.GetString("processId"));
    }

    [Fact]
    public async Task Pick_string_and_command_inputs_work()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/launch.json", """
            {"configurations":[],"inputs":[
              {"id":"which","type":"pickString","description":"Pick","options":["first",{"label":"Second one","value":"second"}]},
              {"id":"viaCmd","type":"command","command":"my.cmd"}]}
            """);
        f.Window.QuickPickResponses.Enqueue(1);
        f.Commands.Register("my.cmd", _ => ValueTask.FromResult<object?>("cmd-result"));
        var svc = f.Create();
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "t", request = "launch", name = "N", a = "${input:which}", b = "${input:viaCmd}" }), f.Folder);
        var resolved = await svc.ResolveVariablesAsync(cfg, Ct);
        Assert.Equal(("second", "cmd-result"), (resolved.GetString("a"), resolved.GetString("b")));
    }

    [Fact]
    public async Task An_undefined_input_is_a_clear_error()
    {
        await using var f = new ConfigServiceFixture();
        var svc = f.Create();
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "t", request = "launch", name = "N", a = "${input:ghost}" }), f.Folder);
        var ex = await Assert.ThrowsAsync<VariableResolutionException>(async () => await svc.ResolveVariablesAsync(cfg, Ct));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public async Task Command_variables_go_through_the_command_service()
    {
        await using var f = new ConfigServiceFixture();
        f.Commands.Register("pickProcess", _ => ValueTask.FromResult<object?>("777"));
        var svc = f.Create();
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "t", request = "attach", name = "N", processId = "${command:pickProcess}" }), f.Folder);
        Assert.Equal("777", (await svc.ResolveVariablesAsync(cfg, Ct)).GetString("processId"));
    }

    [Fact]
    public async Task File_variables_use_the_active_document_and_selection_provider()
    {
        await using var f = new ConfigServiceFixture();
        var docs = new CsCode.Core.Text.DocumentManager();
        var doc = new FakeEditorDoc(new Uri(f.Folder, "src/Foo.cs"));
        var nav = new RecordingNavigationService(docs) { ActiveDocument = doc, ActiveCaret = new CsCode.Core.Text.TextPosition(6, 0) };
        var svc = f.Create(nav, new FixedSelection("picked"));
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "t", request = "launch", name = "N", a = "${fileBasename}", b = "${lineNumber}", c = "${selectedText}", d = "${relativeFile}" }), f.Folder);
        var r = await svc.ResolveVariablesAsync(cfg, Ct);
        Assert.Equal(("Foo.cs", "7", "picked", Path.Combine("src", "Foo.cs")), (r.GetString("a"), r.GetString("b"), r.GetString("c"), r.GetString("d")));
    }

    [Fact]
    public async Task DefaultBuildTask_asks_the_task_service()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".cscode/tasks.json", """{"tasks":[{"label":"compile","type":"shell","command":"true","group":{"kind":"build","isDefault":true}}]}""");
        var tasks = new CsCode.Dap.Tasks.TaskService(f.Lane, f.Platform.Build());
        var svc = f.Create(tasks: () => tasks);
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { type = "t", request = "launch", name = "N", preLaunchTask = "${defaultBuildTask}" }), f.Folder);
        var r = await svc.ResolveVariablesAsync(cfg, Ct);
        Assert.Equal("compile", r.PreLaunchTask);
    }

    [Fact]
    public async Task Platform_blocks_in_programmatic_configs_are_merged_too()
    {
        await using var f = new ConfigServiceFixture();
        var key = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var obj = new Dictionary<string, object?> { ["type"] = "t", ["request"] = "launch", ["name"] = "N", ["program"] = "base", [key] = new { program = "specific" } };
        var cfg = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(obj), f.Folder);
        Assert.Equal("specific", (await f.Create().ResolveVariablesAsync(cfg, Ct)).GetString("program"));
    }

    [Fact]
    public async Task CreateLaunchJson_writes_the_vscode_layout_from_a_providers_initial_configurations()
    {
        await using var f = new ConfigServiceFixture();
        var svc = f.Create();
        svc.AddProvider("mydbg", new FixedInitialProvider());
        await Task.Delay(50, Ct);
        var uri = await svc.CreateLaunchJsonAsync("mydbg", null, Ct);
        Assert.Equal(new Uri(f.Folder, ".cscode/launch.json"), uri);
        var text = File.ReadAllText(uri.LocalPath);
        Assert.Contains("// Use IntelliSense", text);
        var parsed = LaunchJson.Parse(text, f.Folder, "x");
        Assert.Equal(["Generated launch"], parsed.Configurations.Select(c => c.Name));
        Assert.Equal(["Generated launch"], (await svc.GetLaunchConfigurationsAsync(null, Ct)).Select(c => c.Name)); // cache was invalidated
    }

    [Fact]
    public async Task CreateLaunchJson_never_overwrites_an_existing_file()
    {
        await using var f = new ConfigServiceFixture();
        f.Dir.WriteFile(".vscode/launch.json", """{"configurations":[]}""");
        var svc = f.Create();
        svc.AddProvider("mydbg", new FixedInitialProvider());
        await Task.Delay(50, Ct);
        var uri = await svc.CreateLaunchJsonAsync("mydbg", null, Ct);
        Assert.Equal(new Uri(f.Folder, ".vscode/launch.json"), uri);
        Assert.Equal("""{"configurations":[]}""", File.ReadAllText(uri.LocalPath));
        Assert.False(File.Exists(Path.Combine(f.Dir.Path, ".cscode", "launch.json")));
    }

    [Fact]
    public async Task CreateLaunchJson_uses_initialConfigurations_of_user_debugger_contributions_and_extra_files_are_written_once()
    {
        await using var f = new ConfigServiceFixture();
        var registry = new ContributionRegistry();
        registry.Contribute(ContributionSources.User, "", [new DebuggerContribution("custom", "Custom", Program: "/bin/adapter",
            InitialConfigurations: JsonSerializer.SerializeToElement(new[] { new { name = "Custom launch", type = "custom", request = "launch" } }))]);
        var platform = new DapPlatform { Workspace = f.Workspace, Contributions = registry };
        var svc = new DebugConfigurationService(f.Lane, platform);
        var uri = await svc.CreateLaunchJsonAsync("custom", null, Ct);
        Assert.Equal(["Custom launch"], LaunchJson.Parse(File.ReadAllText(uri.LocalPath), f.Folder, "x").Configurations.Select(c => c.Name));
        await svc.DisposeAsync();
    }

    [Fact]
    public async Task CreateLaunchJson_for_an_unknown_type_fails_clearly()
    {
        await using var f = new ConfigServiceFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await f.Create().CreateLaunchJsonAsync("nothing", null, Ct));
    }

    [Fact]
    public async Task CreateLaunchJson_without_a_folder_fails_clearly()
    {
        var f = new ConfigServiceFixture();
        await f.Workspace.CloseFolderAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await f.Create().CreateLaunchJsonAsync("x", null, Ct));
        await f.DisposeAsync();
    }

    private sealed record PersistedSelectionProbe(string Name, string? Folder);

    private sealed class FixedSelection(string text) : IActiveSelectionProvider
    {
        public string? SelectedText => text;
    }

    private sealed class FixedInitialProvider : IDebugConfigurationProvider
    {
        public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct) => ValueTask.FromResult<DebugConfiguration?>(configuration);

        public ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<DebugConfiguration>>([DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new { name = "Generated launch", type = "mydbg", request = "launch" }), folder)]);
    }

    private sealed class FakeEditorDoc(Uri uri) : CsCode.Core.Text.IEditorDocument
    {
        public Uri Uri => uri;
        public string LanguageId => "csharp";
        public int Version => 1;
        public bool IsDirty => false;
        public int Length => 0;
        public int LineCount => 1;
        public bool IsReadOnly => false;
        public bool IsUntitled => false;
        public string EncodingName => "utf-8";
        public string EndOfLine => "\n";
        public string GetText() => "";
        public string GetText(CsCode.Core.Text.TextRange range) => "";
        public string GetLineText(int line) => "";
        public int OffsetAt(CsCode.Core.Text.TextPosition position) => 0;
        public CsCode.Core.Text.TextPosition PositionAt(int offset) => default;
        public CsCode.Core.Text.TextPosition Validate(CsCode.Core.Text.TextPosition position) => position;
        public bool ApplyEdits(IReadOnlyList<CsCode.Core.Text.TextEdit> edits) => true;
        public void SetLanguageId(string languageId) { }
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public CsCode.Core.Lifecycle.IEventSource<CsCode.Core.Text.TextDocumentChangedEventArgs> Changed => new CsCode.Core.Lifecycle.Emitter<CsCode.Core.Text.TextDocumentChangedEventArgs>();
        public CsCode.Core.Lifecycle.IEventSource<CsCode.Core.Text.ITextDocument> LanguageIdChanged => new CsCode.Core.Lifecycle.Emitter<CsCode.Core.Text.ITextDocument>();
        public CsCode.Core.Lifecycle.IEventSource<CsCode.Core.Text.IEditorDocument> Saved => new CsCode.Core.Lifecycle.Emitter<CsCode.Core.Text.IEditorDocument>();
        public CsCode.Core.Lifecycle.IEventSource<CsCode.Core.Text.IEditorDocument> DirtyChanged => new CsCode.Core.Lifecycle.Emitter<CsCode.Core.Text.IEditorDocument>();
    }
}
