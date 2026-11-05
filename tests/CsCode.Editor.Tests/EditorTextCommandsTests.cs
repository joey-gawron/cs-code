using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Case = CsCode.Editor.Editing.TextTransforms.Case;

namespace CsCode.Editor.Tests;

/// <summary>
/// Toggle Word Wrap, the font zoom keys, the case transforms and Duplicate Selection (docs/EDITOR.md §7): the pure transforms against VS Code's
/// own expectations (linesOperations' tests), then the commands on real views with several carets, one undo step each, the view state and the
/// settings.
/// </summary>
public class EditorTextCommandsTests
{
    // ---- the transforms (VS Code's rules) ----

    [Theory]
    [InlineData(Case.Upper, "hello world", "HELLO WORLD")]
    [InlineData(Case.Lower, "HELLO World", "hello world")]
    [InlineData(Case.Title, "foO baR BaZ", "Foo Bar Baz")]
    [InlineData(Case.Title, "foO'baR'BaZ", "Foo'bar'baz")]
    [InlineData(Case.Title, "foO[baR]BaZ", "Foo[Bar]Baz")]
    [InlineData(Case.Title, "foO`baR~BaZ", "Foo`Bar~Baz")]
    [InlineData(Case.Title, "'physician's assistant'", "'Physician's Assistant'")]
    [InlineData(Case.Snake, "parseHTMLString", "parse_html_string")]
    [InlineData(Case.Snake, "getElementById", "get_element_by_id")]
    [InlineData(Case.Snake, "insertHTML", "insert_html")]
    [InlineData(Case.Snake, "PascalCaseString", "pascal_case_string")]
    [InlineData(Case.Snake, "parseHTML4String", "parse_html4_string")]
    [InlineData(Case.Snake, "Hello World", "hello world")]
    [InlineData(Case.Camel, "camel_case", "camelCase")]
    [InlineData(Case.Camel, "camel-case words", "camelCaseWords")]
    [InlineData(Case.Camel, "Snake_case_word", "SnakeCaseWord")]
    [InlineData(Case.Kebab, "helloWorld", "hello-world")]
    [InlineData(Case.Kebab, "hello_world", "hello-world")]
    [InlineData(Case.Kebab, "parseHTMLString", "parse-html-string")]
    [InlineData(Case.Kebab, "getElementById", "get-element-by-id")]
    [InlineData(Case.Kebab, "Hello World", "hello world")]
    [InlineData(Case.Pascal, "hello_world", "HelloWorld")]
    [InlineData(Case.Pascal, "hello world-wide", "HelloWorldWide")]
    [InlineData(Case.Pascal, "foo.bar_baz", "Foo.BarBaz")]
    public void Each_case_follows_VS_Codes_rules(Case kind, string text, string expected) => Assert.Equal(expected, TextTransforms.Apply(kind, text));

    private static (InMemoryTextDocument Doc, string Text, TextRange[] Selections) Run(string text, TextRange[] selections, Func<ITextDocument, TextTransforms.Result?> transform)
    {
        var doc = new InMemoryTextDocument(new Uri("file:///t.txt"), "plaintext", text);
        var result = transform(doc);
        Assert.NotNull(result);
        Assert.True(doc.ApplyEdits(result.Edits));
        return (doc, doc.GetText(), result.Selections.Select(s => new TextRange(doc.PositionAt(s.Start), doc.PositionAt(s.End))).ToArray());
    }

    [Fact]
    public void A_case_transform_changes_every_selection_and_the_word_at_each_empty_caret()
    {
        var selections = new[] { TextRange.FromLines(0, 0, 0, 3), TextRange.At(1, 1), TextRange.At(1, 7), TextRange.At(1, 2) };
        var (_, text, after) = Run("foo bar\nbaz qux", selections, d => TextTransforms.TransformCase(d, selections, Case.Upper));
        Assert.Equal("FOO bar\nBAZ QUX", text); // "bar" has no caret; the two carets in "baz" change it once
        Assert.Equal([TextRange.FromLines(0, 0, 0, 3), TextRange.At(1, 1), TextRange.At(1, 7), TextRange.At(1, 2)], after);
    }

    [Fact]
    public void A_case_transform_keeps_a_selection_over_its_text_and_a_caret_at_the_end_of_its_word()
    {
        var selections = new[] { TextRange.At(0, 6), TextRange.FromLines(1, 0, 1, 9) };
        var (_, text, after) = Run("fooBar\nsomeThing x", selections, d => TextTransforms.TransformCase(d, selections, Case.Snake));
        Assert.Equal("foo_bar\nsome_thing x", text);
        Assert.Equal([TextRange.At(0, 7), TextRange.FromLines(1, 0, 1, 10)], after);
    }

