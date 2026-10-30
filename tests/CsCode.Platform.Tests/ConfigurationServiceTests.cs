using System.Reflection;
using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Io;
using CsCode.Platform.Tests.Support;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public sealed class ConfigFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-cfg-");
    public IoLane Lane { get; } = new("test.cfg", 2);
    public FakeFileWatcher Watcher { get; } = new();
    public AppPaths Paths { get; }
    public string SettingsPath => Path.Combine(Paths.ConfigDir, "settings.json");
    public string WorkspaceDir => Temp.Combine("ws");
    public PlatformOptions Options { get; } = new() { ConfigReloadDebounce = TimeSpan.FromMilliseconds(20) };

    public ConfigFixture()
    {
        Paths = new AppPaths(k => k == "CSCODE_HOME" ? Temp.Combine("home") : null, HostOs.Linux, "/home/test");
    }

    public ConfigurationService Create(bool withWatcher = true) => new(Paths, Lane, withWatcher ? Watcher : null, Options);

    public void WriteUser(string text) { Directory.CreateDirectory(Paths.ConfigDir); File.WriteAllText(SettingsPath, text); }
    public string ReadUser() => File.ReadAllText(SettingsPath);

    public string WriteWorkspace(string sub, string text)
    {
        var path = Path.Combine(WorkspaceDir, sub, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public async Task OpenWorkspace(ConfigurationService svc, params string[] folders)
    {
        var uris = folders.Length == 0 ? [FileUri.FromDirectoryPath(WorkspaceDir)] : folders.Select(FileUri.FromDirectoryPath).ToArray();
        Directory.CreateDirectory(WorkspaceDir);
        await ((IWorkspaceBound)svc).OnWorkspaceFoldersChangedAsync(uris, CancellationToken.None);
    }

    public void Dispose() { Lane.Dispose(); Temp.Dispose(); }
}

public class ConfigurationServiceTests : IDisposable
{
    private readonly ConfigFixture _f = new();
    public void Dispose() => _f.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- defaults ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_key_in_SettingKeys_has_a_registered_default()
    {
        using var svc = _f.Create();
        var keys = typeof(SettingKeys).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToArray();
        Assert.NotEmpty(keys);
        foreach (var k in keys) Assert.True(svc.GetRaw(k) is not null, $"no default for {k}");
        Assert.All(keys, k => Assert.Contains(svc.Properties, p => p.Key == k && p.Description is { Length: > 0 }));
    }

    [Fact]
    public void Defaults_match_the_documented_vs_code_like_values()
    {
        using var svc = _f.Create();
        Assert.Equal(OperatingSystem.IsMacOS() ? "Fira Code, Menlo, SF Mono, Monaco, monospace" : "Fira Code, Cascadia Code, Consolas, monospace", svc.Get<string>(SettingKeys.EditorFontFamily));
        Assert.Equal(14, svc.Get<int>(SettingKeys.EditorFontSize));
        Assert.Equal(4, svc.Get<int>(SettingKeys.EditorTabSize));
        Assert.True(svc.Get<bool>(SettingKeys.EditorInsertSpaces));
        Assert.Equal("off", svc.Get<string>(SettingKeys.EditorWordWrap));
        Assert.True(svc.Get<bool>(SettingKeys.EditorMinimapEnabled));
        Assert.False(svc.Get<bool>(SettingKeys.EditorFormatOnSave));
        Assert.Equal("off", svc.Get<string>(SettingKeys.FilesAutoSave));
        Assert.Equal(0, svc.Get<int>(SettingKeys.MemoryBudgetMb));
        Assert.True(svc.Get<bool>(SettingKeys.MemoryTrimOnIdle));
        Assert.Equal("visualstudio", svc.Get<string>(BuiltInSettings.KeymapKey));
        Assert.True(svc.Get<Dictionary<string, bool>>(SettingKeys.FilesWatcherExclude)!["**/node_modules/**"]);
    }

    [Fact]
    public void Get_with_a_fallback_uses_it_only_when_nothing_is_set_anywhere()
    {
        using var svc = _f.Create();
        Assert.Equal("fb", svc.Get("nothing.here", "fb"));
        Assert.Equal(14, svc.Get(SettingKeys.EditorFontSize, 99)); // registered default wins over the caller's fallback
        Assert.Null(svc.Get<string>("nothing.here"));
        Assert.Equal(0, svc.Get<int>("nothing.here"));
        Assert.Null(svc.GetRaw("nothing.here"));
    }

    [Fact]
    public void A_value_of_the_wrong_type_yields_the_fallback_instead_of_throwing()
    {
        _f.WriteUser("""{ "editor.fontSize": "big" }""");
        using var svc = _f.Create();
        Assert.Equal(99, svc.Get(SettingKeys.EditorFontSize, 99));
        Assert.Equal("big", svc.Get<string>(SettingKeys.EditorFontSize));
    }

    // ---- file format -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dotted_keys_and_nested_objects_are_equivalent_and_mix_freely()
    {
        _f.WriteUser("""
            {
              // comments and trailing commas are fine
              "editor.fontSize": 20,
              "editor": { "tabSize": 2, "minimap": { "enabled": false }, },
              "workbench": { "colorTheme": "X" },
              "a.b.c": 1,
            }
            """);
        using var svc = _f.Create();
        Assert.Equal(20, svc.Get<int>("editor.fontSize"));
        Assert.Equal(2, svc.Get<int>("editor.tabSize"));
        Assert.False(svc.Get<bool>("editor.minimap.enabled"));
        Assert.Equal("X", svc.Get<string>("workbench.colorTheme"));
        Assert.Equal(1, svc.Get<int>("a.b.c"));
        Assert.Equal(1, svc.GetRaw("a")!.Value.GetProperty("b").GetProperty("c").GetInt32());
    }

    [Fact]
    public void Section_reads_return_the_merged_object_including_defaults()
    {
        _f.WriteUser("""{ "editor.fontSize": 18 }""");
        using var svc = _f.Create();
        var editor = svc.GetRaw("editor")!.Value;
        Assert.Equal(JsonValueKind.Object, editor.ValueKind);
        Assert.Equal(18, editor.GetProperty("fontSize").GetInt32());   // user value
        Assert.Equal(4, editor.GetProperty("tabSize").GetInt32());     // default
        Assert.True(editor.GetProperty("minimap").GetProperty("enabled").GetBoolean());  // default, nested under the same section
    }

    [Fact]
    public void BOM_crlf_and_blank_files_are_accepted()
    {
        Directory.CreateDirectory(_f.Paths.ConfigDir);
        File.WriteAllBytes(_f.SettingsPath, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "{\r\n  // c\r\n  \"editor.tabSize\": 3\r\n}\r\n"u8.ToArray()]);
        using (var svc = _f.Create()) { Assert.Equal(3, svc.Get<int>("editor.tabSize")); Assert.Null(svc.LastError); }

        _f.WriteUser("   \n// nothing but a comment\n");
        using (var svc = _f.Create()) { Assert.Equal(4, svc.Get<int>("editor.tabSize")); Assert.Null(svc.LastError); }
    }

    [Fact]
    public void Registered_object_settings_stay_whole_so_glob_keys_with_dots_survive_and_objects_merge_across_layers()
    {
        _f.WriteUser("""{ "files.exclude": { "**/*.js": true, "**/.git": false } }""");
        using var svc = _f.Create();
        var ex = svc.Get<Dictionary<string, bool>>(SettingKeys.FilesExclude)!;
        Assert.True(ex["**/*.js"]);          // key containing dots was NOT split into nested objects
        Assert.False(ex["**/.git"]);         // user turned a default off...
        Assert.True(ex["**/.svn"]);          // ...and the other defaults survive (objects merge, they do not replace)
    }

    [Fact]
    public void Language_scoped_blocks_are_normalised_but_kept_under_their_bracketed_key()
    {
        _f.WriteUser("""{ "[csharp]": { "editor.tabSize": 2, "editor": { "insertSpaces": false } }, "editor.tabSize": 4 }""");
        using var svc = _f.Create();
        var cs = svc.GetRaw("[csharp]")!.Value;
        Assert.Equal(2, cs.GetProperty("editor").GetProperty("tabSize").GetInt32());
        Assert.False(cs.GetProperty("editor").GetProperty("insertSpaces").GetBoolean());
        Assert.Equal(4, svc.Get<int>("editor.tabSize"));
    }

    [Fact]
    public void Duplicate_keys_last_one_wins()
    {
        _f.WriteUser("""{ "editor.tabSize": 2, "editor.tabSize": 6 }""");
        using var svc = _f.Create();
        Assert.Equal(6, svc.Get<int>("editor.tabSize"));
    }

    // ---- layering ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Layers_apply_defaults_then_user_then_vscode_then_cscode()
    {
        _f.WriteUser("""{ "editor.tabSize": 3, "only.user": "u", "editor.fontSize": 11 }""");
        _f.WriteWorkspace(".vscode", """{ "editor.tabSize": 2, "only.vscode": "v", "editor.fontSize": 12 }""");
        _f.WriteWorkspace(".cscode", """{ "editor.tabSize": 8, "only.cscode": "c" }""");
        using var svc = _f.Create();
        Assert.Equal(3, svc.Get<int>("editor.tabSize"));

        await _f.OpenWorkspace(svc);

        Assert.Equal(8, svc.Get<int>("editor.tabSize"));      // .cscode beats .vscode beats user
        Assert.Equal(12, svc.Get<int>("editor.fontSize"));    // .vscode beats user where .cscode is silent
        Assert.Equal("u", svc.Get<string>("only.user"));
        Assert.Equal("v", svc.Get<string>("only.vscode"));
        Assert.Equal("c", svc.Get<string>("only.cscode"));
        Assert.True(svc.Get<bool>("editor.insertSpaces"));    // default untouched
    }

    [Fact]
    public async Task Closing_the_folder_drops_workspace_values_and_reports_the_changed_keys()
    {
        _f.WriteWorkspace(".cscode", """{ "editor.tabSize": 8 }""");
        using var svc = _f.Create();
        await _f.OpenWorkspace(svc);
        var seen = new List<string>();
        using var sub = svc.Changed.Subscribe(e => seen.AddRange(e.AffectedKeys));

        await ((IWorkspaceBound)svc).OnWorkspaceFoldersChangedAsync([], Ct);

        Assert.Equal(4, svc.Get<int>("editor.tabSize"));
        Assert.Equal(["editor.tabSize"], seen);
    }

    [Fact]
    public async Task Additional_workspace_folders_overlay_on_top()
    {
        var second = _f.Temp.Combine("ws2");
        Directory.CreateDirectory(Path.Combine(second, ".cscode"));
        File.WriteAllText(Path.Combine(second, ".cscode", "settings.json"), """{ "editor.tabSize": 5, "second": true }""");
        _f.WriteWorkspace(".cscode", """{ "editor.tabSize": 8 }""");
        using var svc = _f.Create();
        await _f.OpenWorkspace(svc, _f.WorkspaceDir, second);
        Assert.Equal(5, svc.Get<int>("editor.tabSize"));
        Assert.True(svc.Get<bool>("second"));
    }

    // ---- change events & live reload ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task External_edit_reload_lists_exactly_the_affected_keys()
    {
        _f.WriteUser("""{ "editor.fontSize": 14, "editor.tabSize": 4, "x.y": 1 }""");
        using var svc = _f.Create();
        var seen = new List<string[]>();
        using var sub = svc.Changed.Subscribe(e => seen.Add(e.AffectedKeys.Order().ToArray()));

        _f.WriteUser("""{ "editor.fontSize": 16, "editor.tabSize": 4, "x.z": 2 }""");
        await svc.ReloadAsync(Ct);

        Assert.Single(seen);
        Assert.Equal(["editor.fontSize", "x.y", "x.z"], seen[0]);
        Assert.Equal(16, svc.Get<int>("editor.fontSize"));
        Assert.Null(svc.GetRaw("x.y"));
        Assert.True(seen[0].Length == 3 && new ConfigurationChangedEventArgs(seen[0]).Affects("x"));
    }

    [Fact]
    public async Task A_no_op_rewrite_fires_nothing_even_when_comments_whitespace_and_order_change()
    {
        _f.WriteUser("""{ "a": 1, "b": 2 }""");
        using var svc = _f.Create();
        var count = 0;
        using var sub = svc.Changed.Subscribe(_ => count++);

        _f.WriteUser("// now with a comment\n{\n  \"b\": 2,\n  \"a\": 1\n}\n");   // same values, different text
        await svc.ReloadAsync(Ct);
        _f.WriteUser("""{ "a": 1, "b": 2 }""");
        await svc.ReloadAsync(Ct);
        await svc.ReloadAsync(Ct);                                                // unchanged file

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Setting_a_value_to_its_default_fires_nothing()
    {
        _f.WriteUser("{}");
        using var svc = _f.Create();
        var count = 0;
        using var sub = svc.Changed.Subscribe(_ => count++);
        _f.WriteUser("""{ "editor.tabSize": 4 }""");
        await svc.ReloadAsync(Ct);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_registered_object_setting_reports_itself_not_its_members()
    {
        using var svc = _f.Create();
        var seen = new List<string>();
        using var sub = svc.Changed.Subscribe(e => seen.AddRange(e.AffectedKeys));
        _f.WriteUser("""{ "files.exclude": { "**/*.tmp": true } }""");
        await svc.ReloadAsync(Ct);
        Assert.Equal(["files.exclude"], seen);
    }

    [Fact]
    public async Task The_file_watcher_triggers_a_debounced_reload_that_coalesces_a_burst_of_events()
    {
        _f.WriteUser("""{ "editor.fontSize": 14 }""");
        using var svc = _f.Create();
        var fired = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var sub = svc.Changed.Subscribe(e => { Interlocked.Increment(ref count); fired.TrySetResult(e.AffectedKeys.ToArray()); });

        _f.WriteUser("""{ "editor.fontSize": 22 }""");
        for (var i = 0; i < 10; i++) _f.Watcher.Raise(_f.SettingsPath);        // burst

        Assert.Equal(["editor.fontSize"], await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await Task.Delay(200, Ct);
        Assert.Equal(1, count);
        Assert.Equal(22, svc.Get<int>("editor.fontSize"));
    }

    [Fact]
    public async Task Events_for_unrelated_files_in_the_watched_directory_do_not_trigger_a_reload()
    {
        using var svc = _f.Create();
        var count = 0;
        using var sub = svc.Changed.Subscribe(_ => count++);
        _f.WriteUser("""{ "editor.fontSize": 22 }""");
        _f.Watcher.Raise(Path.Combine(_f.Paths.ConfigDir, "keybindings.json"));
        await Task.Delay(300, Ct);
        Assert.Equal(0, count); // settings.json was never announced, so the (unannounced) change is not picked up
    }

    [Fact]
    public async Task Workspace_settings_files_are_watched_and_a_newly_created_dot_cscode_dir_is_discovered()
    {
        using var svc = _f.Create();
        await _f.OpenWorkspace(svc);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = svc.Changed.Subscribe(_ => fired.TrySetResult());

        var file = _f.WriteWorkspace(".cscode", """{ "editor.tabSize": 7 }""");
        _f.Watcher.Raise(Path.GetDirectoryName(file)!, FileChangeKind.Created); // the folder-level watch sees ".cscode" appear

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(7, svc.Get<int>("editor.tabSize"));
        for (var i = 0; i < 100 && !_f.Watcher.ActiveRoots.Any(r => r.EndsWith("/.cscode")); i++) await Task.Delay(20, Ct); // re-arm runs on the lane
        Assert.Contains(_f.Watcher.ActiveRoots, r => r.EndsWith("/.cscode")); // and it is now watched for edits
    }

    [Fact]
    public async Task Reopening_a_folder_replaces_the_workspace_watches()
    {
        using var svc = _f.Create();
        await _f.OpenWorkspace(svc);
        var afterFirst = _f.Watcher.ActiveCount;
        await _f.OpenWorkspace(svc);
        Assert.Equal(afterFirst, _f.Watcher.ActiveCount); // old ones disposed, not accumulated
    }

    // ---- UpdateAsync -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_edits_in_place_preserving_comments_order_and_formatting()
    {
        var before = "{\n    // my font\n    \"editor.fontSize\": 14, // px\n    \"editor.tabSize\": 4\n}\n";
        _f.WriteUser(before);
        using var svc = _f.Create();

        await svc.UpdateAsync("editor.fontSize", 18, ConfigurationTarget.User, Ct);

        Assert.Equal("{\n    // my font\n    \"editor.fontSize\": 18, // px\n    \"editor.tabSize\": 4\n}\n", _f.ReadUser());
        Assert.Equal(18, svc.Get<int>("editor.fontSize"));
    }

    [Fact]
    public async Task UpdateAsync_adds_new_keys_and_creates_a_missing_file_and_directory()
    {
        using var svc = _f.Create();
        Assert.False(File.Exists(_f.SettingsPath));

        await svc.UpdateAsync("editor.fontSize", 20, ct: Ct);
        Assert.Equal("{\n    \"editor.fontSize\": 20\n}\n", _f.ReadUser());

        await svc.UpdateAsync("workbench.colorTheme", "Visual Studio Light", ct: Ct);
        Assert.Equal("{\n    \"editor.fontSize\": 20,\n    \"workbench.colorTheme\": \"Visual Studio Light\"\n}\n", _f.ReadUser());
        Assert.Equal("Visual Studio Light", svc.Get<string>("workbench.colorTheme"));
    }

    [Fact]
    public async Task UpdateAsync_into_a_nested_file_keeps_the_nested_shape()
    {
        _f.WriteUser("{\n  \"editor\": {\n    \"tabSize\": 2\n  }\n}\n");
        using var svc = _f.Create();
        await svc.UpdateAsync("editor.fontSize", 15, ct: Ct);
        Assert.Equal("{\n  \"editor\": {\n    \"tabSize\": 2,\n    \"fontSize\": 15\n  }\n}\n", _f.ReadUser());
        Assert.Equal(15, svc.Get<int>("editor.fontSize"));
        Assert.Equal(2, svc.Get<int>("editor.tabSize"));
    }

    [Fact]
    public async Task UpdateAsync_with_null_removes_the_key_so_the_default_returns()
    {
        _f.WriteUser("{\n    \"editor.fontSize\": 30,\n    \"keep\": 1\n}\n");
        using var svc = _f.Create();
        await svc.UpdateAsync("editor.fontSize", null, ct: Ct);
        Assert.Equal("{\n    \"keep\": 1\n}\n", _f.ReadUser());
        Assert.Equal(14, svc.Get<int>("editor.fontSize"));
    }

    [Fact]
    public async Task UpdateAsync_preserves_crlf_files()
    {
        _f.WriteUser("{\r\n    \"a\": 1\r\n}\r\n");
        using var svc = _f.Create();
        await svc.UpdateAsync("b", 2, ct: Ct);
        Assert.Equal("{\r\n    \"a\": 1,\r\n    \"b\": 2\r\n}\r\n", _f.ReadUser());
    }

    [Fact]
    public async Task UpdateAsync_preserves_a_utf8_bom()
    {
        Directory.CreateDirectory(_f.Paths.ConfigDir);
        File.WriteAllBytes(_f.SettingsPath, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "{\n    \"a\": 1\n}\n"u8.ToArray()]);
        using var svc = _f.Create();
        await svc.UpdateAsync("b", 2, ct: Ct);
        var bytes = File.ReadAllBytes(_f.SettingsPath);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(2, svc.Get<int>("b"));
    }

    [Fact]
    public async Task UpdateAsync_accepts_structured_values()
    {
        using var svc = _f.Create();
        await svc.UpdateAsync("files.exclude", new Dictionary<string, bool> { ["**/*.log"] = true }, ct: Ct);
        await svc.UpdateAsync("editor.rulers", new[] { 80, 120 }, ct: Ct);
        await svc.UpdateAsync("raw", JsonDocument.Parse("""{"a":[1,2]}""").RootElement, ct: Ct);
        Assert.True(svc.Get<Dictionary<string, bool>>("files.exclude")!["**/*.log"]);
        Assert.Equal([80, 120], svc.Get<int[]>("editor.rulers")!);
        Assert.Equal(2, svc.GetRaw("raw")!.Value.GetProperty("a")[1].GetInt32());
    }

    [Fact]
    public async Task UpdateAsync_fires_exactly_one_event_and_its_own_write_echo_is_ignored()
    {
        _f.WriteUser("{}");
        using var svc = _f.Create();
        var seen = new List<string[]>();
        using var sub = svc.Changed.Subscribe(e => seen.Add(e.AffectedKeys.ToArray()));

        await svc.UpdateAsync("editor.fontSize", 19, ct: Ct);
        Assert.Equal([["editor.fontSize"]], seen);

        // The watcher now reports the write we just made: reloading must find identical text and stay silent.
        _f.Watcher.Raise(_f.SettingsPath);
        await svc.ReloadAsync(Ct);
        await Task.Delay(150, Ct);
        Assert.Single(seen);
    }

    [Fact]
    public async Task UpdateAsync_reads_the_file_fresh_so_an_unannounced_external_edit_is_not_clobbered()
    {
        _f.WriteUser("""{ "a": 1 }""");
        using var svc = _f.Create();
        File.WriteAllText(_f.SettingsPath, """{ "a": 1, "external": true }"""); // user edit the watcher has not delivered yet
        await svc.UpdateAsync("b", 2, ct: Ct);
        var text = _f.ReadUser();
        Assert.Contains("external", text);
        Assert.Contains("\"b\": 2", text);
        Assert.True(svc.Get<bool>("external")); // and memory caught up
    }

    [Fact]
    public async Task UpdateAsync_refuses_a_malformed_file_and_leaves_it_untouched()
    {
        const string broken = "{ \"a\": 1, ";
        _f.WriteUser(broken);
        using var svc = _f.Create();
        var ex = await Assert.ThrowsAsync<ConfigurationFileException>(async () => await svc.UpdateAsync("b", 2, ct: Ct));
        Assert.Equal(_f.SettingsPath, ex.Path);
        Assert.Equal(broken, _f.ReadUser());
    }

    [Fact]
    public async Task UpdateAsync_leaves_no_temp_files_behind()
    {
        using var svc = _f.Create();
        for (var i = 0; i < 20; i++) await svc.UpdateAsync("n", i, ct: Ct);
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(_f.Paths.ConfigDir).Select(e => Path.GetFileName(e)!).ToArray());
    }

    [Fact]
    public async Task Workspace_target_writes_dot_cscode_settings_and_requires_an_open_folder()
    {
        using var svc = _f.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await svc.UpdateAsync("a", 1, ConfigurationTarget.Workspace, Ct));

        await _f.OpenWorkspace(svc);
        await svc.UpdateAsync("editor.tabSize", 6, ConfigurationTarget.Workspace, Ct);
        var file = Path.Combine(_f.WorkspaceDir, ".cscode", "settings.json");
        Assert.Equal("{\n    \"editor.tabSize\": 6\n}\n", File.ReadAllText(file));
        Assert.Equal(6, svc.Get<int>("editor.tabSize"));
        Assert.False(File.Exists(_f.SettingsPath)); // user file untouched
    }

    [Fact]
    public async Task Concurrent_updates_are_serialised_and_none_is_lost()
    {
        using var svc = _f.Create();
        var tasks = Enumerable.Range(0, 40).Select(i => svc.UpdateAsync($"k{i}", i, ct: Ct).AsTask()).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        for (var i = 0; i < 40; i++) Assert.Equal(i, svc.Get<int>($"k{i}", -1));
        var onDisk = JsoncParser.ToJsonObject((JsoncObject)JsoncParser.Parse(_f.ReadUser())!);
        Assert.Equal(40, onDisk.Count);
    }

    // ---- malformed files ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_that_turns_malformed_keeps_the_last_good_values_and_warns_once()
    {
        _f.WriteUser("""{ "editor.fontSize": 21 }""");
        using var svc = _f.Create();
        var errors = new List<ConfigurationError?>();
        var changes = 0;
        using var s1 = svc.ErrorChanged.Subscribe(errors.Add);
        using var s2 = svc.Changed.Subscribe(_ => changes++);
        Assert.Null(svc.LastError);

        _f.WriteUser("""{ "editor.fontSize": 21, oops""");
        await svc.ReloadAsync(Ct);
        await svc.ReloadAsync(Ct);   // same bad text again: no second warning
        _f.Watcher.Raise(_f.SettingsPath);
        await Task.Delay(200, Ct);

        Assert.Equal(21, svc.Get<int>("editor.fontSize"));        // last good value survives
        Assert.NotNull(svc.LastError);
        Assert.Equal(_f.SettingsPath, svc.LastError!.Path);
        Assert.Single(errors);
        Assert.Equal(0, changes);

        _f.WriteUser("""{ "editor.fontSize": 23 }""");           // fixed
        await svc.ReloadAsync(Ct);
        Assert.Null(svc.LastError);
        Assert.Equal(23, svc.Get<int>("editor.fontSize"));
        Assert.Equal(2, errors.Count);
        Assert.Null(errors[1]);
    }

    [Fact]
    public void A_malformed_file_at_startup_yields_defaults_and_a_warning_never_a_crash()
    {
        _f.WriteUser("{ this is not json");
        using var svc = _f.Create();
        Assert.Equal(14, svc.Get<int>("editor.fontSize"));
        Assert.NotNull(svc.LastError);
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void A_non_object_root_is_an_error_not_an_exception(string content)
    {
        _f.WriteUser(content);
        using var svc = _f.Create();
        Assert.NotNull(svc.LastError);
        Assert.Equal(14, svc.Get<int>("editor.fontSize"));
    }

    // ---- registered properties ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Registered_defaults_are_visible_events_fire_and_disposing_removes_them()
    {
        using var svc = _f.Create();
        var seen = new List<string>();
        using var sub = svc.Changed.Subscribe(e => seen.AddRange(e.AffectedKeys));

        var reg = svc.RegisterProperties([new ConfigurationProperty("cscode.lsp.maxCompletionItems", JsonSerializer.SerializeToElement(200), "Max items")]);
        Assert.Equal(200, svc.Get<int>("cscode.lsp.maxCompletionItems"));
        Assert.Contains(svc.Properties, p => p.Key == "cscode.lsp.maxCompletionItems" && p.Description == "Max items");
        Assert.Equal(["cscode.lsp.maxCompletionItems"], seen);

        seen.Clear();
        reg.Dispose();
        Assert.Null(svc.GetRaw("cscode.lsp.maxCompletionItems"));
        Assert.DoesNotContain(svc.Properties, p => p.Key == "cscode.lsp.maxCompletionItems");
        Assert.Equal(["cscode.lsp.maxCompletionItems"], seen);
    }

    [Fact]
    public void A_user_value_beats_a_registered_default_and_a_late_registration_does_not_change_it()
    {
        _f.WriteUser("""{ "x.setting": "mine" }""");
        using var svc = _f.Create();
        var seen = new List<string>();
        using var sub = svc.Changed.Subscribe(e => seen.AddRange(e.AffectedKeys));
        svc.RegisterProperties([new ConfigurationProperty("x.setting", JsonSerializer.SerializeToElement("default"))]);
        Assert.Equal("mine", svc.Get<string>("x.setting"));
        Assert.Empty(seen); // effective value did not change
    }

    [Fact]
    public void Object_settings_keep_member_names_whole_whether_registered_or_not()
    {
        _f.WriteUser("""{ "my.globs": { "**/*.cs": true } }""");
        using var svc = _f.Create();
        // lsp-core-2: only top-level keys are split (VS Code), so an unregistered object setting keeps its globs too (it used to be split)...
        Assert.True(svc.Get<Dictionary<string, bool>>("my.globs")!["**/*.cs"]);
        // ...and registering it later changes nothing.
        svc.RegisterProperties([new ConfigurationProperty("my.globs", JsonSerializer.SerializeToElement(new Dictionary<string, bool>()), "globs")]);
        Assert.True(svc.Get<Dictionary<string, bool>>("my.globs")!["**/*.cs"]);
    }

    [Fact]
    public void Property_schema_metadata_with_enum_and_description_is_exposed_for_a_settings_ui()
    {
        using var svc = _f.Create();
        var wrap = svc.Properties.Single(p => p.Key == SettingKeys.EditorWordWrap);
        Assert.Equal("Controls how lines should wrap.", wrap.Description);
        var values = wrap.Schema!.Value.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("on", values);
        Assert.Contains("off", values);
    }

    // ---- performance & concurrency -----------------------------------------------------------------------------------------------

    [Fact]
    public void Primitive_reads_allocate_nothing_after_warm_up_over_one_million_calls()
    {
        using var svc = _f.Create();
        for (var i = 0; i < 20_000; i++) { svc.Get<int>("editor.fontSize"); svc.Get<bool>("editor.minimap.enabled"); svc.Get<string>("editor.wordWrap"); svc.GetRaw("editor.tabSize"); }

        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var i = 0; i < 1_000_000; i++)
        {
            sum += svc.Get<int>("editor.fontSize");
            if (svc.Get<bool>("editor.minimap.enabled")) sum++;
            if (svc.Get<string>("editor.wordWrap") is { Length: > 0 }) sum++;
            if (svc.GetRaw("editor.tabSize") is not null) sum++;
            sum += svc.Get("editor.missing", 3);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sum > 0);
        Assert.True(allocated <= 4096, $"config reads allocated {allocated:N0} bytes over 5,000,000 reads");
    }

    [Fact]
    public async Task Parallel_get_update_and_register_have_no_exceptions_or_deadlocks()
    {
        using var svc = _f.Create();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();
        for (var w = 0; w < 3; w++)
        {
            var id = w;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var i = 0;
                    while (!cts.IsCancellationRequested)
                    {
                        await svc.UpdateAsync($"c.k{id}", i++, ct: Ct);
                        using var reg = svc.RegisterProperties([new ConfigurationProperty($"c.p{id}", JsonSerializer.SerializeToElement(i))]);
                        _ = svc.Get<int>($"c.p{id}");
                    }
                }
                catch (Exception ex) { errors.Add(ex); }
            }, Ct));
            tasks.Add(Task.Run(() =>
            {
                try { while (!cts.IsCancellationRequested) { _ = svc.Get<int>("editor.fontSize"); _ = svc.GetRaw("c"); _ = svc.Properties; } }
                catch (Exception ex) { errors.Add(ex); }
            }, Ct));
        }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task UpdateAsync_does_not_block_the_caller_even_when_the_io_lane_is_saturated()
    {
        using var lane = new IoLane("test.sat", 1);
        using var svc = new ConfigurationService(_f.Paths, lane, null, _f.Options);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, Ct);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = svc.UpdateAsync("a", 1, ct: Ct);
        var reload = svc.ReloadAsync(Ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"calls took {sw.Elapsed.TotalMilliseconds:F1} ms on the caller thread");
        Assert.False(pending.IsCompleted);

        gate.Set();
        await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await reload.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await blocker;
    }

    // ---- lifetime ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Disposing_the_service_releases_its_file_watches()
    {
        var svc = _f.Create();
        Assert.True(_f.Watcher.ActiveCount > 0);
        svc.Dispose();
        Assert.Equal(0, _f.Watcher.ActiveCount);
    }

    [Fact]
    public void Disposed_event_subscriptions_and_registrations_are_not_retained()
    {
        using var svc = _f.Create();
        LeakAssert.NotRetained(() =>
        {
            var subscriber = new List<string>();
            var s = svc.Changed.Subscribe(e => subscriber.AddRange(e.AffectedKeys));
            var reg = svc.RegisterProperties([new ConfigurationProperty("leak.k", JsonSerializer.SerializeToElement(1))]);
            s.Dispose();
            reg.Dispose();
            return [subscriber];
        });
    }

    [Fact]
    public void A_disposed_service_is_collectable_and_the_watcher_forgets_it()
    {
        LeakAssert.NotRetained(() =>
        {
            var svc = _f.Create();
            svc.Dispose();
            return [svc];
        });
        Assert.Equal(0, _f.Watcher.ActiveCount);
    }
}
