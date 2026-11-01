using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// Steady-state typing allocation on the UI thread. The yardstick is a plain, themed AvaloniaEdit <c>TextEditor</c> over a document of the
/// same size, so the editor's own overhead is the DIFFERENCE, and that must stay small and independent of the document's size. The bare editor
/// used to allocate ≈ 90 B per line per keystroke — its theme's watermark binding read the whole text on every edit — until
/// <see cref="TextAreaWatermarkBinding"/> stopped it (covered by <see cref="TextAreaWatermarkBindingTests"/>); it must not grow with the document
/// either now. The per-feature breakdown is printed so a regression names its culprit.
/// </summary>
public class KeystrokeAllocationTests(ITestOutputHelper output)
{
    private const string Typed = "the quick brown fox jumps over the lazy dog 0123456789 ";

    private static string Source(int lines) =>
        string.Join('\n', Enumerable.Range(0, lines).Select(i => i == 0 ? "// typing happens on this line" : $"word{i} = value{i}; // some trailing text"));

    private static long BytesPerKeystroke(Action<string> type, int count)
    {
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            type(Typed[i % Typed.Length].ToString());
            total += GC.GetAllocatedBytesForCurrentThread() - before;
            ViewHost.Pump();
        }
        return total / count;
    }

    private static long Plain(int lines)
    {
        EditorStyles.Ensure(); // an unstyled TextEditor has no template and does nothing: the theme makes it a live editor
        var editor = new AvaloniaEdit.TextEditor { Document = new AvaloniaEdit.Document.TextDocument(Source(lines)), Width = 800, Height = 500 };
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = editor };
        window.Show();
        ViewHost.Pump();
        editor.TextArea.Focus();
        editor.CaretOffset = 0;
        BytesPerKeystroke(editor.TextArea.PerformTextInput, 80);
        var bytes = BytesPerKeystroke(editor.TextArea.PerformTextInput, 200);
        window.Content = null;
        window.Close();
        return bytes;
    }

    private static async Task<long> Ours(int lines)
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Source(lines), "doc.cs");
        view.Controller.SetSelection(TextRange.At(0, 0));
        BytesPerKeystroke(view.TextArea.PerformTextInput, 80);
        return BytesPerKeystroke(view.TextArea.PerformTextInput, 200);
    }

    [AvaloniaFact]
    public async Task The_editors_own_per_keystroke_allocation_is_small_and_does_not_grow_with_the_document()
    {
        var plainSmall = Plain(50);
        var plainBig = Plain(6000);
        var oursSmall = await Ours(50);
        var oursBig = await Ours(6000);
        output.WriteLine($"plain AvaloniaEdit : 50 lines {plainSmall:N0} B/keystroke, 6000 lines {plainBig:N0} B/keystroke");
        output.WriteLine($"CodeEditorControl  : 50 lines {oursSmall:N0} B/keystroke, 6000 lines {oursBig:N0} B/keystroke");
        var overheadSmall = oursSmall - plainSmall;
        var overheadBig = oursBig - plainBig;
        output.WriteLine($"this module's overhead per keystroke: {overheadSmall:N0} B (50 lines), {overheadBig:N0} B (6000 lines)");
        Assert.True(overheadSmall < 300_000, $"overhead {overheadSmall:N0} B per keystroke in a small document");
        Assert.True(overheadBig < overheadSmall + 60_000, $"our overhead must not scale with document size: {overheadSmall:N0} B (50 lines) vs {overheadBig:N0} B (6000 lines)");
        Assert.True(plainBig < plainSmall + 60_000, $"the bare editor must not allocate per line on every edit: {plainSmall:N0} B (50 lines) vs {plainBig:N0} B (6000 lines)");
    }

    [AvaloniaFact]
    public async Task Allocation_breakdown_by_feature_report()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Source(2000), "big.cs");
        view.Controller.SetSelection(TextRange.At(0, 0));
        BytesPerKeystroke(view.TextArea.PerformTextInput, 80);
        var lines = new List<string> { $"all features: {BytesPerKeystroke(view.TextArea.PerformTextInput, 100):N0} B" };
        void Remove<T>(string name) where T : class, IDisposable
        {
            view.Feature<T>()?.Dispose();
            lines.Add($"  without {name}: {BytesPerKeystroke(view.TextArea.PerformTextInput, 100):N0} B");
        }
        Remove<HighlightFeature>("highlights");
        Remove<BracketFeature>("brackets");
        Remove<FormattingFeature>("formatting");
        Remove<NavigationFeature>("navigation");
        Remove<CodeActionFeature>("code actions");
        Remove<SignatureFeature>("signature");
        Remove<HoverFeature>("hover");
        Remove<DiagnosticsFeature>("diagnostics");
        Remove<CompletionFeature>("completion");
        Remove<SnippetFeature>("snippets");
        Remove<FoldingFeature>("folding");
        view.TextView.LineTransformers.Clear(); // the syntax colouring (and every other line transformer)
        lines.Add($"  + without any line transformer (TextMate colouring…): {BytesPerKeystroke(view.TextArea.PerformTextInput, 100):N0} B");
        view.TextView.BackgroundRenderers.Clear();
        lines.Add($"  + without background renderers: {BytesPerKeystroke(view.TextArea.PerformTextInput, 100):N0} B");
        foreach (var l in lines) output.WriteLine(l);
    }
}
