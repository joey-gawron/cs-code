using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.Platform;
using CsCode.Panels.Search.Settings;
using CsCode.Panels.Search.ViewModels;
using CsCode.Panels.Search.Views;
using Xunit;
using KeyBinding = CsCode.Core.Platform.KeyBinding;

namespace CsCode.Panels.Search.Tests.Ui;

public class FindInFilesViewTests
{
    private static async Task<(UiFixture F, FindInFilesViewModel Vm, FindInFilesView View, Window Window)> OpenAsync(string? query = "UserService", double w = 420, double h = 640)
    {
        UiSupport.EnsureTheme();
        var f = new UiFixture();
        f.WriteSampleSources();
        var vm = new FindInFilesViewModel(f.Services);
        var view = new FindInFilesView(vm);
        var window = UiSupport.Host(view, w, h);
        if (query is not null)
        {
            vm.Query = query;
            vm.Start();
            await UiSupport.WaitUntilAsync(() => vm.WaitForSearchAsync().IsCompleted);
            UiSupport.Pump();
        }
        return (f, vm, view, window);
    }

    [AvaloniaFact]
    public async Task Initial_form_renders_with_query_toggles_and_details()
    {
        var (f, vm, view, window) = await OpenAsync(query: null);
        using (f)
        {
            vm.DetailsVisible = true;
            UiSupport.Pump();
            UiSupport.Screenshot(window, "search-details");
            vm.DetailsVisible = false;
            var path = UiSupport.Screenshot(window, "search-initial");
            Assert.True(File.Exists(path));
            var names = view.Descendants<ToggleButton>().Select(t => t.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)).ToList();
            Assert.Contains("Match case", names);
            Assert.Contains("Match whole word", names);
            Assert.Contains("Use regular expression", names);
            Assert.NotNull(view.FindControl<TextBox>("QueryBox"));
            Assert.NotNull(view.FindControl<TextBox>("IncludeBox"));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Results_render_grouped_with_highlighted_matches()
    {
        var (f, vm, view, window) = await OpenAsync();
        using (f)
        {
            UiSupport.Screenshot(window, "search-results");
            Assert.Equal(5, vm.Rows.OfType<FileRow>().Count());
            var list = view.FindControl<ListBox>("ResultsList")!;
            Assert.True(list.ItemCount > 0);

            // every realised match line has a highlighted run
            var previews = view.Descendants<MatchPreviewText>().ToList();
            Assert.NotEmpty(previews);
            foreach (var p in previews)
            {
                var runs = p.Inlines!.OfType<Run>().ToList();
                var info = $"preview='{p.Preview}' bounds={p.Bounds} visible={p.IsEffectivelyVisible} runs={string.Join("|", runs.Select(r => (r.Text, r.Background is null).ToString()))} find={p.TryFindResource("vsc:editor.findMatchHighlightBackground", out var res)}:{res}";
                Assert.True(runs.Any(r => r.Background is not null && r.Text == "UserService"), info);
            }
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Replace_preview_shows_struck_through_old_text_and_the_new_text()
    {
        var (f, vm, view, window) = await OpenAsync();
        using (f)
        {
            vm.ReplaceVisible = true;
            vm.ReplaceText = "AccountService";
            UiSupport.Pump();
            UiSupport.Screenshot(window, "search-replace");
            var preview = view.Descendants<MatchPreviewText>().First();
            var runs = preview.Inlines!.OfType<Run>().ToList();
            Assert.Contains(runs, r => r.Text == "UserService" && r.TextDecorations is { Count: > 0 });
            Assert.Contains(runs, r => r.Text == "AccountService");
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Toggle_buttons_two_way_bind_to_the_view_model()
    {
        var (f, vm, view, window) = await OpenAsync(query: null);
        using (f)
        {
            var toggles = view.Descendants<ToggleButton>().Where(t => t.Classes.Contains("opt")).ToList();
            var byName = toggles.Where(t => t.GetValue(Avalonia.Automation.AutomationProperties.NameProperty) is { Length: > 0 }).ToDictionary(t => t.GetValue(Avalonia.Automation.AutomationProperties.NameProperty)!);
            byName["Match case"].IsChecked = true;
            byName["Match whole word"].IsChecked = true;
            byName["Use regular expression"].IsChecked = true;
            Assert.True(vm.MatchCase && vm.WholeWord && vm.IsRegex);
            vm.MatchCase = false;
            Assert.False(byName["Match case"].IsChecked);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Typing_and_pressing_enter_runs_the_search_and_arrow_keys_walk_the_history()
    {
        var (f, vm, view, window) = await OpenAsync(query: null);
        using (f)
        {
            var box = view.FindControl<TextBox>("QueryBox")!;
            box.Focus();
            box.Text = "needle";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiSupport.WaitUntilAsync(() => vm.WaitForSearchAsync().IsCompleted && !vm.IsSearching);
            Assert.Equal("needle", vm.History.Items.Single());
            box.Text = "other";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiSupport.WaitUntilAsync(() => vm.WaitForSearchAsync().IsCompleted);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal("other", box.Text);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal("needle", box.Text);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F4_and_shift_f4_step_through_results_and_open_them()
    {
        var (f, vm, view, window) = await OpenAsync();
        using (f)
        {
            view.FindControl<TextBox>("QueryBox")!.Focus();
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            await UiSupport.WaitUntilAsync(() => vm.SelectedRow is MatchRow);
            var first = vm.SelectedRow;
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            await UiSupport.WaitUntilAsync(() => !ReferenceEquals(vm.SelectedRow, first));
            window.KeyPress(Key.F4, RawInputModifiers.Shift, PhysicalKey.F4, null);
            await UiSupport.WaitUntilAsync(() => ReferenceEquals(vm.SelectedRow, first));
            Assert.True(f.Navigation.Opened.Count >= 3);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Delete_dismisses_the_selected_row_and_enter_opens_it()
    {
        var (f, vm, view, window) = await OpenAsync();
        using (f)
        {
            var list = view.FindControl<ListBox>("ResultsList")!;
            var before = vm.TotalMatches;
            var match = vm.Rows.OfType<MatchRow>().First();
            vm.SelectedRow = match;
            UiSupport.Pump();
            list.Focus();
            UiSupport.Pump();
            Assert.True(list.IsKeyboardFocusWithin, "the results list should have keyboard focus");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiSupport.WaitUntilAsync(() => f.Navigation.Opened.Count == 1);
            Assert.Equal(match.Match.Range, f.Navigation.Opened[0].Selection);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Equal(before - 1, vm.TotalMatches);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Collapsed_view_and_the_bottom_dock_layout_render()
    {
        var (f, vm, view, window) = await OpenAsync(w: 360, h: 420);
        using (f)
        {
            vm.CollapseAll();
            UiSupport.Screenshot(window, "search-collapsed");
            window.Close();
        }
        var (f2, vm2, view2, window2) = await OpenAsync(w: 1000, h: 300);
        using (f2)
        {
            UiSupport.Screenshot(window2, "search-bottom");
            window2.Close();
        }
    }

    [AvaloniaFact]
    public async Task No_results_and_error_states_render()
    {
        var (f, vm, view, window) = await OpenAsync(query: "zzzNotFoundzzz", w: 420, h: 300);
        using (f)
        {
            UiSupport.Screenshot(window, "search-noresults");
            vm.IsRegex = true;
            vm.Query = "(broken";
            vm.Start();
            await UiSupport.WaitUntilAsync(() => !vm.IsSearching);
            UiSupport.Screenshot(window, "search-error");
            Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Disposing_the_view_releases_the_view_model()
    {
        var (f, vm, view, window) = await OpenAsync();
        using (f)
        {
            window.Close();
            view.Dispose();
            Assert.Empty(vm.Rows);
            Assert.Equal(0, vm.TotalMatches);
        }
    }

    [AvaloniaFact]
    public async Task Light_theme_renders_too()
    {
        var (f, vm, view, window) = await OpenAsync(w: 420, h: 480);
        using (f)
        {
            UiSupport.EnsureTheme(CsCode.Ui.Theming.VsThemes.Light);
            UiSupport.Pump();
            UiSupport.Screenshot(window, "search-light");
            UiSupport.EnsureTheme(CsCode.Ui.Theming.VsThemes.Dark);
            window.Close();
        }
    }
}

public class SettingsViewTests
{
    private static ConfigurationProperty P(string key, object? def, string desc, string? schema = null) =>
        new(key, JsonSerializer.SerializeToElement(def), desc, schema is null ? null : JsonDocument.Parse(schema).RootElement.Clone());

    private static (UiFixture F, SettingsViewModel Vm, SettingsView View, Window Window) Open(double w = 980, double h = 700, Action<UiFixture>? arrange = null)
    {
        UiSupport.EnsureTheme();
        var f = new UiFixture();
        f.Harness.Config.RegisterProperties(
        [
            P("editor.fontFamily", "Cascadia Code, Consolas, 'Courier New', monospace", "Controls the font family.", """{"type":"string"}"""),
            P("editor.fontSize", 14, "Controls the font size in pixels.", """{"type":"number","minimum":6,"maximum":100}"""),
            P("editor.tabSize", 4, "The number of spaces a tab is equal to.", """{"type":"integer","minimum":1}"""),
            P("editor.insertSpaces", true, "Insert spaces when pressing Tab.", """{"type":"boolean"}"""),
            P("editor.minimap.enabled", true, "Controls whether the minimap is shown.", """{"type":"boolean"}"""),
            P("editor.wordWrap", "off", "Controls how lines should wrap.", """{"type":"string","enum":["off","on","wordWrapColumn","bounded"],"enumDescriptions":["Lines will never wrap.","Lines will wrap at the viewport width.","Lines will wrap at the configured wrap column.","Lines will wrap at the minimum of viewport and wrap column."]}"""),
            P("files.autoSave", "off", "Controls auto save of dirty editors.", """{"type":"string","enum":["off","afterDelay","onFocusChange","onWindowChange"]}"""),
            P("files.exclude", new Dictionary<string, bool> { ["**/.git"] = true, ["**/.DS_Store"] = true, ["**/node_modules"] = true }, "Glob patterns for files and folders to exclude from the explorer, quick open and search.", """{"type":"object"}"""),
            P("workbench.colorTheme", "Visual Studio Dark", "Specifies the color theme used in the workbench.", """{"type":"string"}"""),
            P("cscode.memory.budgetMb", 0, "Soft memory budget in MB (0 = unlimited). Above it caches are trimmed.", """{"type":"integer","minimum":0}"""),
            P("cscode.memory.trimOnIdle", true, "Trim re-creatable caches when the application is idle.", """{"type":"boolean"}"""),
            P("search.maxResults", 20000, "Maximum number of results Find in Files collects before it stops.", """{"type":"integer","minimum":1}"""),
        ]);
        arrange?.Invoke(f);
        var vm = new SettingsViewModel(f.Services, debounceMs: 20);
        var view = new SettingsView(vm);
        var window = UiSupport.Host(view, w, h);
        return (f, vm, view, window);
    }

    [AvaloniaFact]
    public void Settings_render_with_a_control_per_type()
    {
        var (f, vm, view, window) = Open();
        using (f)
        {
            UiSupport.Screenshot(window, "settings-all");
            Assert.NotEmpty(view.Descendants<CheckBox>());
            Assert.NotEmpty(view.Descendants<TextBox>());
            vm.SearchText = "auto save";                  // an enum setting; only the first screenful of the list is realised
            UiSupport.Pump();
            Assert.NotEmpty(view.Descendants<ComboBox>());
            vm.SearchText = "exclude";                    // an object setting: summary + "Edit in settings.json" link
            UiSupport.Pump();
            UiSupport.Screenshot(window, "settings-complex");
            Assert.Contains(view.Descendants<TextBlock>(), t => t.Text == "Edit in settings.json");
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Modified_markers_filter_and_scope_tabs_work_in_the_view()
    {
        var (f, vm, view, window) = Open(arrange: x => x.Harness.Config.Set("editor.tabSize", 2));
        using (f)
        {
            await vm.RefreshAsync();
            UiSupport.Pump();
            vm.SearchText = "@modified";
            UiSupport.Pump();
            UiSupport.Screenshot(window, "settings-modified");
            Assert.Single(vm.Items);
            vm.SearchText = "font";
            UiSupport.Pump();
            UiSupport.Screenshot(window, "settings-filtered");
            Assert.Contains(vm.Items, i => i.Key == "editor.fontSize");
            vm.SearchText = "";
            vm.SelectedCategory = vm.Categories.Single(c => c.Title == "Files");
            UiSupport.Pump();
            UiSupport.Screenshot(window, "settings-files");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Editing_in_the_view_writes_through_the_view_model()
    {
        var (f, vm, view, window) = Open();
        using (f)
        {
            var checkbox = view.Descendants<CheckBox>().First();
            var before = ((BoolSettingViewModel)checkbox.DataContext!).Value;
            checkbox.IsChecked = !before;
            Assert.NotEmpty(f.Configuration.Writes);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Disposing_the_view_flushes_pending_edits()
    {
        var (f, vm, view, window) = Open();
        using (f)
        {
            ((NumberSettingViewModel)vm.AllItems.Single(i => i.Key == "editor.tabSize")).Text = "6";
            window.Close();
            view.Dispose();
            Assert.Empty(vm.Items);
        }
    }
}

public class KeyboardShortcutsViewTests
{
    [AvaloniaFact]
    public void Shortcuts_render_as_a_table()
    {
        UiSupport.EnsureTheme();
        using var f = new UiFixture();
        f.Commands.Register("workbench.action.findInFiles", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("workbench.action.findInFiles", "Find in Files"));
        f.Commands.Register("editor.action.rename", _ => ValueTask.FromResult<object?>(null), new CommandMetadata("editor.action.rename", "Rename Symbol"));
        f.Keybindings.Bindings.AddRange(
        [
            new KeyBinding("workbench.action.findInFiles", KeyStrokeParser.ParseChord("ctrl+shift+f"), null, null, KeyBindingSource.Default),
            new KeyBinding("editor.action.rename", KeyStrokeParser.ParseChord("f2"), "editorHasRenameProvider && !editorReadonly", null, KeyBindingSource.Default),
            new KeyBinding("editor.action.commentLine", KeyStrokeParser.ParseChord("ctrl+k ctrl+c"), "editorTextFocus", null, KeyBindingSource.User),
            new KeyBinding("workbench.action.debug.start", KeyStrokeParser.ParseChord("f5"), "!inDebugMode", null, KeyBindingSource.Default),
        ]);
        var vm = new KeyboardShortcutsViewModel(f.Services);
        var view = new KeyboardShortcutsView(vm);
        var window = UiSupport.Host(view, 980, 420);
        UiSupport.Screenshot(window, "keybindings");
        Assert.Equal(4, view.Descendants<ListBoxItem>().Count());
        window.Close();
        view.Dispose();
    }
}
