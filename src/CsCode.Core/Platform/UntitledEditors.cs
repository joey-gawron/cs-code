using CsCode.Core.Text;

namespace CsCode.Core.Platform;

/// <summary>
/// A new untitled editor with text in it (<see cref="INavigationService.OpenUntitledAsync"/>): the search editor opens one in the
/// <c>search-result</c> language, titled "Search: query". Additive (search editor).
/// </summary>
/// <param name="LanguageId">The document's language (kept when the document is saved under another name).</param>
/// <param name="Text">The initial text; it is not an undo step, so Undo never empties the editor.</param>
public sealed record UntitledEditorOptions(string LanguageId, string Text)
{
    /// <summary>The tab's title instead of "Untitled-N"; null keeps that name.</summary>
    public string? Title { get; init; }

    /// <summary>The file Save As proposes (folder and name), e.g. <c>&lt;workspace&gt;/query.code-search</c>; null: the dialog's default.</summary>
    public Uri? SuggestedSaveUri { get; init; }

    /// <summary>Open the editor in the group beside the active one (VS Code's "to the side").</summary>
    public bool ToSide { get; init; }

    /// <summary>Where to put the caret (and the selection) once the editor shows.</summary>
    public TextRange? Selection { get; init; }
}
