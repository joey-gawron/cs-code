using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.Settings;
using CsCode.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Search.Tests.Ui;

/// <summary>
/// Integration against the REAL platform pieces that exist in this base: <see cref="LocalFileSystem"/> (IO-lane file system) and
/// <see cref="ConfigurationService"/> (the comment-preserving settings writer). The real WorkspaceService is not in this base yet.
/// </summary>
public class RealPlatformTests : IDisposable
{
    private readonly TempDirectory _home = new("cscode-home-");
    private readonly TempDirectory _work = new("cscode-work-");
    private readonly IoLane _lane = new("test-io", maxThreads: 2);
    private readonly LocalFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly ConfigurationService _config;

    public RealPlatformTests()
    {
        _fs = new LocalFileSystem(_lane);
        _paths = new AppPaths(name => name == AppPaths.HomeEnvironmentVariable ? _home.Path : null);
        _config = new ConfigurationService(_paths, _lane);
    }

    public void Dispose()
    {
        _config.Dispose();
        _lane.Dispose();
        _home.Dispose();
        _work.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_walker_over_the_real_file_system_applies_gitignore_and_streams_results()
    {
        _work.WriteFile(".gitignore", "bin/\n*.log\n");
        _work.WriteFile("src/a.cs", "var needle = 1;");
        _work.WriteFile("src/b.log", "needle");
        _work.WriteFile("bin/out.dll.txt", "needle");
        _work.WriteFile("docs/readme.md", "a needle in docs");
        var ws = new TestWorkspace(_work.Uri) { FileSystem = _fs };
        using var engine = new SearchEngine(ws, null, null, new WalkingFileSource(_fs));
        using var session = engine.Start(new SearchQuery("needle", UseExcludeSettings: false));
        var r = await SearchHarness.Collect(session);
        Assert.Equal(["a.cs", "readme.md"], r.FileNames);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
    }

    [Fact]
    public async Task Replace_reads_and_plans_through_the_real_file_system()
    {
        _work.WriteFile("a.txt", "one foo\r\ntwo foo\r\n");
        var ws = new TestWorkspace(_work.Uri) { FileSystem = _fs };
        using var replace = new ReplaceService(ws, null);
        var plan = await replace.PlanAsync(new SearchQuery("foo"), [new ReplaceTarget(new Uri(_work.Combine("a.txt")))], new ReplaceOptions("bar"), Ct);
        Assert.Equal(2, plan.ReplacementCount);
        var nav = new CsCode.Panels.Search.Tests.Engine.DiskApplyingNavigation(new CsCode.Core.Text.DocumentManager());
        var result = await replace.ApplyAsync(plan, nav, ct: Ct);
        Assert.Equal(1, result.FilesChanged);
        Assert.Equal("one bar\r\ntwo bar\r\n", File.ReadAllText(_work.Combine("a.txt")));
    }

    private ConfigurationProperty[] Props() =>
    [
        new("editor.tabSize", JsonSerializer.SerializeToElement(4), "Tab size", JsonDocument.Parse("""{"type":"integer","minimum":1}""").RootElement.Clone()),
        new("editor.insertSpaces", JsonSerializer.SerializeToElement(true), "Insert spaces", JsonDocument.Parse("""{"type":"boolean"}""").RootElement.Clone()),
        new("editor.wordWrap", JsonSerializer.SerializeToElement("off"), "Word wrap", JsonDocument.Parse("""{"type":"string","enum":["off","on"]}""").RootElement.Clone()),
    ];

    private async Task<(SettingsViewModel Vm, VmFixture Fixture)> OpenSettingsAsync()
    {
        _config.RegisterProperties(Props());
        var f = new VmFixture();
        var services = new CsCode.Panels.Search.ViewModels.SearchServices
        {
            Workspace = new TestWorkspace(_work.Uri) { FileSystem = _fs }, Ui = f.Ui, Engine = f.Harness.Engine, Replace = f.Replace, Configuration = _config,
            Navigation = f.Navigation, Windows = f.Windows, FileSystem = _fs,
        };
        var vm = new SettingsViewModel(services, debounceMs: 20);
        await vm.RefreshAsync();
        return (vm, f);
    }

    [Fact]
    public async Task Edits_go_through_the_real_comment_preserving_writer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_config.UserSettingsPath)!);
        await File.WriteAllTextAsync(_config.UserSettingsPath, "{\n  // my favourite size\n  \"editor.tabSize\": 2,\n  \"other.key\": \"keep me\"\n}\n", Ct);
        var (vm, f) = await OpenSettingsAsync();
        using (f)
        using (vm)
        {
            await _config.UpdateAsync("editor.tabSize", 2, ConfigurationTarget.User, Ct);
            await vm.RefreshAsync();
            var tab = (NumberSettingViewModel)vm.AllItems.Single(i => i.Key == "editor.tabSize");
            Assert.True(tab.IsModified);                       // set in the user file
            Assert.Equal("2", tab.Text);

            tab.Text = "8";
            ((BoolSettingViewModel)vm.AllItems.Single(i => i.Key == "editor.insertSpaces")).Value = false;
            await vm.FlushAsync();
            await vm.WaitIdleAsync();
            var text = await File.ReadAllTextAsync(_config.UserSettingsPath, Ct);
            Assert.Contains("// my favourite size", text);       // comments survive
            Assert.Contains("\"other.key\": \"keep me\"", text);  // other keys untouched
            Assert.Contains("\"editor.tabSize\": 8", text);
            Assert.Contains("\"editor.insertSpaces\": false", text);
            Assert.Equal(8, _config.Get("editor.tabSize", 0));
        }
    }

    [Fact]
    public async Task Reset_removes_the_key_from_the_real_settings_file()
    {
        var (vm, f) = await OpenSettingsAsync();
        using (f)
        using (vm)
        {
            var wrap = (EnumSettingViewModel)vm.AllItems.Single(i => i.Key == "editor.wordWrap");
            wrap.Selected = wrap.Choices[1];
            await vm.WaitIdleAsync();
            await vm.RefreshAsync();
            Assert.True(wrap.IsModified);
            Assert.Contains("\"editor.wordWrap\": \"on\"", await File.ReadAllTextAsync(_config.UserSettingsPath, Ct));

            await vm.ResetAsync(wrap);
            Assert.DoesNotContain("editor.wordWrap", await File.ReadAllTextAsync(_config.UserSettingsPath, Ct));
            Assert.False(wrap.IsModified);
            Assert.Equal("off", wrap.Selected?.Value);
        }
    }

    [Fact]
    public async Task The_real_changed_event_arrives_off_the_ui_thread_and_is_marshalled()
    {
        var (vm, f) = await OpenSettingsAsync();
        using (f)
        using (vm)
        {
            var tab = (NumberSettingViewModel)vm.AllItems.Single(i => i.Key == "editor.tabSize");
            await _config.UpdateAsync("editor.tabSize", 6, ConfigurationTarget.User, Ct);   // someone else (e.g. keybinding command) changes it
            await f.Ui.PumpUntilAsync(() => tab.Text == "6");
            Assert.True(tab.IsModified);
        }
    }

    [Fact]
    public async Task Edit_in_settings_json_opens_the_real_file_at_the_key()
    {
        var (vm, f) = await OpenSettingsAsync();
        using (f)
        using (vm)
        {
            await vm.EditInJsonAsync(vm.AllItems.Single(i => i.Key == "editor.tabSize"));
            var (uri, selection) = Assert.Single(f.Navigation.Opened);
            Assert.True(File.Exists(uri.LocalPath));
            var text = await File.ReadAllTextAsync(uri.LocalPath, Ct);
            Assert.Contains("\"editor.tabSize\": 4", text);       // the current value was written first
            var line = text.Split('\n')[selection!.Value.Start.Line];
            Assert.Contains("editor.tabSize", line);
        }
    }
}
