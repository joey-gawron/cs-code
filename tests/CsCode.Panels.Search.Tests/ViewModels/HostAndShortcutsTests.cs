using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.ViewModels;
using CsCode.TestDoubles;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Search.Tests.ViewModels;

public class HostAndShortcutsTests : IDisposable
{
    private readonly VmFixture _f = new();
    private readonly RecordingViews _views = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _f.Dispose();
    }

    private sealed class RecordingViews : IViewActivator
    {
        public List<string> Shown { get; } = new();
        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) { Shown.Add(viewId); return ValueTask.FromResult(true); }
    }

    private sealed class FixedSelection(string? text) : IActiveSelectionProvider
    {
        public string? SelectedText => text;
    }

    private ServiceProvider Provider(string? selection = null, bool preRegister = false)
    {
        var s = new ServiceCollection();
        s.AddSingleton(_f.Harness.Workspace);
        s.AddSingleton<IWorkspaceService>(_f.Harness.Workspace);
        s.AddSingleton<IDocumentManager>(_f.Harness.Documents);
        s.AddSingleton(_f.Configuration);
        s.AddSingleton<INavigationService>(_f.Navigation);
        s.AddSingleton<IWindowService>(_f.Windows);
        s.AddSingleton<IStateStorage>(_f.State);
        s.AddSingleton<ICommandService>(_f.Commands);
        s.AddSingleton<IKeybindingService>(_f.Keybindings);
        s.AddSingleton<IContextKeyService>(_f.ContextKeys);
        s.AddSingleton<IViewActivator>(_views);
        s.AddSingleton<CsCode.Ui.Threading.IUiDispatcher>(_f.Ui);
        if (selection is not null) s.AddSingleton<IActiveSelectionProvider>(new FixedSelection(selection));
        if (preRegister) _f.Commands.Register(SearchPanelsHost.FindInFiles, _ => ValueTask.FromResult<object?>("shell"));
        s.AddCsCodeSearchPanels();
        var sp = s.BuildServiceProvider();
        _owned.Add(sp);
        return sp;
    }

    // ------------------------------------------------------------------------------- registration

    [Fact]
    public void The_module_registers_three_views_with_stable_ids_and_locations()
    {
        var sp = Provider();
        var tools = sp.GetServices<IToolWindow>().ToDictionary(t => t.Id);
        Assert.Equal(["keybindings", "search", "settings"], tools.Keys.Order());
        Assert.Equal(ToolWindowLocation.Left, tools["search"].DefaultLocation);
        Assert.Equal(ToolWindowLocation.Document, tools["settings"].DefaultLocation);
        Assert.Equal(ToolWindowLocation.Document, tools["keybindings"].DefaultLocation);
        Assert.Equal("workbench.action.openSettings", tools["settings"].ToggleCommandId);
        Assert.All(tools.Values, t => Assert.True(Avalonia.Application.Current is null || t.IconKey is not null));
        Assert.All(tools.Values, t => Assert.Contains(t.IconKey, CsCode.Ui.Icons.IconCatalog.Names));
    }

    [Fact]
    public void Enumerating_the_tool_windows_registers_every_command_and_the_settings()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        foreach (var id in new[]
        {
            "workbench.action.findInFiles", "workbench.action.replaceInFiles", "search.action.focusNextSearchResult", "search.action.focusPreviousSearchResult",
            "search.action.clearSearchResults", "search.action.collapseSearchResults", "workbench.action.openSettings", "workbench.action.openSettingsJson",
            "workbench.action.openGlobalKeybindings", "workbench.action.openGlobalKeybindingsFile",
        })
            Assert.True(_f.Commands.Has(id), id);
        Assert.Contains(_f.ConfigStore.Properties, p => p.Key == "search.maxResults");
        Assert.Contains(_f.ConfigStore.Properties, p => p.Key == "search.maxFileSizeBytes");
    }

    [Fact]
    public async Task A_command_the_shell_already_registered_is_not_replaced()
    {
        var sp = Provider(preRegister: true);
        _ = sp.GetServices<IToolWindow>().ToList();
        var result = await _f.Commands.ExecuteAsync(SearchPanelsHost.FindInFiles);
        Assert.Equal("shell", result);
    }

    // ------------------------------------------------------------------------------- find command

    [Fact]
    public async Task Find_in_files_without_a_view_parks_the_request_and_shows_the_view()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.FindInFiles, JsonSerializer.SerializeToElement(new { query = "foo", filesToInclude = "*.cs", isRegex = true }));
        Assert.Equal(["search"], _views.Shown);
        var vm = sp.GetRequiredService<SearchPanelsHost>().CreateFindViewModel();   // the shell creates the view afterwards
        Assert.Equal("foo", vm.Query);
        Assert.Equal("*.cs", vm.Include);
        Assert.True(vm.IsRegex);
        sp.GetRequiredService<SearchPanelsHost>().ReleaseFindViewModel(vm);
    }

    [Fact]
    public async Task Find_in_files_with_a_live_view_applies_the_request_at_once()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.FindInFiles, "needle");
        Assert.Equal("needle", vm.Query);
        Assert.Same(vm, host.LiveFind);
        host.ReleaseFindViewModel(vm);
        Assert.Null(host.LiveFind);
    }

    [Fact]
    public async Task Replace_in_files_opens_the_replace_row()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.ReplaceInFiles);
        Assert.True(vm.ReplaceVisible);
        Assert.Equal(FindFocusTarget.Replace, vm.PendingFocus);
        host.ReleaseFindViewModel(vm);
    }

    [Fact]
    public async Task The_query_is_seeded_from_the_editor_selection()
    {
        var sp = Provider(selection: "selectedWord");
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.FindInFiles);
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        Assert.Equal("selectedWord", vm.Query);
        host.ReleaseFindViewModel(vm);
    }

    [Fact]
    public async Task A_multi_line_selection_is_not_used_but_the_word_at_the_caret_is()
    {
        var sp = Provider(selection: "two\nlines");
        _ = sp.GetServices<IToolWindow>().ToList();
        var doc = new TestEditorDocument(_f.Harness.UriOf("a.cs"), "let someValue = 3;");
        _f.Navigation.ActiveDocument = doc;
        _f.Navigation.ActiveCaret = new TextPosition(0, 7);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.FindInFiles);
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        Assert.Equal("someValue", vm.Query);
        host.ReleaseFindViewModel(vm);
    }

    [Theory]
    [InlineData("hello world", 3, "hello")]
    [InlineData("hello world", 5, "hello")]
    [InlineData("hello world", 6, "world")]
    [InlineData("a_b.c", 1, "a_b")]
    [InlineData("   ", 1, null)]
    [InlineData("", 0, null)]
    public void Word_at_the_caret(string line, int col, string? expected) => Assert.Equal(expected, SearchPanelsHost.WordAt(line, col));

    [Fact]
    public void Command_arguments_follow_the_vs_code_shape()
    {
        var e = JsonSerializer.SerializeToElement(new { query = "q", replace = "r", triggerSearch = true, filesToExclude = "bin", isCaseSensitive = true, matchWholeWord = true, preserveCase = true });
        var r = SearchPanelsHost.ParseRequest(new CommandInvocation("x", [e]));
        Assert.Equal(("q", "r", "bin", true, true, true, true, true), (r.Query, r.Replace, r.Exclude, r.TriggerSearch, r.MatchCase, r.WholeWord, r.PreserveCase, r.TriggerSearch));
        Assert.Equal("plain", SearchPanelsHost.ParseRequest(new CommandInvocation("x", ["plain"])).Query);
        Assert.Null(SearchPanelsHost.ParseRequest(new CommandInvocation("x", [])).Query);
        Assert.Null(SearchPanelsHost.ParseRequest(new CommandInvocation("x", [JsonSerializer.SerializeToElement(5)])).Query);
    }

    [Fact]
    public async Task Find_in_folder_sets_the_include_to_the_folder_relative_to_the_workspace()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync("search.action.findInFolder", _f.Harness.UriOf("src/app/").ToString());
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        Assert.Equal("./src/app", vm.Include);
        host.ReleaseFindViewModel(vm);
    }

    [Fact]
    public async Task Next_and_clear_commands_act_on_the_live_view()
    {
        _f.Harness.Write("a.txt", "needle\nneedle");
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateFindViewModel();
        await _f.SearchAsync(vm, "needle");
        await _f.Commands.ExecuteAsync(SearchPanelsHost.NextResult);
        Assert.NotNull(vm.SelectedRow);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.CollapseResults);
        Assert.True(vm.AllCollapsed);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.ClearResults);
        Assert.Empty(vm.Rows);
        host.ReleaseFindViewModel(vm);
    }

    // ------------------------------------------------------------------------- settings commands

    [Fact]
    public async Task Open_settings_shows_the_view_and_a_query_argument_pre_filters_it()
    {
        _f.ConfigStore.RegisterProperties([new ConfigurationProperty("editor.tabSize", JsonSerializer.SerializeToElement(4), "Tab size")]);
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenSettings, "tab");
        Assert.Equal(["settings"], _views.Shown);
        var host = sp.GetRequiredService<SearchPanelsHost>();
        var vm = host.CreateSettingsViewModel();
        Assert.Equal("tab", vm.SearchText);
        host.ReleaseSettingsViewModel(vm);
    }

    [Fact]
    public async Task Open_settings_json_creates_the_file_when_missing_and_opens_it()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenSettingsJson);
        Assert.True(File.Exists(_f.UserSettingsFile));
        Assert.EndsWith("settings.json", Assert.Single(_f.Navigation.Opened).Uri.LocalPath);
    }

    [Fact]
    public async Task Open_keyboard_shortcuts_json_opens_keybindings_json_next_to_the_settings()
    {
        var sp = Provider();
        _ = sp.GetServices<IToolWindow>().ToList();
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenKeybindingsJson);
        Assert.EndsWith("keybindings.json", Assert.Single(_f.Navigation.Opened).Uri.LocalPath);
        await _f.Commands.ExecuteAsync(SearchPanelsHost.OpenKeybindings);
        Assert.Equal(["keybindings"], _views.Shown);
    }

    // --------------------------------------------------------------------------------- shortcuts

    private KeyboardShortcutsViewModel Shortcuts()
    {
        _f.Commands.Register("editor.action.rename", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("editor.action.rename", "Rename Symbol"));
        _f.Commands.Register("workbench.action.findInFiles", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("workbench.action.findInFiles", "Find in Files"));
        _f.Keybindings.Bindings.AddRange(
        [
            new KeyBinding("editor.action.rename", KeyStrokeParser.ParseChord("f2"), "editorHasRenameProvider", null, KeyBindingSource.Default),
            new KeyBinding("workbench.action.findInFiles", KeyStrokeParser.ParseChord("ctrl+shift+f"), null, null, KeyBindingSource.Default),
            new KeyBinding("editor.action.commentLine", KeyStrokeParser.ParseChord("ctrl+k ctrl+c"), "editorTextFocus", null, KeyBindingSource.User),
            new KeyBinding("-workbench.action.quickOpen", KeyStrokeParser.ParseChord("ctrl+p"), null, null, KeyBindingSource.User, IsNegative: true),
        ]);
        var vm = new KeyboardShortcutsViewModel(_f.Services);
        _owned.Add(vm);
        return vm;
    }

    [Fact]
    public void The_shortcuts_table_lists_every_binding_with_title_keys_when_and_source()
    {
        var vm = Shortcuts();
        Assert.Equal(4, vm.TotalCount);
        var rename = vm.Rows.Single(r => r.Command == "editor.action.rename");
        Assert.Equal(("Rename Symbol", "F2", "editorHasRenameProvider", "Default"), (rename.Title, rename.Keys, rename.When, rename.Source));
        Assert.Equal("Ctrl+K, Ctrl+C", vm.Rows.Single(r => r.Command == "editor.action.commentLine").Keys);
        Assert.Equal("Ctrl+Shift+F", vm.Rows.Single(r => r.Title == "Find in Files").Keys);
        Assert.Equal("User", vm.Rows.Single(r => r.Command == "editor.action.commentLine").Source);
        Assert.True(vm.Rows.Single(r => r.IsRemoval).Command.StartsWith('-'));
    }

    [Fact]
    public void The_shortcuts_filter_matches_command_key_when_and_source()
    {
        var vm = Shortcuts();
        vm.Filter = "rename";
        Assert.Equal(["editor.action.rename"], vm.Rows.Select(r => r.Command));
        vm.Filter = "ctrl+k";
        Assert.Equal(["editor.action.commentLine"], vm.Rows.Select(r => r.Command));
        vm.Filter = "editorTextFocus";
        Assert.Single(vm.Rows);
        vm.Filter = "user";
        Assert.Equal(2, vm.Rows.Count);
        vm.Filter = "";
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public async Task The_shortcuts_table_follows_binding_changes_and_opens_the_json_file()
    {
        var vm = Shortcuts();
        _f.Keybindings.Bindings.Add(new KeyBinding("x.y", KeyStrokeParser.ParseChord("alt+x"), null, null, KeyBindingSource.User));
        _f.Keybindings.Fire();
        await _f.Ui.PumpUntilAsync(() => vm.TotalCount == 5);
        _f.Commands.Register("workbench.action.openGlobalKeybindingsFile", _ => ValueTask.FromResult<object?>("opened"));
        await vm.OpenKeybindingsJsonAsync();
    }

    [Theory]
    [InlineData("ctrl+shift+f", "Ctrl+Shift+F")]
    [InlineData("f5", "F5")]
    [InlineData("alt+enter", "Alt+Enter")]
    [InlineData("pageup", "PageUp")]
    [InlineData("ctrl+/", "Ctrl+/")]
    [InlineData("shift+f11", "Shift+F11")]
    public void Key_labels(string chord, string label) => Assert.Equal(label, KeyLabel.Format(KeyStrokeParser.ParseChord(chord)));
}
