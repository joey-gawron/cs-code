using CsCode.Core.Text;
using Xunit;

namespace CsCode.Vim.Tests;

/// <summary>
/// The optional host-integration hooks the editor adapter uses (docs/VIM.md, "Integration"): typing through the editor's own typing path
/// (<see cref="IVimHost.InsertTypedText"/>), changes the engine did not make (<see cref="IVimBuffer.TryTakeExternalChanges"/>), and undo
/// mirrored in the host's own history (<see cref="IVimBuffer.CanUndoRedoStep"/>/<see cref="IVimBuffer.UndoRedoStep"/>) with the text check
/// that catches a host history that does not hold what the engine expects.
/// </summary>
public class HostIntegrationTests
{
    /// <summary>
    /// A buffer like the editor adapter's: reports foreign changes with the lines they replaced, and keeps an editor-style history of snapshots,
    /// one group per Vim step (labelled before the change with <see cref="VimEngine.NextChangeUndoSequence"/>), that it can undo itself.
    /// </summary>
    private sealed class MirrorBuffer(string text) : IVimBuffer
    {
        private readonly LineVimBuffer _inner = new(text);
        private readonly List<VimExternalChange> _external = [];
        private readonly List<(int Seq, string Before, string After)> _undo = [], _redo = [];
        private int _pump, _groupSeq = -1;
        private string? _groupBefore;
        public VimEngine? Engine { get; set; }
        public bool Mirror { get; set; } = true;
        /// <summary>Makes the host's undo produce this text instead of the real one (a history that holds something else).</summary>
        public string? CorruptUndoTo { get; set; }
        /// <summary>Text the engine's own edits trigger as a side effect (a snippet mirror): appended to the last line after every engine edit.</summary>
        public string? SideEffect { get; set; }
        public int HostUndos, HostRedos;

        public string Name => _inner.Name;
        public Uri? Uri => null;
        public string LanguageId => "plaintext";
        public int Version => _inner.Version;
        public int LineCount => _inner.LineCount;
        public string GetLine(int line) => _inner.GetLine(line);
        public string EndOfLine => "\n";
        public bool IsReadOnly => false;
        public string Text => _inner.GetText();

        public bool ApplyEdit(in VimEdit edit)
        {
            BeginChange(Engine is { IsApplyingUndo: true } ? -1 : Engine?.NextChangeUndoSequence ?? -1);
            var ok = _inner.ApplyEdit(edit);
            if (SideEffect is { } s) External(new VimEdit(new TextPosition(LineCount - 1, GetLine(LineCount - 1).Length), new TextPosition(LineCount - 1, GetLine(LineCount - 1).Length), s), 0);
            return ok;
        }

        public bool ApplyEdits(ReadOnlySpan<VimEdit> edits)
        {
            BeginChange(Engine is { IsApplyingUndo: true } ? -1 : Engine?.NextChangeUndoSequence ?? -1);
            return _inner.ApplyEdits(edits);
        }

        /// <summary>A change made by "the editor": reported with the lines it replaced.</summary>
        public void External(in VimEdit edit, int group)
        {
            if (_pump > 0) BeginChange(Engine?.NextChangeUndoSequence ?? -1);
            var old = new string[edit.End.Line - edit.Start.Line + 1];
            for (var i = 0; i < old.Length; i++) old[i] = GetLine(edit.Start.Line + i);
            _inner.ApplyEdit(edit);
            _external.Add(new VimExternalChange(edit.Start.Line, old, 1 + edit.Text.Count(c => c == '\n'), group));
            if (_pump == 0) { _undo.Add((-1, "", "")); _redo.Clear(); } // a foreign group in the host history
        }

        private void BeginChange(int seq)
        {
            if (_pump == 0) return;
            if (_groupBefore is not null && seq == _groupSeq) return;
            CloseGroup();
            _groupSeq = seq;
            _groupBefore = Text;
        }

        private void CloseGroup()
        {
            if (_groupBefore is null) return;
            var after = Text;
            if (after != _groupBefore)
            {
                if (_undo.Count > 0 && _undo[^1].Seq == _groupSeq && _groupSeq >= 0) _undo[^1] = (_groupSeq, _undo[^1].Before, after);
                else _undo.Add((_groupSeq, _groupBefore, after));
                _redo.Clear();
            }
            _groupBefore = null;
        }

        public void BeginEditGroup() => _pump++;
        public void EndEditGroup()
        {
            if (--_pump == 0) CloseGroup();
        }

