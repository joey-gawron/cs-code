using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

/// <summary>
/// Ranges the editor paints with the find-match background whatever the caret does: the search editor's matches (VS Code's
/// <c>searchEditor.findMatchBackground</c> decorations). Pull model, like folding: the editor asks for each document version (debounced, off the
/// UI thread, the answer dropped when the text changed meanwhile), moves the ranges it has with the edits until the next answer arrives, and asks
/// again when <see cref="Changed"/> fires. Additive (search editor).
/// </summary>
public interface IMatchHighlightProvider : ILanguageProvider
{
    /// <summary>The ranges to highlight in <paramref name="document"/>'s current text, in any order; null or empty for none.</summary>
    ValueTask<IReadOnlyList<TextRange>?> ProvideMatchHighlightsAsync(ITextDocument document, CancellationToken ct);

    /// <summary>Fires, on any thread, with a document whose highlights changed without an edit (a search ran with another query).</summary>
    IEventSource<Uri> Changed => EventSource.Never<Uri>();
}

/// <summary>
/// Takes a double-click in a document's text before the editor selects the word there: the search editor opens the location of a result line
/// (VS Code's <c>search.searchEditor.doubleClickBehaviour</c>). Additive (search editor).
/// </summary>
public interface IDoubleClickHandler : ILanguageProvider
{
    /// <summary>
    /// UI thread, and it must answer at once: read the clicked line at most, start anything slow without awaiting it. True: handled (the editor
    /// puts the caret at <paramref name="position"/> and selects nothing); false: the editor selects the word as usual.
    /// </summary>
    bool OnDoubleClick(ITextDocument document, TextPosition position);
}
