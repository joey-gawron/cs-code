using CsCode.Editor.Documents;

namespace CsCode.Editor.View;

/// <summary>
/// Creates the language-feature components of a view (completion, hover, signature help, diagnostics, code actions, navigation, folding,
/// bracket matching, breakpoints…). In large-file mode a document gets none of them: it is plain text and must stay responsive.
/// </summary>
internal static class EditorFeatures
{
    public static IEnumerable<IDisposable> Create(CodeEditorControl view)
    {
        if (view.Document.IsLargeFile) return [];
        var list = new List<IDisposable>();
        // components are added here as they are implemented (each takes the view and unhooks itself on Dispose)
        return list;
    }
}
