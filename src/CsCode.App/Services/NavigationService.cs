using System.Text;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// <see cref="INavigationService"/> over the document well. Callable from any thread (the synchronous cost on the caller is a single
/// dispatcher post); the active document / caret are published through volatile snapshots so non-UI readers never touch the UI thread.
/// <see cref="ApplyWorkspaceEditAsync"/> edits open documents in-editor as ONE undo group each, edits closed files on disk through
/// <see cref="IFileSystem"/> (read, apply, write, off the UI thread), performs create/rename/delete resource operations (with the file-operation
/// participants around them: will-rename edits first, did-create/rename/delete afterwards) and asks for confirmation when an edit spans several files.
/// </summary>
public sealed class NavigationService : INavigationService, IDisposable
{
    public const string ConfirmMultiFileSetting = "cscode.shell.confirmMultiFileEdits";

    private readonly DocumentWell _well;
    private readonly IUiDispatcher _ui;
    private readonly IFileSystem? _fs;
    private readonly IDocumentManager? _documents;
    private readonly IModalDialogs _dialogs;
    private readonly IWindowService? _windows;
    private readonly IConfigurationService? _config;
    private readonly FileParticipants? _participants;
    private readonly DisposableStore _subs = new();
    private readonly Emitter<IEditorDocument?> _activeChanged = new();
    private readonly Emitter<TextPosition?> _caretChanged = new();
    private IDisposable? _caretSub, _viewSub;
    private volatile IEditorDocument? _activeDoc;
    private volatile object? _activeCaret; // boxed TextPosition
    private volatile IReadOnlyList<IEditorDocument> _open = [];
    private Uri? _activeUri; // UI thread: the active document's path as last announced

    public NavigationService(DocumentWell well, IUiDispatcher ui, IModalDialogs dialogs, IFileSystem? fileSystem = null, IDocumentManager? documents = null,
        IWindowService? windows = null, IConfigurationService? config = null, FileParticipants? participants = null)
    {
        _participants = participants;
        _well = well;
        _ui = ui;
        _dialogs = dialogs;
        _fs = fileSystem;
        _documents = documents;
        _windows = windows;
        _config = config;
        _subs.Add(well.ActiveChanged.Subscribe(OnActive));
        _subs.Add(well.TabsChanged.Subscribe(_ =>
        {
            _open = well.EditorTabs.Select(t => t.Doc).ToArray();
            // A rename or move re-points the active editor in place (DocumentWell.RelocateAsync): same document, new path. VS Code announces
            // that as an active-editor change, and the Timeline, breadcrumbs and title follow the new path from it.
            if (_activeDoc is { } doc && doc.Uri != _activeUri)
            {
                _activeUri = doc.Uri;
                _activeChanged.Fire(doc);
            }
        }));
    }

    public IEditorDocument? ActiveDocument => _activeDoc;
    public TextPosition? ActiveCaret => _activeCaret is TextPosition p ? p : null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _activeChanged;
    /// <summary>Fires (UI thread) whenever the active editor's caret moves or the active editor changes (null = no editor). Shell-internal.</summary>
    public IEventSource<TextPosition?> ActiveCaretChanged => _caretChanged;
    public IReadOnlyList<IEditorDocument> OpenDocuments => _open;

    private void OnActive(ShellDocument? tab)
    {
        _caretSub?.Dispose();
        _viewSub?.Dispose();
        _caretSub = _viewSub = null;
        var editor = tab as EditorTab;
        _activeDoc = editor?.Doc;
        _activeUri = _activeDoc?.Uri;
        _activeCaret = null;
        _caretChanged.Fire(null);
        if (editor is not null)
        {
            // The view (and with it the controller) may not exist yet: attach the caret listener when it appears.
            if (editor.Controller is { } c) AttachCaret(c);
            else _viewSub = editor.ViewCreated.Subscribe(t => { if (t.Controller is { } cc) AttachCaret(cc); });
        }
        _activeChanged.Fire(_activeDoc);
    }

    private void AttachCaret(Editor.IEditorViewController c)
    {
        _caretSub?.Dispose();
        _activeCaret = c.Caret;
        _caretChanged.Fire(c.Caret);
        _caretSub = c.CaretChanged.Subscribe(p => { _activeCaret = p; _caretChanged.Fire(p); });
    }