    [Fact]
    public void A_case_transform_stops_at_the_word_separators_and_does_nothing_on_blank()
    {
        var doc = new InMemoryTextDocument(new Uri("file:///t.txt"), "plaintext", "foo-bar  x");
        var (_, text, _) = Run("foo-bar  x", [TextRange.At(0, 1)], d => TextTransforms.TransformCase(d, [TextRange.At(0, 1)], Case.Upper));
        Assert.Equal("FOO-bar  x", text); // '-' is one of editor.wordSeparators
        Assert.Null(TextTransforms.TransformCase(doc, [TextRange.At(0, 8)], Case.Upper)); // between two blanks
        Assert.Null(TextTransforms.TransformCase(doc, [TextRange.FromLines(0, 7, 0, 9)], Case.Upper)); // nothing to change
    }

    [Fact]
    public void Duplicate_copies_a_selection_after_itself_and_an_empty_carets_line_below()
    {
        var selections = new[] { TextRange.FromLines(0, 1, 0, 2), TextRange.At(1, 1), TextRange.At(1, 2), TextRange.At(2, 0) };
        var (_, text, after) = Run("abc\ndef\nghi", selections, d => TextTransforms.Duplicate(d, selections, "\n"));
        Assert.Equal("abbc\ndef\ndef\nghi\nghi", text); // two carets on one line duplicate it once
        Assert.Equal([TextRange.FromLines(0, 2, 0, 3), TextRange.At(2, 1), TextRange.At(2, 2), TextRange.At(4, 0)], after);
    }

    [Fact]
    public void Duplicate_copies_a_selection_over_several_lines_and_uses_the_documents_line_ending()
    {
        var selections = new[] { TextRange.FromLines(0, 1, 1, 1) };
        var (_, text, after) = Run("ab\r\ncd", selections, d => TextTransforms.Duplicate(d, selections, "\r\n"));
        Assert.Equal("ab\r\ncb\r\ncd", text);
        Assert.Equal([TextRange.FromLines(1, 1, 2, 1)], after);
        var line = new[] { TextRange.At(0, 1) };
        Assert.Equal("ab\r\nab\r\ncd", Run("ab\r\ncd", line, d => TextTransforms.Duplicate(d, line, "\r\n")).Text);
    }

    // ---- the commands on real views ----

    [AvaloniaFact]
    public async Task The_commands_are_registered_under_VS_Codes_ids_and_palette_titles()
    {
        using var host = ViewHost.Create();
        await Task.CompletedTask;
        string Label(string id) => host.Commands.All.Single(c => c.Id == id) is var m && m.Category is { } cat ? cat + ": " + m.Title : m.Title!;
        Assert.Equal("View: Toggle Word Wrap", Label("editor.action.toggleWordWrap"));
        Assert.Equal("Editor Font Zoom In", Label("editor.action.fontZoomIn"));
        Assert.Equal("Editor Font Zoom Out", Label("editor.action.fontZoomOut"));
        Assert.Equal("Editor Font Zoom Reset", Label("editor.action.fontZoomReset"));
        Assert.Equal("Transform to Uppercase", Label("editor.action.transformToUppercase"));
        Assert.Equal("Transform to Lowercase", Label("editor.action.transformToLowercase"));
        Assert.Equal("Transform to Title Case", Label("editor.action.transformToTitlecase"));
        Assert.Equal("Transform to Snake Case", Label("editor.action.transformToSnakecase"));
        Assert.Equal("Transform to Camel Case", Label("editor.action.transformToCamelcase"));
        Assert.Equal("Transform to Kebab Case", Label("editor.action.transformToKebabcase"));
        Assert.Equal("Transform to Pascal Case", Label("editor.action.transformToPascalcase"));
        Assert.Equal("Duplicate Selection", Label("editor.action.duplicateSelection"));
    }

    [AvaloniaFact]
    public async Task Toggle_Word_Wrap_wraps_the_active_view_only_keeps_it_in_the_view_state_and_gives_way_to_a_setting_change()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("a long line\n", "a.txt");
        var keys = (TestContextKeyService)host.Provider.GetRequiredService<IContextKeyService>();
        var other = (CodeEditorControl)host.Factory.CreateView(doc); // a split of the same document
        view.Editor.TextArea.Focus();
        ViewHost.Pump();
        Assert.False(view.Editor.WordWrap);

        await host.Commands.ExecuteAsync("editor.action.toggleWordWrap");
        Assert.True(view.Editor.WordWrap);
        Assert.False(other.Editor.WordWrap);
        Assert.Null(host.Config.GetRaw(SettingKeys.EditorWordWrap)); // the setting is not written
        Assert.Equal(true, keys.Get(EditorViewFactory.WordWrapKey)); // View > Word Wrap's check mark

