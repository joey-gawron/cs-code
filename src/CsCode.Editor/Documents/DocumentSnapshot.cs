using AvaloniaEdit.Document;
using AvaloniaEdit.Utils;
using CsCode.Core.Text;

namespace CsCode.Editor.Documents;

/// <summary>
/// Immutable, thread-safe copy of a document at one version: a rope clone (structure shared with the live buffer, copy on
/// write) plus a lazily built line index. Everything that is not the UI thread reads documents through one of these —
/// language servers, the editor worker (tokenisation, folding), tests. Semantics (clamping, line terminators) are
/// identical to <see cref="InMemoryTextDocument"/>, which is the oracle in the fuzz tests.
/// </summary>
public sealed class DocumentSnapshot
{
    private readonly RopeTextSource _source;
    private int[]? _lineStarts;
    private string? _text;

    internal DocumentSnapshot(int version, Rope<char> rope)
    {
        Version = version;
        // Wrap WITHOUT the ITextSourceVersion: that object heads a linked list of every later change (with their text), so keeping
        // it alive from a long-lived snapshot would retain edit history.
        _source = new RopeTextSource(rope);
    }

    public int Version { get; }
    public int Length => _source.TextLength;
    public int LineCount => LineStarts.Length;

    /// <summary>The whole text (materialised once per snapshot; do not call on huge documents in hot paths).</summary>
    public string GetText() => _text ??= _source.Text;

    public string GetText(int offset, int length)
    {
        offset = Math.Clamp(offset, 0, Length);
        length = Math.Clamp(length, 0, Length - offset);
        return _source.GetText(offset, length);
    }

    public char GetCharAt(int offset) => _source.GetCharAt(offset);

    public ReadOnlyMemory<char> GetMemory(int offset, int length) => _source.GetTextAsMemory(offset, length);

    public string GetText(TextRange range) => GetText(OffsetAt(range.Start), Math.Max(0, OffsetAt(range.End) - OffsetAt(range.Start)));

    /// <summary>Text of the line excluding the terminator; empty when out of range.</summary>
    public string GetLineText(int line)
    {
        var starts = LineStarts;
        if (line < 0 || line >= starts.Length) return string.Empty;
        var start = starts[line];
        return _source.GetText(start, LineContentLength(line));
    }

    /// <summary>Offset of the first char of <paramref name="line"/> (clamped to the document).</summary>
    public int LineStart(int line)
    {
        var starts = LineStarts;
        return starts[Math.Clamp(line, 0, starts.Length - 1)];
    }

    /// <summary>Length of the line without its terminator.</summary>
    public int LineContentLength(int line)
    {
        var starts = LineStarts;
        var start = starts[line];
        var end = line + 1 < starts.Length ? starts[line + 1] : Length;
        while (end > start && _source.GetCharAt(end - 1) is '\n' or '\r') end--;
        return end - start;
    }

    public int OffsetAt(TextPosition p)
    {
        var starts = LineStarts;
        var line = Math.Clamp(p.Line, 0, starts.Length - 1);
        return starts[line] + Math.Clamp(p.Character, 0, LineContentLength(line));
    }

    public TextPosition PositionAt(int offset)
    {
        var starts = LineStarts;
        offset = Math.Clamp(offset, 0, Length);
        var idx = Array.BinarySearch(starts, offset);
        var line = idx >= 0 ? idx : ~idx - 1;
        return new TextPosition(line, offset - starts[line]);
    }

    public TextPosition Validate(TextPosition p)
    {
        var starts = LineStarts;
        var line = Math.Clamp(p.Line, 0, starts.Length - 1);
        return new TextPosition(line, Math.Clamp(p.Character, 0, LineContentLength(line)));
    }

    private int[] LineStarts => _lineStarts ?? (_lineStarts = ComputeLineStarts()); // benign race: the result is deterministic

    private int[] ComputeLineStarts()
    {
        var starts = new List<int>(Math.Max(16, Length / 40)) { 0 };
        var buffer = new char[16 * 1024];
        using var reader = _source.CreateReader();
        var offset = 0;
        var pendingCr = false; // a '\r' ended the previous block: a '\n' at the start of this one belongs to it
        int n;
        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var span = buffer.AsSpan(0, n);
            var i = 0;
            if (pendingCr)
            {
                pendingCr = false;
                if (span[0] == '\n')
                {
                    // CRLF split across blocks: the line start recorded for the '\r' moves past the '\n'.
                    starts[^1] = offset + 1;
                    i = 1;
                }
            }
            while (i < span.Length)
            {
                var rel = span[i..].IndexOfAny('\r', '\n');
                if (rel < 0) break;
                i += rel;
                if (span[i] == '\r')
                {
                    if (i + 1 < span.Length)
                    {
                        if (span[i + 1] == '\n') i++;
                        starts.Add(offset + i + 1);
                    }
                    else
                    {
                        // '\r' is the last char of the block: provisional line start; fixed up if the next block starts with '\n'.
                        starts.Add(offset + i + 1);
                        pendingCr = true;
                    }
                }
                else starts.Add(offset + i + 1);
                i++;
            }
            offset += n;
        }
        return starts.ToArray();
    }
}
