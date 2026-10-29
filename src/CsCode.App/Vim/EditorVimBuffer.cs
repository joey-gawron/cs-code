using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>
/// The <see cref="IVimBuffer"/> of one open document, shared by the engines of all its views (through the session they share the document's undo
/// tree, marks and jump list). It lives as long as the document is open while Vim is on, so the history survives the shell destroying and
/// recreating inactive tabs' views.
/// <para>
/// <b>Undo.</b> Every Vim change becomes ONE undo group in the editor's own history, labelled with the Vim step it belongs to: the group is opened
/// lazily at the first change of a key (continuing the previous group when the change joins the same step, e.g. typing in an Insert session) and
/// closed when the key is done; a marker in each group reports the step back when the editor undoes or redoes it. The engine asks
/// <see cref="CanUndoRedoStep"/> before <c>u</c>/<c>Ctrl-R</c>: when the editor's history holds exactly that step on top, the editor undoes it
/// (so its history, dirty marker and the engine's tree stay in step); otherwise (a foreign change in between, a step that resumed after one,
/// undo-tree branches) the engine applies the step as edits. The marker identity check here and the engine's text check after a host undo make a
/// wrong picture of the history cost only that fallback, never wrong text.
/// </para>
/// <para>
/// <b>Changes the engine did not make</b> (the editor's typing path in Insert mode, an accepted completion, a snippet, a formatter, a workspace
/// edit) are reported through <see cref="TryTakeExternalChanges"/> with the lines they replaced, read from the document just before it changed:
/// during a key or an Insert session they join that change; otherwise each update is an undo step of its own.
/// </para>
/// UI thread only.
/// </summary>
internal sealed class EditorVimBuffer : IVimBuffer, IDisposable
{
    private const int CacheSize = 64; // power of two
    private const int MaxPendingChanges = 10_000;
    private const long MaxPendingChars = 4_000_000;
    private const long Unlabelled = long.MinValue;

    private readonly EditorDocument _doc;
    private readonly string?[] _cache = new string?[CacheSize];
    private readonly int[] _cacheLine = new int[CacheSize];
    private int _cacheVersion = -1;
    private int _changes = 1;
    private readonly DisposableStore _subscriptions = new();
    private readonly List<VimExternalChange> _external = [];
    private long _externalChars;
    private bool _externalOverflow;
    private int _foreignGroup, _nextForeignGroup = 1;

    private VimEngine? _engine;
    private int _selfDepth, _typingDepth, _pumpDepth;
    private bool _groupOpen, _groupContinued, _groupChanged;
    private long _groupLabel = Unlabelled;
    private bool _disposed;

    public EditorVimBuffer(EditorDocument document)
    {
        _doc = document;
        _subscriptions.Add(document.BeforeChange.Subscribe(OnBeforeChange));
        _subscriptions.Add(document.Changed.Subscribe(_ => _foreignGroup = 0)); // the end of an update: the next foreign change is a new step
    }

    public EditorDocument Document => _doc;

    /// <summary>The picture of the editor's undo history (tests and diagnostics).</summary>
    internal UndoLedger Ledger { get; } = new();

    /// <summary>The engine whose key is being handled, or that handled the last one (its mode decides how foreign changes are grouped).</summary>
    public void SetActiveEngine(VimEngine? engine) => _engine = engine;

    public VimEngine? ActiveEngine => _engine;

    // ---------------------------------------------------------------- IVimBuffer: identity and reads

    public string Name => _doc.Uri.IsFile ? Path.GetFileName(_doc.Uri.LocalPath) : _doc.Uri.ToString();
    public Uri? Uri => _doc.Uri;
    public string LanguageId => _doc.LanguageId;
    /// <summary>Bumps on every primitive change, also inside an atomic group (where the document's own version moves only at its end).</summary>
    public int Version => _changes;
    public int LineCount => _doc.LineCount;
    public string EndOfLine => _doc.EndOfLine;
    public bool IsReadOnly => _doc.IsReadOnly;

    public string GetLine(int line)
    {
        var v = _changes;
        if (v != _cacheVersion)
        {
            Array.Clear(_cache);
            _cacheVersion = v;
        }
        var slot = line & (CacheSize - 1);
        if (_cache[slot] is { } s && _cacheLine[slot] == line) return s;
        s = _doc.GetLineText(line);
        _cache[slot] = s;
        _cacheLine[slot] = line;
        return s;
    }

