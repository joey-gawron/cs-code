using System.ComponentModel;
using AvaloniaEdit.Document;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Threading;

namespace CsCode.Editor.Documents;

/// <summary>
/// <see cref="IEditorDocument"/> over AvaloniaEdit's rope-backed <see cref="TextDocument"/>.
///
/// Threading: the <see cref="TextDocument"/> belongs to the UI thread (it is not thread-safe); typing mutates it directly, so a
/// keystroke costs one buffer mutation plus the change bookkeeping below and nothing else. Every member of
/// <see cref="ITextDocument"/> may be called from any thread: on the UI thread reads go to the live buffer, elsewhere to the last
/// published immutable <see cref="DocumentSnapshot"/>, which is replaced exactly when a change event for that version is raised
/// — so a background reader never sees half of an atomic edit.
///
/// Change events: one <see cref="TextDocumentChangedEventArgs"/> per atomic operation (outermost BeginUpdate/EndUpdate = one
/// undo group = typing a character, a paste, ApplyEdits, an undo/redo step); its changes are sequential (each range is expressed
/// against the state produced by the previous change).
/// </summary>
public sealed partial class EditorDocument : IEditorDocument, IDisposable
{
    private readonly IUiThread _ui;
    private readonly TextDocument _text;
    private readonly List<TextContentChange> _pending = new();
    private readonly Emitter<TextDocumentChangedEventArgs> _changed = new();
    private readonly Emitter<ITextDocument> _languageChanged = new();
    private readonly Emitter<IEditorDocument> _saved = new();
    private readonly Emitter<IEditorDocument> _dirtyChanged = new();
    private readonly Emitter<TextContentChange> _beforeChange = new();
    private readonly DisposableStore _disposables = new();
    private DocumentSnapshot _published;
    private string _languageId;
    private volatile bool _isDirty;
    private volatile bool _isReadOnly;
    private bool _languageExplicit;
    private bool _disposed;

    internal EditorDocument(Uri uri, string languageId, TextDocument buffer, IUiThread ui, int undoLimit = 1000)
    {
        Uri = uri;
        _languageId = languageId;
        _ui = ui;
        _text = buffer;
        _text.SetOwnerThread(ui.OwnerThread);
        _text.UndoStack.SizeLimit = Math.Max(0, undoLimit);
        _text.UndoStack.MarkAsOriginalFile();
        _published = Snapshot(1);

        _text.UpdateStarted += OnUpdateStarted;
        _text.Changing += OnChanging;
        _text.UpdateFinished += OnUpdateFinished;
        _text.UndoStack.PropertyChanged += OnUndoStackPropertyChanged;
        _disposables.Add(Disposable.Create(() =>
        {
            _text.UpdateStarted -= OnUpdateStarted;
            _text.Changing -= OnChanging;
            _text.UpdateFinished -= OnUpdateFinished;
            _text.UndoStack.PropertyChanged -= OnUndoStackPropertyChanged;
        }));
    }

    /// <summary>
    /// Builds a rope-backed buffer. Safe on ANY thread (a 50 MB file is turned into a rope on the IO lane); the buffer is handed to
    /// the UI thread here, by its current owner, because only the owner may transfer ownership.
    /// </summary>
    internal static TextDocument CreateBuffer(string text, IUiThread ui)
    {
        var buffer = new TextDocument(text);
        buffer.SetOwnerThread(ui.OwnerThread);
        return buffer;
    }

    // ---- identity / state ----

    public Uri Uri { get; private set; }

    public string LanguageId => Volatile.Read(ref _languageId);

    public int Version => Volatile.Read(ref _published).Version;