        public bool TryTakeExternalChanges(List<VimExternalChange> into)
        {
            into.AddRange(_external);
            _external.Clear();
            return true;
        }

        public bool CanUndoRedoStep(int seq, bool redo) => Mirror && (redo ? _redo : _undo) is { Count: > 0 } l && l[^1].Seq == seq;

        public bool UndoRedoStep(int seq, bool redo)
        {
            CloseGroup();
            var (from, to) = redo ? (_redo, _undo) : (_undo, _redo);
            if (from.Count == 0 || from[^1].Seq != seq) return false;
            var e = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(e);
            if (redo) HostRedos++; else HostUndos++;
            _inner.SetText(redo ? e.After : CorruptUndoTo ?? e.Before);
            if (!redo && CorruptUndoTo is not null) CorruptUndoTo = null; // only the first undo lies; putting it back (redo) is exact
            return true;
        }
    }

    private sealed class Host(MirrorBuffer buffer) : IVimHost
    {
        public TextPosition Caret;
        public IVimBuffer Buffer => buffer;
        public IVimTimer Timer { get; } = new ManualVimTimer();
        public TextPosition GetCaret() => Caret;
        public void SetCaret(TextPosition position, bool insertLike) => Caret = position;
        public bool TypingPath { get; set; } = true;

        /// <summary>The editor's typing path: "(" auto-closes to "()", everything else is inserted; the caret ends after the typed character.</summary>
        public bool InsertTypedText(string text)
        {
            if (!TypingPath) return false;
            var line = buffer.GetLine(Caret.Line);
            var col = Math.Min(Caret.Character, line.Length);
            var insert = text == "(" ? "()" : text == "\n" ? "\n" : text;
            buffer.External(new VimEdit(new TextPosition(Caret.Line, col), new TextPosition(Caret.Line, col), insert), group: 0);
            Caret = text == "\n" ? new TextPosition(Caret.Line + 1, 0) : new TextPosition(Caret.Line, col + text.Length);
            return true;
        }
    }

    private static (VimEngine Engine, Host Host, MirrorBuffer Buffer) Create(string text)
    {
        var buffer = new MirrorBuffer(text);
        var host = new Host(buffer);
        var engine = new VimEngine(host, new VimSession());
        buffer.Engine = engine;
        return (engine, host, buffer);
    }

    [Fact]
    public void Typing_goes_through_the_host_typing_path_and_the_insert_is_one_undo_step()
    {
        var (engine, host, buffer) = Create("x");
        engine.TypeKeysOneByOne("A f(a<Esc>");
        Assert.Equal("x f(a)", buffer.Text); // the editor's auto-close happened
        Assert.Equal(new TextPosition(0, 4), engine.Cursor);   // Esc moves back onto the last typed character
        engine.TypeKeysOneByOne("u");
        Assert.Equal("x", buffer.Text);
        Assert.Equal(1, buffer.HostUndos);                    // the host undid its own group
        engine.TypeKeysOneByOne("<C-r>");
        Assert.Equal("x f(a)", buffer.Text);
        Assert.Equal(1, buffer.HostRedos);
    }

    [Fact]
    public void Dot_repeat_and_macros_insert_the_recorded_keys_exactly_without_the_typing_path()
    {
        var (engine, host, buffer) = Create("a\nb\nc");
        engine.TypeKeysOneByOne("A;(<Esc>");
        Assert.Equal("a;()\nb\nc", buffer.Text); // typed: the editor auto-closed the bracket
        engine.TypeKeysOneByOne("j.");
        Assert.Equal("a;()\nb;(\nc", buffer.Text); // replayed: exactly the keys that were typed, as Vim does
        engine.TypeKeysOneByOne("jqqA!<Esc>q");
        Assert.Equal("c!", buffer.GetLine(2));
        engine.TypeKeysOneByOne("k@q");
        Assert.Equal("b;(!", buffer.GetLine(1));
        engine.TypeKeysOneByOne("uuuu");
        Assert.Equal("a\nb\nc", buffer.Text);
        Assert.Equal(new TextPosition(0, 0), engine.Cursor); // where the first undone change started
        Assert.Equal(engine.Cursor, host.Caret);
    }