    public int GetLineLength(int line) => GetLine(line).Length;

    // ---------------------------------------------------------------- IVimBuffer: edits

    public bool ApplyEdit(in VimEdit edit)
    {
        if (_doc.IsReadOnly) return false;
        EnsureGroup();
        EnsureAtomic();
        _selfDepth++;
        try { return _doc.ApplyEdits([new TextEdit(new TextRange(edit.Start, edit.End), ToDocText(edit.Text))]); }
        finally { _selfDepth--; }
    }

    public bool ApplyEdits(ReadOnlySpan<VimEdit> edits)
    {
        if (_doc.IsReadOnly) return false;
        if (edits.Length == 0) return true;
        var list = new TextEdit[edits.Length];
        for (var i = 0; i < edits.Length; i++) list[i] = new TextEdit(new TextRange(edits[i].Start, edits[i].End), ToDocText(edits[i].Text));
        EnsureGroup();
        EnsureAtomic();
        _selfDepth++;
        try { return _doc.ApplyEdits(list); }
        finally { _selfDepth--; }
    }

    private string ToDocText(string text)
    {
        var eol = _doc.EndOfLine;
        return eol == "\n" || text.IndexOf('\n') < 0 ? text : text.Replace("\n", eol);
    }

    /// <summary>The engine brackets every key (and slice of work) with these: the undo group of the key is closed at the end.</summary>
    public void BeginEditGroup() => _pumpDepth++;

    public void EndEditGroup()
    {
        if (_pumpDepth > 0 && --_pumpDepth == 0)
        {
            EndAtomic();
            if (_groupOpen) CloseGroup();
        }
    }

    private bool _atomic;

    /// <summary>
    /// The engine's own edits of one key (or one slice of a long command) form one atomic change of the document: the editor's features react
    /// once, not once per line (":g/pat/s//" over thousands of lines). Never around the typing path, whose features read the text as typed.
    /// </summary>
    private void EnsureAtomic()
    {
        if (_atomic || _pumpDepth == 0 || _typingDepth > 0 || _disposed) return;
        _atomic = true;
        _doc.BeginAtomic();
    }

    private void EndAtomic()
    {
        if (!_atomic) return;
        _atomic = false;
        _doc.EndAtomic();
    }

    /// <summary>
    /// The editor's typing path is about to run on the engine's behalf (Insert mode): its changes belong to the engine's current change, in
    /// the key's undo group. Dispose the result when it returned.
    /// </summary>
    public TypingScope BeginTyping()
    {
        EndAtomic(); // the typing path's features (completion, signature help) read the text as typed
        EnsureGroup();
        _typingDepth++;
        return new TypingScope(this);
    }

    public readonly struct TypingScope(EditorVimBuffer owner) : IDisposable
    {
        public void Dispose() => owner._typingDepth--;
    }

    private bool _editorKey;

    /// <summary>
    /// A key Vim leaves to the editor in Insert mode (Enter or Tab accepting a completion, a paste) is about to be handled: what the editor
    /// changes belongs to the Insert session, in its undo group. <see cref="EndEditorKey"/> after the key event.
    /// </summary>
    public void BeginEditorKey()
    {
        if (_editorKey || _disposed) return;
        EndAtomic();
        _editorKey = true;
        _pumpDepth++;
        _typingDepth++;
        EnsureGroup();
    }

    public void EndEditorKey()
    {
        if (!_editorKey) return;
        _editorKey = false;
        _typingDepth--;
        EndEditGroup();
    }

    // ---------------------------------------------------------------- undo groups

    private static long Label(int generation, int seq) => ((long)generation << 32) | (uint)seq;

    /// <summary>The step the next change of the active engine belongs to (-1: the engine is applying an undo itself; no step of its own).</summary>
    private long NextLabel() => _engine is not { } e ? Unlabelled : e.IsApplyingUndo ? UndoLedger.ForeignLabel : Label(e.UndoHistoryGeneration, e.NextChangeUndoSequence);

