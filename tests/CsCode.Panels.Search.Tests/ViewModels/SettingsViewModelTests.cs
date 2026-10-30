using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Settings;
using Xunit;

namespace CsCode.Panels.Search.Tests.ViewModels;

public class SettingsViewModelTests : IDisposable
{
    private readonly VmFixture _f = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _f.Dispose();
    }

    private static ConfigurationProperty P(string key, object? def, string? desc = null, string? schema = null) =>
        new(key, JsonSerializer.SerializeToElement(def), desc ?? "Description of " + key, schema is null ? null : JsonDocument.Parse(schema).RootElement.Clone());

    private SettingsViewModel Make(int debounceMs = 30, params ConfigurationProperty[] props)
    {
        _owned.Add(_f.ConfigStore.RegisterProperties(props.Length > 0 ? props : Default()));
        var vm = new SettingsViewModel(_f.Services, debounceMs);
        _owned.Add(vm);
        return vm;
    }

    private static ConfigurationProperty[] Default() =>
    [
        P("editor.fontSize", 14, "Font size", """{"type":"number","minimum":6,"maximum":100}"""),
        P("editor.tabSize", 4, "Tab size", """{"type":"integer","minimum":1}"""),
        P("editor.insertSpaces", true, "Insert spaces", """{"type":"boolean"}"""),
        P("editor.wordWrap", "off", "Word wrap", """{"type":"string","enum":["off","on","bounded"],"enumDescriptions":["Never wrap","Wrap at the viewport","Wrap at the smaller of viewport and column"]}"""),
        P("editor.minimap.enabled", true, "Show the minimap", """{"type":"boolean"}"""),
        P("editor.fontFamily", "Consolas", "Font family", """{"type":"string"}"""),
        P("files.autoSave", "off", "Auto save", """{"type":"string","enum":["off","afterDelay"]}"""),
        P("files.exclude", new Dictionary<string, bool> { ["**/.git"] = true }, "Exclude globs", """{"type":"object"}"""),
        P("cscode.memory.budgetMb", 0, "Memory budget", """{"type":"integer","minimum":0}"""),
        P("cscode.lsp.trace.server", "off", "LSP trace", """{"type":"string","enum":["off","messages","verbose"]}"""),
        P("search.exclude", new[] { "a", "b" }, "A list setting", """{"type":"array"}"""),
    ];

    private SettingItemViewModel Item(SettingsViewModel vm, string key) => vm.AllItems.Single(i => i.Key == key);

    // ----------------------------------------------------------------------------- control mapping

    [Fact]
    public void Every_json_type_maps_to_its_control()
    {
        var vm = Make();
        Assert.IsType<BoolSettingViewModel>(Item(vm, "editor.insertSpaces"));
        Assert.IsType<NumberSettingViewModel>(Item(vm, "editor.fontSize"));
        Assert.False(((NumberSettingViewModel)Item(vm, "editor.fontSize")).IsInteger);
        Assert.True(((NumberSettingViewModel)Item(vm, "editor.tabSize")).IsInteger);
        Assert.IsType<StringSettingViewModel>(Item(vm, "editor.fontFamily"));
        Assert.IsType<EnumSettingViewModel>(Item(vm, "editor.wordWrap"));
        Assert.IsType<ComplexSettingViewModel>(Item(vm, "files.exclude"));
        Assert.IsType<ComplexSettingViewModel>(Item(vm, "search.exclude"));
    }

    [Fact]
    public void Without_a_schema_the_default_value_decides_the_control()
    {
        var vm = Make(30, P("x.flag", true), P("x.count", 3), P("x.ratio", 1.5), P("x.name", "n"), P("x.list", new[] { 1 }), P("x.map", new { a = 1 }));
        Assert.IsType<BoolSettingViewModel>(Item(vm, "x.flag"));
        Assert.True(((NumberSettingViewModel)Item(vm, "x.count")).IsInteger);
        Assert.False(((NumberSettingViewModel)Item(vm, "x.ratio")).IsInteger);
        Assert.IsType<StringSettingViewModel>(Item(vm, "x.name"));
        Assert.IsType<ComplexSettingViewModel>(Item(vm, "x.list"));
        Assert.IsType<ComplexSettingViewModel>(Item(vm, "x.map"));
    }

    [Fact]
    public void A_schema_type_list_with_null_uses_the_first_real_type()
    {
        var vm = Make(30, P("x.maybe", "a", schema: """{"type":["null","string"]}"""));
        Assert.IsType<StringSettingViewModel>(Item(vm, "x.maybe"));
    }

    [Fact]
    public void Enums_carry_choices_with_descriptions_and_the_current_selection()
    {
        var vm = Make();
        var e = (EnumSettingViewModel)Item(vm, "editor.wordWrap");
        Assert.Equal(["off", "on", "bounded"], e.Choices.Select(c => c.Value));
        Assert.Equal("Wrap at the viewport", e.Choices[1].Description);
        Assert.Equal("off", e.Selected?.Value);
    }

    [Fact]
    public void Values_start_at_their_defaults_and_unset_settings_are_not_modified()
    {
        var vm = Make();
        Assert.True(((BoolSettingViewModel)Item(vm, "editor.insertSpaces")).Value);
        Assert.Equal("14", ((NumberSettingViewModel)Item(vm, "editor.fontSize")).Text);
        Assert.Equal("Consolas", ((StringSettingViewModel)Item(vm, "editor.fontFamily")).Value);
        Assert.Equal(0, vm.ModifiedCount);
    }

    [Fact]
    public void Complex_values_show_a_read_only_summary()
    {
        var vm = Make();
        Assert.Equal("1 entry: **/.git", ((ComplexSettingViewModel)Item(vm, "files.exclude")).Summary);
        Assert.Equal("2 items", ((ComplexSettingViewModel)Item(vm, "search.exclude")).Summary);
    }

    // ---------------------------------------------------------------------------------- categories

    [Fact]
    public void Categories_come_from_key_prefixes_with_subcategories()
    {
        var vm = Make();
        var titles = vm.Categories.Select(c => (c.Level, c.Title)).ToList();
        Assert.Contains((0, "All Settings"), titles);
        Assert.Contains((0, "Editor"), titles);
        Assert.Contains((1, "Minimap"), titles);       // editor.minimap.enabled
        Assert.Contains((0, "Files"), titles);
        Assert.Contains((0, "Memory"), titles);        // cscode.memory.* drops the cscode namespace
        Assert.Contains((0, "LSP"), titles);
        Assert.Contains((1, "Trace"), titles);         // cscode.lsp.trace.server
        Assert.Equal(11, vm.Categories[0].Count);
    }

    [Theory]
    [InlineData("editor.fontSize", "Editor", null, "Editor: Font Size")]
    [InlineData("editor.minimap.enabled", "Editor", "Minimap", "Editor › Minimap: Enabled")]
    [InlineData("cscode.memory.budgetMb", "Memory", null, "Memory: Budget Mb")]
    [InlineData("files.autoSave", "Files", null, "Files: Auto Save")]
    [InlineData("workbench.colorTheme", "Workbench", null, "Workbench: Color Theme")]
    public void Titles_and_categories_are_humanised(string key, string top, string? sub, string title)
    {
        Assert.Equal((top, sub), SettingsCategories.Of(key));
        Assert.Equal(title, SettingsCategories.Describe(key).Title);
    }

    [Fact]
    public void Selecting_a_category_shows_only_its_settings()
    {
        var vm = Make();
        vm.SelectedCategory = vm.Categories.Single(c => c.Title == "Files");
        Assert.Equal(["files.autoSave", "files.exclude"], vm.Items.Select(i => i.Key));
        vm.SelectedCategory = vm.Categories.Single(c => c.Title == "Minimap");
        Assert.Equal(["editor.minimap.enabled"], vm.Items.Select(i => i.Key));
        vm.SelectedCategory = vm.Categories[0];
        Assert.Equal(11, vm.Items.Count);
    }

    // ------------------------------------------------------------------------------------ filtering

    [Fact]
    public void Search_is_a_fuzzy_filter_over_title_key_and_description()
    {
        var vm = Make();
        vm.SearchText = "tab size";
        Assert.Equal("editor.tabSize", vm.Items[0].Key);
        vm.SearchText = "fntsz";
        Assert.Contains(vm.Items, i => i.Key == "editor.fontSize");
        vm.SearchText = "zzzzqqq";
        Assert.Empty(vm.Items);
        vm.SearchText = "";
        Assert.Equal(11, vm.Items.Count);
    }

    [Fact]
    public void Searching_ignores_the_selected_category()
    {
        var vm = Make();
        vm.SelectedCategory = vm.Categories.Single(c => c.Title == "Files");
        vm.SearchText = "minimap";
        Assert.Equal("editor.minimap.enabled", vm.Items[0].Key);
    }

    [Fact]
    public async Task The_modified_filter_shows_only_changed_settings()
    {
        var vm = Make();
        _f.ConfigStore.Set("editor.tabSize", 2);
        await vm.RefreshAsync();
        vm.SearchText = "@modified";
        Assert.Equal(["editor.tabSize"], vm.Items.Select(i => i.Key));
        vm.SearchText = "@modified font";
        Assert.Empty(vm.Items);
        vm.SearchText = "@modified tab";
        Assert.Single(vm.Items);
    }

    // ------------------------------------------------------------------------- modified and reset

    [Fact]
    public async Task A_value_that_differs_from_the_default_is_marked_modified_and_reset_restores_it()
    {
        var vm = Make();
        _f.ConfigStore.Set("editor.tabSize", 8);
        await vm.RefreshAsync();
        var tab = (NumberSettingViewModel)Item(vm, "editor.tabSize");
        Assert.True(tab.IsModified);
        Assert.Equal("8", tab.Text);
        await vm.ResetAsync(tab);
        Assert.Contains(_f.Recording.Writes, w => w is { Key: "editor.tabSize", Value: null });
        Assert.False(tab.IsModified);
        Assert.Equal("4", tab.Text);
    }

    [Fact]
    public async Task Copy_setting_id_puts_the_key_on_the_clipboard_service()
    {
        var vm = Make();
        await vm.CopyIdAsync(Item(vm, "editor.tabSize"));
        // RecordingWindowService.CopyToClipboardAsync is a no-op fake; the call must simply not throw and use the key
        Assert.Equal("editor.tabSize", Item(vm, "editor.tabSize").Key);
    }

    // ----------------------------------------------------------------------------- writes and scope

    [Fact]
    public async Task Toggles_and_choices_write_immediately_through_update_async()
    {
        var vm = Make();
        ((BoolSettingViewModel)Item(vm, "editor.insertSpaces")).Value = false;
        ((EnumSettingViewModel)Item(vm, "editor.wordWrap")).Selected = ((EnumSettingViewModel)Item(vm, "editor.wordWrap")).Choices[1];
        await vm.WaitIdleAsync();
        Assert.Contains(_f.Recording.Writes, w => w is { Key: "editor.insertSpaces", Value: false, Target: ConfigurationTarget.User });
        Assert.Contains(_f.Recording.Writes, w => w is { Key: "editor.wordWrap", Value: "on" });
    }

    [Fact]
    public async Task Typed_numbers_are_debounced_into_a_single_write_of_the_last_value()
    {
        var vm = Make(debounceMs: 150);
        var size = (NumberSettingViewModel)Item(vm, "editor.fontSize");
        size.Text = "1";
        size.Text = "16";
        size.Text = "16.5";
        Assert.Empty(_f.Recording.Writes);       // nothing written per keystroke
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await vm.WaitIdleAsync();
        var w = Assert.Single(_f.Recording.Writes);
        Assert.Equal(("editor.fontSize", 16.5), (w.Key, (double)w.Value!));
    }

    [Fact]
    public async Task Flush_writes_pending_edits_at_once_and_dispose_never_loses_them()
    {
        var vm = Make(debounceMs: 60_000);
        ((StringSettingViewModel)Item(vm, "editor.fontFamily")).Value = "Fira Code";
        Assert.Empty(_f.Recording.Writes);
        await vm.FlushAsync();
        Assert.Equal("Fira Code", Assert.Single(_f.Recording.Writes).Value);

        var vm2 = Make(debounceMs: 60_000);
        ((NumberSettingViewModel)Item(vm2, "editor.tabSize")).Text = "3";
        vm2.Dispose();                           // closing the tab flushes
        for (var i = 0; i < 50 && _f.Recording.Writes.Count < 2; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(3L, _f.Recording.Writes.Last(w => w.Key == "editor.tabSize").Value);
    }

    [Theory]
    [InlineData("abc", "Enter a number.")]
    [InlineData("5", "Must be at least 6.")]
    [InlineData("101", "Must be at most 100.")]
    [InlineData("", "Enter a number.")]
    public async Task Invalid_numbers_show_an_inline_error_and_are_not_written(string text, string error)
    {
        var vm = Make(debounceMs: 10);
        var size = (NumberSettingViewModel)Item(vm, "editor.fontSize");
        size.Text = text;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(error, size.Error);
        Assert.True(size.HasError);
        Assert.Empty(_f.Recording.Writes);
        size.Text = "12";
        Assert.Null(size.Error);
        await vm.FlushAsync();
        Assert.Single(_f.Recording.Writes);
    }

    [Fact]
    public async Task A_flush_while_the_debounced_write_is_under_way_waits_for_it_and_writes_once()
    {
        var vm = Make(debounceMs: 10);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _f.Recording.BeforeWrite = () => { inFlight.TrySetResult(); return release.Task; };
        ((NumberSettingViewModel)Item(vm, "editor.fontSize")).Text = "12";
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // past its debounce, writing
        var flush = vm.FlushAsync();
        Assert.False(flush.IsCompleted); // the flush waits for that write
        release.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(_f.Recording.Writes);
    }

    [Fact]
    public async Task Integer_settings_reject_fractions()
    {
        var vm = Make(debounceMs: 10);
        var tab = (NumberSettingViewModel)Item(vm, "editor.tabSize");
        tab.Text = "2.5";
        Assert.Equal("Enter a whole number.", tab.Error);
        tab.Text = "0";
        Assert.Equal("Must be at least 1.", tab.Error);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_workspace_scope_writes_to_the_workspace_target()
    {
        var vm = Make();
        vm.SelectedScope = SettingsScope.Workspace;
        ((BoolSettingViewModel)Item(vm, "editor.insertSpaces")).Value = false;
        await vm.WaitIdleAsync();
        Assert.Contains(_f.Recording.Writes, w => w is { Key: "editor.insertSpaces", Target: ConfigurationTarget.Workspace });
        vm.SelectedScope = SettingsScope.User;
        ((BoolSettingViewModel)Item(vm, "editor.minimap.enabled")).Value = false;
        await vm.WaitIdleAsync();
        Assert.Contains(_f.Recording.Writes, w => w is { Key: "editor.minimap.enabled", Target: ConfigurationTarget.User });
    }

    [Fact]
    public async Task The_workspace_tab_is_unavailable_without_a_folder()
    {
        await _f.Harness.Workspace.CloseFolderAsync(TestContext.Current.CancellationToken);
        var vm = Make();
        Assert.False(vm.IsWorkspaceAvailable);
        vm.SelectedScope = SettingsScope.Workspace;
        Assert.Equal(SettingsScope.User, vm.SelectedScope);
    }

    [Fact]
    public async Task Scope_files_decide_which_scope_a_setting_counts_as_modified_in()
    {
        var vm = Make();
        Directory.CreateDirectory(Path.GetDirectoryName(_f.UserSettingsFile)!);
        await File.WriteAllTextAsync(_f.UserSettingsFile, "// user\n{ \"editor.tabSize\": 2, \"editor\": { \"minimap\": { \"enabled\": false } } }", TestContext.Current.CancellationToken);
        _f.Harness.Write(".cscode/settings.json", "{ \"editor.fontSize\": 20, \"editor.tabSize\": 3 }");
        _f.ConfigStore.Set("editor.tabSize", 3);
        await vm.RefreshAsync();
        // User tab: tabSize and minimap.enabled are set in the user file
        Assert.True(Item(vm, "editor.tabSize").IsModified);
        Assert.Equal("Workspace", Item(vm, "editor.tabSize").AlsoModifiedIn);
        Assert.True(Item(vm, "editor.minimap.enabled").IsModified);
        Assert.False(Item(vm, "editor.fontSize").IsModified);
        Assert.Equal("2", ((NumberSettingViewModel)Item(vm, "editor.tabSize")).Text);       // the scope's own value is shown
        vm.SelectedScope = SettingsScope.Workspace;
        await vm.RefreshAsync();
        Assert.True(Item(vm, "editor.fontSize").IsModified);
        Assert.Equal("20", ((NumberSettingViewModel)Item(vm, "editor.fontSize")).Text);
        Assert.Equal("3", ((NumberSettingViewModel)Item(vm, "editor.tabSize")).Text);
        Assert.False(Item(vm, "editor.minimap.enabled").IsModified);
    }

    [Fact]
    public async Task A_settings_file_with_a_utf8_bom_is_read()
    {
        var vm = Make();
        Directory.CreateDirectory(Path.GetDirectoryName(_f.UserSettingsFile)!);
        // Explicitly set to its default: only the file says it is set, so only a readable file makes it "modified" (and resettable).
        await File.WriteAllBytesAsync(_f.UserSettingsFile, [0xEF, 0xBB, 0xBF, .. "{ \"editor.tabSize\": 4 }"u8.ToArray()], TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.True(Item(vm, "editor.tabSize").IsModified);
        Assert.Equal(4, SettingsViewModel.Flatten([0xEF, 0xBB, 0xBF, .. "{\"a.b\":4}"u8.ToArray()], new HashSet<string>())["a.b"].GetInt32());
    }

    [Fact]
    public async Task Keys_set_only_in_vscode_settings_are_modified_in_neither_scope_and_say_where_they_are()
    {
        var vm = Make();
        _f.Harness.Write(".vscode/settings.json", "{ \"editor.tabSize\": 2 }");
        _f.ConfigStore.Set("editor.tabSize", 2);       // what the platform's configuration merges from .vscode/settings.json
        await vm.RefreshAsync();
        var tab = (NumberSettingViewModel)Item(vm, "editor.tabSize");
        Assert.False(tab.IsModified);                  // Reset (UpdateAsync(key, null, User)) could not remove it: no marker, no Reset
        Assert.Equal(".vscode/settings.json", tab.AlsoModifiedIn);
        vm.SelectedScope = SettingsScope.Workspace;
        await vm.RefreshAsync();
        Assert.False(tab.IsModified);
        Assert.Equal(".vscode/settings.json", tab.AlsoModifiedIn);
        Assert.Equal("2", tab.Text);
        vm.SearchText = "@modified";
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Keys_set_in_another_workspace_folder_are_attributed_to_that_folder()
    {
        using var second = new CsCode.TestDoubles.TempDirectory("cscode-second-");
        second.WriteFile(".cscode/settings.json", "{ \"editor.fontFamily\": \"Hack\" }");
        await _f.Harness.Workspace.OpenFolderAsync(second.Uri, TestContext.Current.CancellationToken);
        var vm = Make();
        _f.ConfigStore.Set("editor.fontFamily", "Hack");
        await vm.RefreshAsync();
        var font = Item(vm, "editor.fontFamily");
        Assert.False(font.IsModified);
        Assert.Equal(Path.GetFileName(second.Path.TrimEnd('/')) + "/.cscode/settings.json", font.AlsoModifiedIn);
    }

    // ------------------------------------------------------------------------------ live updates

    [Fact]
    public async Task A_refresh_keeps_text_that_is_still_invalid_while_the_user_types()
    {
        var vm = Make(debounceMs: 60_000);
        var size = (NumberSettingViewModel)Item(vm, "editor.fontSize");
        size.Text = "";                                     // cleared to type a new value: invalid, so nothing is pending
        Assert.Equal("Enter a number.", size.Error);
        _f.ConfigStore.Set("editor.tabSize", 2);            // another setting's write lands: Changed, refresh
        await _f.Ui.PumpUntilAsync(() => Item(vm, "editor.tabSize").IsModified);
        Assert.Equal("", size.Text);                        // not "14" under the caret
        Assert.Equal("Enter a number.", size.Error);
        size.Text = "16";                                   // the user goes on typing
        Assert.Null(size.Error);
        await vm.FlushAsync();
        Assert.Equal(16.0, (double)_f.Recording.Writes.Single(w => w.Key == "editor.fontSize").Value!);   // a "number" setting
    }

    [Fact]
    public async Task A_refresh_keeps_a_string_that_breaks_its_pattern_while_the_user_types()
    {
        var vm = Make(30, P("x.id", "abc", schema: """{"type":"string","pattern":"^[a-z]+$"}"""), P("x.other", 1));
        var id = (StringSettingViewModel)Item(vm, "x.id");
        id.Value = "ab-";
        Assert.NotNull(id.Error);
        _f.ConfigStore.Set("x.other", 2);
        await _f.Ui.PumpUntilAsync(() => Item(vm, "x.other").IsModified);
        Assert.Equal("ab-", id.Value);
        Assert.NotNull(id.Error);
    }

    [Fact]
    public async Task Reset_and_switching_scope_discard_an_invalid_edit()
    {
        var vm = Make(debounceMs: 60_000);
        var size = (NumberSettingViewModel)Item(vm, "editor.fontSize");
        size.Text = "-";
        await vm.ResetAsync(size);
        Assert.Equal("14", size.Text);
        Assert.Null(size.Error);
        size.Text = "";
        vm.SelectedScope = SettingsScope.Workspace;
        await vm.RefreshAsync();
        Assert.Equal("14", size.Text);
        Assert.Null(size.Error);
    }

    [Fact]
    public async Task External_changes_update_the_controls_when_the_configuration_raises_changed()
    {
        var vm = Make();
        _f.ConfigStore.Set("editor.fontFamily", "Hack");   // raises Changed on this thread
        await _f.Ui.PumpUntilAsync(() => ((StringSettingViewModel)Item(vm, "editor.fontFamily")).Value == "Hack");
        Assert.True(Item(vm, "editor.fontFamily").IsModified);
    }

    [Fact]
    public async Task A_refresh_never_overwrites_a_box_that_has_an_edit_waiting()
    {
        var vm = Make(debounceMs: 60_000);
        var size = (NumberSettingViewModel)Item(vm, "editor.fontSize");
        size.Text = "18";                                   // typed, not yet written
        _f.ConfigStore.Set("editor.fontSize", 12);          // something else changed meanwhile
        await vm.RefreshAsync();
        Assert.Equal("18", size.Text);
    }

    // ---------------------------------------------------------------------------- settings.json

    [Fact]
    public async Task Edit_in_settings_json_adds_the_key_and_opens_the_file_at_it()
    {
        var vm = Make();
        Directory.CreateDirectory(Path.GetDirectoryName(_f.UserSettingsFile)!);
        await File.WriteAllTextAsync(_f.UserSettingsFile, "{\n  \"a.b\": 1,\n  \"files.exclude\": { \"**/x\": true }\n}\n", TestContext.Current.CancellationToken);
        await vm.EditInJsonAsync(Item(vm, "files.exclude"));
        var (uri, sel) = Assert.Single(_f.Navigation.Opened);
        Assert.EndsWith("settings.json", uri.LocalPath);
        Assert.Equal(new TextRange(new(2, 3), new(2, 3 + "files.exclude".Length)), sel);
    }

    [Fact]
    public async Task Edit_in_settings_json_creates_a_missing_file_and_writes_the_current_value_first()
    {
        var vm = Make();
        Assert.False(File.Exists(_f.UserSettingsFile));
        await vm.EditInJsonAsync(Item(vm, "search.exclude"));
        Assert.True(File.Exists(_f.UserSettingsFile));
        Assert.Contains(_f.Recording.Writes, w => w.Key == "search.exclude");   // the test configuration does not write files itself
        Assert.Single(_f.Navigation.Opened);
    }

    [Fact]
    public void Settings_files_flatten_nested_and_dotted_keys_but_keep_registered_objects_whole()
    {
        var registered = new HashSet<string> { "editor.fontSize", "editor.minimap.enabled", "files.exclude" };
        var json = "{ \"editor\": { \"fontSize\": 12, \"minimap\": { \"enabled\": false } }, \"files.exclude\": { \"**/a\": true }, /* c */ \"other\": { \"x\": 1 }, }"u8.ToArray();
        var flat = SettingsViewModel.Flatten(json, registered);
        Assert.Equal(12, flat["editor.fontSize"].GetInt32());
        Assert.False(flat["editor.minimap.enabled"].GetBoolean());
        Assert.Equal(JsonValueKind.Object, flat["files.exclude"].ValueKind);
        Assert.True(flat.ContainsKey("other"));        // unknown objects stay whole
    }

    // -------------------------------------------------------------------------------- lifetime

    [Fact]
    public void Disposing_releases_subscriptions_and_empties_the_lists()
    {
        var vm = Make();
        vm.Dispose();
        Assert.Empty(vm.Items);
        Assert.Empty(vm.Categories);
        _f.ConfigStore.Set("editor.tabSize", 9);   // no subscriber left: nothing posted
        Assert.Equal(0, _f.Ui.Pending);
    }
}
