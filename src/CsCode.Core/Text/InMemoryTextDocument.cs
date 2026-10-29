using System.Text;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Text;

/// <summary>
/// Simple, correct, UI-free <see cref="ITextDocument"/> (string + line-start index). Intended for tests, fakes and
/// small non-editor buffers (e.g. extension-host virtual documents); the real editor uses a rope-backed document.
/// Lines are terminated by \n, \r\n or \r.
/// </summary>
public sealed class InMemoryTextDocument : ITextDocument
{
    private readonly object _gate = new();
    private readonly Emitter<TextDocumentChangedEventArgs> _changed = new();
    private readonly Emitter<ITextDocument> _langChanged = new();
    private string _text;
    private int[] _lineStarts;
    private string _languageId;

    public InMemoryTextDocument(Uri uri, string languageId, string text = "")
    {
        Uri = uri;
        _languageId = languageId;
        _text = text;
        _lineStarts = ComputeLineStarts(text);
    }

    public Uri Uri { get; }
    public int Version { get; private set; } = 1;
    public bool IsDirty { get; private set; }

    public string LanguageId
    {
        get { lock (_gate) return _languageId; }
    }

    public void SetLanguageId(string languageId)
    {
        lock (_gate)
        {
            if (_languageId == languageId) return;
            _languageId = languageId;
        }
        _langChanged.Fire(this);
    }

    public int Length { get { lock (_gate) return _text.Length; } }
    public int LineCount { get { lock (_gate) return _lineStarts.Length; } }

    public IEventSource<TextDocumentChangedEventArgs> Changed => _changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _langChanged;

    public string GetText() { lock (_gate) return _text; }

    public string GetText(TextRange range)
    {
        lock (_gate)
        {
            var s = OffsetAtCore(range.Start);
            var e = OffsetAtCore(range.End);
            return _text.Substring(s, Math.Max(0, e - s));
        }
    }

    public string GetLineText(int line)
    {
        lock (_gate)
        {
            if (line < 0 || line >= _lineStarts.Length) return string.Empty;
            var start = _lineStarts[line];
            var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
            while (end > start && (_text[end - 1] is '\n' or '\r')) end--;
            return _text.Substring(start, end - start);
        }
    }

    public int OffsetAt(TextPosition position) { lock (_gate) return OffsetAtCore(position); }

    public TextPosition PositionAt(int offset)
    {
        lock (_gate)
        {
            offset = Math.Clamp(offset, 0, _text.Length);
            var idx = Array.BinarySearch(_lineStarts, offset);
            var line = idx >= 0 ? idx : ~idx - 1;
            return new TextPosition(line, offset - _lineStarts[line]);
        }
    }

    public TextPosition Validate(TextPosition position)
    {
        lock (_gate)
        {
            var line = Math.Clamp(position.Line, 0, _lineStarts.Length - 1);
            var maxChar = LineContentLength(line);
            return new TextPosition(line, Math.Clamp(position.Character, 0, maxChar));
        }
    }

    public bool ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) return true;
        TextDocumentChangedEventArgs args;
        lock (_gate)
        {
            // Resolve every edit against the ORIGINAL text first, then apply from the end so offsets stay valid.
            var resolved = edits
                .Select(e => (Start: OffsetAtCore(e.Range.Start), End: OffsetAtCore(e.Range.End), e.NewText, e.Range))
                .OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
            for (var i = 1; i < resolved.Count; i++)
                if (resolved[i].Start < resolved[i - 1].End) return false; // overlapping

            var changes = new List<TextContentChange>(resolved.Count);
            var sb = new StringBuilder(_text);
            // Report changes in application order (descending) so each range is valid against the previous state.
            foreach (var r in Enumerable.Reverse(resolved))
            {
                sb.Remove(r.Start, r.End - r.Start).Insert(r.Start, r.NewText);
                changes.Add(new TextContentChange(r.Range, r.Start, r.End - r.Start, r.NewText));
            }
            _text = sb.ToString();
            _lineStarts = ComputeLineStarts(_text);
            Version++;
            IsDirty = true;
            args = new TextDocumentChangedEventArgs(this, Version, changes);
        }
        _changed.Fire(args);
        return true;
    }

    public void MarkClean() { lock (_gate) IsDirty = false; }

    private int OffsetAtCore(TextPosition p)
    {
        var line = Math.Clamp(p.Line, 0, _lineStarts.Length - 1);
        var start = _lineStarts[line];
        var max = LineContentLength(line);
        return start + Math.Clamp(p.Character, 0, max);
    }

    private int LineContentLength(int line)
    {
        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
        while (end > start && (_text[end - 1] is '\n' or '\r')) end--;
        return end - start;
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}
