using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>Insert mode next to the editor's own typing machinery: IntelliSense, snippets, and selections made with the mouse.</summary>
public class VimIntelliSenseTests
{
    private sealed class Words : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => ["."];

        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
            ValueTask.FromResult<CompletionList?>(new CompletionList([
                new CompletionItem { Label = "alpha", SortText = "1" },
                new CompletionItem { Label = "alphabet", SortText = "2" },
                new CompletionItem { Label = "beta", SortText = "3" },
            ]));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_completion_list_keeps_tab_enter_arrows_and_escape_and_the_insert_stays_one_undo_step()
    {
        await using var app = await VimApp.StartAsync("x\n");
        using var _ = app.Host.Get<ILanguageFeatureRegistry>().Register<ICompletionProvider>(DocumentSelector.All, new Words());
        var modal = app.View.Modal;

        app.Keys("A al");                                                  // quick suggestions open the list while typing
        Assert.True(await RealAppHost.WaitFor(() => modal.IsCompletionOpen), "no completion list");
        app.Keys("<Down>");                                                // the list's selection, not Vim's cursor
        Assert.True(modal.IsCompletionOpen);
        Assert.Equal("-- INSERT --", app.ModeText);
        app.Keys("<CR>");                                                  // accepts (the second item), no new line
        Assert.False(modal.IsCompletionOpen);
        Assert.Equal("x alphabet\n", app.Text);
        Assert.Equal("-- INSERT --", app.ModeText);

        app.Keys(" be");
        Assert.True(await RealAppHost.WaitFor(() => modal.IsCompletionOpen));
        app.Keys("<Esc>");                                                 // the first Esc closes the list...
        Assert.False(modal.IsCompletionOpen);
        Assert.Equal("-- INSERT --", app.ModeText);
        app.Keys("<Esc>");                                                 // ...the second leaves Insert mode
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal("x alphabet be\n", app.Text);

        app.Keys("A <C-n>");                                               // Vim's keyword completion opens the editor's list
        Assert.True(await RealAppHost.WaitFor(() => modal.IsCompletionOpen));
        app.Keys("<C-n><C-n><C-p><Tab>");                                  // Ctrl-N / Ctrl-P move through it, Tab accepts
        Assert.False(modal.IsCompletionOpen);
        Assert.Equal("x alphabet be alphabet\n", app.Text);
        app.Keys("<Esc>");

        app.Keys("u");                                                     // each Insert session, completions included, is one step
        Assert.Equal("x alphabet be\n", app.Text);
        app.Keys("u");
        Assert.Equal("x\n", app.Text);
        Assert.False(app.View.Document.IsDirty);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Tab_moves_through_a_snippet_and_escape_ends_it_with_insert_mode()
    {
        await using var app = await VimApp.StartAsync("\n", settings: "\"editor.quickSuggestions\": false");
        app.Keys("i");
        await app.Host.Get<ICommandService>().ExecuteAsync("editor.action.insertSnippet", "for (${1:i}; ${2:n}) {$0}");
        await DevScene.SettleAsync(5);
        await app.FocusAsync();
        var modal = app.View.Modal;
        Assert.True(await RealAppHost.WaitFor(() => modal.IsSnippetActive), "no snippet session");
        app.Keys("k<Tab>");                                               // types over the first placeholder, Tab goes to the next
        Assert.True(modal.IsSnippetActive);
        app.Keys("m");
        Assert.Equal("for (k; m) {}\n", app.Text);
        app.Keys("<Esc>");
        Assert.False(modal.IsSnippetActive);
        Assert.Equal("-- NORMAL --", app.ModeText);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_selection_made_with_the_mouse_becomes_visual_mode()
    {
        await using var app = await VimApp.StartAsync("hello world\nsecond\n", settings: "\"editor.quickSuggestions\": false");
        // what a drag leaves behind: a selection, the caret at its end, then the button goes up over the editor
        app.View.Modal.Controller.SetSelection(new TextRange(new TextPosition(0, 6), new TextPosition(0, 11)), reveal: false);
        var centre = app.View.TranslatePoint(new Point(app.View.Bounds.Width / 2, 10), app.Host.Window) ?? default;
        app.Host.Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        await DevScene.SettleAsync(3);
        Assert.Equal("-- VISUAL --", app.ModeText);
        app.Keys("d");
        Assert.Equal("hello \nsecond\n", app.Text);
        Assert.Equal("-- NORMAL --", app.ModeText);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_click_in_visual_mode_ends_it_where_the_click_put_the_caret()
    {
        await using var app = await VimApp.StartAsync("hello world\nsecond line\n", settings: "\"editor.quickSuggestions\": false");
        app.Keys("wvll");
        Assert.Equal("-- VISUAL --", app.ModeText);
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right })   // a right-click outside the selection moves the caret too
        {
            var at = app.View.TranslatePoint(PointOf(app, 1, 3), app.Host.Window) ?? default;
            app.Host.Window.MouseDown(at, button, RawInputModifiers.None);
            app.Host.Window.MouseUp(at, button, RawInputModifiers.None);
            await DevScene.SettleAsync(3);
            if (button == MouseButton.Right) CsCode.Ui.Menus.ContextMenus.LastOpened?.Close();
            await app.FocusAsync();
            Assert.Equal("-- NORMAL --", app.ModeText);
            Assert.Equal(new TextPosition(1, 3), app.Caret);
            Assert.False(app.View.Modal.HasSelection);
            app.Keys("x");                                                      // and the engine works from there
            Assert.Equal(button == MouseButton.Left ? "hello world\nsecnd line\n" : "hello world\nsecd line\n", app.Text);
            app.Keys("ggwvll");
            Assert.Equal("-- VISUAL --", app.ModeText);
        }
    }

    private static Point PointOf(VimApp app, int line, int column)
    {
        var tv = app.View.GetVisualDescendants().OfType<AvaloniaEdit.Editing.TextArea>().First().TextView;
        var p = tv.GetVisualPosition(new AvaloniaEdit.TextViewPosition(line + 1, column + 1), AvaloniaEdit.Rendering.VisualYPosition.TextMiddle) - tv.ScrollOffset;
        return tv.TranslatePoint(new Point(p.X + 1, p.Y), app.View) ?? p;
    }
}
