using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using AvaloniaEdit.Editing;

namespace CsCode.Editor.View;

/// <summary>
/// AvaloniaEdit 12's <c>TextArea</c> theme shows its watermark with
/// <c>IsVisible="{Binding $templatedParent.Document.Text, Converter={x:Static StringConverters.IsNullOrEmpty}}"</c>: the document raises
/// <c>PropertyChanged("Text")</c> after every change, and the binding answers it by reading <c>TextDocument.Text</c>, which builds the whole
/// document as one string. Every keystroke therefore allocated twice the file's size in characters (24 MB per character typed into a 12 MB file,
/// on the large object heap, so a gen-2 collection every few keys: the 35–40 ms typing p99 on macOS that docs/handoff/known-issues.md recorded),
/// and ≈ 90 B per line of any document (what docs/EDITOR.md used to call AvaloniaEdit's own per-line cost).
///
/// The editor never sets <c>TextArea.Watermark</c>, so this stops that binding in every text area as its template is applied and keeps the
/// (empty) watermark hidden. The binding has template priority, which <c>ClearValue</c> leaves alone; it is disposed through its expression.
/// One class handler for the process, registered before the first text area exists; idempotent.
/// </summary>
internal static class TextAreaWatermarkBinding
{
    internal const string PartName = "PART_Watermark";
    private static int _registered;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextArea>((_, e) => Detach(e.NameScope));
    }

    private static void Detach(INameScope scope)
    {
        if (scope.Find<Control>(PartName) is not { } watermark) return; // another theme: nothing to undo
        BindingOperations.GetBindingExpressionBase(watermark, Visual.IsVisibleProperty)?.Dispose();
        watermark.IsVisible = false;
    }
}
