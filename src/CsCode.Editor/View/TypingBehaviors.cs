using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CsCode.Core.Lifecycle;
using CsCode.Editor.Editing;
using CsCode.Editor.Theming;

namespace CsCode.Editor.View;

/// <summary>
/// The typing path of one view, hooked into AvaloniaEdit's <c>TextEntering</c>/<c>TextEntered</c>: auto-closing brackets and quotes
/// (respecting the language's <c>notIn</c> and the token under the caret), over-typing the closer we inserted, surrounding a selection, deleting
/// a pair with Backspace, Backspace in leading whitespace back to the previous indentation stop (<c>editor.useTabStops</c>), and re-indenting
/// a lone closing bracket. With several carets (<see cref="MultiCursorController"/>) the same decisions are made at every caret
/// (<see cref="TypedBatch"/>). Every decision is O(1) on the current line; nothing here waits for anything.
/// </summary>
internal sealed class TypingBehaviors : IDisposable, ISettingsAware
{
    private const int MaxTrackedClosers = 32;

    /// <summary>With several carets each one's closers are tracked (a pair typed at 10,000 carets is 10,000 closers); still bounded.</summary>
    private const int MaxTrackedClosersMulti = 20_000;

    internal const string UseTabStopsKey = "editor.useTabStops";

    /// <summary>The typing settings this component reads beyond the ones the Platform declares (VS Code's ids), registered by the factory.</summary>
    internal static IReadOnlyList<CsCode.Core.Platform.ConfigurationProperty> Properties { get; } =
    [
        new(UseTabStopsKey, System.Text.Json.JsonSerializer.SerializeToElement(true),
            "Backspace in a line's leading whitespace deletes back to the previous indentation stop (one indentation unit) instead of one space.",
            System.Text.Json.JsonDocument.Parse("""{"type":"boolean"}""").RootElement.Clone()),
    ];

    private readonly CodeEditorControl _v;
    private readonly List<TextAnchor> _closers = new(); // anchors on closing characters WE inserted (so only those are over-typed)
    private readonly Emitter<string> _typed = new();
    private readonly DisposableStore _d = new();

    public TypingBehaviors(CodeEditorControl view)
    {
        _v = view;
        var area = view.TextArea;
        area.TextEntering += OnEntering;
        area.TextEntered += OnEntered;
        area.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _d.Add(Disposable.Create(() =>
        {
            area.TextEntering -= OnEntering;
            area.TextEntered -= OnEntered;
            area.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        }));
        view.Typed = _typed;
    }

    /// <summary>Text typed at several carets by <see cref="MultiCursorController"/>: the features that follow typing (completion, signature help) see it too.</summary>
    internal void NotifyTyped(string text) => _typed.Fire(text);

    public void OnSettingsChanged()
    {
        // nothing cached: every keystroke reads view.Settings
    }

    // ---- undo coalescing: a burst of typing is ONE undo step (a caret move, another edit or Enter starts a new one) ----

    private bool _groupOpen;
    private int _expectedCaret = -1;
    private int _expectedVersion = -1;

    // The groups go through the document (not its undo stack directly) so that a held undo group (macro playback) sees where a typed run starts.
    private void BeginTypingGroup(string text)
    {
        var doc = _v.Document;
        if (_groupOpen)
        {
            doc.EndUndoGroup(); // a previous group never got its TextEntered (some other handler swallowed the input)
            _groupOpen = false;
        }
        var isNewline = text is "\n" or "\r" or "\r\n";
        // A modal layer (Vim) groups its own changes into undo steps: typing must never extend a group the layer closed.
        var continues = !isNewline && !_v.ModalAttached && _expectedCaret == _v.Editor.CaretOffset && _expectedVersion == doc.Version && _v.Editor.SelectionLength == 0;
        doc.BeginUndoGroup(continueLast: continues);
        _groupOpen = true;
    }

    /// <summary>
    /// Another feature edited the document right after a typed character, as part of its undo step (linked editing's mirror): a typed run
    /// that would have continued still continues, so a typed word stays one undo step.
    /// </summary>
    internal void FollowEdit(int versionBefore, int caretBefore)
    {
        if (_expectedVersion != versionBefore || _expectedCaret != caretBefore) return;
        _expectedVersion = _v.Document.Version;
        _expectedCaret = _v.Editor.CaretOffset;
    }

