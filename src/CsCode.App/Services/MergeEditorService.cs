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
/// when the tab's view is rebuilt. The tab holds that document open (<see cref="DocumentWell.HoldDocument"/>) also when no editor tab shows the
/// file; Complete Merge closes the tab. Closing it while the result has unsaved changes that no editor tab shows opens the file in one, so they
/// are never lost. Registers Complete Merge and Next / Previous Conflict for the active merge editor and keeps <c>isMergeEditor</c> current.
/// </summary>
public sealed class MergeEditorService(IUiDispatcher ui, IEditorViewFactory editors, Func<DocumentWell> well) : IMergeEditorService
{
    public const string AcceptMerge = "mergeEditor.acceptMerge";
    public const string NextConflict = "cscode.mergeEditor.nextConflict";
    public const string PreviousConflict = "cscode.mergeEditor.previousConflict";
    public const string IsMergeEditorKey = "isMergeEditor";

    private sealed class Entry(MergeEditorRequest request, IEditorDocument document, IDisposable hold)
    {
        public MergeEditorRequest Request { get; } = request;
        public IEditorDocument Document { get; } = document;
        public IDisposable Hold { get; } = hold;
        public ShellDocument? Tab { get; set; }
    }

    private readonly Dictionary<string, Entry> _open = new(StringComparer.Ordinal);

    public async ValueTask OpenMergeEditorAsync(MergeEditorRequest request, CancellationToken ct = default)
    {
        var doc = await editors.OpenDocumentAsync(request.Result, ct).ConfigureAwait(false);
        await ui.InvokeAsync(() =>
        {
            var w = well();
            var id = "merge:" + request.Result;
            if (_open.TryGetValue(id, out var open) && open.Tab is { } tab && w.Tabs.Contains(tab))
            {
                w.Activate(tab);
                return;
            }
            var entry = new Entry(request, doc, w.HoldDocument(doc));
            _open[id] = entry;
            entry.Tab = w.OpenView(id, request.Title, () => CreateView(entry), "diff");
        }).ConfigureAwait(false);
    }

    private Control CreateView(Entry entry)
    {
        if (editors.CreateMergeView(entry.Request, entry.Document) is not MergeEditorControl view) return new TextBlock { Text = "The merge editor is not available." };
        // Complete Merge: the tab closes (the subscription goes with the control: it clears its listeners when disposed)
        view.Completed.Subscribe(_ => ui.Post(() => { if (entry.Tab is { } t) _ = well().CloseAsync(t, prompt: false); }));
        return view;
    }

    /// <summary>The merge editor of the active tab, if it is one (UI thread).</summary>
    public MergeEditorControl? ActiveMergeEditor => well().Active?.LiveView as MergeEditorControl;

    /// <summary>Follows the tabs (releasing the documents of closed merge editors), registers the commands, keeps the context key. Called once by the shell.</summary>
    public IDisposable Start(DocumentWell documents, ICommandService commands, IContextKeyService keys)
    {
        var store = new DisposableStore();
        void Update(ShellDocument? tab) => keys.Set(IsMergeEditorKey, tab?.Id?.StartsWith("view:merge:", StringComparison.Ordinal) == true);
        store.Add(documents.ActiveChanged.Subscribe(Update));
        store.Add(documents.TabsChanged.Subscribe(_ =>
        {
            Update(documents.Active);
            var live = documents.Tabs;
            foreach (var (id, entry) in _open.Where(kv => kv.Value.Tab is { } t && !live.Contains(t)).ToList())
            {
                _open.Remove(id);
                _ = ReleaseAsync(documents, entry);
            }
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
        store.Add(Disposable.Create(() =>
        {
            foreach (var entry in _open.Values) entry.Hold.Dispose();
            _open.Clear();
        }));
        return store;
    }

    /// <summary>A merge editor tab closed: unsaved result changes no editor tab shows get one; then the document is let go.</summary>
    private static async Task ReleaseAsync(DocumentWell documents, Entry entry)
    {
        try
        {
            if (entry.Document.IsDirty && !documents.EditorTabs.Any(t => ReferenceEquals(t.Doc, entry.Document)))
                await documents.OpenAsync(entry.Document.Uri, preserveFocus: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally { entry.Hold.Dispose(); }
    }
}
