namespace CsCode.Editor.Features;

// What the editing commands need from the language-feature components of a view. Each is implemented by one component class and found
// with CodeEditorControl.Feature<T>(); in large-file mode none of them exist and the commands simply do nothing.

internal interface ICompletionFeature
{
    void Trigger();
}

internal interface ISignatureFeature
{
    void Trigger();
}

internal interface ICodeActionFeature
{
    Task ShowQuickFixAsync(string? onlyKind = null);
    Task OrganizeImportsAsync();
}

internal interface INavigationFeature
{
    Task GoToDefinitionAsync(Core.Languages.DefinitionKind kind = Core.Languages.DefinitionKind.Definition);
    Task FindReferencesAsync();
    Task RenameAsync();
    /// <summary>Whether Rename applies at the caret (the server's prepareRename, else a word under the caret). Never blocks the caller.</summary>
    Task<bool> CanRenameAsync(CancellationToken ct);
}

internal interface IFormattingFeature
{
    Task FormatDocumentAsync();
    Task FormatSelectionAsync();
    /// <summary>A paste landed on <paramref name="range"/>: formats it through the language server when <c>editor.formatOnPaste</c> is on.</summary>
    void OnPasted(Core.Text.TextRange range);
}

internal interface IFoldingFeature
{
    void Fold(bool recursive = false);
    void Unfold(bool recursive = false);
    void ToggleFold();
    void FoldAll();
    void UnfoldAll();
    IReadOnlyList<int> FoldedStartLines { get; }
    void RestoreFolds(IReadOnlyList<int> startLines);
}

internal interface ISnippetFeature
{
    /// <summary>Inserts a snippet body at the caret (replacing the selection) and starts a tab-stop session.</summary>
    void Insert(string snippetBody);
    /// <summary>Whether the language has snippets that wrap the selection (<c>$TM_SELECTED_TEXT</c>); computed off the UI thread, cached.</summary>
    bool HasSurroundSnippets { get; }
    /// <summary>Visual Studio's Insert Snippet (Ctrl+K, Ctrl+X) / Surround With (Ctrl+K, Ctrl+S): pick a snippet of the language and insert it.</summary>
    Task PickAndInsertAsync(bool surround);
}
