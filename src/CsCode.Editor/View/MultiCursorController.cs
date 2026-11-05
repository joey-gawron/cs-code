using System.Text.Json;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Features;

namespace CsCode.Editor.View;

/// <summary>
/// Multi-cursor editing for one view, layered on AvaloniaEdit (which has a single caret). The primary caret and selection stay AvaloniaEdit's
/// own, so every single-caret feature keeps working unchanged; the other carets live here in a <see cref="CaretSet"/> of document offsets,
/// are drawn by two background renderers (selection backgrounds, blinking carets; only the visible ones) and are edited through
/// <see cref="MultiCaretOperations"/>: a typed character, Backspace, Delete, Enter, Tab, a paste... is ONE batch of edits applied back to
/// front inside one document update, so it is one change event and one undo step (typing runs coalesce as single-caret typing does), and undo
/// / redo bring the carets back (up to <see cref="UndoSnapshotLimit"/> carets).
/// <para>
/// Lifetime of the extra carets: they follow edits made elsewhere (another view, a language server's edit) through the buffer's change events;
/// anything else that moves the primary caret (a click, Page Down, Go to Definition, a snippet) collapses them to the primary, as in VS Code.
/// Escape collapses them too. Under a modal layer the feature is off unless the layer handles several carets itself (the Vim adapter does:
/// <see cref="EditorModalSurface.AllowMultipleCarets"/>, <see cref="HoldCarets"/>).
/// </para>
/// <para>
/// Cost: nothing while there is one caret (each handler returns at once). With n carets a keystroke is O(n log document) plus each caret's own
/// line; drawing touches only the carets in the visible lines (binary search). The number of carets is bounded by <c>editor.multiCursorLimit</c>.
/// </para>
/// </summary>
internal sealed class MultiCursorController : IDisposable, IThemeAware
{
    /// <summary>Undo/redo restore the carets of an edit made with at most this many (a snapshot per undo step; bounded memory).</summary>
    internal const int UndoSnapshotLimit = 1_000;

    /// <summary>How many caret states Cursor Undo (Ctrl+U) can go back.</summary>
    internal const int CursorUndoDepth = 50;

    /// <summary>Above this many characters Select All Occurrences searches on a worker instead of the UI thread.</summary>
    private const int WorkerSearchThreshold = 4_000_000;

    internal const string LimitKey = "editor.multiCursorLimit";
    internal const string PasteKey = "editor.multiCursorPaste";
    internal const string ModifierKey = "editor.multiCursorModifier";