    private void EnsureGroup()
    {
        if (_pumpDepth == 0 || _disposed) return; // a change outside a key forms its own group, as any editor change
        var label = NextLabel();
        if (label == Unlabelled) return;
        if (_groupOpen)
        {
            if (label == _groupLabel) return;
            CloseGroup(); // a second step within one key (typeahead): a group of its own
        }
        var continued = label >= 0 && Ledger.TopLabel == label;
        _doc.BeginUndoGroup(continued);
        _groupOpen = true;
        _groupContinued = continued;
        _groupChanged = false;
        _groupLabel = label;
    }

    private void CloseGroup()
    {
        EndAtomic(); // the atomic change lives inside the undo group
        var label = _groupLabel;
        if (_groupChanged && !_groupContinued && label >= 0)
        {
            var ledger = Ledger; // the marker outlives this buffer in the editor's history: it must hold nothing but the ledger
            _doc.AddUndoMarker(_ => ledger.Reported = label);
        }
        _groupOpen = false;
        _groupLabel = Unlabelled;
        _doc.EndUndoGroup();
        if (_groupChanged) Ledger.Group(label, _groupContinued);
        _groupChanged = false;
    }

    public bool CanUndoRedoStep(int seq, bool redo)
    {
        if (_engine is not { } e || _disposed) return false;
        var label = Label(e.UndoHistoryGeneration, seq);
        return redo ? _doc.CanRedo && Ledger.CanRedo(label) : _doc.CanUndo && Ledger.CanUndo(label);
    }

    public bool UndoRedoStep(int seq, bool redo)
    {
        if (_engine is not { } e || _disposed) return false;
        EndAtomic();
        if (_groupOpen) CloseGroup(); // the editor cannot undo while a group is open
        var label = Label(e.UndoHistoryGeneration, seq);
        var groups = Ledger.GroupsOnTop(label, redo);
        if (groups == 0) return false;
        var done = 0;
        _selfDepth++;
        try
        {
            for (; done < groups; done++)
            {
                if (redo ? !_doc.CanRedo : !_doc.CanUndo) break;
                Ledger.Reported = UndoLedger.NoReport;
                var version = _doc.Version;
                if (redo) _doc.Redo(); else _doc.Undo();
                if (Ledger.Reported == label) continue;
                // Not the group we thought (or nothing happened): put it back exactly as it was.
                if (_doc.Version != version)
                {
                    if (redo) _doc.Undo(); else _doc.Redo();
                }
                break;
            }
            if (done < groups)
            {
                for (var i = 0; i < done; i++)
                {
                    if (redo) _doc.Undo(); else _doc.Redo();
                }
                Ledger.Invalidate();
                return false;
            }
        }
        finally { _selfDepth--; }
        Ledger.Move(label, redo);
        return true;
    }

    // ---------------------------------------------------------------- changes the engine did not make

    private void OnBeforeChange(TextContentChange change)
    {
        if (_disposed) return;
        unchecked { _changes++; } // invalidates the line cache for reads after this change
        if (_groupOpen) _groupChanged = true;
        if (_selfDepth > 0) return; // the engine's own edit or a host undo: it knows
        if (!_groupOpen) Ledger.Foreign();
        if (_externalOverflow) return;

        var start = change.Range.Start.Line;
        var end = change.Range.End.Line;
        var old = new string[Math.Max(1, end - start + 1)];
        for (var i = 0; i < old.Length; i++)
        {
            old[i] = _doc.GetLineText(start + i);
            _externalChars += old[i].Length + 16;
        }
        // During a key, the typing path or an Insert session the change joins the change in progress; otherwise each update is a step.
        var inInsert = _engine is { Mode: VimMode.Insert or VimMode.Replace };
        var group = _pumpDepth > 0 || _typingDepth > 0 || inInsert ? 0 : ForeignGroup();
        _external.Add(new VimExternalChange(start, old, 1 + CountLineBreaks(change.Text), group));
        if (_external.Count > MaxPendingChanges || _externalChars > MaxPendingChars)
        {
            // Nobody has taken them for a long time (no view of this document): stop recording; the engine then starts a fresh history.
            _external.Clear();
            _externalChars = 0;
            _externalOverflow = true;
        }
    }

    private int ForeignGroup()
    {
        if (_foreignGroup == 0)
        {
            _foreignGroup = _nextForeignGroup;
            _nextForeignGroup = _nextForeignGroup == int.MaxValue ? 1 : _nextForeignGroup + 1;
        }
        return _foreignGroup;
    }

