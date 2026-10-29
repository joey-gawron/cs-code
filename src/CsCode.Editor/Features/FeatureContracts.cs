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
}

internal interface IFormattingFeature
{
    Task FormatDocumentAsync();
    Task FormatSelectionAsync();
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
}