    /// <summary>The settings this feature reads (VS Code's own ids), registered by the factory so the Settings panel lists them.</summary>
    internal static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(LimitKey, JsonSerializer.SerializeToElement(10_000),
            "The maximum number of cursors an editor can have at once (Add Next Occurrence, Select All Occurrences, Insert Cursor Above/Below, Alt+Click).",
            JsonDocument.Parse("""{"type":"integer","minimum":1,"maximum":100000}""").RootElement.Clone()),
        new(PasteKey, JsonSerializer.SerializeToElement("spread"),
            "Pasting with several cursors: spread puts one line of the clipboard at each cursor when the line count equals the cursor count; full pastes the whole text at every cursor.",
            JsonDocument.Parse("""{"type":"string","enum":["spread","full"]}""").RootElement.Clone()),
        new(ModifierKey, JsonSerializer.SerializeToElement("alt"),
            "The modifier that adds a caret with the mouse: alt (Alt+Click, Option+Click on macOS) or ctrlCmd (Ctrl+Click, ⌘+Click on macOS). Go to Definition and Open Link take the other one.",
            JsonDocument.Parse("""{"type":"string","enum":["ctrlCmd","alt"]}""").RootElement.Clone()),
    ];

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly SelectionRenderer _selectionRenderer;
    private readonly CaretRenderer _caretRenderer;
    private readonly DispatcherTimer _blink;
    private readonly LinkedList<(CaretSet Set, int Version)> _cursorUndo = new();
    private readonly WeakReference<MultiCursorController> _self;
    private CaretSet? _set;               // null while there is a single caret
    private OccurrenceQuery? _query;      // what Add Next Occurrence keeps looking for
    private bool _internal;               // this class is moving AvaloniaEdit's caret
    private bool _inUpdate;               // the buffer is inside an update (its own changes move the caret)
    private bool _applying;               // a batch of ours is being applied (do not map it again)
    private CaretSet? _pendingRestore;    // undo/redo asked for these carets; applied when the update ends
    private readonly List<DocumentChangeEventArgs> _pendingChanges = new(); // changes from elsewhere in the current update, not yet mapped
    private bool _blinkOn = true;
    private bool _swallowNewlineText;     // Enter was handled as a key: a platform's "\r" text that follows is not a second Enter
    private (int Version, CaretSet Set)? _typingRun; // the last typed batch, which the next typed character may continue as one undo step
    private EndMarker? _lastEndMarker;
    private (CaretSet Before, Point At)? _altClick;
    private bool _disposed;

    public MultiCursorController(CodeEditorControl view)
    {
        _v = view;
        _self = new WeakReference<MultiCursorController>(this);
        _selectionRenderer = new SelectionRenderer(this);
        _caretRenderer = new CaretRenderer(this);
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += OnBlink;
        var area = view.TextArea;
        var buffer = view.Document.Buffer;
        view.TextView.BackgroundRenderers.Add(_selectionRenderer);
        view.TextView.BackgroundRenderers.Add(_caretRenderer);
        // Registered before TypingBehaviors, the completion list and the snippet session (this component is created first). TextEntering is a
        // CLR event, so with several carets typing reaches this component first. Avalonia calls one element's tunnelling KeyDown handlers in
        // the REVERSE order of registration, so the later features see a key first: they step aside while several carets exist (TypingBehaviors'
        // Backspace) or keep the keys they own (the completion list, parameter hints and a snippet session; PopupWantsKey here). With one
        // caret these handlers return at once and those features run as before.
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        area.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        area.TextEntering += OnTextEntering;
        area.Caret.PositionChanged += OnPrimaryMoved;
        area.SelectionChanged += OnPrimaryMoved;
        area.GotFocus += OnFocusChanged;
        area.LostFocus += OnFocusChanged;
        buffer.UpdateStarted += OnUpdateStarted;
        buffer.Changed += OnBufferChanged;
        buffer.UpdateFinished += OnUpdateFinished;
        _d.Add(Disposable.Create(() =>
        {
            area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            area.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            area.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            area.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
            area.TextEntering -= OnTextEntering;
            area.Caret.PositionChanged -= OnPrimaryMoved;
            area.SelectionChanged -= OnPrimaryMoved;
            area.GotFocus -= OnFocusChanged;
            area.LostFocus -= OnFocusChanged;
            buffer.UpdateStarted -= OnUpdateStarted;
            buffer.Changed -= OnBufferChanged;
            buffer.UpdateFinished -= OnUpdateFinished;
            view.TextView.BackgroundRenderers.Remove(_selectionRenderer);
            view.TextView.BackgroundRenderers.Remove(_caretRenderer);
        }));
    }

    // ------------------------------------------------------------------ state

    /// <summary>Whether the view has more than one caret right now.</summary>
    public bool IsActive => _set is { IsMulti: true };

    /// <summary>The carets (primary included), in document order; one caret when the feature is not active.</summary>
    public CaretSet Current
    {
        get
        {
            FollowPendingChanges();
            return _set is { } s ? s.WithPrimaryRange(PrimaryFromEditor()) : CaretSet.Single(PrimaryFromEditor());
        }
    }

    /// <summary>How many times the carets were moved along changes made elsewhere (at the end of a document update, or earlier when something reads them).</summary>
    internal int FollowCount { get; private set; }

    /// <summary>Off while a modal layer is attached, unless the layer handles several carets itself (<see cref="EditorModalSurface.AllowMultipleCarets"/>).</summary>
    private bool Enabled => !_disposed && !_v.IsDisposed && (!_v.ModalAttached || _v.ModalAllowsMultipleCarets);

    private int _hold;

    /// <summary>
    /// While the result is not disposed, moves of the primary caret by someone else do not collapse the other carets: a modal layer (Vim) moving
    /// its cursor as part of a command it runs at every caret, which places the other carets itself.
    /// </summary>
    internal IDisposable HoldCarets()
    {
        _hold++;
        var released = false;
        return Disposable.Create(() =>
        {
            if (released) return;
            released = true;
            _hold--;
        });
    }

    private int Limit => _v.Settings.MultiCursorLimit;

    /// <summary>
    /// AvaloniaEdit's caret and selection as a caret range (a box selection counts as its caret). A selection's ends are its own: a modal layer
    /// may put the caret inside it (Vim's Visual mode keeps the caret ON the last selected character).
    /// </summary>
    private CaretRange PrimaryFromEditor()
    {
        var area = _v.TextArea;
        var caret = area.Caret.Offset;
        if (area.Selection is SimpleSelection { IsEmpty: false } s)
        {
            var buffer = _v.Document.Buffer;
            var anchor = buffer.GetOffset(s.StartPosition.Location);
            var end = buffer.GetOffset(s.EndPosition.Location);
            // the caret at the selection's start: it runs backwards from its end (Vim draws a backward Visual selection this way)
            return caret == anchor && caret != end ? new CaretRange(end, anchor) : new CaretRange(anchor, end);
        }
        return CaretRange.At(caret);
    }

    /// <summary>Makes <paramref name="set"/> the view's carets: its primary goes to AvaloniaEdit, the rest are kept and drawn here.</summary>
    internal void SetCarets(CaretSet set, bool reveal = true)
    {
        if (_disposed) return;
        var length = _v.Document.Buffer.TextLength;
        if (set.Carets.Any(c => c.End > length || c.Start < 0))
            set = CaretSet.Create(set.Carets.Select(c => new CaretRange(Math.Clamp(c.Anchor, 0, length), Math.Clamp(c.Active, 0, length))).ToArray(), set.PrimaryIndex, Limit);
        var wasActive = IsActive;
        _pendingChanges.Clear(); // they moved the carets being replaced
        _set = set.IsMulti ? set : null;
        if (!set.IsMulti)
        {
            _query = _query is not null && !set.Primary.IsEmpty ? _query : null;
            _typingRun = null;
        }
        ApplyPrimary(set.Primary);
        UpdateBlink();
        Invalidate();
        if (reveal) Reveal(set.Primary);
        if (set.IsMulti || wasActive) _v.Controller.RaiseSelectionChanged();
    }

    /// <summary>Back to the primary caret alone (Escape, editor.action.removeSecondaryCursors).</summary>
    public void Collapse()
    {
        if (_set is null) return;
        _set = null;
        _pendingChanges.Clear();
        _query = null;
        _typingRun = null;
        _altClick = null;
        UpdateBlink();
        Invalidate();
        _v.Controller.RaiseSelectionChanged();
    }

    private void ApplyPrimary(CaretRange primary)
    {
        var area = _v.TextArea;
        _internal = true;
        try
        {
            if (primary.IsEmpty) area.ClearSelection();
            else area.Selection = Selection.Create(area, primary.Anchor, primary.Active);
            area.Caret.Offset = primary.Active;
        }
        finally
        {
            _internal = false;
        }
    }

    private void Reveal(CaretRange caret)
    {
        var at = _v.PositionOf(caret.Active);
        _v.Controller.Reveal(new TextRange(at, at), center: false);
        _v.TextArea.Caret.BringCaretToView();
    }

    private void Invalidate()
    {
        _blinkOn = true;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
        _v.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    // ------------------------------------------------------------------ following the document and the primary caret

    private void OnPrimaryMoved(object? sender, EventArgs e)
    {
        // A move of the primary that is not ours and not the buffer's own (its edits move the caret along): a click, Page Down, a command...
        if (_internal || _inUpdate || _applying || _set is null || _altClick is not null || _hold > 0) return;
        Collapse();
    }

    private void OnUpdateStarted(object? sender, EventArgs e) => _inUpdate = true;

    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_applying || _set is null) return;
        // An edit from elsewhere (another view, a language server, undo): the carets follow it when the update ends, through all of its changes
        // at once. An undo replays a multi-caret step as one change per caret, so mapping every caret at each change would be O(carets²); and
        // when the undo restores its carets (_pendingRestore, set by the step's first marker after these changes) nothing is mapped at all.
        _pendingChanges.Add(e);
        if (!_inUpdate) FollowPendingChanges();
    }

    /// <summary>Moves the carets along the changes made elsewhere since the last time: O(carets × log changes) for an undo or a batch of edits.</summary>
    private void FollowPendingChanges()
    {
        if (_pendingChanges.Count == 0) return;
        var mapper = new ChangeSequenceMapper(_pendingChanges);
        _pendingChanges.Clear();
        if (_set is not { } set) return;
        FollowCount++;
        var carets = new CaretRange[set.Count];
        for (var i = 0; i < carets.Length; i++)
        {
            var c = set[i];
            carets[i] = new CaretRange(mapper.Map(c.Anchor), mapper.Map(c.Active), c.DesiredColumn);
        }
        _set = CaretSet.Create(carets, set.PrimaryIndex, Limit);
    }

    private void OnUpdateFinished(object? sender, EventArgs e)
    {
        _inUpdate = false;
        if (_applying || _disposed) return;
        if (_pendingRestore is { } restore)
        {
            _pendingChanges.Clear(); // the restored carets replace the moved ones
            _pendingRestore = null;
            _typingRun = null;
            SetCarets(restore, reveal: true);
            return;
        }
        FollowPendingChanges();
        if (_set is not { } set) return;
        // the primary is AvaloniaEdit's: take where its caret went, merge carets the edit brought together
        _set = set.WithPrimaryRange(PrimaryFromEditor());
        if (!_set.IsMulti) Collapse();
        Invalidate();
    }

    private void OnFocusChanged(object? sender, RoutedEventArgs e) => UpdateBlink();

    // ------------------------------------------------------------------ editing

    /// <summary>
    /// Applies a batch as ONE undo step and makes its carets current. Typed characters (<paramref name="typing"/>) continue the previous typed
    /// batch's undo step while nothing else happened in between, as single-caret typing does. Undo restores the carets from before, redo those after.
    /// </summary>
    internal void ApplyBatch(CaretSet before, CaretEditBatch batch, bool typing = false) =>
        ApplyCore(before, batch.Edits, () => CaretSet.Create(batch.Carets, batch.Primary, Limit), typing);

    /// <summary>
    /// Line commands at every caret (move / copy / delete / comment / indent lines): <paramref name="edits"/> in offsets of the current text, as
    /// ONE batch and undo step; the carets after it are known as line/column positions of the new text (a moved line keeps its column), so
    /// they are made offsets once the edits are applied. <paramref name="after"/>: anchor and active of each caret, the primary at <paramref name="primary"/>.
    /// </summary>
    internal void ApplyLineEdits(CaretSet before, IReadOnlyList<OffsetEdit> edits, IReadOnlyList<(TextPosition Anchor, TextPosition Active)> after, int primary)
    {
        var sorted = edits.Where(e => !e.IsNoOp).OrderBy(e => e.Start).ThenBy(e => e.End).ToArray();
        ApplyCore(before, sorted, () =>
        {
            var doc = _v.Document;
            var carets = after.Select(p => new CaretRange(doc.OffsetAt(doc.Validate(p.Anchor)), doc.OffsetAt(doc.Validate(p.Active)))).ToArray();
            return CaretSet.Create(carets, primary, Limit);
        }, typing: false);
    }

    private void ApplyCore(CaretSet before, OffsetEdit[] edits, Func<CaretSet> carets, bool typing)
    {
        var doc = _v.Document;
        if (doc.IsReadOnly || _disposed) return;
        var continues = typing && _typingRun is { } run && run.Version == doc.Version && ReferenceEquals(run.Set, _set);
        if (edits.Length == 0)
        {
            // nothing to edit (closers over-typed at every caret): the carets move, and no empty undo step is made; a typed run goes on
            SetCarets(carets());
            _typingRun = continues && IsActive ? (doc.Version, _set!) : null;
            return;
        }
        var record = before.Count <= UndoSnapshotLimit;
        CaretSet after;
        _applying = true;
        try
        {
            continues = doc.BeginUndoGroup(continueLast: continues);
            try
            {
                if (record && !continues) doc.AddUndoMarker(StartMarker(before));
                MultiCaretOperations.Apply(doc.Buffer, edits);
                after = carets();
                record &= after.Count <= UndoSnapshotLimit;
                if (record)
                {
                    if (continues && _lastEndMarker is { } previous) previous.Carets = null; // the run's last marker restores its final carets
                    var end = new EndMarker(_self, after);
                    _lastEndMarker = end;
                    doc.AddUndoMarker(end.Run);
                }
                else _lastEndMarker = null;
            }
            finally
            {
                doc.EndUndoGroup();
            }
        }
        finally
        {
            _applying = false;
        }
        SetCarets(after);
        _typingRun = typing && IsActive ? (doc.Version, _set!) : null;
        _batchApplied.Fire(typing); // a snippet session brings its mirrors up to date (joining the typed run's step)
    }

    private readonly Emitter<bool> _batchApplied = new();

    /// <summary>
    /// Raised after a batch was applied and its carets made current (argument: it was typing). Features that must not edit the buffer while a
    /// batch is applied (a snippet's mirrors) do their follow-up edits here, through <see cref="ApplyMapped(IReadOnlyList{OffsetEdit}, bool, bool)"/>.
    /// </summary>
    internal IEventSource<bool> BatchApplied => _batchApplied;

    /// <summary>True while a batch of this controller is being applied to the buffer (inside its one change event).</summary>
    internal bool IsApplyingBatch => _applying;

    private Action<bool> StartMarker(CaretSet before)
    {
        var self = _self;
        return redo =>
        {
            if (!redo && self.TryGetTarget(out var me)) me._pendingRestore = before;
        };
    }

    /// <summary>The marker at the end of an undo step: redo restores the carets the step ended with. A typing run keeps only its last one's.</summary>
    private sealed class EndMarker(WeakReference<MultiCursorController> owner, CaretSet carets)
    {
        public CaretSet? Carets { get; set; } = carets;

        public void Run(bool redo)
        {
            if (redo && Carets is { } c && owner.TryGetTarget(out var me)) me._pendingRestore = c;
        }
    }

    private void Type(string text)
    {
        var before = Current;
        _v.PreTyping.Fire(text); // a commit character accepts the completion first (at every caret)
        if (!IsActive) return;   // ... which may have collapsed the carets
        before = Current;
        var newline = text is "\n" or "\r" or "\r\n";
        if (newline)
        {
            ApplyBatch(before, MultiCaretOperations.Enter(_v.Document.Buffer, before, _v.GetEditingContext()));
            return;
        }
        // each caret auto-closes, over-types or surrounds as a single caret there would (TypingBehaviors decides per caret)
        var typing = _v.Feature<TypingBehaviors>();
        AutoCloseDecision[]? decisions = null;
        var batch = typing?.TypedBatch(before, text, out decisions) ?? MultiCaretOperations.Type(before, text);
        ApplyBatch(before, batch, typing: true);
        typing?.AfterTypedBatch(this, text, decisions, batch.Carets); // closers to over-type later; a lone closer re-indented (same undo step)
        typing?.NotifyTyped(text); // completion and signature help follow the primary
    }

    /// <summary>Replaces [caret − before, caret + after] around every caret with <paramref name="text"/> (a completion accepted with several carets).</summary>
    internal void ReplaceAroundCarets(int before, int after, string text)
    {
        var buffer = _v.Document.Buffer;
        var set = Current;
        ApplyBatch(set, MultiCaretOperations.PerCaret(set, c =>
        {
            var line = buffer.GetLineByOffset(c.Start);
            var start = Math.Max(line.Offset, c.Start - before);
            var end = Math.Min(buffer.GetLineByOffset(c.End).EndOffset, c.End + after);
            return (start, end, text, text.Length);
        }));
    }

    /// <summary>Edits that are not one per caret (Cut's whole lines, whole lines pasted above each caret): the carets follow them.</summary>
    internal void ApplyMapped(IReadOnlyList<TextEdit> edits, bool keepSelections)
    {
        var doc = _v.Document;
        var offsets = edits.Select(e => new OffsetEdit(doc.OffsetAt(e.Range.Start), doc.OffsetAt(e.Range.End), e.NewText)).ToArray();
        var set = Current;
        ApplyBatch(set, MultiCaretOperations.Mapped(set, offsets, keepSelections));
    }

    /// <summary>
    /// Edits in offsets of the current text that the carets follow (<see cref="MultiCaretOperations.Mapped"/>); with <paramref name="typing"/>
    /// they join the typed run's undo step (a lone closer re-indented, snippet mirrors updated after typing at several carets).
    /// </summary>
    internal void ApplyMapped(IReadOnlyList<OffsetEdit> edits, bool keepSelections, bool typing)
    {
        var set = Current;
        ApplyBatch(set, MultiCaretOperations.Mapped(set, edits, keepSelections), typing);
    }

    /// <summary>A paste with several carets: one line (or copied piece) per caret when the counts match (<c>editor.multiCursorPaste</c> "spread"), else all of it at each.</summary>
    internal void Paste(ClipboardText clip)
    {
        var doc = _v.Document;
        if (doc.IsReadOnly) return;
        var set = Current;
        var eol = doc.EndOfLine;
        var text = ClipboardTextExtensions.NormalizeLineEndings(clip.Text, eol);
        if (clip.Shape == ClipboardTextShape.FullLines && set.Carets.All(c => c.IsEmpty))
        {
            // whole lines go above each caret's line (one line per caret when they match), the carets staying on their lines
            var selection = new SelectionSet(set.Carets.Select(c => new TextRange(_v.PositionOf(c.Start), _v.PositionOf(c.End))).ToArray());
            if (ClipboardOperations.Paste(doc, selection, clip, _v.GetEditingContext(), autoIndent: false) is { } result) ApplyMapped(result.Plan.Edits, keepSelections: false);
            return;
        }
        string[]? rows = null;
        if (_v.Settings.MultiCursorPaste != "full")
        {
            var pieces = clip.Pieces?.Select(p => ClipboardTextExtensions.NormalizeLineEndings(p, eol)).ToArray();
            if (pieces is { } p && p.Length == set.Count) rows = p;
            else
            {
                var lines = ClipboardTextExtensions.SplitLines(text);
                if (lines.Length == set.Count) rows = lines;
                else if (lines.Length == set.Count + 1 && lines[^1].Length == 0) rows = lines[..^1];
            }
        }
        ApplyBatch(set, MultiCaretOperations.Insert(set, rows ?? Enumerable.Repeat(text, set.Count).ToArray()));
    }

    // ------------------------------------------------------------------ input

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || !IsActive || !Enabled || string.IsNullOrEmpty(e.Text)) return;
        var text = e.Text;
        var newline = text is "\n" or "\r" or "\r\n";
        if (newline && _swallowNewlineText)
        {
            _swallowNewlineText = false;
            e.Handled = true;
            return;
        }
        if (text.Any(ch => ch < ' ' && ch != '\t') && !newline) return; // other control characters are not typing
        e.Handled = true; // AvaloniaEdit would insert at its caret only
        if (_v.Document.IsReadOnly) return;
        Type(text);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        _swallowNewlineText = false;
        if (e.Handled || !IsActive || !ReferenceEquals(e.Source, _v.TextArea)) return;
        if (!Enabled)
        {
            Collapse();
            return;
        }
        if (PopupWantsKey(e.Key)) return;
        var buffer = _v.Document.Buffer;
        var mac = OperatingSystem.IsMacOS();
        var m = e.KeyModifiers;
        var shift = (m & KeyModifiers.Shift) != 0;
        var core = m & ~KeyModifiers.Shift;
        var wordModifier = mac ? KeyModifiers.Alt : KeyModifiers.Control;
        var word = core == wordModifier;
        var plain = core == KeyModifiers.None;
        var lineJump = mac && core == KeyModifiers.Meta; // ⌘← / ⌘→ go to the line's start / end on a Mac
        var set = Current;
        var readOnly = _v.Document.IsReadOnly;
        if (readOnly && plain && e.Key is Key.Back or Key.Delete or Key.Tab or Key.Enter)
        {
            e.Handled = true; // nothing to edit, and AvaloniaEdit must not edit at the primary alone
            return;
        }
        switch (e.Key)
        {
            case Key.Left when lineJump:
                Move(MultiCaretOperations.MoveHome(buffer, set, shift));
                break;
            case Key.Right when lineJump:
                Move(MultiCaretOperations.MoveEnd(buffer, set, shift));
                break;
            case Key.Left or Key.Right when plain || word:
                Move(MultiCaretOperations.MoveHorizontal(buffer, set, forward: e.Key == Key.Right, word, shift));
                break;
            case Key.Up or Key.Down when plain:
                Move(_v.Editor.WordWrap
                    ? MultiCaretOperations.MoveVertical(buffer, set, down: e.Key == Key.Down, shift, _v.Settings.TabSize, RowStarts) // by wrapped rows, as one caret moves
                    : MultiCaretOperations.MoveVertical(buffer, set, down: e.Key == Key.Down, shift, _v.Settings.TabSize));
                break;
            case Key.Home when plain:
                Move(MultiCaretOperations.MoveHome(buffer, set, shift));
                break;
            case Key.End when plain:
                Move(MultiCaretOperations.MoveEnd(buffer, set, shift));
                break;
            case Key.Back when (plain || (word && !shift)) && !readOnly: // Shift+Backspace is Backspace (AvaloniaEdit would delete at the primary alone)
                // as one caret: an auto-inserted pair goes with its opener, leading whitespace back to the previous indentation stop
                ApplyBatch(set, MultiCaretOperations.Backspace(buffer, set, word, _v.Settings.TabSize, _v.Settings.UseTabStops, _v.Feature<TypingBehaviors>()?.AutoClosedPairPredicate()));
                break;
            case Key.Delete when (plain || word) && !shift && !readOnly:
                ApplyBatch(set, MultiCaretOperations.Delete(buffer, set, word));
                break;
            case Key.Tab when plain && !readOnly:
                ApplyBatch(set, shift ? MultiCaretOperations.IndentLines(buffer, set, _v.GetEditingContext(), outdent: true) : MultiCaretOperations.Tab(buffer, set, _v.GetEditingContext()));
                break;
            case Key.Enter when plain && !readOnly:
                _swallowNewlineText = true;
                Type("\n");
                break;
            case Key.Escape when plain && !shift:
                Collapse();
                break;
            default:
                return; // everything else (Page Down, Ctrl+Home, Ctrl+A...) is AvaloniaEdit's: it moves the primary, which collapses the others
        }
        e.Handled = true;
    }

    /// <summary>The completion list, parameter hints or a snippet session take their keys first (VS Code: Escape closes them before the carets go).</summary>
    private bool PopupWantsKey(Key key)
    {
        var completionOpen = _v.Feature<CompletionFeature>()?.IsOpen == true;
        if (completionOpen && key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Enter or Key.Tab or Key.Escape) return true;
        if (_v.Feature<SignatureFeature>()?.IsOpen == true && key is Key.Up or Key.Down or Key.Escape) return true;
        return _v.Feature<SnippetFeature>()?.IsActive == true && key is Key.Tab or Key.Escape;
    }

    /// <summary>
    /// Where each wrapped row of <paramref name="line"/> starts (document offsets), from AvaloniaEdit's own layout of the line, so the carets
    /// move by the rows the single caret moves by. A line too short to wrap is one row without laying it out; a line off screen is laid out
    /// (AvaloniaEdit's caret does the same for its one line).
    /// </summary>
    private IReadOnlyList<int> RowStarts(DocumentLine line)
    {
        var tv = _v.TextView;
        var cell = tv.WideSpaceWidth;
        var width = tv.Bounds.Width;
        if (width <= 0 || cell <= 0) return [line.Offset];
        // twice the tab-expanded length: wide (CJK) characters take two cells
        var expanded = MultiCaretOperations.VisualColumn(_v.Document.Buffer, line, line.EndOffset, Math.Max(1, _v.Settings.TabSize));
        if (expanded * 2 < width / cell) return [line.Offset];
        var visual = tv.GetVisualLine(line.LineNumber) ?? tv.GetOrConstructVisualLine(line);
        if (visual.TextLines.Count <= 1) return [line.Offset];
        var starts = new List<int>(visual.TextLines.Count);
        var first = visual.FirstDocumentLine.Offset;
        foreach (var row in visual.TextLines)
        {
            var offset = first + visual.GetRelativeOffset(visual.GetTextLineVisualStartColumn(row));
            if (offset < line.Offset || offset > line.EndOffset) continue; // a fold joined other lines to this visual line
            if (starts.Count == 0 && offset > line.Offset) starts.Add(line.Offset);
            if (starts.Count == 0 || offset > starts[^1]) starts.Add(offset);
        }
        return starts.Count == 0 ? [line.Offset] : starts;
    }

    /// <summary>The carets move to <paramref name="moved"/> (a key, a snippet's next tab stop): a typed run ends.</summary>
    internal void Move(CaretSet moved)
    {
        _typingRun = null;
        SetCarets(moved);
    }

    // ------------------------------------------------------------------ Alt+Click (Option+Click on macOS)

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _altClick = null;
        // editor.multiCursorModifier: Alt+Click (Option on macOS) by default, Ctrl+Click (⌘ on macOS) with "ctrlCmd"
        if (!Enabled || e.KeyModifiers != EditorKeys.AddCaretModifier(_v.Settings.MultiCursorModifier) || e.ClickCount != 1) return;
        var point = e.GetCurrentPoint(_v.TextView);
        if (!point.Properties.IsLeftButtonPressed || !new Rect(_v.TextView.Bounds.Size).Contains(point.Position)) return;
        // AvaloniaEdit starts a box selection on Alt+press (Alt+drag keeps working): remember the carets, and decide at the release whether this
        // was a click (add a caret) or a drag (the box selection stands).
        _altClick = (Current, point.Position);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_altClick is not { } click) return;
        _altClick = null;
        var at = e.GetPosition(_v.TextView);
        if (Math.Abs(at.X - click.At.X) > 3 || Math.Abs(at.Y - click.At.Y) > 3)
        {
            if (_set is not null) Collapse(); // a box selection: its rows are the carets now
            return;
        }
        var view = _v.TextView;
        var pos = view.GetPosition(at + view.ScrollOffset) ?? view.GetPositionFloor(at + view.ScrollOffset);
        if (pos is not { } p) return;
        AddCaretAt(click.Before, _v.Document.Buffer.GetOffset(p.Location));
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // the release may never come (the window lost the pointer); a later move of the primary must collapse as usual
        if (_altClick is not null) Dispatcher.UIThread.Post(() => _altClick = null);
    }

    /// <summary>Alt+Click: a caret at <paramref name="offset"/> added to <paramref name="before"/>, or the caret there removed when there is one.</summary>
    internal void AddCaretAt(CaretSet before, int offset)
    {
        if (!Enabled) return;
        PushCursorUndo(before);
        var existing = before.IndexAt(offset);
        if (existing >= 0 && before[existing].IsEmpty && before.Count > 1)
        {
            SetCarets(before.Remove(existing), reveal: false);
            return;
        }
        var next = before.Add(CaretRange.At(offset), Limit);
        ReportLimit(next);
        SetCarets(next, reveal: false);
    }

    // ------------------------------------------------------------------ commands

    private bool CheckEnabled()
    {
        if (Enabled) return true;
        if (_v.ModalAttached && !_v.ModalAllowsMultipleCarets) FeatureSupport.Status(_v, "Multiple cursors are not available while Vim mode is on");
        return false;
    }

    /// <summary>
    /// editor.action.addSelectionToNextFindMatch (Ctrl+D in VS Code): a caret selects its word first; then each press adds the next occurrence of
    /// the selected text (after the primary, wrapping) as a new primary selection. Started from a word it matches whole words and case, from a
    /// selection the text as it is, ignoring case (VS Code's find defaults).
    /// </summary>
    public void AddSelectionToNextFindMatch()
    {
        if (!CheckEnabled()) return;
        var set = Current;
        if (ExpandEmptyToWords(set)) return;
        var query = QueryFor(set);
        var snapshot = _v.Document.Snapshot();
        var found = MatchFinder.FindNext(snapshot.Length, snapshot.GetMemory, query, set.Primary.End, start => set.IndexAt(start) is var i && (i < 0 || set[i].Start != start));
        if (found < 0) return;
        PushCursorUndo(set);
        var next = set.Add(CaretRange.Select(found, found + query.Text.Length), Limit);
        ReportLimit(next);
        SetCarets(next);
    }

    /// <summary>editor.action.moveSelectionToNextFindMatch (Ctrl+K Ctrl+D in VS Code): the primary selection jumps to the next occurrence.</summary>
    public void MoveSelectionToNextFindMatch()
    {
        if (!CheckEnabled()) return;
        var set = Current;
        if (ExpandEmptyToWords(set)) return;
        var query = QueryFor(set);
        var snapshot = _v.Document.Snapshot();
        var primary = set.Primary;
        var found = MatchFinder.FindNext(snapshot.Length, snapshot.GetMemory, query, primary.End, start => set.IndexAt(start) < 0);
        if (found < 0) return;
        PushCursorUndo(set);
        var without = set.Remove(set.PrimaryIndex);
        SetCarets(set.Count == 1 ? CaretSet.Single(CaretRange.Select(found, found + query.Text.Length)) : without.Add(CaretRange.Select(found, found + query.Text.Length), Limit));
    }

    /// <summary>editor.action.selectHighlights (Ctrl+Shift+L in VS Code): every occurrence of the selection (or of the word at the caret) selected.</summary>
    public async Task SelectAllOccurrencesAsync()
    {
        if (!CheckEnabled()) return;
        var set = Current;
        var primary = set.Primary;
        OccurrenceQuery query;
        if (primary.IsEmpty)
        {
            var (a, b) = FeatureSupport.WordAt(_v.Document.Buffer, primary.Active);
            if (a == b) return;
            query = new OccurrenceQuery(_v.Document.Buffer.GetText(a, b - a), WholeWord: true, MatchCase: true);
        }
        else query = _query is { } q && Matches(q, _v.Document.Buffer.GetText(primary.Start, primary.Length)) ? q : new OccurrenceQuery(_v.Document.Buffer.GetText(primary.Start, primary.Length), false, false);
        var snapshot = _v.Document.Snapshot();
        var limit = Limit;
        (List<int> Starts, bool Limited) result;
        if (snapshot.Length > WorkerSearchThreshold)
        {
            result = await Task.Run(() => MatchFinder.FindAll(snapshot.Length, snapshot.GetMemory, query, limit)).ConfigureAwait(true);
            if (_disposed || _v.IsDisposed || _v.Document.Version != snapshot.Version) return; // edited meanwhile: the offsets are stale
        }
        else result = MatchFinder.FindAll(snapshot.Length, snapshot.GetMemory, query, limit);
        if (result.Starts.Count == 0) return;
        PushCursorUndo(set);
        var carets = result.Starts.Select(s => CaretRange.Select(s, s + query.Text.Length)).ToArray();
        var primaryIndex = Math.Max(0, result.Starts.FindLastIndex(s => s <= primary.Start));
        var next = CaretSet.Create(carets, primaryIndex, limit);
        _query = query;
        if (result.Limited || next.WasLimited) FeatureSupport.Status(_v, $"The number of cursors has been limited to {limit}.");
        SetCarets(next, reveal: false);
    }

    /// <summary>editor.action.insertCursorAbove / insertCursorBelow (Ctrl+Alt+Up/Down; ⌘⌥↑/↓ on macOS).</summary>
    public void InsertCursor(bool below)
    {
        if (!CheckEnabled()) return;
        var set = Current;
        var next = MultiCaretOperations.InsertCaretsVertically(_v.Document.Buffer, set, below, _v.Settings.TabSize, Limit);
        if (ReferenceEquals(next, set)) return;
        PushCursorUndo(set);
        ReportLimit(next);
        SetCarets(next);
    }

    /// <summary>cursorUndo (Ctrl+U in VS Code): back to the carets before the last caret command (Add Next Occurrence, Insert Cursor, Alt+Click...).</summary>
    public void CursorUndo()
    {
        if (!CheckEnabled()) return;
        var version = _v.Document.Version;
        while (_cursorUndo.Last is { } last)
        {
            _cursorUndo.RemoveLast();
            if (last.Value.Version != version) continue; // edited since: those offsets mean nothing now
            SetCarets(last.Value.Set);
            return;
        }
    }

    /// <summary>editor.action.removeSecondaryCursors (Escape).</summary>
    public void RemoveSecondaryCursors() => Collapse();

    private void PushCursorUndo(CaretSet set)
    {
        _cursorUndo.AddLast((set, _v.Document.Version));
        if (_cursorUndo.Count > CursorUndoDepth) _cursorUndo.RemoveFirst();
    }

    /// <summary>With empty carets the first press selects each caret's word (and looks for whole words with matching case). True when it did.</summary>
    private bool ExpandEmptyToWords(CaretSet set)
    {
        if (set.Carets.All(c => !c.IsEmpty)) return false;
        var buffer = _v.Document.Buffer;
        var carets = set.Carets.Select(c =>
        {
            if (!c.IsEmpty) return c;
            var (a, b) = FeatureSupport.WordAt(buffer, c.Active);
            return a == b ? c : CaretRange.Select(a, b);
        }).ToArray();
        var next = CaretSet.Create(carets, set.PrimaryIndex, Limit);
        if (next.Primary.IsEmpty) return true; // no word at the primary: nothing to look for
        PushCursorUndo(set);
        _query = new OccurrenceQuery(buffer.GetText(next.Primary.Start, next.Primary.Length), WholeWord: true, MatchCase: true);
        SetCarets(next);
        return true;
    }

    /// <summary>The search of an Add Next Occurrence run: kept while every selection still holds its text, else the primary's text as it is.</summary>
    private OccurrenceQuery QueryFor(CaretSet set)
    {
        var buffer = _v.Document.Buffer;
        if (_query is { } q && set.Carets.All(c => Matches(q, buffer.GetText(c.Start, c.Length)))) return q;
        return _query = new OccurrenceQuery(buffer.GetText(set.Primary.Start, set.Primary.Length), WholeWord: false, MatchCase: false);
    }

    private static bool Matches(OccurrenceQuery q, string text) => string.Equals(q.Text, text, q.Comparison);

    private void ReportLimit(CaretSet set)
    {
        if (set.WasLimited) FeatureSupport.Status(_v, $"The number of cursors has been limited to {Limit}.");
    }

    // ------------------------------------------------------------------ the controller's view of the selections

    /// <summary>The selections as the controller reports them: the primary first, then the others in document order.</summary>
    internal IReadOnlyList<TextRange> Selections()
    {
        var set = Current;
        var list = new List<TextRange>(set.Count) { RangeOf(set.Primary) };
        for (var i = 0; i < set.Count; i++)
            if (i != set.PrimaryIndex) list.Add(RangeOf(set[i]));
        return list;
    }

    /// <summary>The selections in document order (what Copy, Cut and PRIMARY act on).</summary>
    internal IReadOnlyList<TextRange> RangesInOrder() => Current.Carets.Select(RangeOf).ToArray();

    internal bool AnySelection => Current.Carets.Any(c => !c.IsEmpty);

    private TextRange RangeOf(CaretRange c) => new(_v.PositionOf(c.Start), _v.PositionOf(c.End));

    /// <summary>The carets for a view state (<see cref="EditorViewState.Carets"/>): the primary first, then the others in document order, with their direction.</summary>
    internal IReadOnlyList<EditorCaretState> CaretStates()
    {
        var set = Current;
        var list = new List<EditorCaretState>(set.Count) { StateOf(set.Primary) };
        for (var i = 0; i < set.Count; i++)
            if (i != set.PrimaryIndex) list.Add(StateOf(set[i]));
        return list;
    }

    private EditorCaretState StateOf(CaretRange c) => new(_v.PositionOf(c.Anchor), _v.PositionOf(c.Active));

    /// <summary>A recreated view gets the carets of its view state back (positions clamped to the text, which may have changed meanwhile).</summary>
    internal void RestoreCarets(IReadOnlyList<EditorCaretState> carets)
    {
        if (_disposed || carets.Count == 0) return;
        var doc = _v.Document;
        var ranges = carets.Select(c => new CaretRange(doc.OffsetAt(doc.Validate(c.Anchor)), doc.OffsetAt(doc.Validate(c.Active)))).ToArray();
        if (!Enabled) ranges = [ranges[0]];
        SetCarets(CaretSet.Create(ranges, 0, Limit), reveal: false);
    }

    /// <summary>Sets several selections at once (the first is the primary); the controller's <c>SetSelections</c>.</summary>
    internal void SetSelections(IReadOnlyList<TextRange> selections, bool reveal)
    {
        if (selections.Count == 0) return;
        var doc = _v.Document;
        var carets = selections.Select(r => CaretRange.Select(doc.OffsetAt(r.Start), doc.OffsetAt(r.End))).ToArray();
        if (!Enabled) carets = [carets[0]];
        var set = CaretSet.Create(carets, 0, Limit);
        ReportLimit(set);
        SetCarets(set, reveal);
    }

    // ------------------------------------------------------------------ drawing

    public void OnThemeChanged() => Invalidate();

    private void UpdateBlink()
    {
        var run = IsActive && !_disposed && _v.TextArea.IsKeyboardFocusWithin;
        _blinkOn = true;
        if (run && !_blink.IsEnabled) _blink.Start();
        else if (!run && _blink.IsEnabled) _blink.Stop();
        _v.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    private void OnBlink(object? sender, EventArgs e)
    {
        if (!IsActive || _disposed)
        {
            _blink.Stop();
            return;
        }
        _blinkOn = !_blinkOn;
        _v.TextView.InvalidateLayer(KnownLayer.Caret);
    }

    /// <summary>The secondary carets in the visible lines (index range into the set), or an empty range.</summary>
    private (CaretSet Set, int From, int To) Visible(TextView textView)
    {
        FollowPendingChanges();
        if (_set is not { } set || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return (CaretSet.Single(default), 0, 0);
        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var from = set.FirstEndingAtOrAfter(first);
        var to = from;
        while (to < set.Count && set[to].Start <= last) to++;
        return (set, from, to);
    }

    /// <summary>Drawing state for tests: the secondary carets the caret layer would draw now.</summary>
    internal int VisibleSecondaryCount
    {
        get
        {
            var (set, from, to) = Visible(_v.TextView);
            var n = 0;
            for (var i = from; i < to; i++)
                if (i != set.PrimaryIndex) n++;
            return n;
        }
    }

    internal bool BlinkRunning => _blink.IsEnabled;

    private sealed class SelectionRenderer(MultiCursorController owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            var (set, from, to) = owner.Visible(textView);
            if (to <= from) return;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 3 };
            var any = false;
            for (var i = from; i < to; i++)
            {
                var c = set[i];
                if (i == set.PrimaryIndex || c.IsEmpty) continue; // AvaloniaEdit draws the primary's
                builder.AddSegment(textView, new TextSegment { StartOffset = c.Start, EndOffset = c.End });
                any = true;
            }
            if (any && builder.CreateGeometry() is { } geometry) context.DrawGeometry(owner._v.Palette.Selection, null, geometry);
        }
    }

    private sealed class CaretRenderer(MultiCursorController owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Caret;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!owner._blinkOn || !owner._v.TextArea.IsKeyboardFocusWithin) return;
            var (set, from, to) = owner.Visible(textView);
            if (to <= from) return;
            var brush = owner._v.Palette.Caret;
            var doc = textView.Document;
            var modal = owner._v.ModalAttached ? owner._v.Modal : null; // a modal layer's caret shape (Vim's block in Normal mode) at every caret
            for (var i = from; i < to; i++)
            {
                if (i == set.PrimaryIndex) continue;
                var location = doc.GetLocation(set[i].Active);
                if (textView.GetVisualLine(location.Line) is null) continue; // folded away
                var pos = new AvaloniaEdit.TextViewPosition(location);
                if (modal is not null && modal.DrawsShapedCaret)
                {
                    modal.DrawCaretShape(textView, context, pos);
                    continue;
                }
                var top = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
                var bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
                context.FillRectangle(brush, new Rect(top.X, top.Y, 2, Math.Max(1, bottom.Y - top.Y)));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _blink.Stop(); // a running DispatcherTimer would keep the view alive
        _blink.Tick -= OnBlink;
        _d.Dispose();
        _set = null;
        _cursorUndo.Clear();
        _pendingChanges.Clear();
        _pendingRestore = null;
        _lastEndMarker = null;
        _typingRun = null;
        _batchApplied.Clear();
    }
}