    private void EndTypingGroup(string text)
    {
        if (!_groupOpen) return;
        _v.Document.EndUndoGroup();
        _groupOpen = false;
        var isNewline = text is "\n" or "\r" or "\r\n";
        _expectedCaret = isNewline ? -1 : _v.Editor.CaretOffset;
        _expectedVersion = isNewline ? -1 : _v.Document.Version;
    }

    private void OnEntering(object? sender, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text) || e.Handled || _v.Document.IsReadOnly) return;
        BeginTypingGroup(text);
        _v.PreTyping.Fire(text);
        if (HandleAutoClosing(e, text)) EndTypingGroup(text); // handled here: TextEntered will not come
    }

    private bool HandleAutoClosing(TextInputEventArgs e, string text)
    {
        if (text.Length != 1) return false;
        var s = _v.Settings;
        if (!s.AutoClosingBrackets && !s.AutoClosingQuotes && !s.AutoSurround) return false;

        var editor = _v.Editor;
        var doc = editor.Document;
        var selStart = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        var line = doc.GetLineByOffset(selStart);
        var before = doc.GetText(line.Offset, selStart - line.Offset);
        var afterStart = editor.SelectionLength > 0 ? editor.SelectionStart + editor.SelectionLength : editor.CaretOffset;
        var afterLine = doc.GetLineByOffset(afterStart);
        var after = doc.GetText(afterStart, afterLine.EndOffset - afterStart);
        var selected = editor.SelectionLength > 0 ? editor.SelectedText : string.Empty;
        var ext = _v.LanguageConfiguration;
        var kind = _v.Tokens?.KindAt(line.LineNumber - 1, selStart - line.Offset) ?? TokenKind.Other;

        var decision = AutoClosing.Decide(text, before, after, selected, ext.Base, ext.EffectiveAutoCloseBefore, kind,
            IsAutoCloserAt(editor.CaretOffset, text[0]), s.AutoClosingBrackets, s.AutoClosingQuotes, s.AutoSurround);

        switch (decision.Kind)
        {
            case AutoCloseKind.InsertPair:
            {
                e.Handled = true;
                var at = editor.CaretOffset;
                using (doc.RunUpdate()) doc.Insert(at, text + decision.Close);
                editor.CaretOffset = at + 1;
                Track(doc.CreateAnchor(at + 1));
                _typed.Fire(text);
                break;
            }
            case AutoCloseKind.OverType:
            {
                e.Handled = true;
                ForgetCloserAt(editor.CaretOffset);
                editor.CaretOffset += 1;
                _typed.Fire(text);
                break;
            }
            case AutoCloseKind.Surround:
            {
                e.Handled = true;
                var start = editor.SelectionStart;
                var end = start + editor.SelectionLength;
                using (doc.RunUpdate())
                {
                    doc.Insert(end, decision.Close);
                    doc.Insert(start, decision.Open);
                }
                // keep the surrounded text selected, as VS Code does, so the user can surround again or type over it
                _v.TextArea.Selection = Selection.Create(_v.TextArea, start + decision.Open.Length, end + decision.Open.Length);
                editor.CaretOffset = end + decision.Open.Length;
                break;
            }
        }
        return e.Handled;
    }

    private void OnEntered(object? sender, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length == 1 && !_v.Document.IsReadOnly) TryOutdentAfterTyping(text); // inside the typing group: one Ctrl+Z reverts both
        EndTypingGroup(text);
        _typed.Fire(text);
    }

    /// <summary>A closing bracket typed alone on a line, or a line now matching decreaseIndentPattern, is pulled back to its block's indent.</summary>
    private void TryOutdentAfterTyping(string typed)
    {
        var editor = _v.Editor;
        var caret = _v.PositionOf(editor.CaretOffset);
        var plan = AutoIndent.AfterTyping(_v.Document, caret, typed, _v.GetEditingContext());
        if (plan is null) return;
        if (_v.Document.ApplyEdits(plan.Edits)) _v.SetSelection(plan.Selection);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back || e.KeyModifiers != KeyModifiers.None || _v.Editor.SelectionLength > 0 || _v.Document.IsReadOnly) return;
        // Several carets: MultiCursorController's Backspace does this at every caret. (Avalonia runs one element's tunnelling handlers in the
        // reverse order of their registration, so this one, added later, sees the key first.)
        if (_v.MultiCursor.IsActive) return;
        var editor = _v.Editor;
        var caret = editor.CaretOffset;
        if (caret == 0) return;
        // Backspace between an opener and the closer we auto-inserted removes both.
        if (caret < editor.Document.TextLength && IsAutoClosedPair(caret))
        {
            e.Handled = true;
            using (editor.Document.RunUpdate()) editor.Document.Remove(caret - 1, 2);
            ForgetCloserAt(caret);
            return;
        }
        // In leading whitespace Backspace goes back to the previous indentation stop (VS Code's editor.useTabStops, default on).
        if (_v.Settings.UseTabStops && MultiCaretOperations.IndentStopBefore(editor.Document, caret, _v.Settings.TabSize) is { } stop && stop < caret - 1)
        {
            e.Handled = true;
            using (editor.Document.RunUpdate()) editor.Document.Remove(stop, caret - stop);
        }
    }

    /// <summary>The character at <paramref name="caret"/> is a closer auto-closing inserted and the one before it is its opener.</summary>
    private bool IsAutoClosedPair(int caret)
    {
        var buffer = _v.Editor.Document;
        if (caret <= 0 || caret >= buffer.TextLength || !IsAutoCloserAt(caret, buffer.GetCharAt(caret))) return false;
        return IsPair(buffer.GetCharAt(caret - 1), buffer.GetCharAt(caret));
    }

    private bool IsPair(char open, char close)
    {
        foreach (var p in _v.LanguageConfiguration.Base.AutoClosingPairs)
            if (p.Open.Length > 0 && p.Open[^1] == open && p.Close.Length == 1 && p.Close[0] == close) return true;
        return false;
    }

    // ---- several carets (MultiCursorController): each caret decides as a single caret would, from its own context ----

    /// <summary>
    /// The batch for <paramref name="text"/> typed at every caret of <paramref name="set"/>: with one character and auto-closing on, each caret
    /// gets the decision a single caret there would get (<see cref="AutoClosing.Decide"/> with its own line, selection, token kind and whether
    /// the closer after it was auto-inserted); VS Code applies a pair only when every caret allows it, here a caret whose context refuses
    /// it types the plain character while the others get theirs. <paramref name="decisions"/> is null when nothing but plain typing happens.
    /// </summary>
    internal CaretEditBatch TypedBatch(CaretSet set, string text, out AutoCloseDecision[]? decisions)
    {
        decisions = null;
        var s = _v.Settings;
        if (text.Length != 1 || (!s.AutoClosingBrackets && !s.AutoClosingQuotes && !s.AutoSurround)) return MultiCaretOperations.Type(set, text);
        var buffer = _v.Document.Buffer;
        var ext = _v.LanguageConfiguration;
        var closers = LiveCloserOffsets();
        var result = new AutoCloseDecision[set.Count];
        var any = false;
        for (var i = 0; i < set.Count; i++)
        {
            var c = set[i];
            var line = buffer.GetLineByOffset(c.Start);
            var endLine = c.IsEmpty ? line : buffer.GetLineByOffset(c.End);
            var before = buffer.GetText(line.Offset, c.Start - line.Offset);
            var after = buffer.GetText(c.End, endLine.EndOffset - c.End);
            var selected = c.IsEmpty ? string.Empty : buffer.GetText(c.Start, c.Length);
            var kind = _v.Tokens?.KindAt(line.LineNumber - 1, c.Start - line.Offset) ?? TokenKind.Other;
            var next = c.IsEmpty && c.Active < buffer.TextLength && closers.Contains(c.Active) && buffer.GetCharAt(c.Active) == text[0];
            result[i] = AutoClosing.Decide(text, before, after, selected, ext.Base, ext.EffectiveAutoCloseBefore, kind, next, s.AutoClosingBrackets, s.AutoClosingQuotes, s.AutoSurround);
            any |= result[i].Kind != AutoCloseKind.None;
        }
        if (!any) return MultiCaretOperations.Type(set, text);
        decisions = result;
        // closers stepped over are not auto-inserted ones any more (as ForgetCloserAt for one caret)
        HashSet<int>? over = null;
        for (var i = 0; i < set.Count; i++)
            if (result[i].Kind == AutoCloseKind.OverType) (over ??= []).Add(set[i].Active);
        if (over is not null) _closers.RemoveAll(a => a.IsDeleted || over.Contains(a.Offset));
        return MultiCaretOperations.TypeWithDecisions(set, text, result);
    }

    /// <summary>
    /// After a typed batch at several carets: the closers it inserted are remembered (so they are over-typed and deleted with their opener),
    /// then a closing bracket typed alone on its line is pulled back to its block's indentation at every caret, joined to the same undo step.
    /// </summary>
    internal void AfterTypedBatch(MultiCursorController carets, string text, AutoCloseDecision[]? decisions, IReadOnlyList<CaretRange> batchCarets)
    {
        var buffer = _v.Document.Buffer;
        if (decisions is not null && decisions.Length == batchCarets.Count)
        {
            for (var i = 0; i < batchCarets.Count; i++)
                if (decisions[i].Kind == AutoCloseKind.InsertPair && batchCarets[i].Active < buffer.TextLength) Track(buffer.CreateAnchor(batchCarets[i].Active), MaxTrackedClosersMulti);
        }
        if (text.Length != 1 || _v.Document.IsReadOnly || !carets.IsActive) return;
        var ctx = _v.GetEditingContext();
        var edits = new List<OffsetEdit>();
        var lastLine = -1;
        foreach (var c in carets.Current.Carets)
        {
            var line = buffer.GetLineByOffset(c.Active);
            if (line.LineNumber == lastLine) continue; // a line once
            lastLine = line.LineNumber;
            if (!c.IsEmpty) continue;
            if (AutoIndent.AfterTyping(_v.Document, _v.PositionOf(c.Active), text, ctx) is not { Edits: [var edit] }) continue;
            edits.Add(new OffsetEdit(_v.Document.OffsetAt(edit.Range.Start), _v.Document.OffsetAt(edit.Range.End), edit.NewText));
        }
        if (edits.Count > 0) carets.ApplyMapped(edits, keepSelections: true, typing: true);
    }

    /// <summary>For Backspace at several carets: whether the caret at an offset sits between an opener and the closer auto-closing inserted.</summary>
    internal Func<int, bool> AutoClosedPairPredicate()
    {
        var closers = LiveCloserOffsets();
        var buffer = _v.Editor.Document;
        return offset => offset > 0 && offset < buffer.TextLength && closers.Contains(offset) && IsPair(buffer.GetCharAt(offset - 1), buffer.GetCharAt(offset));
    }

    /// <summary>Offsets of the closers auto-closing inserted that are still there (one pass over the tracked anchors).</summary>
    private HashSet<int> LiveCloserOffsets()
    {
        _closers.RemoveAll(a => a.IsDeleted);
        var set = new HashSet<int>(_closers.Count);
        foreach (var a in _closers) set.Add(a.Offset);
        return set;
    }

    private bool IsAutoCloserAt(int offset, char expected)
    {
        for (var i = _closers.Count - 1; i >= 0; i--)
        {
            var a = _closers[i];
            if (a.IsDeleted)
            {
                _closers.RemoveAt(i);
                continue;
            }
            if (a.Offset == offset && offset < _v.Editor.Document.TextLength && _v.Editor.Document.GetCharAt(offset) == expected) return true;
        }
        return false;
    }

    private void ForgetCloserAt(int offset) => _closers.RemoveAll(a => a.IsDeleted || a.Offset == offset);

    private void Track(TextAnchor anchor, int limit = MaxTrackedClosers)
    {
        anchor.MovementType = AnchorMovementType.AfterInsertion; // typing inside the pair must keep the anchor glued to the closer
        _closers.Add(anchor);
        if (_closers.Count > limit) _closers.RemoveRange(0, _closers.Count - limit);
    }

    public void Dispose()
    {
        _d.Dispose();
        _closers.Clear();
        _typed.Clear();
    }
}
