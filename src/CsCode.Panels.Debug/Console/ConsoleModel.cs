using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Console;

/// <summary>How a line of the Debug Console is coloured (derived from the DAP output category, or from what the user typed).</summary>
public enum ConsoleLineKind
{
    /// <summary>Program output (<c>stdout</c>).</summary>
    Output,
    /// <summary>Program error output (<c>stderr</c>).</summary>
    Error,
    /// <summary>Debugger/adapter messages (<c>console</c>).</summary>
    Console,
    /// <summary>Messages the adapter wants noticed (<c>important</c>).</summary>
    Important,
    /// <summary>An expression the user entered at the prompt.</summary>
    Input,
    /// <summary>The value an entered expression evaluated to.</summary>
    Result,
    /// <summary>An entered expression failed.</summary>
    ResultError,
    /// <summary>A note from the console itself ("N lines not shown").</summary>
    Info,
}

/// <summary>One immutable, single-line entry of the Debug Console.</summary>
public sealed class ConsoleLine
{
    internal ConsoleLine(long sequence, ConsoleLineKind kind, string text, DebugSource? source = null, int? line = null)
    {
        Sequence = sequence;
        Kind = kind;
        Text = text;
        Source = source;
        Line = line;
    }

    /// <summary>Monotonic per console; lets the filter and the live append path agree on what has been applied.</summary>
    public long Sequence { get; }
    public ConsoleLineKind Kind { get; }
    public string Text { get; }
    public DebugSource? Source { get; }
    /// <summary>0-based line of <see cref="Source"/>, when the adapter attached a location.</summary>
    public int? Line { get; }

    /// <summary><c>Program.cs:12</c> when the adapter attached a source location.</summary>
    public string Location => Source is { } s && Line is { } l ? $"{(!string.IsNullOrEmpty(s.Name) ? s.Name : System.IO.Path.GetFileName(s.Path ?? ""))}:{l + 1}" : "";
    public bool HasLocation => Source is not null && Line is not null;

    /// <summary>Name of the style class the row template applies (<c>out</c>, <c>err</c>, <c>info</c>…).</summary>
    public string StyleClass => Kind switch
    {
        ConsoleLineKind.Error or ConsoleLineKind.ResultError => "err",
        ConsoleLineKind.Console => "dbg",
        ConsoleLineKind.Important => "imp",
        ConsoleLineKind.Input => "inp",
        ConsoleLineKind.Result => "res",
        ConsoleLineKind.Info => "info",
        _ => "out",
    };

    public override string ToString() => Text;
}

/// <summary>A completion offered for the REPL input.</summary>
public sealed class CompletionEntry(string label, string text, string? type, int start, int length) : ObservableObject
{
    public string Label { get; } = label;
    /// <summary>What replaces <c>text[Start..Start+Length]</c> when accepted.</summary>
    public string Text { get; } = text;
    public string? Type { get; } = type;
    public int Start { get; } = start;
    public int Length { get; } = length;
}

/// <summary>Thread-safe ring of the most recent console lines: the single bounded store the filter re-reads (≤ <see cref="Capacity"/> lines).</summary>
public sealed class ConsoleBuffer(int capacity = ConsoleBuffer.DefaultCapacity)
{
    public const int DefaultCapacity = 5_000;

    private readonly object _gate = new();
    private readonly Queue<ConsoleLine> _lines = new();

    public int Capacity { get; } = capacity;

    public int Count { get { lock (_gate) return _lines.Count; } }

    public void Append(IReadOnlyList<ConsoleLine> lines)
    {
        lock (_gate)
        {
            foreach (var line in lines) _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
        }
    }

    public ConsoleLine[] Snapshot()
    {
        lock (_gate) return [.. _lines];
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}

/// <summary>The REPL's command history: bounded, consecutive duplicates collapsed, navigated with ↑/↓ like a shell.</summary>
public sealed class ReplHistory(int capacity = ReplHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 200;

    private readonly List<string> _entries = new();
    private int _cursor;
    private string _draft = "";

    public int Count => _entries.Count;
    public IReadOnlyList<string> Entries => _entries;

    public void Add(string entry)
    {
        if (entry.Length == 0) return;
        if (_entries.Count == 0 || _entries[^1] != entry) _entries.Add(entry);
        if (_entries.Count > capacity) _entries.RemoveRange(0, _entries.Count - capacity);
        Reset();
    }

    public void Reset()
    {
        _cursor = _entries.Count;
        _draft = "";
    }

    /// <summary>Moves to the previous (older) entry; <paramref name="current"/> is kept as the draft to come back to.</summary>
    public string? Previous(string current)
    {
        if (_entries.Count == 0 || _cursor == 0) return null;
        if (_cursor == _entries.Count) _draft = current;
        _cursor--;
        return _entries[_cursor];
    }

    /// <summary>Moves to the next (newer) entry, ending at the draft that was being typed.</summary>
    public string? Next()
    {
        if (_cursor >= _entries.Count) return null;
        _cursor++;
        return _cursor == _entries.Count ? _draft : _entries[_cursor];
    }
}
