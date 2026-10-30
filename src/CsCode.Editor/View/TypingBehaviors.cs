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
/// a pair with Backspace, and re-indenting a lone closing bracket. Every decision is O(1) on the current line; nothing here waits for anything.
/// </summary>
internal sealed class TypingBehaviors : IDisposable, ISettingsAware
{
    private const int MaxTrackedClosers = 32;

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
        var editor = _v.Editor;
        var caret = editor.CaretOffset;
        if (caret == 0 || caret >= editor.Document.TextLength) return;
        // Backspace between an opener and the closer we auto-inserted removes both.
        if (!IsAutoCloserAt(caret, editor.Document.GetCharAt(caret))) return;
        var open = editor.Document.GetCharAt(caret - 1).ToString();
        var close = editor.Document.GetCharAt(caret).ToString();
        if (!_v.LanguageConfiguration.Base.AutoClosingPairs.Any(p => p.Open.EndsWith(open, StringComparison.Ordinal) && p.Close == close)) return;
        e.Handled = true;
        using (editor.Document.RunUpdate()) editor.Document.Remove(caret - 1, 2);
        ForgetCloserAt(caret);
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

    private void Track(TextAnchor anchor)
    {
        anchor.MovementType = AnchorMovementType.AfterInsertion; // typing inside the pair must keep the anchor glued to the closer
        _closers.Add(anchor);
        if (_closers.Count > MaxTrackedClosers) _closers.RemoveAt(0);
    }

    public void Dispose()
    {
        _d.Dispose();
        _closers.Clear();
        _typed.Clear();
    }
}
