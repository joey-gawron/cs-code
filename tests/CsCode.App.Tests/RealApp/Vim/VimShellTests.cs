using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Vim;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>Vim's window, buffer and file commands, language-server keys, the on/off switch and key ownership against the shell, in the real app.</summary>
public class VimShellTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Write_edit_buffers_and_quit_go_through_the_document_well()
    {
        await using var app = await VimApp.StartAsync("hello\n", settings: Quiet);
        var path = app.Host.Files.Combine("test.txt");
        app.Keys("x");
        Assert.True(app.View.Document.IsDirty);
        await app.KeysAsync(":w<CR>", 10);
        Assert.True(await RealAppHost.WaitFor(() => File.ReadAllText(path) == "ello\n"), File.ReadAllText(path));
        Assert.True(await RealAppHost.WaitFor(() => !app.View.Document.IsDirty));

        await app.KeysAsync(":w copy.txt<CR>", 10);                     // a copy; the buffer stays this file
        Assert.True(await RealAppHost.WaitFor(() => File.Exists(app.Host.Files.Combine("copy.txt"))));
        Assert.Equal("ello\n", File.ReadAllText(app.Host.Files.Combine("copy.txt")));
        Assert.Equal("test.txt", app.Tab.Title);

        app.Host.Files.WriteFile("other.txt", "other\n");
        await app.KeysAsync(":e other.txt<CR>", 10);                    // relative to the current file's folder
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor?.Title == "other.txt"));
        await app.FocusAsync();
        Assert.Equal(2, app.Well.EditorTabs.Count);
        await app.KeysAsync(":bn<CR>", 5);
        Assert.Equal("test.txt", app.Well.ActiveEditor?.Title);
        await app.FocusAsync();
        await app.KeysAsync(":b other<CR>", 5);
        Assert.Equal("other.txt", app.Well.ActiveEditor?.Title);
        await app.FocusAsync();

        var running = Task.CompletedTask;
        await app.KeysAsync(":ls<CR>", 5);                               // the buffer list is a quick pick (picking switches)
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<QuickInputViewModel>().IsVisible));
        app.Host.Get<QuickInputViewModel>().Cancel();
        await DevScene.SettleAsync(3);
        await app.FocusAsync();

        await app.KeysAsync(":q<CR>", 10);                               // clean: closes the editor, no prompt
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 1));
        await app.FocusAsync();
        app.Keys("x");
        Assert.True(app.View.Document.IsDirty);
        await app.KeysAsync(":q<CR>", 5);                                // dirty: the shell's Save / Don't Save / Cancel prompt
        var dialogs = app.Host.Get<DialogHostViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible));
        Assert.Contains("Don't Save", dialogs.Buttons);
        dialogs.Choose("Cancel");
        await DevScene.SettleAsync(5);
        Assert.Single(app.Well.EditorTabs);
        await app.FocusAsync();
        await app.KeysAsync(":q!<CR>", 10);                              // the last editor closes; the application stays
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 0));
        Assert.True(app.Host.Window.IsVisible);
        Assert.Equal("ello\n", File.ReadAllText(path));                  // :q! discarded the change
        Assert.Equal(0, app.Vim.EngineCount);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Write_all_save_as_reload_new_buffer_and_delete_buffer()
    {
        await using var app = await VimApp.StartAsync("one\n", settings: Quiet);
        var first = app.Host.Files.Combine("test.txt");
        app.Keys("x");
        var second = await app.OpenAsync("two.txt", "two\n");
        app.Keys("x");
        await app.KeysAsync(":wa<CR>", 10);                               // every dirty editor
        Assert.True(await RealAppHost.WaitFor(() => File.ReadAllText(first) == "ne\n" && File.ReadAllText(app.Host.Files.Combine("two.txt")) == "wo\n"));

        app.Keys("x");
        await app.KeysAsync(":e!<CR>", 10);                               // re-read: the change is gone
        Assert.True(await RealAppHost.WaitFor(() => app.Text == "wo\n"), app.Describe());

        await app.KeysAsync(":sav saved.txt<CR>", 10);                    // the buffer becomes the new file
        Assert.True(await RealAppHost.WaitFor(() => File.Exists(app.Host.Files.Combine("saved.txt"))), "no saved.txt");
        Assert.True(await RealAppHost.WaitFor(() => app.View.Document.Uri.LocalPath.EndsWith("saved.txt", StringComparison.Ordinal)), app.View.Document.Uri.ToString());

        await app.KeysAsync(":enew<CR>", 10);
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 3 && app.Well.ActiveEditor!.Doc.IsUntitled));
        await app.FocusAsync();
        await app.KeysAsync(":bd<CR>", 10);                               // an untouched buffer closes without a prompt
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 2));
        await app.FocusAsync();
        app.Keys("x");
        await app.KeysAsync(":x<CR>", 10);                                // write and close
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 1));
        _ = second;
    }

    /// <summary>
    /// The Vim side (:sp :vsp :new, Ctrl-W s v h j k l w W t b o c q) maps onto the shell's split and the groups it creates. It was skipped while
    /// the split itself threw inside Dock.Avalonia (T14: the shell's data template for editor tabs also matched the tab strip's close-button
    /// presenters; fixed by <see cref="CsCode.App.Docking.ShellDockContent"/>, see <c>SplitEditorTests</c>).
    /// </summary>
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Split_windows_and_Ctrl_W_move_between_editor_groups()
    {
        await using var app = await VimApp.StartAsync("left\n", settings: Quiet);
        var factory = app.Host.Get<CsCode.App.Docking.ShellDockFactory>();
        app.Host.Files.WriteFile("right.txt", "right\n");
        await app.KeysAsync(":vsp right.txt<CR>", 10);
        Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 2 && app.Well.ActiveEditor?.Title == "right.txt"));
        await app.FocusAsync();
        await DevScene.SettleAsync(10);
        app.Keys("<C-w>h");
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor?.Title == "test.txt"), app.Well.ActiveEditor?.Title);
        await app.FocusAsync();
        app.Keys("<C-w>l");
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor?.Title == "right.txt"), app.Well.ActiveEditor?.Title);
        await app.FocusAsync();
        app.Keys("<C-w>w");                                              // next window (wraps)
        Assert.True(await RealAppHost.WaitFor(() => app.Well.ActiveEditor?.Title == "test.txt"));
        await app.FocusAsync();
        await app.KeysAsync(":only<CR>", 10);
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 1 && app.Well.ActiveEditor?.Title == "test.txt"));
        await app.FocusAsync();
        await app.KeysAsync(":new<CR>", 10);                              // an untitled buffer in a new group below
        Assert.True(await RealAppHost.WaitFor(() => app.Well.EditorTabs.Count == 2 && factory.DocumentGroups().Count == 2));
    }

    private sealed class FakeLanguage(Uri uri) : IDefinitionProvider, IHoverProvider
    {
        public List<DefinitionKind> Kinds { get; } = [];

        public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct)
        {
            Kinds.Add(kind);
            var line = kind switch { DefinitionKind.Declaration => 1, DefinitionKind.Implementation => 2, _ => 3 };
            var target = new TextRange(new TextPosition(line, 4), new TextPosition(line, 7));
            return ValueTask.FromResult<IReadOnlyList<LocationLink>?>([new LocationLink(uri, target, target)]);
        }

        public ValueTask<Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
            ValueTask.FromResult<Hover?>(new Hover([MarkupContent.Plain("the hover text")]));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Language_keys_run_the_editors_commands()
    {
        await using var app = await VimApp.StartAsync("use foo here\n    declared\n    implemented\n    defined\nlast\n", settings: Quiet);
        var fake = new FakeLanguage(app.View.Document.Uri);
        using var a = app.Host.Get<ILanguageFeatureRegistry>().Register<IDefinitionProvider>(DocumentSelector.All, fake);
        using var b = app.Host.Get<ILanguageFeatureRegistry>().Register<IHoverProvider>(DocumentSelector.All, fake);

        app.Keys("wgd");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(3, 4)), app.Describe() + " requests=" + fake.Kinds.Count + " msg=" + app.CommandLineText + " status=" + string.Join(" | ", app.Host.Get<IStatusBarService>().Items.Select(i => i.Text)));
        app.Keys("gggD");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(1, 4)), app.Describe());
        app.Keys("gggi");                                                 // nvim-lspconfig's gi: implementation
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(2, 4)), app.Describe());
        Assert.Equal([DefinitionKind.Definition, DefinitionKind.Declaration, DefinitionKind.Implementation], fake.Kinds);
        Assert.Equal("-- NORMAL --", app.ModeText);

        app.Keys("K");
        Assert.True(await RealAppHost.WaitFor(() => app.View.Modal.IsHoverOpen), "K showed no hover");
        app.Keys("<Esc>");
        Assert.False(app.View.Modal.IsHoverOpen);

        // ]d [d: next / previous problem (wrapping)
        app.Host.Get<IDiagnosticService>().Set(app.View.Document.Uri, "test", [
            new Diagnostic(new TextRange(new TextPosition(0, 4), new TextPosition(0, 7)), "first"),
            new Diagnostic(new TextRange(new TextPosition(4, 0), new TextPosition(4, 4)), "second"),
        ]);
        await DevScene.SettleAsync(20);
        app.Keys("gg]d");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(0, 4)), app.Describe());
        app.Keys("<Esc>]d");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(4, 0)), app.Describe());
        app.Keys("<Esc>[d");
        Assert.True(await RealAppHost.WaitFor(() => app.Caret == new TextPosition(0, 4)), app.Describe());
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Switching_Vim_off_restores_the_plain_editor_and_on_again_creates_engines()
    {
        await using var app = await VimApp.StartAsync("abc\n", settings: Quiet);
        var commands = app.Host.Get<ICommandService>();
        var modal = app.View.Modal;
        Assert.Equal("Vim", app.Vim.StatusText(VimIntegration.ToggleItemId));
        Assert.Contains(app.Host.Get<IStatusBarService>().Items, i => i.Id == VimIntegration.ToggleItemId && i.CommandId == "vim.toggle");
        Assert.Contains(commands.All, c => c.Id == "vim.toggle" && c.Title == "Toggle Vim Mode");

        await commands.ExecuteAsync("vim.toggle");
        await DevScene.SettleAsync(3);
        Assert.False(app.Vim.Enabled);
        Assert.Equal(0, app.Vim.EngineCount);
        Assert.Equal(EditorCaretShape.Line, modal.CaretShape);
        Assert.False(modal.IsAttached);
        Assert.Null(app.ModeText);
        Assert.DoesNotContain(app.Host.Get<IStatusBarService>().Items, i => i.Id == VimIntegration.ModeItemId);
        Assert.Equal("Vim: Off", app.Vim.StatusText(VimIntegration.ToggleItemId));
        app.SetCaret(0, 0);
        app.Keys("x");                                                    // a plain editor: typing inserts
        Assert.Equal("xabc\n", app.Text);

        await commands.ExecuteAsync("vim.toggle");
        await DevScene.SettleAsync(3);
        Assert.True(app.Vim.Enabled);
        Assert.Equal(1, app.Vim.EngineCount);
        Assert.Equal(EditorCaretShape.Block, modal.CaretShape);
        await app.FocusAsync();
        Assert.Equal("-- NORMAL --", app.ModeText);
        app.SetCaret(0, 0);
        app.Keys("x");
        Assert.Equal("abc\n", app.Text);

        // the setting switches it too (live), as does its older spelling
        await app.Host.Get<IConfigurationService>().UpdateAsync("vim.enable", false);
        Assert.True(await RealAppHost.WaitFor(() => !app.Vim.Enabled && app.Vim.EngineCount == 0));
        await app.Host.Get<IConfigurationService>().UpdateAsync("vim.enable", true);
        Assert.True(await RealAppHost.WaitFor(() => app.Vim.Enabled && app.Vim.EngineCount == 1));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Vim_owns_its_ctrl_keys_in_normal_mode_and_app_chords_keep_working()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => "line " + i)) + "\n";
        await using var app = await VimApp.StartAsync(text, settings: Quiet);
        var keyboard = app.Host.Shell.Keyboard;
        var executed = keyboard.ExecutedCount;
        app.Keys("<C-d>");                                                // Vim's half page down, not the VS "copy lines down"
        Assert.True(app.Caret.Line > 5, app.Describe());
        Assert.Equal(text, app.Text);
        app.Keys("<C-f><C-b><C-u><C-e><C-y>");
        app.Keys("10G<C-o>");                                             // jump list back
        Assert.Equal(executed, keyboard.ExecutedCount);                    // none of these reached a key binding
        app.Keys("gg<C-v>");
        Assert.Equal("-- VISUAL BLOCK --", app.ModeText);
        app.Keys("<Esc>j<C-p>");                                          // Ctrl-P in Normal mode: Vim's k
        Assert.Equal(0, app.Caret.Line);
        app.Keys("<C-a>");
        Assert.StartsWith("line 1\n", app.Text);                          // Ctrl-A increments, not Select All

        app.Keys(OperatingSystem.IsMacOS() ? "<D-S-p>" : "<C-S-p>");      // the command palette
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<QuickInputViewModel>().IsVisible));
        app.Host.Get<QuickInputViewModel>().Cancel();
        await DevScene.SettleAsync(3);
        await app.FocusAsync();
        Assert.Equal(executed + 1, keyboard.ExecutedCount);

        app.Keys(OperatingSystem.IsMacOS() ? "i<D-p>" : "i<C-p>");        // Insert mode: Ctrl+P (Cmd+P) is Quick Open
        Assert.True(await RealAppHost.WaitFor(() => app.Host.Get<QuickInputViewModel>().IsVisible));
        app.Host.Get<QuickInputViewModel>().Cancel();
        await DevScene.SettleAsync(3);
        await app.FocusAsync();
        Assert.Equal("-- INSERT --", app.ModeText);
        app.Keys("<C-w>");                                                // Insert mode Ctrl-W deletes the word before the cursor
        app.Keys("<Esc>");
        Assert.Equal(executed + 2, keyboard.ExecutedCount);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Ctrl_v_is_visual_block_outside_insert_mode_and_the_clipboard_keys_reach_the_editor()
    {
        // the shell binds Ctrl+C/X/V, Ctrl+Insert, Shift+Insert, Shift+Delete to the editor (when editorTextFocus); Vim takes its keys first
        await using var app = await VimApp.StartAsync("alpha beta\ngamma\n", settings: Quiet);
        var clipboard = app.Host.Window.Clipboard!;
        await clipboard.SetTextAsync("PASTED");
        await DevScene.SettleAsync(2);

        app.Keys("<C-v>");                                                // Normal mode: visual block, not the editor's paste
        Assert.Equal("-- VISUAL BLOCK --", app.ModeText);
        app.Keys("<Esc>");
        Assert.Equal("alpha beta\ngamma\n", app.Text);

        await app.KeysAsync(OperatingSystem.IsMacOS() ? "A <D-v>" : "A <C-v>", 10); // Insert mode: Ctrl+V (Cmd+V) is the editor's paste
        Assert.Equal("alpha beta PASTED\ngamma\n", app.Text);
        Assert.Equal("-- INSERT --", app.ModeText);
        await app.KeysAsync("<S-Insert>", 10);                            // and so is Shift+Insert
        Assert.Equal("alpha beta PASTEDPASTED\ngamma\n", app.Text);
        app.Keys("<Esc>");
        Assert.Equal("-- NORMAL --", app.ModeText);
        app.Keys("u");                                                    // the pastes belong to the insert's undo step
        Assert.Equal("alpha beta\ngamma\n", app.Text);

        app.Keys("j0vll");                                                // Visual "gam": Ctrl+C and Ctrl+Insert copy the selection
        await app.KeysAsync(OperatingSystem.IsMacOS() ? "<D-c>" : "<C-c>", 5);
        Assert.Equal("gam", await clipboard.TryGetTextAsync());
        await clipboard.SetTextAsync("other");
        await app.KeysAsync("<C-Insert>", 5);
        Assert.Equal("gam", await clipboard.TryGetTextAsync());
        await clipboard.SetTextAsync("other");
        await app.KeysAsync("<S-Del>", 10);                               // Shift+Delete cuts it (Vim's own <Del> would not touch the clipboard)
        Assert.Equal("alpha beta\nma\n", app.Text);
        Assert.Equal("gam", await clipboard.TryGetTextAsync());
        await app.KeysAsync("<Esc>");
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal("alpha beta\nma\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Handle_keys_gives_a_key_back_to_the_application()
    {
        // macOS's preset binds Find to Cmd+F: Ctrl+F is bound as on Linux, so the key given back has a binding to reach
        await using var app = await VimApp.StartAsync("one\ntwo\nthree\n", settings: Quiet + ",\n\"vim.handleKeys\": { \"<C-f>\": false, \"<C-h>\": true }",
            seed: h => { if (OperatingSystem.IsMacOS()) h.Home.WriteFile("config/keybindings.json", """[ { "key": "ctrl+f", "command": "actions.find" } ]"""); });
        var keyboard = app.Host.Shell.Keyboard;
        var executed = keyboard.ExecutedCount;
        app.Keys("<C-f>");                                                // the editor's Find (VS keymap), not Vim's page down
        Assert.Equal(executed + 1, keyboard.ExecutedCount);
        await DevScene.SettleAsync(3);
        await app.FocusAsync();
        app.Keys("ll<C-h>");                                              // given to Vim: <C-h> is h in Normal mode, not the editor's Replace
        Assert.Equal(new TextPosition(0, 1), app.Caret);
        Assert.Equal(executed + 1, keyboard.ExecutedCount);

        await app.Host.Get<IConfigurationService>().UpdateAsync("vim.handleKeys", new Dictionary<string, bool>());
        await DevScene.SettleAsync(10);
        app.Keys("gg<C-f>");                                              // live: Vim's again
        Assert.Equal(executed + 1, keyboard.ExecutedCount);
    }
}
