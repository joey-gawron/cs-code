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
        // large-file mode keeps the bookmarks (not a language feature): in a margin of their own, there is no breakpoint margin
        if (view.Document.IsLargeFile) return Features.BookmarkFeature.TryCreate(view, null) is { } plainBookmarks ? [plainBookmarks] : [];
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
            new Features.PeekFeature(view), // Peek Definition / References: an editor embedded under the line (needs navigation's lookups)
            new Features.LinkedEditingFeature(view), // editor.linkedEditing: tag pairs edited together
            new Features.FormattingFeature(view),
            new Features.BracketFeature(view),
            new Features.HighlightFeature(view),
            new Features.BreadcrumbFeature(view),
            new Features.InlayHintFeature(view),
            new Features.CodeLensFeature(view), // its zone generator goes first in the generator list (a zone opens its line)
            new Features.SemanticTokensFeature(view), // its colorizer goes right after the grammar's
            new Features.InactiveRegionsFeature(view), // after every colorizer: it fades the colours they chose
            new Features.StickyScrollFeature(view), // after folding: it pins the headers of folding's regions
            new Features.TestGutterFeature(view), // before the debug feature: its breakpoint margin then goes left of the test glyphs
        };
        // the breakpoint margin, stop-line arrow and breakpoint tracking exist only when a debugger is registered
        Features.DebugFeature? debugFeature = null;
        if (view.Services.Debug is { } debug) list.Add(debugFeature = new Features.DebugFeature(view, debug));
        // bookmarks: drawn in the breakpoint margin when there is one (Visual Studio's indicator margin), else in a margin of their own
        if (Features.BookmarkFeature.TryCreate(view, debugFeature) is { } bookmarks) list.Add(bookmarks);
        // source control: gutter change markers and inline blame, only for files and only when a provider (the git module) is registered
        if (view.Document.Uri.IsFile && view.Services.Provider?.GetService(typeof(Core.Scm.IQuickDiffProvider)) is Core.Scm.IQuickDiffProvider quickDiff)
            list.Add(new Features.QuickDiffFeature(view, quickDiff));
        if (view.Document.Uri.IsFile && view.Services.Provider?.GetService(typeof(Core.Scm.ILineBlameProvider)) is Core.Scm.ILineBlameProvider blame)
            list.Add(new Features.InlineBlameFeature(view, blame));
        return list;
    }
}