    public async ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        var tab = await await _ui.InvokeAsync(() => _well.OpenAsync(uri, preview, preserveFocus, ct).AsTask()).ConfigureAwait(false);
        if (selection is { } sel)
            await _ui.InvokeAsync(() =>
            {
                if (tab.Controller is { } c) c.SetSelection(sel, reveal: true);
                else tab.PendingReveal = sel; // applied when the view is created
            }).ConfigureAwait(false);
        return tab.Doc;
    }

    // ------------------------------------------------------------------ search editor: untitled editors with text, to the side, titles

    public async ValueTask<IEditorDocument?> OpenUntitledAsync(UntitledEditorOptions options, CancellationToken ct = default)
    {
        var tab = await _ui.InvokeAsync(() =>
        {
            var t = _well.OpenUntitled(options.LanguageId, options.Text, options.Title, options.SuggestedSaveUri, options.ToSide);
            if (options.Selection is { } sel)
            {
                if (t.Controller is { } c) c.SetSelection(sel, reveal: true);
                else t.PendingReveal = sel; // applied when the view is created
            }
            return t;
        }).ConfigureAwait(false);
        return tab.Doc;
    }

    public async ValueTask<IEditorDocument?> OpenToSideAsync(Uri uri, TextRange? selection = null, CancellationToken ct = default)
    {
        var tab = await await _ui.InvokeAsync(() => _well.OpenToSideAsync(uri, ct).AsTask()).ConfigureAwait(false);
        if (selection is { } sel)
            await _ui.InvokeAsync(() =>
            {
                if (tab.Controller is { } c) c.SetSelection(sel, reveal: true);
                else tab.PendingReveal = sel;
            }).ConfigureAwait(false);
        return tab.Doc;
    }

    public void SetEditorTitle(Uri document, string? title, Uri? suggestedSaveUri = null) =>
        _ui.Post(() => { if (_well.Find(document) is { } tab) _well.SetTitle(tab, title, suggestedSaveUri); });

    // ------------------------------------------------------------------ workspace edits

    public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default) =>
        ApplyAsync(edit, label, nested: false, ct);

    /// <param name="nested">The edit will-rename participants returned for a rename inside another edit: applied without a second confirmation and
    /// without asking the participants again (their edit already accounts for the rename).</param>
    private async ValueTask<bool> ApplyAsync(WorkspaceEdit edit, string? label, bool nested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.IsEmpty) return true;

        var files = new HashSet<Uri>(edit.Changes.Keys);
        foreach (var op in edit.ResourceOperations ?? [])
            files.Add(op switch { CreateFileOperation c => c.Uri, RenameFileOperation r => r.OldUri, DeleteFileOperation d => d.Uri, _ => throw new NotSupportedException(op.GetType().Name) });

        if (!nested && files.Count > 1 && (_config?.Get(ConfirmMultiFileSetting, true) ?? true))
        {
            var answer = await await _ui.InvokeAsync(() => _dialogs.ShowAsync(
                label ?? "Apply changes",
                $"{label ?? "This change"} will modify {files.Count} files. Apply it?", ["Apply", "Cancel"], 0)).ConfigureAwait(false);
            if (answer != "Apply") return false;
        }

        try
        {
            foreach (var op in edit.ResourceOperations ?? [])
                await ApplyResourceOperationAsync(op, nested, ct).ConfigureAwait(false);

            foreach (var (uri, edits) in edit.Changes)
            {
                ct.ThrowIfCancellationRequested();
                var open = await _ui.InvokeAsync(() => FindOpen(uri)).ConfigureAwait(false);
                if (open is not null)
                {
                    // Documents are edited on the UI thread (the buffer is not thread-safe); ApplyEdits is one undo unit.
                    if (!await _ui.InvokeAsync(() => open.ApplyEdits(edits)).ConfigureAwait(false))
                        return await FailAsync($"Could not apply the edits to {uri}").ConfigureAwait(false);
                }
                else if (!await EditClosedFileAsync(uri, edits, ct).ConfigureAwait(false))
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return await FailAsync("Workspace edit failed: " + ex.Message).ConfigureAwait(false);
        }
    }

    private IEditorDocument? FindOpen(Uri uri) => (_documents?.Get(uri) as IEditorDocument) ?? _well.Find(uri)?.Doc;

    // Not awaited: an action-less toast completes only when dismissed or demoted, and callers (Replace All) apply files one after another.
    private ValueTask<bool> FailAsync(string message)
    {
        if (_windows is not null) _ = _windows.ShowMessageAsync(MessageSeverity.Error, message).AsTask();
        return ValueTask.FromResult(false);
    }

    private async ValueTask<bool> EditClosedFileAsync(Uri uri, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        if (_fs is null) return await FailAsync($"Cannot edit {uri}: no file system available").ConfigureAwait(false);
        var bytes = await _fs.ReadFileAsync(uri, ct).ConfigureAwait(false);
        // Edit on the calling worker, never on the UI thread.
        var (text, encoding) = Decode(bytes);
        var doc = new InMemoryTextDocument(uri, "plaintext", text);
        if (!doc.ApplyEdits(edits)) return await FailAsync($"Could not apply the edits to {uri}").ConfigureAwait(false);
        var updated = doc.GetText();
        var body = encoding.GetBytes(updated);
        var preamble = encoding.GetPreamble();
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        await _fs.WriteFileAsync(uri, result, createParents: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>BOM-aware decode that refuses invalid UTF-8 rather than silently corrupting a file.</summary>
    internal static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));
        return (new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false));
    }

    private async ValueTask ApplyResourceOperationAsync(ResourceOperation op, bool nested, CancellationToken ct)
    {
        if (_fs is null) throw new InvalidOperationException("No file system available for resource operations");
        switch (op)
        {
            case CreateFileOperation c:
            {
                var exists = await _fs.StatAsync(c.Uri, ct).ConfigureAwait(false) is not null;
                if (exists && c.IgnoreIfExists) return;
                if (exists && !c.Overwrite) throw new IOException($"{c.Uri} already exists");
                await _fs.WriteFileAsync(c.Uri, ReadOnlyMemory<byte>.Empty, createParents: true, ct).ConfigureAwait(false);
                if (_participants is not null) await _participants.DidCreateAsync([c.Uri]).ConfigureAwait(false);
                break;
            }
            case RenameFileOperation r:
            {
                if (await _fs.StatAsync(r.NewUri, ct).ConfigureAwait(false) is not null)
                {
                    if (r.IgnoreIfExists) return;
                    if (!r.Overwrite) throw new IOException($"{r.NewUri} already exists");
                }
                // An open tab for the old uri follows the rename: close it (it is saved or the user was warned) and reopen at the new uri.
                var wasOpen = await _ui.InvokeAsync(() => _well.Find(r.OldUri) is not null).ConfigureAwait(false);
                if (wasOpen)
                    await await _ui.InvokeAsync(async () => await _well.CloseAsync(_well.Find(r.OldUri)!, prompt: true).ConfigureAwait(true)).ConfigureAwait(false);
                IReadOnlyList<(Uri, Uri)> renamed = [(r.OldUri, r.NewUri)];
                if (!nested && _participants is not null && await _participants.WillRenameAsync(renamed, ct).ConfigureAwait(false) is { } participantEdit)
                    await ApplyAsync(participantEdit, null, nested: true, ct).ConfigureAwait(false); // e.g. imports that name the file
                await _fs.RenameAsync(r.OldUri, r.NewUri, r.Overwrite, ct).ConfigureAwait(false);
                if (_participants is not null) await _participants.DidRenameAsync(renamed).ConfigureAwait(false);
                if (wasOpen) await OpenAsync(r.NewUri, ct: ct).ConfigureAwait(false);
                break;
            }
            case DeleteFileOperation d:
            {
                if (await _fs.StatAsync(d.Uri, ct).ConfigureAwait(false) is null)
                {
                    if (d.IgnoreIfNotExists) return;
                    throw new FileNotFoundException(d.Uri.ToString());
                }
                var open = await _ui.InvokeAsync(() => _well.Find(d.Uri)).ConfigureAwait(false);
                if (open is not null) await await _ui.InvokeAsync(async () => await _well.CloseAsync(open, prompt: false).ConfigureAwait(true)).ConfigureAwait(false);
                await _fs.DeleteAsync(d.Uri, d.Recursive, useTrash: true, ct).ConfigureAwait(false);
                if (_participants is not null) await _participants.DidDeleteAsync([d.Uri]).ConfigureAwait(false);
                break;
            }
        }
    }

    public void Dispose()
    {
        _caretSub?.Dispose();
        _viewSub?.Dispose();
        _subs.Dispose();
        _activeChanged.Clear();
        _caretChanged.Clear();
    }
}