    public bool IsDirty => _isDirty;

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => _isReadOnly = value;
    }

    public bool IsUntitled => string.Equals(Uri.Scheme, "untitled", StringComparison.OrdinalIgnoreCase);

    public IEventSource<TextDocumentChangedEventArgs> Changed => _changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _languageChanged;
    public IEventSource<IEditorDocument> Saved => _saved;
    public IEventSource<IEditorDocument> DirtyChanged => _dirtyChanged;

    /// <summary>
    /// Raised for every primitive change BEFORE the buffer mutates (UI thread), with its range against the text as it still is: a handler can
    /// read the lines the change is about to replace. One per change of an atomic group; <see cref="Changed"/> follows at the end of the group.
    /// </summary>
    public IEventSource<TextContentChange> BeforeChange => _beforeChange;

    /// <summary>The live AvaloniaEdit buffer. UI thread only; views bind their <c>TextEditor</c> to it.</summary>
    internal TextDocument Buffer => _text;

    internal IUiThread Ui => _ui;

    /// <summary>The latest published immutable snapshot (any thread).</summary>
    public DocumentSnapshot Snapshot() => Volatile.Read(ref _published);

    /// <summary>User/shell choice of language (status bar picker). Stops automatic re-detection after Save As.</summary>
    public void SetLanguageId(string languageId)
    {
        _languageExplicit = true;
        ApplyLanguageId(languageId);
    }

    internal void ApplyLanguageId(string languageId)
    {
        if (string.IsNullOrEmpty(languageId)) return;
        if (Interlocked.Exchange(ref _languageId, languageId) == languageId) return;
        _languageChanged.Fire(this);
    }

    // ---- reads (any thread) ----

    public int Length => _ui.CheckAccess() ? _text.TextLength : Snapshot().Length;

    public int LineCount => _ui.CheckAccess() ? _text.LineCount : Snapshot().LineCount;

    public string GetText() => _ui.CheckAccess() ? _text.Text : Snapshot().GetText();

    public string GetText(TextRange range)
    {
        if (!_ui.CheckAccess()) return Snapshot().GetText(range);
        var s = OffsetAt(range.Start);
        var e = OffsetAt(range.End);
        return _text.GetText(s, Math.Max(0, e - s));
    }

    public string GetLineText(int line)
    {
        if (!_ui.CheckAccess()) return Snapshot().GetLineText(line);
        if (line < 0 || line >= _text.LineCount) return string.Empty;
        var l = _text.GetLineByNumber(line + 1);
        return _text.GetText(l.Offset, l.Length);
    }

    public int OffsetAt(TextPosition position)
    {
        if (!_ui.CheckAccess()) return Snapshot().OffsetAt(position);
        var line = _text.GetLineByNumber(Math.Clamp(position.Line, 0, _text.LineCount - 1) + 1);
        return line.Offset + Math.Clamp(position.Character, 0, line.Length);
    }

    public TextPosition PositionAt(int offset)
    {
        if (!_ui.CheckAccess()) return Snapshot().PositionAt(offset);
        offset = Math.Clamp(offset, 0, _text.TextLength);
        var line = _text.GetLineByOffset(offset);
        return new TextPosition(line.LineNumber - 1, offset - line.Offset);
    }

    public TextPosition Validate(TextPosition position)
    {
        if (!_ui.CheckAccess()) return Snapshot().Validate(position);
        var line = _text.GetLineByNumber(Math.Clamp(position.Line, 0, _text.LineCount - 1) + 1);
        return new TextPosition(line.LineNumber - 1, Math.Clamp(position.Character, 0, line.Length));
    }

    // ---- writes ----

    /// <summary>
    /// Applies edits (ranges relative to the current state, non-overlapping) as ONE undo unit. From a non-UI thread this blocks
    /// that thread until the UI thread has applied them; on the UI thread it is a plain synchronous mutation.
    /// </summary>
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) return true;
        if (_isReadOnly || _disposed) return false;
        if (_ui.CheckAccess()) return ApplyEditsOnUi(edits);
        var ok = false;
        _ui.Invoke(() => ok = ApplyEditsOnUi(edits));
        return ok;
    }

    private bool ApplyEditsOnUi(IReadOnlyList<TextEdit> edits)
    {
        // Resolve every edit against the CURRENT text first, then apply back-to-front so earlier offsets stay valid.
        var resolved = new (int Start, int End, string NewText)[edits.Count];
        for (var i = 0; i < edits.Count; i++)
            resolved[i] = (OffsetAt(edits[i].Range.Start), OffsetAt(edits[i].Range.End), edits[i].NewText);
        // Stable sort: edits at the same position keep their list order (the first one ends up first in the text).
        var order = Enumerable.Range(0, resolved.Length).OrderBy(i => resolved[i].Start).ThenBy(i => resolved[i].End).ToArray();
        for (var i = 1; i < order.Length; i++)
            if (resolved[order[i]].Start < resolved[order[i - 1]].End)
                return false; // overlapping
        _text.BeginUpdate();
        try
        {
            for (var i = order.Length - 1; i >= 0; i--)
            {
                var r = resolved[order[i]];
                _text.Replace(r.Start, Math.Max(0, r.End - r.Start), r.NewText);
            }
        }
        finally
        {
            _text.EndUpdate();
        }
        return true;
    }

    // ---- undo (UI thread) ----

    public bool CanUndo => _text.UndoStack.CanUndo;
    public bool CanRedo => _text.UndoStack.CanRedo;

    /// <summary>
    /// Undoes one group. The whole group is ONE change event: UndoStack registers the document as "affected" for the entire
    /// playback (one BeginUpdate/EndUpdate), and it must NOT be wrapped in our own update (it throws if an undo group is open).
    /// </summary>
    public void Undo() => _text.UndoStack.Undo();

    public void Redo() => _text.UndoStack.Redo();

    /// <summary>
    /// Opens an undo group (UI thread): every change until the matching <see cref="EndUndoGroup"/> becomes ONE undo step; with
    /// <paramref name="continueLast"/> they join the last step instead (when the history allows it). Groups nest. Undo/Redo must not be
    /// called while one is open (the buffer throws). Used by modal editing layers to make one Vim change one undo step.
    /// </summary>
    public void BeginUndoGroup(bool continueLast)
    {
        if (continueLast) _text.UndoStack.StartContinuedUndoGroup();
        else _text.UndoStack.StartUndoGroup();
    }

    public void EndUndoGroup() => _text.UndoStack.EndUndoGroup();

    /// <summary>
    /// Records a marker in the open undo group (UI thread; a group must be open): <paramref name="onUndoRedo"/> runs when the group is undone
    /// (false) or redone (true). The marker is not a modification (dirty tracking ignores it), and a group holding nothing else is dropped.
    /// </summary>
    public void AddUndoMarker(Action<bool> onUndoRedo) => _text.UndoStack.PushOptional(new UndoMarker(onUndoRedo));

    private sealed class UndoMarker(Action<bool> callback) : IUndoableOperation
    {
        public void Undo() => callback(false);
        public void Redo() => callback(true);
    }

    /// <summary>Runs <paramref name="action"/> as one atomic change group (one event, one undo unit).</summary>
    public void RunAtomic(Action action)
    {
        _text.BeginUpdate();
        try
        {
            action();
        }
        finally
        {
            _text.EndUpdate();
        }
    }

    // ---- change bookkeeping (UI thread, inside TextDocument events) ----

    private void OnUpdateStarted(object? sender, EventArgs e) => _pending.Clear();

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        // Runs BEFORE the mutation, so positions are expressed against the state the change applies to.
        var start = _text.GetLocation(e.Offset);
        var end = e.RemovalLength == 0 ? start : _text.GetLocation(e.Offset + e.RemovalLength);
        var change = new TextContentChange(
            new TextRange(new TextPosition(start.Line - 1, start.Column - 1), new TextPosition(end.Line - 1, end.Column - 1)),
            e.Offset,
            e.RemovalLength,
            e.InsertedText.Text);
        _pending.Add(change);
        _beforeChange.Fire(change);
    }

    private void OnUpdateFinished(object? sender, EventArgs e)
    {
        if (_pending.Count == 0) return;
        var changes = _pending.ToArray();
        _pending.Clear();
        var version = Volatile.Read(ref _published).Version + 1;
        var snapshot = Snapshot(version);
        Volatile.Write(ref _published, snapshot); // publish BEFORE the event so handlers that read through any thread see this version
        _changed.Fire(new TextDocumentChangedEventArgs(this, version, changes));
    }

    private void OnUndoStackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) UpdateDirty(!_text.UndoStack.IsOriginalFile);
    }

    private void UpdateDirty(bool dirty)
    {
        if (_isDirty == dirty) return;
        _isDirty = dirty;
        _dirtyChanged.Fire(this);
    }

    private DocumentSnapshot Snapshot(int version) => new(version, ((AvaloniaEdit.Document.RopeTextSource)_text.CreateSnapshot()).GetRope());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeFileState();
        _disposables.Dispose();
        _changed.Clear();
        _languageChanged.Clear();
        _saved.Clear();
        _dirtyChanged.Clear();
        _beforeChange.Clear();
    }
}
