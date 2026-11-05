using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// What the status bar's encoding, line-ending and indentation items open (docs/EDITOR.md §7): Reopen / Save with Encoding (a dirty document
/// asks first), LF / CRLF as one undo step that keeps the caret, and the indentation choices as the document's own options over the settings,
/// with VS Code's detection and conversion.
/// </summary>
public class DocumentOptionCommandsTests
{
    private static int ChoiceIndex(string id) => FileEncoding.Choices.Select(c => c.Id).ToList().IndexOf(id);

    // ---- encoding ----

    [AvaloniaFact]
    public async Task Reopen_with_Encoding_decodes_the_file_again_with_the_chosen_encoding()
    {
        using var host = ViewHost.Create();
        var path = host.Dir.Combine("latin.txt");
        File.WriteAllBytes(path, [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']); // "café" in Windows-1252
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var (_, view, window) = await Show(host, doc);
        Assert.Equal("café\n", doc.GetText());
        Assert.Equal("windows1252", doc.EncodingName);
        var changes = 0;
        using var _ = doc.OptionsChanged.Subscribe(_ => changes++);

        host.Window.QuickPickResponses.Enqueue(0); // Reopen with Encoding
        host.Window.QuickPickResponses.Enqueue(ChoiceIndex("utf8"));
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEncoding");
        Assert.Equal("caf�\n", doc.GetText());
        Assert.Equal("utf8", doc.EncodingName);
        Assert.False(doc.IsDirty);
        Assert.Equal(1, changes);
        Assert.False(view.IsDisposed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reopen_with_Encoding_asks_before_it_discards_unsaved_changes()
    {
        using var host = ViewHost.Create();
        var (doc, _, _) = await host.OpenAsync("one\n", "a.txt");
        doc.ApplyEdits([new TextEdit(TextRange.At(0, 0), "x")]);

        host.Window.QuickPickResponses.Enqueue(0);
        host.Window.QuickPickResponses.Enqueue(ChoiceIndex("iso88591"));
        host.Window.MessageActionResponses.Enqueue("Cancel");
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEncoding");
        Assert.Equal("xone\n", doc.GetText());
        Assert.Contains(host.Window.Messages, m => m.Message.Contains("unsaved changes", StringComparison.Ordinal));

        host.Window.QuickPickResponses.Enqueue(0);
        host.Window.QuickPickResponses.Enqueue(ChoiceIndex("iso88591"));
        host.Window.MessageActionResponses.Enqueue("Discard and Reopen");
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEncoding");
        Assert.Equal("one\n", doc.GetText());
        Assert.Equal("iso88591", doc.EncodingName);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaTheory]
    [InlineData("utf16le", new byte[] { 0xFF, 0xFE, (byte)'h', 0, 0xE9, 0, (byte)'\n', 0 })]
    [InlineData("utf8bom", new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', 0xC3, 0xA9, (byte)'\n' })]
    [InlineData("windows1252", new byte[] { (byte)'h', 0xE9, (byte)'\n' })]
    public async Task Save_with_Encoding_writes_the_file_in_the_chosen_encoding(string id, byte[] expected)
    {
        using var host = ViewHost.Create();
        var (doc, _, _) = await host.OpenAsync("hé\n", "a.txt");
        host.Window.QuickPickResponses.Enqueue(1); // Save with Encoding
        host.Window.QuickPickResponses.Enqueue(ChoiceIndex(id));
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEncoding");
        Assert.Equal(expected, File.ReadAllBytes(host.Dir.Combine("a.txt")));
        Assert.Equal(id, doc.EncodingName);
    }

    [Fact]
    public void The_encoding_list_has_VS_Codes_ids_and_labels_and_the_code_page_encodings_round_trip()
    {
        Assert.Equal(("utf8", "UTF-8"), FileEncoding.Choices[0]);
        Assert.Equal(39, FileEncoding.Choices.Count); // every code page of the list is provided by .NET's CodePagesEncodingProvider
        Assert.Contains(("shiftjis", "Japanese (Shift JIS)"), FileEncoding.Choices);
        Assert.Contains(("koi8r", "Cyrillic (KOI8-R)"), FileEncoding.Choices);
        Assert.Equal("UTF-8", FileEncoding.ShortLabel("utf8"));
        Assert.Equal("Windows 1252", FileEncoding.ShortLabel("windows1252"));
        var sjis = FileEncoding.FromSettingName("shift_jis");
        Assert.NotNull(sjis);
        Assert.Same(sjis, FileEncoding.FromSettingName("shiftjis"));
        var bytes = TextFileCodec.Encode("日本語\n", sjis);
        Assert.Equal(Encoding.GetEncoding(932).GetBytes("日本語\n"), bytes);
        Assert.Equal("日本語\n", TextFileCodec.Decode(bytes, sjis));
    }

    // ---- line endings ----

    [AvaloniaFact]
    public async Task Change_End_of_Line_Sequence_rewrites_every_line_ending_as_one_undo_step_and_keeps_the_caret()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("a\r\nbb\r\nccc\r\n", "a.txt");
        Assert.Equal("\r\n", doc.EndOfLine);
        view.Controller.SetSelection(TextRange.At(2, 2));

        host.Window.QuickPickResponses.Enqueue(0); // LF
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEOL");
        Assert.Equal("a\nbb\nccc\n", doc.GetText());
        Assert.Equal("\n", doc.EndOfLine);
        Assert.True(doc.IsDirty);
        Assert.Equal(TextRange.At(2, 2), view.Controller.Selections[0]); // the caret did not jump

        view.Controller.Undo();
        Assert.Equal("a\r\nbb\r\nccc\r\n", doc.GetText());
        Assert.Equal("\r\n", doc.EndOfLine); // undo gives the line ending back too
        Assert.False(doc.IsDirty);
        view.Controller.Redo();
        Assert.Equal("\n", doc.EndOfLine);

        await doc.SaveAsync();
        Assert.Equal("a\nbb\nccc\n", File.ReadAllText(host.Dir.Combine("a.txt")));
    }

    [AvaloniaFact]
    public async Task Change_End_of_Line_Sequence_keeps_a_folded_region_folded()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("head\r\n  a\r\n  b\r\n  c\r\ntail\r\n", "region.txt");
        var folding = view.Feature<Features.FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 1, "the indentation fold");
        view.Controller.SetSelection(TextRange.At(0, 0));
        folding.Fold();
        ViewHost.Pump();
        Assert.Equal([0], folding.FoldedStartLines);

        host.Window.QuickPickResponses.Enqueue(0); // LF
        await host.Commands.ExecuteAsync("workbench.action.editor.changeEOL");
        ViewHost.Pump();
        Assert.Equal("head\n  a\n  b\n  c\ntail\n", doc.GetText());
        Assert.Equal([0], folding.FoldedStartLines); // each line ending moved on its own: the fold kept its lines
    }

    // ---- indentation ----

    [AvaloniaFact]
    public async Task The_indentation_choices_are_the_documents_own_over_the_settings()
    {
        using var host = ViewHost.Create();
        host.Config.Set(SettingKeys.EditorTabSize, 4);
        host.Config.Set(SettingKeys.EditorInsertSpaces, true);
        var (doc, view, _) = await host.OpenAsync("x\n", "a.txt");
        var other = (View.CodeEditorControl)host.Factory.CreateView(doc);

        host.Window.QuickPickResponses.Enqueue(1); // the picker: Indent Using Tabs
        host.Window.QuickPickResponses.Enqueue(1); // tab size 2
        await host.Commands.ExecuteAsync("changeEditorIndentation");
        ViewHost.Pump();
        Assert.Equal(new IndentationOptions(2, false), doc.IndentationOverride);
        Assert.Equal((2, false), (view.Settings.TabSize, view.Settings.InsertSpaces));
        Assert.Equal((2, false), (other.Settings.TabSize, other.Settings.InsertSpaces)); // every view of the document
        Assert.Equal((2, false), View.EditorSettings.IndentationFor(host.Config, doc)); // what the status bar shows
        Assert.Equal(4, host.Config.Get(SettingKeys.EditorTabSize, 0)); // the settings are left alone

        host.Window.QuickPickResponses.Enqueue(5); // Change Tab Display Size: 6, still tabs
        await host.Commands.ExecuteAsync("editor.action.changeTabDisplaySize");
        Assert.Equal(new IndentationOptions(6, false), doc.IndentationOverride);
        host.Window.QuickPickResponses.Enqueue(2); // Indent Using Spaces: 3
        await host.Commands.ExecuteAsync("editor.action.indentUsingSpaces");
        Assert.Equal(new IndentationOptions(3, true), doc.IndentationOverride);

        // what Tab inserts follows it
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 0));
        Assert.Equal(3, view.GetEditingContext().TabSize);
        other.Dispose();
    }

    [AvaloniaFact]
    public async Task Detect_Indentation_from_Content_and_the_conversions_follow_VS_Code()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("class C\n{\n  void M()\n  {\n    x();\n  }\n}\n", "a.txt");

        await host.Commands.ExecuteAsync("editor.action.detectIndentation");
        Assert.Equal(new IndentationOptions(2, true), doc.IndentationOverride);

        await host.Commands.ExecuteAsync("editor.action.indentationToTabs");
        Assert.Equal("class C\n{\n\tvoid M()\n\t{\n\t\tx();\n\t}\n}\n", doc.GetText());
        Assert.Equal(new IndentationOptions(2, false), doc.IndentationOverride);
        await host.Commands.ExecuteAsync("editor.action.indentationToSpaces");
        Assert.Equal("class C\n{\n  void M()\n  {\n    x();\n  }\n}\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("class C\n{\n\tvoid M()\n\t{\n\t\tx();\n\t}\n}\n", doc.GetText()); // one undo step per conversion
    }

    [Theory]
    [InlineData("a\n  b\n    c\n  d\n", 4, true, 2, true)]
    [InlineData("a\n    b\n        c\n    d\n", 2, true, 4, true)]
    [InlineData("a\n\tb\n\t\tc\n\td\n", 4, true, 4, false)]
    [InlineData("a\nb\n", 3, false, 3, false)] // no evidence: the defaults
    [InlineData("const a = b + c,\n      d = b || c;\nif (x)\n  y();\n", 4, true, 2, true)] // the alignment step does not count
    public void Guessing_the_indentation_follows_VS_Codes_guesser(string text, int defaultSize, bool defaultSpaces, int size, bool spaces)
    {
        var doc = new InMemoryTextDocument(new Uri("file:///g.txt"), "plaintext", text);
        Assert.Equal((size, spaces), IndentationOperations.Guess(doc, defaultSize, defaultSpaces));
    }

    [Theory]
    [InlineData("\ta\n  \t b\n", 2, true, "  a\n     b\n")]
    [InlineData("    a\n     b\n  \n", 4, false, "\ta\n\t b\n  \n")]
    [InlineData("x\n", 4, false, "x\n")]
    public void Converting_the_indentation_touches_only_the_leading_whitespace(string text, int tabSize, bool toSpaces, string expected)
    {
        var doc = new InMemoryTextDocument(new Uri("file:///c.txt"), "plaintext", text);
        Assert.True(doc.ApplyEdits(IndentationOperations.Convert(doc, tabSize, toSpaces)));
        Assert.Equal(expected, doc.GetText());
    }

    private static async Task<(EditorDocument Doc, View.CodeEditorControl View, Avalonia.Controls.Window Window)> Show(ViewHost host, EditorDocument doc)
    {
        var view = (View.CodeEditorControl)host.Factory.CreateView(doc);
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = view };
        window.Show();
        ViewHost.Pump();
        view.Editor.TextArea.Focus();
        ViewHost.Pump();
        await Task.CompletedTask;
        return (doc, view, window);
    }
}
