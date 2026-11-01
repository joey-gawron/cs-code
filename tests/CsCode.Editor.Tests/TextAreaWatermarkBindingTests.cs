using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// AvaloniaEdit's text-area theme binds its watermark's visibility to <c>Document.Text</c>, so every edit built the whole document as a string
/// (twice the file's size in bytes per keystroke: the large-file typing p99 of 35–40 ms on macOS). <see cref="TextAreaWatermarkBinding"/> stops
/// that binding in every text area; these tests type into documents of a megabyte and more and hold the UI thread's allocation per keystroke
/// to a small fraction of the document.
/// </summary>
[Collection(MemoryMeasurementCollection.Name)]
public class TextAreaWatermarkBindingTests(ITestOutputHelper output)
{
    private static string Lines(int bytes)
    {
        var sb = new StringBuilder(bytes + 64);
        for (var i = 0; sb.Length < bytes; i++) sb.Append("line ").Append(i).Append(" of a document that is about one megabyte long\n");
        return sb.ToString();
    }

    private static long BytesPerKeystroke(AvaloniaEdit.Editing.TextArea area, int count)
    {
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            area.PerformTextInput("x");
            total += GC.GetAllocatedBytesForCurrentThread() - before;
            if (i % 10 == 0) ViewHost.Pump();
        }
        return total / count;
    }

    private static Control? Watermark(Visual area) =>
        area.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == TextAreaWatermarkBinding.PartName);

    [AvaloniaFact]
    public async Task Typing_into_a_megabyte_document_does_not_copy_the_document_per_keystroke()
    {
        using var host = ViewHost.Create();
        var text = Lines(1024 * 1024);
        var (doc, view, window) = await host.OpenAsync(text, "big.txt");
        Assert.False(doc.IsLargeFile);
        view.Controller.SetSelection(TextRange.At(doc.LineCount / 2, 0));
        BytesPerKeystroke(view.TextArea, 10); // warm-up
        var bytes = BytesPerKeystroke(view.TextArea, 50);
        output.WriteLine($"{text.Length / 1024} KB document: {bytes:N0} B per keystroke on the UI thread");
        // the whole text as a string is 2 MB; what remains is the edited line, the caret and the undo step
        Assert.True(bytes < 200_000, $"{bytes:N0} B per keystroke in a {text.Length / 1024} KB document: something reads the whole text on every edit");
        host.Factory.DisposeView(view);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Typing_in_large_file_mode_allocates_a_few_kilobytes_per_keystroke()
    {
        using var host = ViewHost.Create();
        var text = Lines(6 * 1024 * 1024);
        var (doc, view, window) = await host.OpenAsync(text, "huge.cs");
        Assert.True(doc.IsLargeFile);
        view.Controller.SetSelection(TextRange.At(doc.LineCount / 2, 0));
        BytesPerKeystroke(view.TextArea, 10);
        var gen2 = GC.CollectionCount(2);
        var bytes = BytesPerKeystroke(view.TextArea, 50);
        var collections = GC.CollectionCount(2) - gen2;
        output.WriteLine($"{text.Length / 1048576.0:F1} MB document (large-file mode): {bytes:N0} B per keystroke, {collections} gen-2 collections in 50 keys");
        Assert.True(bytes < 200_000, $"{bytes:N0} B per keystroke in large-file mode");
        Assert.True(collections <= 1, $"{collections} gen-2 collections while typing 50 keys: typing must not churn the large object heap");
        host.Factory.DisposeView(view);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_text_areas_watermark_is_hidden_and_bound_to_nothing()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("abc", "w.cs");
        var watermark = Watermark(view.TextArea);
        Assert.NotNull(watermark); // AvaloniaEdit 12's theme: if this part goes away, the fix (and this test) can go too
        Assert.False(watermark.IsVisible);
        Assert.Null(BindingOperations.GetBindingExpressionBase(watermark, Visual.IsVisibleProperty));
        // still hidden and unbound once the document is emptied (the binding used to flip it on)
        view.TextArea.Document.Text = "";
        ViewHost.Pump();
        Assert.False(watermark.IsVisible);
        host.Factory.DisposeView(view);
        window.Close();
    }

    [AvaloniaFact]
    public void Any_text_area_made_after_the_editor_styles_are_in_place_is_covered()
    {
        // the class handler covers every AvaloniaEdit text area in the process (diff panes, any other TextEditor), not just CodeEditorControl
        EditorStyles.Ensure();
        var editor = new AvaloniaEdit.TextEditor { Document = new AvaloniaEdit.Document.TextDocument(Lines(512 * 1024)) };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        window.Show();
        ViewHost.Pump();
        Assert.Null(BindingOperations.GetBindingExpressionBase(Watermark(editor.TextArea)!, Visual.IsVisibleProperty));
        editor.CaretOffset = editor.Document.TextLength / 2;
        BytesPerKeystroke(editor.TextArea, 10);
        var bytes = BytesPerKeystroke(editor.TextArea, 50);
        output.WriteLine($"bare TextEditor, 512 KB: {bytes:N0} B per keystroke");
        Assert.True(bytes < 100_000, $"{bytes:N0} B per keystroke in a bare 512 KB TextEditor");
        window.Content = null;
        window.Close();
    }
}