    private static int CountLineBreaks(string text)
    {
        var n = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') n++;
            else if (c == '\r')
            {
                n++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
        }
        return n;
    }

    public bool TryTakeExternalChanges(List<VimExternalChange> into)
    {
        if (_externalOverflow)
        {
            _externalOverflow = false;
            return false; // too much happened unobserved: the engine treats it as one change it cannot describe
        }
        into.AddRange(_external);
        _external.Clear();
        _externalChars = 0;
        return true;
    }

    /// <summary>Pending (not yet adopted) foreign changes, for tests.</summary>
    internal int PendingExternalCount => _external.Count;

    public void Dispose()
    {
        if (_disposed) return;
        EndAtomic();
        if (_groupOpen) CloseGroup();
        _disposed = true;
        _subscriptions.Dispose();
        _external.Clear();
        _engine = null;
    }
}

/// <summary>
/// The top of the editor's undo and redo histories as the Vim layer knows them: one entry per run of groups of one Vim step (label = history
/// generation and step number), and <see cref="ForeignLabel"/> entries for groups made by anything else. Holds nothing but numbers: the markers in
/// the editor's history reference it and may outlive the buffer.
/// </summary>
internal sealed class UndoLedger
{
    public const long ForeignLabel = -1;
    public const long NoReport = long.MinValue;
    private const int MaxEntries = 2048;
    private const int MixedScan = 64;

    private readonly List<(long Label, int Groups)> _undo = [];
    private readonly List<(long Label, int Groups)> _redo = [];
    private readonly HashSet<long> _mixed = [];

    /// <summary>Set by a marker when the editor undoes or redoes its group.</summary>
    public long Reported { get; set; } = NoReport;

    public long TopLabel => _undo.Count > 0 ? _undo[^1].Label : ForeignLabel;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>One of our groups closed with changes (continued = it was merged into the group on top, which has the same label).</summary>
    public void Group(long label, bool continued)
    {
        if (continued) return;
        _redo.Clear(); // the editor drops its redo history on any new change
        if (label < 0)
        {
            Foreign();
            return;
        }
        if (_undo.Count > 0 && _undo[^1].Label == label)
        {
            _undo[^1] = (label, _undo[^1].Groups + 1);
            return;
        }
        for (var i = _undo.Count - 1; i >= Math.Max(0, _undo.Count - MixedScan); i--)
            if (_undo[i].Label == label)
            {
                // the step resumed after something else changed the document: undoing it means more than the groups on top
                if (_mixed.Count >= MaxEntries) _mixed.Clear();
                _mixed.Add(label);
                break;
            }
        Push(_undo, (label, 1));
    }

    /// <summary>A change that is not a Vim step's (made outside any key).</summary>
    public void Foreign()
    {
        _redo.Clear();
        if (_undo.Count > 0 && _undo[^1].Label == ForeignLabel) return;
        Push(_undo, (ForeignLabel, 1));
    }

    public bool CanUndo(long label) => label >= 0 && _undo.Count > 0 && _undo[^1].Label == label && !_mixed.Contains(label);

    public bool CanRedo(long label) => label >= 0 && _redo.Count > 0 && _redo[^1].Label == label;

    /// <summary>Number of the editor's groups that make up step <paramref name="label"/> on top of the undo (or redo) history; 0 when it is not on top.</summary>
    public int GroupsOnTop(long label, bool redo)
    {
        if (redo ? !CanRedo(label) : !CanUndo(label)) return 0;
        return (redo ? _redo : _undo)[^1].Groups;
    }

    /// <summary>The editor undid (redid) the step on top: it moves to the other history.</summary>
    public void Move(long label, bool redo)
    {
        var (from, to) = redo ? (_redo, _undo) : (_undo, _redo);
        if (from.Count == 0 || from[^1].Label != label) return;
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, entry);
    }

    /// <summary>Our picture of the history proved wrong: nothing below is trusted until new groups are made.</summary>
    public void Invalidate()
    {
        _undo.Clear();
        _redo.Clear();
        _mixed.Clear();
        Push(_undo, (ForeignLabel, 1));
    }

    private static void Push(List<(long Label, int Groups)> list, (long Label, int Groups) entry)
    {
        if (list.Count >= MaxEntries) list.RemoveRange(0, MaxEntries / 4);
        list.Add(entry);
    }
}
