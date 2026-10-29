using CsCode.Editor.Documents;
using CsCode.Editor.TreeSitter.Highlighting;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// A <see cref="DocumentSnapshot"/> as tree-sitter input: chunks are copied out of the rope (no whole-document string, so a 100 MB file
/// is read in 8 K-character pieces). One instance per snapshot; the engine compares instances to tell whether a halted parse may resume.
/// Worker only (the copy buffer is not shared).
/// </summary>
internal sealed class SnapshotTextInput(DocumentSnapshot snapshot) : ITextInput
{
    private char[] _buffer = new char[1024];

    public DocumentSnapshot Snapshot { get; } = snapshot;

    public int Length => Snapshot.Length;

    public int LineCount => Snapshot.LineCount;

    public void CopyTo(int offset, Span<char> destination)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var n = Math.Min(destination.Length - done, SyntaxDocument.InputBufferChars);
            if (_buffer.Length < n) _buffer = new char[SyntaxDocument.InputBufferChars];
            Snapshot.CopyTo(offset + done, _buffer, 0, n);
            _buffer.AsSpan(0, n).CopyTo(destination[done..]);
            done += n;
        }
    }

    public int LineStart(int line) => Snapshot.LineStart(line);

    public int LineOfOffset(int offset) => Snapshot.PositionAt(offset).Line;

    /// <summary>Offset just past the last character of <paramref name="line"/> (before its terminator).</summary>
    public int LineEnd(int line) => Snapshot.LineStart(line) + Snapshot.LineContentLength(line);
}
