using CsCode.App.Docking;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.App.Services;

/// <summary>
/// <see cref="IActiveSelectionProvider"/> over the active editor tab: <c>${selectedText}</c> in launch configurations (read on the Debug lane) and the
/// seed of Find in Files. The UI thread publishes an immutable (document, primary selection) snapshot whenever the active editor, its view or its
/// selection changes; a reader on any thread takes the text from the document (callable from any thread by contract) only when it asks, so a huge
/// selection costs nothing until someone reads it. No constructor dependencies: Dap resolves this while the container composes, long before a window
/// exists, so <see cref="ShellController"/> attaches it to the document well with <see cref="Track"/>.
/// </summary>
public sealed class ActiveSelectionProvider : IActiveSelectionProvider, IDisposable
{
    private sealed record Snapshot(IEditorDocument Document, TextRange Selection);

    private volatile Snapshot? _current;
    private IDisposable? _activeSub, _viewSub, _selectionSub;

    /// <summary>The primary selection of the active editor, or null when there is no editor or the selection is empty.</summary>
    public string? SelectedText => _current is { Selection.IsEmpty: false } s ? s.Document.GetText(s.Selection) : null;

    /// <summary>UI thread: follows the active tab of <paramref name="well"/> until the returned token (or this object) is disposed.</summary>
    public IDisposable Track(DocumentWell well)
    {
        Detach();
        _activeSub = well.ActiveChanged.Subscribe(OnActive);
        OnActive(well.Active);
        return Disposable.Create(Detach);
    }

    private void OnActive(ShellDocument? tab)
    {
        _viewSub?.Dispose();
        _selectionSub?.Dispose();
        _viewSub = _selectionSub = null;
        _current = null;
        if (tab is not EditorTab editor) return;
        // The view may not exist yet, and an active tab's view can be recreated: (re)attach whenever one appears.
        _viewSub = editor.ViewCreated.Subscribe(Attach);
        Attach(editor);
    }

    private void Attach(EditorTab tab)
    {
        if (tab.Controller is not { } c) return;
        var doc = tab.Doc;
        _selectionSub?.Dispose();
        _current = new Snapshot(doc, c.Selections[0]);
        _selectionSub = c.SelectionChanged.Subscribe(sel => _current = sel.Count > 0 ? new Snapshot(doc, sel[0]) : null);
    }

    private void Detach()
    {
        _activeSub?.Dispose();
        _viewSub?.Dispose();
        _selectionSub?.Dispose();
        _activeSub = _viewSub = _selectionSub = null;
        _current = null;
    }

    public void Dispose() => Detach();
}
