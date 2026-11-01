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
        var list = new List<IDisposable>
        {
            new Features.FoldingFeature(view),
            new Features.SnippetFeature(view),
            new Features.CompletionFeature(view),
            new Features.DiagnosticsFeature(view),
            new Features.HoverFeature(view),
            new Features.SignatureFeature(view),
            new Features.CodeActionFeature(view),
            new Features.NavigationFeature(view),
            new Features.FormattingFeature(view),
            new Features.BracketFeature(view),
            new Features.HighlightFeature(view),
            new Features.BreadcrumbFeature(view),
            new Features.InlayHintFeature(view),
            new Features.CodeLensFeature(view), // its zone generator goes first in the generator list (a zone opens its line)
            new Features.StickyScrollFeature(view), // after folding: it pins the headers of folding's regions
            new Features.TestGutterFeature(view), // before the debug feature: its breakpoint margin then goes left of the test glyphs
        };
        // the breakpoint margin, stop-line arrow and breakpoint tracking exist only when a debugger is registered
        if (view.Services.Debug is { } debug) list.Add(new Features.DebugFeature(view, debug));
        // source control: gutter change markers and inline blame, only for files and only when a provider (the git module) is registered
        if (view.Document.Uri.IsFile && view.Services.Provider?.GetService(typeof(Core.Scm.IQuickDiffProvider)) is Core.Scm.IQuickDiffProvider quickDiff)
            list.Add(new Features.QuickDiffFeature(view, quickDiff));
        if (view.Document.Uri.IsFile && view.Services.Provider?.GetService(typeof(Core.Scm.ILineBlameProvider)) is Core.Scm.ILineBlameProvider blame)
            list.Add(new Features.InlineBlameFeature(view, blame));
        return list;
    }
}
