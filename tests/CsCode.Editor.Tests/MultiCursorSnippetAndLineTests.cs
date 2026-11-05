using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Multi-cursor, second round: snippets at every caret (Insert Snippet, a snippet completion) with a session whose tab stops are selected at
/// every caret and whose mirrors follow per caret, and the line commands (move / copy / delete / comment / indent / outdent lines) at every
/// caret's lines, each ONE undo step with the carets restored.
/// </summary>
public class MultiCursorSnippetAndLineTests
{
    private static async Task<(ViewHost Host, EditorDocument Doc, CodeEditorControl View, Avalonia.Controls.Window Window)> Open(string text, string file = "test.cs")
    {
        var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync(text, file);
        host.Factory.NotifyFocused(view);
        return (host, doc, view, window);
    }

    private static async Task Run(ViewHost host, string command, params object?[] args)
    {
        await host.Commands.ExecuteAsync(command, args);
        ViewHost.Pump();
    }

    private static string[] SelectedTexts(CodeEditorControl view) =>
        view.MultiCursor.Current.Carets.Select(c => view.Document.Buffer.GetText(c.Start, c.Length)).ToArray();

    private static TextPosition[] ActivePositions(CodeEditorControl view) =>
        view.MultiCursor.Current.Carets.Select(c => view.Document.PositionAt(c.Active)).ToArray();

    private static void Press(Avalonia.Controls.Window w, Key key, RawInputModifiers m = RawInputModifiers.None)
    {
        Key(w, key, m);
        ViewHost.Pump();
    }

    private static void TypeKeys(Avalonia.Controls.Window w, string text)
    {
        foreach (var ch in text) w.KeyTextInput(ch.ToString());
        ViewHost.Pump();
    }

    private static void Undo(CodeEditorControl view)
    {
        view.Controller.Undo();
        ViewHost.Pump();
    }

    // ------------------------------------------------------------------ snippets

    [AvaloniaFact]
    public async Task Insert_snippet_at_several_carets_selects_each_tab_stop_at_every_caret_and_keeps_each_instances_mirrors()
    {
        var (host, doc, view, window) = await Open("a\nb", file: "test.txt");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        await Run(host, "editor.action.insertSnippet", "(${1:msg}, ${1}, ${2:n})$0");
        Assert.Equal("a(msg, msg, n)\nb(msg, msg, n)", doc.GetText());
        Assert.Equal(["msg", "msg"], SelectedTexts(view)); // stop 1 of both instances, selected as carets
        Assert.True(view.Feature<SnippetFeature>()!.IsActive);

        TypeKeys(window, "xy");
        Assert.Equal("a(xy, xy, n)\nb(xy, xy, n)", doc.GetText()); // each instance's mirror follows its own placeholder

        Press(window, Avalonia.Input.Key.Tab);
        Assert.Equal(["n", "n"], SelectedTexts(view)); // stop 2 at both carets
        TypeKeys(window, "5");
        Assert.Equal("a(xy, xy, 5)\nb(xy, xy, 5)", doc.GetText());
        Press(window, Avalonia.Input.Key.Tab);
        Assert.Equal([new TextPosition(0, 12), new TextPosition(1, 12)], ActivePositions(view)); // $0: the session ends, the carets stay
        Assert.False(view.Feature<SnippetFeature>()!.IsActive);

        Undo(view); // "5"
        Undo(view); // "xy" with its mirrors: one step
        Assert.Equal("a(msg, msg, n)\nb(msg, msg, n)", doc.GetText());
        Undo(view); // the insertion at both carets: one step
        Assert.Equal("a\nb", doc.GetText());
        Assert.Equal([new TextPosition(0, 1), new TextPosition(1, 1)], ActivePositions(view));
    }

    [AvaloniaFact]
    public async Task A_snippet_takes_each_carets_own_selection_and_indentation()
    {
        var (host, doc, view, _) = await Open("    one\n  two", file: "test.txt");
        using var _h = host;
        view.Controller.SetSelections([TextRange.FromLines(0, 4, 0, 7), TextRange.FromLines(1, 2, 1, 5)]);
        await Run(host, "editor.action.insertSnippet", "[${TM_SELECTED_TEXT}\n\t$0]");
        Assert.Equal("    [one\n        ]\n  [two\n      ]", doc.GetText()); // each copy indented from its own line (tab = 4 spaces)
        Assert.Equal([new TextPosition(1, 8), new TextPosition(3, 6)], ActivePositions(view));
    }

    [AvaloniaFact]
    public async Task Escape_ends_the_snippet_session_first_and_then_collapses_the_carets()
    {
        var (host, _, view, window) = await Open("a\nb", file: "test.txt");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        await Run(host, "editor.action.insertSnippet", "<${1:t}>$0");
        Press(window, Avalonia.Input.Key.Escape);
        Assert.False(view.Feature<SnippetFeature>()!.IsActive);
        Assert.True(view.MultiCursor.IsActive);
        Press(window, Avalonia.Input.Key.Escape);
        Assert.False(view.MultiCursor.IsActive);
    }

