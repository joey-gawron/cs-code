using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.Diff;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// The shell's <see cref="IMergeEditorService"/>: a merge editor opens as a document tab (one per file, id <c>view:merge:&lt;uri&gt;</c>: opening it
/// again activates it) whose view is the editor's <see cref="MergeEditorControl"/> over the file's open document, recreated from the request
/// when the tab's view is rebuilt. The tab is attached to that document (<see cref="DocumentWell.AttachDocument"/>): it keeps it open, and its
/// unsaved changes count like an editor tab's (asked about when the tab closes and at quit, backed up by hot exit) when no editor tab shows the
/// file. Complete Merge closes the tab. Registers Complete Merge and Next / Previous Conflict for the active merge editor and keeps
/// <c>isMergeEditor</c> current.
/// </summary>
public sealed class MergeEditorService(IUiDispatcher ui, IEditorViewFactory editors, Func<DocumentWell> well) : IMergeEditorService
{
    public const string AcceptMerge = "mergeEditor.acceptMerge";
    public const string NextConflict = "cscode.mergeEditor.nextConflict";
    public const string PreviousConflict = "cscode.mergeEditor.previousConflict";
    public const string IsMergeEditorKey = "isMergeEditor";

    private readonly Dictionary<string, ShellDocument> _open = new(StringComparer.Ordinal);

    public async ValueTask OpenMergeEditorAsync(MergeEditorRequest request, CancellationToken ct = default)
    {
        var doc = await editors.OpenDocumentAsync(request.Result, ct).ConfigureAwait(false);
        await ui.InvokeAsync(() =>
        {
            var w = well();
            var id = "merge:" + request.Result;
            if (_open.TryGetValue(id, out var open) && w.Tabs.Contains(open))
            {
                w.Activate(open);
                return;
            }
            ShellDocument? created = null;
            var tab = w.OpenView(id, request.Title, () => CreateView(request, doc, () => created), "diff");
            created = tab;
            _open[id] = tab;
            // the document's dirty state counts once the user changed something (the editor's own starting edit does not; a dropped view undid it)
            w.AttachDocument(tab, doc, () => tab.LiveView is MergeEditorControl m ? m.HasUnsavedChanges : doc.IsDirty);
        }).ConfigureAwait(false);
    }

    private Control CreateView(MergeEditorRequest request, IEditorDocument doc, Func<ShellDocument?> tab)
    {
        if (editors.CreateMergeView(request, doc) is not MergeEditorControl view) return new TextBlock { Text = "The merge editor is not available." };
        // Complete Merge: the tab closes (the subscription goes with the control: it clears its listeners when disposed)
        view.Completed.Subscribe(_ => ui.Post(() => { if (tab() is { } t) _ = well().CloseAsync(t, prompt: false); }));
        return view;
    }

    /// <summary>The merge editor of the active tab, if it is one (UI thread).</summary>
    public MergeEditorControl? ActiveMergeEditor => well().Active?.LiveView as MergeEditorControl;

    /// <summary>Follows the tabs, registers the commands, keeps the context key. Called once by the shell.</summary>
    public IDisposable Start(DocumentWell documents, ICommandService commands, IContextKeyService keys)
    {
        var store = new DisposableStore();
        void Update(ShellDocument? tab) => keys.Set(IsMergeEditorKey, tab?.Id?.StartsWith("view:merge:", StringComparison.Ordinal) == true);
        store.Add(documents.ActiveChanged.Subscribe(Update));
        store.Add(documents.TabsChanged.Subscribe(_ =>
        {
            Update(documents.Active);
            var live = documents.Tabs;
            foreach (var id in _open.Where(kv => !live.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _open.Remove(id);
        }));
        Update(documents.Active);
        void Reg(string id, string title, Func<MergeEditorControl, Task> run)
        {
            if (commands.Has(id)) return;
            store.Add(commands.Register(id, async _ =>
            {
                var editor = await ui.InvokeAsync(() => ActiveMergeEditor).ConfigureAwait(false);
                if (editor is not null) await await ui.InvokeAsync(() => run(editor)).ConfigureAwait(false);
                return null;
            }, new CommandMetadata(id, title, "Merge Editor", Enablement: IsMergeEditorKey)));
        }
        Reg(AcceptMerge, "Complete Merge", e => e.CompleteAsync());
        Reg(NextConflict, "Go to Next Conflict", e => { e.GoToConflict(forward: true); return Task.CompletedTask; });
        Reg(PreviousConflict, "Go to Previous Conflict", e => { e.GoToConflict(forward: false); return Task.CompletedTask; });
        store.Add(Disposable.Create(_open.Clear));
        return store;
    }
}
