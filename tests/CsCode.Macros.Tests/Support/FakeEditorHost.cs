using System.Diagnostics;
using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Macros.Tests.Support;

/// <summary>
/// A small line-based editor behind <see cref="IMacroHost"/>: a caret, a selection anchor, the usual navigation and editing keys, typed text.
/// <see cref="StepCost"/> makes every key and character cost real UI-thread time (a spin), like a real editor's keystroke.
/// </summary>
public sealed class FakeEditorHost : IMacroHost
{
    private readonly List<StringBuilder> _lines;

    public FakeEditorHost(string text = "", StepClock? clock = null)
    {
        _lines = text.Replace("\r\n", "\n").Split('\n').Select(l => new StringBuilder(l)).ToList();
        Clock = clock;
    }

    public StepClock? Clock { get; }
    public Uri Document { get; set; } = new("file:///fake.txt");
    public int Line { get; set; }
    public int Column { get; set; }
    public (int Line, int Column)? Anchor { get; set; }
    public int Version { get; private set; } = 1;
    public bool ReadOnly { get; set; }
    public bool HasEditor { get; set; } = true;
    public string? Unavailable { get; set; }
    public MacroBlock? Block { get; set; }
    /// <summary>Real time each key or character spins for (simulated editor cost).</summary>
    public TimeSpan StepCost { get; set; }
    /// <summary>Virtual time each key or character adds to <see cref="Clock"/>.</summary>
    public TimeSpan VirtualCost { get; set; }
    public List<string> Received { get; } = new();
    public Action<KeyStroke>? OnKey { get; set; }
    /// <summary>When set, every call off this thread is counted in <see cref="WrongThreadCalls"/>.</summary>
    public TestUiThread? Ui { get; set; }
    public int WrongThreadCalls;

    private void CheckThread()
    {
        if (Ui is { IsUiThread: false }) Interlocked.Increment(ref WrongThreadCalls);
    }

    /// <summary>Deletes the caret's line (a stand-in for editor.action.deleteLines).</summary>
    public void DeleteLine()
    {
        CheckThread();
        if (_lines.Count == 1) _lines[0].Clear();
        else _lines.RemoveAt(Line);
        Line = Math.Min(Line, _lines.Count - 1);
        Column = 0;
        Version++;
    }

    public string Text => string.Join("\n", _lines.Select(l => l.ToString()));

    public string? TypingUnavailableReason => !HasEditor ? "No editor is open." : Unavailable;
    public MacroBlock? RecordingBlocked => Block;

    public MacroCaret? Caret => HasEditor ? new MacroCaret(Document, Line, Column, _lines.Count, Version) : null;

    public (int First, int Last)? SelectedLines
    {
        get
        {
            if (!HasEditor) return null;
            if (Anchor is not { } a) return (Line, Line);
            var (sl, sc, el, ec) = a.Line < Line || (a.Line == Line && a.Column <= Column) ? (a.Line, a.Column, Line, Column) : (Line, Column, a.Line, a.Column);
            if (ec == 0 && el > sl) el--;
            return (sl, el);
        }
    }

    public bool MoveCaretToLineStart(int line)
    {
        if (!HasEditor || line < 0 || line >= _lines.Count) return false;
        Line = line;
        Column = 0;
        Anchor = null;
        return true;
    }

    public string? SendKey(KeyStroke key)
    {
        CheckThread();
        if (!HasEditor) return "No editor is open.";
        Cost();
        Received.Add("key:" + key);
        OnKey?.Invoke(key);
        var line = _lines[Line];
        switch (key.Key)
        {
            case "left": if (Column > 0) Column--; else if (Line > 0) { Line--; Column = _lines[Line].Length; } break;
            case "right": if (Column < line.Length) Column++; else if (Line < _lines.Count - 1) { Line++; Column = 0; } break;
            case "up": if (Line > 0) { Line--; Column = Math.Min(Column, _lines[Line].Length); } break;
            case "down": if (Line < _lines.Count - 1) { Line++; Column = Math.Min(Column, _lines[Line].Length); } break;
            case "home": Column = 0; break;
            case "end": Column = line.Length; break;
            case "backspace" when !ReadOnly:
                if (Column > 0) { line.Remove(Column - 1, 1); Column--; }
                else if (Line > 0) { var prev = _lines[Line - 1]; Column = prev.Length; prev.Append(line); _lines.RemoveAt(Line); Line--; }
                Version++;
                break;
            case "delete" when !ReadOnly:
                if (Column < line.Length) line.Remove(Column, 1);
                else if (Line < _lines.Count - 1) { line.Append(_lines[Line + 1]); _lines.RemoveAt(Line + 1); }
                Version++;
                break;
            case "enter" when !ReadOnly:
            {
                var rest = line.ToString(Column, line.Length - Column);
                line.Length = Column;
                _lines.Insert(Line + 1, new StringBuilder(rest));
                Line++;
                Column = 0;
                Version++;
                break;
            }
            case "backspace" or "delete" or "enter":
                return "The document is read-only.";
        }
        if (!key.Shift) Anchor = null;
        return null;
    }

    public string? TypeText(string text)
    {
        CheckThread();
        if (!HasEditor) return "No editor is open.";
        if (ReadOnly) return "The document is read-only.";
        Cost();
        Received.Add("text:" + text);
        _lines[Line].Insert(Column, text);
        Column += text.Length;
        Version++;
        return null;
    }

    private void Cost()
    {
        if (VirtualCost > TimeSpan.Zero) Clock?.Advance(VirtualCost);
        if (StepCost <= TimeSpan.Zero) return;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < StepCost) Thread.SpinWait(20);
    }
}
