using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>
/// <see cref="IVimBuffer"/> over any Core <see cref="ITextDocument"/> (the real editor's document, or an
/// <see cref="InMemoryTextDocument"/> in tests). Reads go through a small per-version line cache so cursor motions
/// do not allocate a string per read; edits are forwarded as one <c>ApplyEdits</c> call each.
/// </summary>
public sealed class TextDocumentVimBuffer : IVimBuffer
{
    private const int CacheSize = 64; // power of two
    private readonly ITextDocument _doc;
    private readonly string?[] _cache = new string?[CacheSize];
    private readonly int[] _cacheLine = new int[CacheSize];
    private int _cacheVersion = -1;

    public TextDocumentVimBuffer(ITextDocument document)
    {
        _doc = document;
        EndOfLine = DetectEol(document);
    }

    public ITextDocument Document => _doc;
    public string Name => _doc.Uri.IsFile ? Path.GetFileName(_doc.Uri.LocalPath) : _doc.Uri.ToString();
    public Uri? Uri => _doc.Uri;
    public string LanguageId => _doc.LanguageId;
    public int Version => _doc.Version;
    public int LineCount => _doc.LineCount;
    public string EndOfLine { get; }
    public bool IsReadOnly => _doc is IEditorDocument { IsReadOnly: true };

    public IEventSource<VimBufferChange>? Changed => new Mapped(_doc);

    public string GetLine(int line)
    {
        var v = _doc.Version;
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

    public bool ApplyEdit(in VimEdit edit)
    {
        if (IsReadOnly) return false;
        return _doc.ApplyEdits([new TextEdit(new TextRange(edit.Start, edit.End), ToDocText(edit.Text))]);
    }

    public bool ApplyEdits(ReadOnlySpan<VimEdit> edits)
    {
        if (IsReadOnly) return false;
        if (edits.Length == 0) return true;
        var list = new TextEdit[edits.Length];
        for (var i = 0; i < edits.Length; i++) list[i] = new TextEdit(new TextRange(edits[i].Start, edits[i].End), ToDocText(edits[i].Text));
        return _doc.ApplyEdits(list);
    }

    private string ToDocText(string text) => EndOfLine == "\n" || text.IndexOf('\n') < 0 ? text : text.Replace("\n", EndOfLine);

    private static string DetectEol(ITextDocument doc)
    {
        if (doc.LineCount < 2) return "\n";
        var first = doc.GetText(new TextRange(new TextPosition(0, 0), new TextPosition(1, 0)));
        return first.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private sealed class Mapped(ITextDocument doc) : IEventSource<VimBufferChange>
    {
        public IDisposable Subscribe(Action<VimBufferChange> handler) =>
            doc.Changed.Subscribe(a => handler(new VimBufferChange(a.Version, a.Changes)));
    }
}
