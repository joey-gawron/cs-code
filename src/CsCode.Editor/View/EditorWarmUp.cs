using Avalonia.Controls;

namespace CsCode.Editor.View;

/// <summary>
/// A throwaway editor view for a warm-up (the warm spare, docs/SESSIONS.md "The warm spare"): a real <see cref="CodeEditorControl"/> over a
/// read-only snapshot document that is NOT registered with the document manager (no language server, file watcher, tab or session ever sees it;
/// the same kind of document as a diff side) and has a URI of its own scheme (no file, so nothing reads the disk or asks git about it). The host
/// lays <see cref="View"/> out in a window and renders it, which runs, for the first time in the process, everything the first editor of a
/// claimed session would otherwise run while the user waits: the view's construction and features, its templates, layout and first render, the
/// grammar's loading and the TextMate engine's first tokenisation (the regex library included), and the colouriser. Dispose removes every trace:
/// the view, its tokenisation session and the document. UI thread.
/// </summary>
public sealed class EditorWarmUp : IDisposable
{
    /// <summary>The URI scheme of warm-up documents.</summary>
    public const string Scheme = "cscode-warmup";

    private readonly EditorViewFactory _factory;
    private readonly Documents.EditorDocument _document;
    private readonly CodeEditorControl _view;
    private bool _disposed;

    private EditorWarmUp(EditorViewFactory factory, Documents.EditorDocument document, CodeEditorControl view)
    {
        _factory = factory;
        _document = document;
        _view = view;
    }

    /// <summary>
    /// The view over <paramref name="text"/> coloured as <paramref name="languageId"/>; null when <paramref name="editors"/> is not this module's
    /// factory (a test double) or it was disposed.
    /// </summary>
    public static EditorWarmUp? Create(IEditorViewFactory editors, string languageId, string text, string fileName = "sample")
    {
        if (editors is not EditorViewFactory factory || factory.IsDisposed) return null;
        var uri = new Uri($"{Scheme}:/{Uri.EscapeDataString(fileName)}");
        var document = factory.CreateSnapshotDocument(uri, text, languageId);
        try { return new EditorWarmUp(factory, document, new CodeEditorControl(factory, document)); }
        catch
        {
            factory.ReleaseSnapshotDocument(document);
            throw;
        }
    }

    /// <summary>The editor control: put it in a visual tree (it needs the application's styles), lay it out, render it.</summary>
    public Control View => _view;

    /// <summary>Whether the language has a grammar here (otherwise there are no colours to wait for).</summary>
    public bool HasGrammar => _view.Tokens is not null;

    /// <summary>Whether the tokens of the visible lines arrived (the colouriser has something to draw), or there is no grammar.</summary>
    public bool TokensReady => _view.Tokens is not { } tokens || tokens.ViewportReady is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Dispose();
        _factory.ReleaseSnapshotDocument(_document);
    }
}