    [AvaloniaFact]
    public async Task Accepting_a_snippet_completion_at_several_carets_replaces_the_typed_prefix_at_each_in_one_step()
    {
        var (host, doc, view, window) = await Open("\n", file: "test.txt");
        using var _h = host;
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.ForLanguage("plaintext"), FakeCompletionProvider.Of(
        [
            new CompletionItem { Label = "fori", Kind = CompletionItemKind.Snippet, InsertText = "for ${1:i} in ${2:xs} {$0}", InsertTextFormat = InsertTextFormat.Snippet },
        ]));
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0)]);
        TypeKeys(window, "fo");
        await ViewHost.WaitFor(() => view.Feature<CompletionFeature>()!.IsOpen, "the completion list at the primary");
        Press(window, Avalonia.Input.Key.Enter);
        await ViewHost.WaitFor(() => doc.GetText() == "for i in xs {}\nfor i in xs {}", "the snippet at both carets");
        Assert.Equal(["i", "i"], SelectedTexts(view));
        TypeKeys(window, "k");
        Assert.Equal("for k in xs {}\nfor k in xs {}", doc.GetText());
        Undo(view);
        Undo(view); // the snippet replaced "fo" at both in one step
        Assert.Equal("fo\nfo", doc.GetText());
    }

    // ------------------------------------------------------------------ line commands

    [AvaloniaFact]
    public async Task Move_line_down_and_up_move_every_carets_line_keeping_the_columns_in_one_undo_step()
    {
        var (host, doc, view, _) = await Open("a1\nb2\nc3\nd4\ne5");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(2, 2)]);
        await Run(host, MultiCaretLineCommands.MoveDown);
        Assert.Equal("b2\na1\nd4\nc3\ne5", doc.GetText());
        Assert.Equal([new TextPosition(1, 1), new TextPosition(3, 2)], ActivePositions(view));
        await Run(host, MultiCaretLineCommands.MoveUp);
        Assert.Equal("a1\nb2\nc3\nd4\ne5", doc.GetText());
        Assert.Equal([new TextPosition(0, 1), new TextPosition(2, 2)], ActivePositions(view));
        Undo(view);
        Assert.Equal("b2\na1\nd4\nc3\ne5", doc.GetText());
        Assert.Equal([new TextPosition(1, 1), new TextPosition(3, 2)], ActivePositions(view));
    }

    [AvaloniaFact]
    public async Task A_column_of_carets_moves_as_one_block_and_a_block_at_the_edge_stays()
    {
        var (host, doc, view, _) = await Open("a\nb\nc\nd");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1), TextRange.At(3, 0)]);
        await Run(host, MultiCaretLineCommands.MoveDown);
        Assert.Equal("c\na\nb\nd", doc.GetText()); // lines 1-2 moved below "c"; "d" was the last line
        Assert.Equal([new TextPosition(1, 1), new TextPosition(2, 1), new TextPosition(3, 0)], ActivePositions(view));
    }

    [AvaloniaFact]
    public async Task Copy_line_down_and_up_copy_every_carets_line()
    {
        var (host, doc, view, _) = await Open("a\nb\nc");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(2, 0)]);
        await Run(host, MultiCaretLineCommands.CopyDown);
        Assert.Equal("a\na\nb\nc\nc", doc.GetText());
        Assert.Equal([new TextPosition(1, 1), new TextPosition(4, 0)], ActivePositions(view)); // on the lower copies
        Undo(view);
        await Run(host, MultiCaretLineCommands.CopyUp);
        Assert.Equal("a\na\nb\nc\nc", doc.GetText());
        Assert.Equal([new TextPosition(0, 1), new TextPosition(3, 0)], ActivePositions(view)); // on the upper copies
    }

    [AvaloniaFact]
    public async Task Delete_line_deletes_every_carets_line_and_leaves_a_caret_where_each_was()
    {
        var (host, doc, view, _) = await Open("a\nbb\nc\ndd\ne");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(2, 1)]);
        await Run(host, MultiCaretLineCommands.Delete);
        Assert.Equal("bb\ndd\ne", doc.GetText());
        Assert.Equal([new TextPosition(0, 1), new TextPosition(1, 1)], ActivePositions(view));
        Undo(view);
        Assert.Equal("a\nbb\nc\ndd\ne", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Toggle_line_comment_comments_every_carets_line_and_the_carets_move_with_their_text()
    {
        var (host, doc, view, _) = await Open("x();\ny();\n    z();");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(2, 5)]);
        await Run(host, MultiCaretLineCommands.Comment);
        Assert.Equal("// x();\ny();\n    // z();", doc.GetText());
        Assert.Equal([new TextPosition(0, 4), new TextPosition(2, 8)], ActivePositions(view));
        await Run(host, MultiCaretLineCommands.Comment);
        Assert.Equal("x();\ny();\n    z();", doc.GetText());
        Undo(view);
        Assert.Equal("// x();\ny();\n    // z();", doc.GetText());
        Assert.Equal([new TextPosition(0, 4), new TextPosition(2, 8)], ActivePositions(view));
    }

    [AvaloniaFact]
    public async Task Indent_and_outdent_lines_act_on_every_carets_lines_once()
    {
        var (host, doc, view, _) = await Open("a\nb\nc");
        using var _h = host;
        // two carets on line 0 and a selection over lines 1-2: each line once
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(0, 1), TextRange.FromLines(1, 0, 2, 1)]);
        await Run(host, MultiCaretLineCommands.Indent);
        Assert.Equal("    a\n    b\n    c", doc.GetText());
        await Run(host, MultiCaretLineCommands.Outdent);
        Assert.Equal("a\nb\nc", doc.GetText());
        Undo(view);
        Assert.Equal("    a\n    b\n    c", doc.GetText());
    }

    [AvaloniaFact]
    public async Task With_one_caret_the_line_commands_work_as_before()
    {
        var (host, doc, view, _) = await Open("a\nb");
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Run(host, MultiCaretLineCommands.MoveDown);
        Assert.Equal("b\na", doc.GetText());
        Assert.False(MultiCaretLineCommands.TryRun(view, MultiCaretLineCommands.MoveDown));
    }
}
