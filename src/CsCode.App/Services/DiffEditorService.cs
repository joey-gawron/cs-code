using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Editor;
using CsCode.Editor.Diff;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// The shell's <see cref="IDiffEditorService"/>: a diff opens as a document tab (one per comparison: opening the same one again activates it,
/// or replaces it when its content changed) whose view is the editor's <see cref="DiffEditorControl"/>, recreated from the request when the tab's
/// view is rebuilt. Keeps <c>textCompareEditorVisible</c> up to date and registers Next / Previous Change for the active diff (Alt+F5 /
/// Shift+Alt+F5 in the keymaps).
/// </summary>
public sealed class DiffEditorService(IUiDispatcher ui, IEditorViewFactory editors, Func<DocumentWell> well) : IDiffEditorService
{
    public const string NextChange = "workbench.action.compareEditor.nextChange";
    public const string PreviousChange = "workbench.action.compareEditor.previousChange";

    private readonly Dictionary<string, DiffEditorRequest> _open = new(StringComparer.Ordinal);

    public async ValueTask OpenDiffAsync(DiffEditorRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await await ui.InvokeAsync<Task>(async () =>
        {
            var w = well();
            var id = "diff:" + request.Key;
            var existing = w.Tabs.FirstOrDefault(t => t.Id == "view:" + id);
            if (existing is not null && _open.TryGetValue(id, out var shown) && shown != request) await w.CloseAsync(existing, prompt: false).ConfigureAwait(true);
            _open[id] = request;
            var tab = w.OpenView(id, request.Title, () => editors.CreateDiffView(request) ?? new TextBlock { Text = "The diff editor is not available." }, "diff");
            if (tab.LiveView is DiffEditorControl d) d.FocusEditor();
        }).ConfigureAwait(false);
    }

    /// <summary>The diff editor of the active tab, if it is one (UI thread).</summary>
    public DiffEditorControl? ActiveDiff => well().Active?.LiveView as DiffEditorControl;

    /// <summary>Registers the change navigation commands and follows the active tab for the context key. Called once by the shell.</summary>
    public IDisposable Start(DocumentWell documents, ICommandService commands, IContextKeyService keys)
    {
        var store = new DisposableStore();
        void Update(ShellDocument? tab) => keys.Set(ScmContextKeys.TextCompareEditorVisible, tab?.Id?.StartsWith("view:diff:", StringComparison.Ordinal) == true);
        store.Add(documents.ActiveChanged.Subscribe(Update));
        store.Add(documents.TabsChanged.Subscribe(_ =>
        {
            Update(documents.Active);
            var live = documents.Tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _open.Keys.Where(k => !live.Contains("view:" + k)).ToList()) _open.Remove(key);
        }));
        Update(documents.Active);
        ValueTask<object?> Step(bool forward) => new(ui.InvokeAsync(() => (object?)ActiveDiff?.GoToChange(forward)));
        if (!commands.Has(NextChange)) store.Add(commands.Register(NextChange, _ => Step(true), new CommandMetadata(NextChange, "Go to Next Change", "Compare")));
        if (!commands.Has(PreviousChange)) store.Add(commands.Register(PreviousChange, _ => Step(false), new CommandMetadata(PreviousChange, "Go to Previous Change", "Compare")));
        return store;
    }
}
