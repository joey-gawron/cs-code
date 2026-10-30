using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit.Editing;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Vim;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Vim;
using Avalonia.VisualTree;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// Vim in the REAL window: real key events into the focused editor of the real composition; checks the real document, the editor's caret and
/// selection, the caret shape and the status bar.
/// </summary>
public class VimKeysTests
{
    // Quick suggestions off: nothing but the keys themselves changes the text or opens a popup (VimIntelliSenseTests covers the popup).
    private const string Quiet = "\"editor.quickSuggestions\": false";

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Vim_is_on_by_default_and_normal_mode_keys_edit_the_document()
    {
        await using var app = await VimApp.StartAsync("one two three\nfour five\n", settings: Quiet);
        Assert.True(app.Vim.Enabled);
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal(EditorCaretShape.Block, app.View.Modal.CaretShape);
        app.Keys("wdw");
        Assert.Equal("one three\nfour five\n", app.Text);
        Assert.Equal(new TextPosition(0, 4), app.Caret);
        app.Keys("ihello <Esc>");
        Assert.Equal("one hello three\nfour five\n", app.Text);
        Assert.Equal(new TextPosition(0, 9), app.Caret);
        Assert.Equal("-- NORMAL --", app.ModeText);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Motions_with_counts_operators_and_text_objects()
    {
        await using var app = await VimApp.StartAsync("alpha beta gamma delta\nsay \"hello there\" now\n(a, (b, c), d)\nline4\nline5\nline6\n", settings: Quiet);
        app.Keys("3w");
        Assert.Equal(new TextPosition(0, 17), app.Caret);
        app.Keys("2b");
        Assert.Equal(new TextPosition(0, 6), app.Caret);
        app.Keys("j$");
        Assert.Equal(new TextPosition(1, 20), app.Caret);
        app.Keys("0fhci\"bye<Esc>");
        Assert.Equal("say \"bye\" now", app.Text.Split('\n')[1]);
        app.Keys("jf(;di(");                                  // inner parentheses of the nested pair
        Assert.Equal("(a, (), d)", app.Text.Split('\n')[2]);
        app.Keys("ggdaw");
        Assert.Equal("beta gamma delta", app.Text.Split('\n')[0]);
        app.Keys("yiwwP");
        Assert.Equal("beta betagamma delta", app.Text.Split('\n')[0]);
        app.Keys("3Gd2j");                                    // a count before the operator's motion: three lines
        Assert.Equal(["beta betagamma delta", "say \"bye\" now", "line6", ""], app.Text.Split('\n'));
        app.Keys("G");
        Assert.Equal(3, app.Caret.Line);
        app.Keys("gg5l");
        Assert.Equal(new TextPosition(0, 5), app.Caret);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Visual_char_line_and_block_modes_select_in_the_editor_and_block_insert_works()
    {
        await using var app = await VimApp.StartAsync("abcd\nefgh\nijkl\nmnop\n", settings: Quiet);
        app.Keys("lvl");
        Assert.Equal("-- VISUAL --", app.ModeText);
        Assert.Equal("bc", app.View.Modal.Controller.Selections.Count == 1 ? app.View.Document.GetText(app.View.Modal.Controller.Selections[0]) : null);
        app.Keys("d");
        Assert.Equal("ad\nefgh\nijkl\nmnop\n", app.Text);
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.True(app.View.Modal.Controller.Selections[0].IsEmpty);

        app.Keys("jVj");
        Assert.Equal("-- VISUAL LINE --", app.ModeText);
        Assert.Equal("efgh\nijkl\n", app.View.Document.GetText(app.View.Modal.Controller.Selections[0]).Replace("\r\n", "\n"));
        app.Keys("d");
        Assert.Equal("ad\nmnop\n", app.Text);

        await app.KeysAsync("ggi0<Esc>jI1<Esc>gg");
        Assert.Equal("0ad\n1mnop\n", app.Text);
        app.Keys("l<C-v>j");
        Assert.Equal("-- VISUAL BLOCK --", app.ModeText);
        Assert.IsType<RectangleSelection>(EditorSelection(app));
        app.Keys("IXY<Esc>");                                  // block insert repeats on every line of the block
        Assert.Equal("0XYad\n1XYmnop\n", app.Text);
        app.Keys("gg$<C-v>j$AZ<Esc>");                          // $ block append: ragged line ends
        Assert.Equal("0XYadZ\n1XYmnopZ\n", app.Text);
    }

    private static Selection EditorSelection(VimApp app)
    {
        var area = app.View.GetVisualDescendants().OfType<TextArea>().Single();
        return area.Selection;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Dot_repeat_and_undo_redo_agree_with_the_editors_undo()
    {
        await using var app = await VimApp.StartAsync("a1\na2\na3\na4\n", settings: Quiet);
        var commands = app.Host.Get<ICommandService>();
        app.Keys("A;<Esc>j.j.");
        Assert.Equal("a1;\na2;\na3;\na4\n", app.Text);
        Assert.True(app.View.Document.IsDirty);
        app.Keys("u");
        Assert.Equal("a1;\na2;\na3\na4\n", app.Text);
        // the editor's Undo (menu, toolbar, Ctrl+Z) is Vim's u: the same history, one Vim change per step
        await commands.ExecuteAsync("undo");
        await DevScene.SettleAsync(3);
        Assert.Equal("a1;\na2\na3\na4\n", app.Text);
        app.Keys("u");
        Assert.Equal("a1\na2\na3\na4\n", app.Text);
        Assert.False(app.View.Document.IsDirty); // undone in the editor's own history: back to the saved text, not dirty
        await commands.ExecuteAsync("redo");
        await DevScene.SettleAsync(3);
        Assert.Equal("a1;\na2\na3\na4\n", app.Text);
        app.Keys("<C-r><C-r>");
        Assert.Equal("a1;\na2;\na3;\na4\n", app.Text);
        app.Keys("u");
        Assert.Equal("a1;\na2;\na3\na4\n", app.Text);

        // an insert typed through the editor's typing path is one step, also for the editor's undo
        app.Keys("Go(x<Esc>");
        Assert.Equal("a1;\na2;\na3\na4\n\n(x)", app.Text);      // below the empty last line; the editor auto-closed the bracket
        await commands.ExecuteAsync("undo");
        await DevScene.SettleAsync(3);
        Assert.Equal("a1;\na2;\na3\na4\n", app.Text);
        Assert.Equal("-- NORMAL --", app.ModeText);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Registers_and_the_system_clipboard()
    {
        await using var app = await VimApp.StartAsync("first line\nsecond line\n", settings: Quiet);
        app.Keys("\"ayyj\"ap");
        Assert.Equal("first line\nsecond line\nfirst line\n", app.Text);
        app.Keys("gg\"+yiw");                                      // "+ writes the system clipboard
        await DevScene.SettleAsync(5);
        var clipboard = app.Host.Window.Clipboard!;
        Assert.Equal("first", await clipboard.TryGetTextAsync());
        await clipboard.SetTextAsync("FROM-OUTSIDE");               // another application copies something
        await DevScene.SettleAsync(2);
        await app.KeysAsync("G\"+P", 10);                           // the keys after "+ wait for the clipboard read
        Assert.Equal("first line\nsecond line\nfirst line\nFROM-OUTSIDE", app.Text);
        app.Keys("\"*yy");                                         // "* is the clipboard as well
        await DevScene.SettleAsync(5);
        Assert.Equal("FROM-OUTSIDE\n", (await clipboard.TryGetTextAsync())?.Replace("\r\n", "\n"));
        app.Keys(":reg a<CR>");                                    // several lines: the status bar shows the first, the Vim output channel all
        Assert.Contains("lines in Output", app.CommandLineText);
        Assert.Contains("first line", app.Host.Get<IOutputService>().GetOrCreate(VimIntegration.OutputChannel).GetText());
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Search_and_n_move_the_caret_and_highlight_the_matches()
    {
        await using var app = await VimApp.StartAsync("foo one\nbar foo\nfoo three\n", settings: Quiet + ",\n\"vim.hlsearch\": true");
        app.Keys("/foo");
        Assert.Equal("/foo█", app.CommandLineText);
        Assert.NotNull(app.View.Modal.PatternHighlight);            // incsearch
        app.Keys("<CR>");
        Assert.Equal(new TextPosition(1, 4), app.Caret);
        Assert.Equal("foo", app.Vim.SearchHighlight?.ToString());
        Assert.Same(app.Vim.SearchHighlight, app.View.Modal.PatternHighlight); // hlsearch: every view shows the matches
        app.Keys("n");
        Assert.Equal(new TextPosition(2, 0), app.Caret);
        app.Keys("N");
        Assert.Equal(new TextPosition(1, 4), app.Caret);
        app.Keys(":noh<CR>");
        Assert.Null(app.View.Modal.PatternHighlight);
        app.Keys("/nothere<CR>");
        Assert.Equal("E486: Pattern not found: nothere", app.CommandLineText);
        app.Keys("j");
        Assert.Null(app.CommandLineText);                          // a message lasts until the next key
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Substitute_with_confirmation_asks_in_the_status_bar()
    {
        await using var app = await VimApp.StartAsync("a1 a2\na3\na4\n", settings: Quiet);
        app.Keys(":%s/a/b/gc");
        Assert.Equal(":%s/a/b/gc█", app.CommandLineText);
        app.Keys("<CR>");
        Assert.StartsWith("replace with b", app.CommandLineText);
        app.Keys("y");
        Assert.StartsWith("replace with b", app.CommandLineText);
        app.Keys("n");
        app.Keys("y");
        app.Keys("q");
        Assert.Equal("b1 a2\nb3\na4\n", app.Text);
        app.Keys("u");
        Assert.Equal("a1 a2\na3\na4\n", app.Text);                  // the whole :s is one undo step
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Macros_record_replay_and_recurse()
    {
        await using var app = await VimApp.StartAsync("1 x\n2 x\n3 x\n4 x\n5 x\n", settings: Quiet);
        app.Keys("qa");
        Assert.Equal("-- NORMAL -- recording @a", app.ModeText);
        app.Keys("A!<Esc>");
        Assert.Contains("recording @a", app.ModeText);
        app.Keys("jq");
        Assert.Equal("-- NORMAL --", app.ModeText);
        app.Keys("2@a");
        Assert.Equal("1 x!\n2 x!\n3 x!\n4 x\n5 x\n", app.Text);
        // recursive: qbq clears b, then the macro calls itself until j fails at the last line
        app.Keys("qbqqb$xj@bq");
        await DevScene.SettleAsync(3);
        app.Keys("@b");
        await DevScene.SettleAsync(5);
        Assert.Equal("1 x!\n2 x!\n3 x!\n4 \n5 \n", app.Text);
        app.Keys("u");
        Assert.Equal("1 x!\n2 x!\n3 x!\n4 \n5 x\n", app.Text);       // one undo step per @b (the recording's own change is separate)
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Mode_labels_showcmd_and_caret_shapes_follow_the_engine()
    {
        await using var app = await VimApp.StartAsync("abc def\nxyz\n", settings: Quiet);
        var modal = app.View.Modal;
        app.Keys("2d");
        Assert.Equal("2d", app.ShowCommandText);
        Assert.Equal(EditorCaretShape.Underline, modal.CaretShape);    // operator pending
        app.Keys("<Esc>");
        Assert.Null(app.ShowCommandText);
        Assert.Equal(EditorCaretShape.Block, modal.CaretShape);
        app.Keys("i");
        Assert.Equal("-- INSERT --", app.ModeText);
        Assert.Equal(EditorCaretShape.Line, modal.CaretShape);
        app.Keys("<C-o>");
        Assert.Equal("-- (insert) --", app.ModeText);
        app.Keys("l<Esc>R");                                             // Esc from (0,1) puts the cursor back on the 'a'
        Assert.Equal("-- REPLACE --", app.ModeText);
        Assert.Equal(EditorCaretShape.Underline, modal.CaretShape);
        app.Keys("XY<Esc>");
        Assert.Equal("XYc def\nxyz\n", app.Text);
        app.Keys("v");
        Assert.Equal(EditorCaretShape.Block, modal.CaretShape);
        app.Keys("<Esc>:");
        Assert.Null(app.ModeText);                                       // the command line takes the mode's place
        Assert.Equal(":█", app.CommandLineText);
        app.Keys("s/<Left>");
        Assert.Equal(":s▏/", app.CommandLineText);
        app.Keys("<Esc>");
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Null(app.CommandLineText);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Text_input_without_key_presses_is_typed_text_ime_commits_included()
    {
        await using var app = await VimApp.StartAsync("abc\n", settings: Quiet);
        app.Commit("x");                                                 // Normal mode: a command
        Assert.Equal("bc\n", app.Text);
        app.Keys("i");
        app.Commit("日本語");                                             // an IME commit in Insert mode
        app.Commit("😀");
        app.Keys("<Esc>");
        Assert.Equal("日本語😀bc\n", app.Text);
        app.Keys("u");
        Assert.Equal("bc\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Scrolling_commands_and_folds_drive_the_editor_viewport()
    {
        var text = string.Join("\n", Enumerable.Range(0, 300).Select(i => i % 10 == 0 ? $"block {i}:" : $"    line {i}")) + "\n";
        await using var app = await VimApp.StartAsync(text, settings: Quiet);
        var modal = app.View.Modal;
        await RealAppHost.WaitFor(() => modal.VisibleLineCount > 5 && modal.LastVisibleLine > 5);
        app.Keys("50Gzt");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Render);
        await DevScene.SettleAsync(3);
        Assert.Equal(49, modal.FirstVisibleLine);
        var page = modal.VisibleLineCount;
        app.Keys("<C-d>");
        await DevScene.SettleAsync(3);
        Assert.True(app.Caret.Line > 49, app.Describe());
        Assert.True(modal.FirstVisibleLine > 49);
        app.Keys("<C-e>");
        await DevScene.SettleAsync(3);
        _ = page;

        // folds: the editor computes indentation regions; zc closes the one under the caret, j steps over it, zo opens it
        await RealAppHost.WaitFor(() => modal.TryGetClosedFold(0, out _, out _) || app.View.GetVisualDescendants().OfType<AvaloniaEdit.Folding.FoldingMargin>().Any());
        app.Keys("ggjzc");
        Assert.True(await RealAppHost.WaitFor(() => modal.TryGetClosedFold(1, out _, out _)), "zc closed no fold (were the regions computed?)");
        Assert.True(modal.TryGetClosedFold(0, out var s, out var e) && s == 0 && e == 9, $"{s}..{e}");
        app.Keys("j");
        Assert.Equal(10, app.Caret.Line);                                // the closed fold counts as one line
        app.Keys("kzo");
        Assert.False(modal.TryGetClosedFold(1, out _, out _));
        app.Keys("zM");
        Assert.True(modal.TryGetClosedFold(25, out _, out _));
        app.Keys("zR");
        Assert.False(modal.TryGetClosedFold(25, out _, out _));
    }

    private sealed class UpperCaseFormatter : CsCode.Core.Languages.IDocumentFormattingProvider
    {
        public ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, CsCode.Core.Languages.FormattingOptions options, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<TextEdit>?>(null);

        public ValueTask<IReadOnlyList<TextEdit>?> FormatRangeAsync(ITextDocument document, TextRange range, CsCode.Core.Languages.FormattingOptions options, CancellationToken ct)
        {
            var text = document.GetText(range);
            return ValueTask.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(range, text.ToUpperInvariant())]);
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Language_hooks_comment_tokens_the_formatter_yank_flash_wrapping_and_mapping_timeouts()
    {
        await using var app = await VimApp.StartAsync("class C\n{\n    int x;\n}\n", "c.cs",
            Quiet + ",\n\"vim.insertModeKeyBindings\": [{ \"before\": [\"j\", \"k\"], \"after\": [\"<Esc>\"] }]");
        app.Keys("jjgcc");                                                 // the language configuration's line comment
        Assert.Equal("class C\n{\n    // int x;\n}\n", app.Text);
        app.Keys("u");

        using (app.Host.Get<CsCode.Core.Languages.ILanguageFeatureRegistry>().Register<CsCode.Core.Languages.IDocumentFormattingProvider>(DocumentSelector.All, new UpperCaseFormatter()))
        {
            app.Keys("gg==");                                              // = goes to the language's formatter (asynchronously)
            Assert.True(await RealAppHost.WaitFor(() => app.Text.StartsWith("CLASS C\n", StringComparison.Ordinal)), app.Describe());
        }
        app.Keys("u");
        Assert.Equal("class C\n{\n    int x;\n}\n", app.Text);       // the formatter's edit is an undo step of its own

        app.Keys("yiw");
        Assert.NotNull(app.View.Modal.FlashedRange);                      // highlightedyank
        Assert.True(await RealAppHost.WaitFor(() => app.View.Modal.FlashedRange is null, 3000));

        app.Keys("Aab");                                                 // "jk" mapped to <Esc> in Insert mode: j waits for timeoutlen...
        app.Keys("j");
        Assert.Equal("-- INSERT --", app.ModeText);
        app.Keys("k");                                                   // ...and jk is the mapping
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal("class Cab", app.Text.Split('\n')[0]);
        app.Keys("Aj");                                                  // a lone j is typed once the timeout passes (real time, dispatcher timer)
        Assert.True(await RealAppHost.WaitFor(() => app.Text.Split('\n')[0] == "class Cabj", 5000), app.Describe());
        app.Keys("<Esc>");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Display_line_motions_follow_the_editors_word_wrap()
    {
        var longLine = string.Join(" ", Enumerable.Range(0, 120).Select(i => "word" + i));
        await using var app = await VimApp.StartAsync(longLine + "\nnext\n", settings: Quiet + ",\n\"editor.wordWrap\": \"on\"");
        var modal = app.View.Modal;
        Assert.True(await RealAppHost.WaitFor(() => modal.DisplayRowsOf(0) > 1), "the long line did not wrap");
        Assert.True(modal.WrapColumns > 0);
        app.Keys("gj");                                                  // the next display row of the same line
        Assert.Equal(0, app.Caret.Line);
        Assert.True(app.Caret.Character > 0);
        app.Keys("j");                                                   // j still moves by buffer lines
        Assert.Equal(1, app.Caret.Line);
    }
}