    [Fact]
    public void A_change_made_by_the_editor_between_keys_is_its_own_undo_step()
    {
        var (engine, _, buffer) = Create("one\ntwo");
        engine.TypeKeysOneByOne("x");
        Assert.Equal("ne\ntwo", buffer.Text);
        buffer.External(new VimEdit(new TextPosition(1, 0), new TextPosition(1, 3), "TWO"), group: 7); // a formatter, a workspace edit
        engine.TypeKeysOneByOne("u");
        Assert.Equal("ne\ntwo", buffer.Text);  // only the editor's change was undone
        engine.TypeKeysOneByOne("u");
        Assert.Equal("one\ntwo", buffer.Text); // the history survived it
        engine.TypeKeysOneByOne("<C-r><C-r>");
        Assert.Equal("ne\nTWO", buffer.Text);
    }

    [Fact]
    public void A_change_made_by_the_editor_during_an_insert_session_joins_the_insert()
    {
        var (engine, _, buffer) = Create("x");
        engine.TypeKeysOneByOne("Aab");
        buffer.External(new VimEdit(new TextPosition(0, 3), new TextPosition(0, 3), "cde"), group: 0); // an accepted completion
        engine.TypeKeysOneByOne("<Esc>");
        Assert.Equal("xabcde", buffer.Text);
        engine.TypeKeysOneByOne("u");
        Assert.Equal("x", buffer.Text); // one step; the host history had a foreign group inside it, so the engine undid it itself
        Assert.Equal(0, buffer.HostUndos);
    }

    [Fact]
    public void Side_effects_of_a_key_join_its_change()
    {
        var (engine, _, buffer) = Create("abc");
        buffer.SideEffect = "!";
        engine.TypeKeysOneByOne("x");
        buffer.SideEffect = null;
        Assert.Equal("bc!", buffer.Text);
        engine.TypeKeysOneByOne("u");
        Assert.Equal("abc", buffer.Text);
        engine.TypeKeysOneByOne("<C-r>");
        Assert.Equal("bc!", buffer.Text);
    }

    [Fact]
    public void A_host_history_that_holds_something_else_is_caught_and_the_engine_applies_the_step_itself()
    {
        var (engine, _, buffer) = Create("hello\nworld");
        engine.TypeKeysOneByOne("dd");
        Assert.Equal("world", buffer.Text);
        buffer.CorruptUndoTo = "garbage";
        engine.TypeKeysOneByOne("u");
        Assert.Equal("hello\nworld", buffer.Text); // the host's wrong undo was put back (redo) and the step applied as edits
        Assert.Equal(1, buffer.HostUndos);
        Assert.Equal(1, buffer.HostRedos);
    }

    [Fact]
    public void Without_a_mirrored_history_undo_is_applied_as_edits()
    {
        var (engine, _, buffer) = Create("abc def");
        buffer.Mirror = false;
        engine.TypeKeysOneByOne("dwx");
        Assert.Equal("ef", buffer.Text);
        engine.TypeKeysOneByOne("u");
        Assert.Equal("def", buffer.Text);
        engine.TypeKeysOneByOne("u");
        Assert.Equal("abc def", buffer.Text);
        Assert.Equal(0, buffer.HostUndos);
    }

    [Fact]
    public void Undo_queries_follow_the_history()
    {
        var (engine, _, _) = Create("abc");
        Assert.False(engine.CanUndo);
        Assert.False(engine.CanRedo);
        Assert.False(engine.IsUndoStepOpen);
        var next = engine.NextChangeUndoSequence;
        engine.TypeKeysOneByOne("x");
        Assert.Equal(next, engine.UndoSequence);
        Assert.True(engine.CanUndo);
        engine.TypeKeysOneByOne("i");
        Assert.Equal(engine.UndoSequence + 1, engine.NextChangeUndoSequence); // a new insert is a new step
        engine.TypeKeysOneByOne("z");
        Assert.True(engine.IsUndoStepOpen);
        Assert.Equal(engine.UndoSequence, engine.NextChangeUndoSequence);     // more typing joins it
        engine.TypeKeysOneByOne("<Esc>u");
        Assert.True(engine.CanRedo);
    }

    [Fact]
    public void A_caret_the_user_moved_is_moved_back_by_a_motion_to_where_the_engine_last_put_it()
    {
        var host = new InMemoryVimHost("foo bar baz");
        var engine = new VimEngine(host, new VimSession());
        engine.TypeKeysOneByOne("0");           // the engine put the caret at 0:0
        host.Caret = new TextPosition(0, 4);    // a mouse click
        engine.TypeKeysOneByOne("b");           // back to 0:0: the host's caret must follow although the engine sent 0:0 before
        Assert.Equal(new TextPosition(0, 0), engine.Cursor);
        Assert.Equal(new TextPosition(0, 0), host.Caret);
    }
}