        // the view state carries it into a recreated view
        var state = view.Controller.GetViewState();
        Assert.True(state.WordWrap);
        var recreated = (CodeEditorControl)host.Factory.CreateView(doc);
        recreated.Controller.RestoreViewState(state);
        Assert.True(recreated.Editor.WordWrap);

        // a change of editor.wordWrap ends it: the setting decides again
        host.Config.Set(SettingKeys.EditorWordWrap, "on");
        ViewHost.Pump();
        Assert.True(view.Editor.WordWrap);
        await host.Commands.ExecuteAsync("editor.action.toggleWordWrap");
        Assert.False(view.Editor.WordWrap);
        host.Config.Set(SettingKeys.EditorWordWrap, "off");
        ViewHost.Pump();
        host.Config.Set(SettingKeys.EditorWordWrap, "on");
        ViewHost.Pump();
        Assert.True(view.Editor.WordWrap);
        Assert.Null(view.Controller.GetViewState().WordWrap);
        // a restored choice the setting now agrees with is dropped
        recreated.Controller.RestoreViewState(state);
        Assert.Null(recreated.WordWrapOverride);
        other.Dispose();
        recreated.Dispose();
    }

    [AvaloniaFact]
    public async Task The_font_zoom_keys_and_Ctrl_wheel_share_one_zoom_per_view_that_survives_a_settings_change()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.mouseWheelZoom", true);
        host.Config.Set(SettingKeys.EditorFontSize, 14);
        var (doc, view, window) = await host.OpenAsync("text\n", "a.txt");
        var other = (CodeEditorControl)host.Factory.CreateView(doc);

        await host.Commands.ExecuteAsync("editor.action.fontZoomIn");
        await host.Commands.ExecuteAsync("editor.action.fontZoomIn");
        Assert.Equal(16, view.Editor.FontSize);
        Assert.Equal(14, other.Editor.FontSize); // per view, as the wheel zoom
        Assert.Equal(14, host.Config.Get(SettingKeys.EditorFontSize, 0)); // not persisted

        var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        var at = view.TranslatePoint(new Point(100, 30), window) ?? default;
        window.MouseWheel(at, new Vector(0, -1), modifier); // Ctrl+wheel down: one step out of the same zoom
        ViewHost.Pump();
        Assert.Equal(15, view.Editor.FontSize);

        host.Config.Set(SettingKeys.EditorFontSize, 20); // the zoom stays an offset from the setting
        ViewHost.Pump();
        Assert.Equal(21, view.Editor.FontSize);
        await host.Commands.ExecuteAsync("editor.action.fontZoomOut");
        Assert.Equal(20, view.Editor.FontSize);
        await host.Commands.ExecuteAsync("editor.action.fontZoomIn");
        await host.Commands.ExecuteAsync("editor.action.fontZoomReset");
        Assert.Equal(20, view.Editor.FontSize);
        for (var i = 0; i < 100; i++) await host.Commands.ExecuteAsync("editor.action.fontZoomIn");
        Assert.Equal(72, view.Editor.FontSize); // the wheel's limits
        other.Dispose();
    }

    [AvaloniaFact]
    public async Task Case_commands_work_at_every_caret_as_one_undo_step()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("first second\nthird fourth\n", "a.txt");
        view.Controller.SetSelections([TextRange.At(0, 2), TextRange.FromLines(1, 6, 1, 12)]);

        await host.Commands.ExecuteAsync("editor.action.transformToUppercase");
        Assert.Equal("FIRST second\nthird FOURTH\n", doc.GetText());
        Assert.Equal([TextRange.At(0, 2), TextRange.FromLines(1, 6, 1, 12)], view.Controller.Selections);

        await host.Commands.ExecuteAsync("editor.action.transformToTitlecase");
        Assert.Equal("First second\nthird Fourth\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("FIRST second\nthird FOURTH\n", doc.GetText());
        view.Controller.Undo();
        Assert.Equal("first second\nthird fourth\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Duplicate_Selection_works_at_every_caret_as_one_undo_step_and_leaves_read_only_documents_alone()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("one\ntwo\n", "a.txt");
        view.Controller.SetSelections([TextRange.FromLines(0, 0, 0, 3), TextRange.At(1, 1)]);

        await host.Commands.ExecuteAsync("editor.action.duplicateSelection");
        Assert.Equal("oneone\ntwo\ntwo\n", doc.GetText());
        Assert.Equal([TextRange.FromLines(0, 3, 0, 6), TextRange.At(2, 1)], view.Controller.Selections);
        view.Controller.Undo();
        Assert.Equal("one\ntwo\n", doc.GetText());

        doc.IsReadOnly = true;
        view.Controller.SetSelection(TextRange.At(0, 1));
        await host.Commands.ExecuteAsync("editor.action.duplicateSelection");
        await host.Commands.ExecuteAsync("editor.action.transformToUppercase");
        Assert.Equal("one\ntwo\n", doc.GetText());
    }
}
