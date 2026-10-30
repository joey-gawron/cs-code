using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Server;

/// <summary>
/// The server's copy of a document the client has open. Its text and version follow the client exactly (<c>doc/open</c> full text, then the client's
/// own <see cref="TextContentChange"/>s with the client's version numbers), so the language servers here see the same versions as the editor and
/// stale-result checks line up on both ends. Read-only on this side: edits go to the client (workspace/applyEdit is forwarded), never here.
/// </summary>
internal sealed class MirrorDocument : ITextDocument
{
    private readonly object _gate = new();
    private readonly Emitter<TextDocumentChangedEventArgs> _changed = new();
    private readonly Emitter<ITextDocument> _langChanged = new();
    private string _text;
    private int[] _lineStarts;
    private string _languageId;
    private int _version;

    public MirrorDocument(Uri uri, string languageId, int version, string text)
    {
        Uri = uri;
        _languageId = languageId;
        _version = version;
        _text = text;
        _lineStarts = LineStarts(_text);
    }

    public Uri Uri { get; }
    public string LanguageId { get { lock (_gate) return _languageId; } }
    public int Version { get { lock (_gate) return _version; } }
    public bool IsDirty => false;
    public int Length { get { lock (_gate) return _text.Length; } }
    public int LineCount { get { lock (_gate) return _lineStarts.Length; } }
    public IEventSource<TextDocumentChangedEventArgs> Changed => _changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _langChanged;

    public string GetText() { lock (_gate) return _text; }

    public string GetText(TextRange range)
    {
        lock (_gate)
        {
            var s = OffsetCore(range.Start);
            var e = OffsetCore(range.End);
            return e > s ? _text.Substring(s, e - s) : "";
        }
    }

    public string GetLineText(int line)
    {
        lock (_gate)
        {
            if (line < 0 || line >= _lineStarts.Length) return "";
            var start = _lineStarts[line];
            return _text.Substring(start, ContentLength(line));
        }
    }

    public int OffsetAt(TextPosition position) { lock (_gate) return OffsetCore(position); }

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
            return new TextPosition(line, Math.Clamp(position.Character, 0, ContentLength(line)));
        }
    }

    /// <summary>Edits are the client's to make; a server-side edit request is refused.</summary>
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => false;

    /// <summary>
    /// Applies the client's changes (each against the result of the previous one, by range) and takes its version. A change whose version is not
    /// newer is ignored (duplicate delivery after a resync). Returns false when a range does not fit the text (the client then resyncs fully).
    /// </summary>
    public bool ApplyClientChanges(int version, IReadOnlyList<TextContentChange> changes)
    {
        TextDocumentChangedEventArgs args;
        lock (_gate)
        {
            if (version <= _version) return true;
            foreach (var c in changes)
            {
                var start = OffsetCore(c.Range.Start);
                var end = OffsetCore(c.Range.End);
                if (end < start) return false;
                _text = string.Concat(_text.AsSpan(0, start), c.Text, _text.AsSpan(end));
                _lineStarts = LineStarts(_text);
            }
            _version = version;
            args = new TextDocumentChangedEventArgs(this, version, changes);
        }
        _changed.Fire(args);
        return true;
    }

    /// <summary>Replaces the whole text (resync after reconnect, or when a change did not apply).</summary>
    public void Reset(int version, string text)
    {
        TextDocumentChangedEventArgs args;
        lock (_gate)
        {
            var oldEnd = PositionAtCore(_text.Length);
            var oldLength = _text.Length;
            _text = text;
            _lineStarts = LineStarts(_text);
            _version = version;
            args = new TextDocumentChangedEventArgs(this, version, [new TextContentChange(new TextRange(TextPosition.Zero, oldEnd), 0, oldLength, text)]);
        }
        _changed.Fire(args);
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

    private TextPosition PositionAtCore(int offset)
    {
        var idx = Array.BinarySearch(_lineStarts, offset);
        var line = idx >= 0 ? idx : ~idx - 1;
        return new TextPosition(line, offset - _lineStarts[line]);
    }

    private int OffsetCore(TextPosition p)
    {
        var line = Math.Clamp(p.Line, 0, _lineStarts.Length - 1);
        return _lineStarts[line] + Math.Clamp(p.Character, 0, ContentLength(line));
    }

    private int ContentLength(int line)
    {
        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;
        while (end > start && (_text[end - 1] is '\n' or '\r')) end--;
        return end - start;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        var span = text.AsSpan();
        var i = 0;
        while (true)
        {
            var hit = span[i..].IndexOfAny('\r', '\n'); // vectorised: typing in a large file stays cheap
            if (hit < 0) break;
            i += hit;
            if (span[i] == '\r' && i + 1 < span.Length && span[i + 1] == '\n') i++;
            starts.Add(++i);
        }
        return starts.ToArray();
    }
}
