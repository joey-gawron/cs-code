using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// MUST 1: EditorDocument (AvaloniaEdit rope) vs InMemoryTextDocument as the oracle, over random edit sequences including
/// CRLF/CR/LF mixes, surrogate pairs, out-of-range (clamped) positions, overlapping edits, undo/redo and direct buffer
/// edits (the typing path).
/// </summary>
public class EditorDocumentFuzzTests
{
    private static readonly string[] Chunks = ["a", "b", "xy", " ", "\n", "\r", "\r\n", "\u00e9", "\U0001F600", "{}", "\t", ""];
    private static readonly Uri DocUri = new("file:///fuzz.txt");

    private static string RandomText(Random r, int maxChunks)
    {
        var n = r.Next(0, maxChunks + 1);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < n; i++) sb.Append(Chunks[r.Next(Chunks.Length)]);
        return sb.ToString();
    }

    private static string ApplyChange(string text, TextContentChange c) =>
        string.Concat(text.AsSpan(0, c.RangeOffset), c.Text, text.AsSpan(c.RangeOffset + c.RangeLength));

    [Fact]
    public void Random_edit_sequences_match_the_in_memory_oracle()
    {
        const int Sequences = 10_000;
        var ui = new ThreadAffinityUiThread();
        long totalOps = 0, crossThreadChecks = 0;

        for (var seed = 0; seed < Sequences; seed++)
        {
            var r = new Random(seed);
            var initial = RandomText(r, 12);
            var doc = new EditorDocument(DocUri, "plaintext", EditorDocument.CreateBuffer(initial, ui), ui);
            var oracle = new InMemoryTextDocument(DocUri, "plaintext", initial);

            var replay = initial;
            var events = 0;
            var lastVersion = doc.Version;
            var failure = (string?)null;
            doc.Changed.Subscribe(e =>
            {
                events++;
                if (e.Version != lastVersion + 1) failure ??= $"version {lastVersion} -> {e.Version}";
                lastVersion = e.Version;
                foreach (var c in e.Changes)
                {
                    // Ranges are expressed against the state BEFORE this change (sequential semantics).
                    var probe = new InMemoryTextDocument(DocUri, "plaintext", replay);
                    if (probe.PositionAt(c.RangeOffset) != c.Range.Start || probe.PositionAt(c.RangeOffset + c.RangeLength) != c.Range.End)
                        failure ??= $"range {c.Range} does not match offset {c.RangeOffset}+{c.RangeLength} in '{Escape(replay)}'";
                    replay = ApplyChange(replay, c);
                }
            });

            // history model for undo/redo (each successful edit op == one undo group)
            var undo = new Stack<string>();
            var redo = new Stack<string>();
            var markDepth = 0;
            var markLost = false;

            var ops = r.Next(1, 16);
            for (var op = 0; op < ops; op++)
            {
                totalOps++;
                var before = oracle.GetText();
                var eventsBefore = events;
                switch (r.Next(6))
                {
                    case 0 or 1 or 2: // ApplyEdits with several edits, positions possibly out of range
                    {
                        var edits = RandomEdits(r, oracle);
                        if (edits.Count == 0) break;
                        var expected = oracle.ApplyEdits(edits);
                        var actual = doc.ApplyEdits(edits);
                        Assert.True(expected == actual, $"seed {seed}: ApplyEdits result oracle={expected} doc={actual}");
                        if (expected)
                        {
                            Assert.Equal(eventsBefore + 1, events); // ONE event for the whole group
                            if (undo.Count < markDepth) markLost = true;
                            undo.Push(before);
                            redo.Clear();
                        }
                        else Assert.Equal(eventsBefore, events);
                        break;
                    }
                    case 3: // direct buffer edit = the typing / paste path
                    {
                        var offset = r.Next(0, oracle.Length + 1);
                        var length = r.Next(0, Math.Min(3, oracle.Length - offset) + 1);
                        var text = RandomText(r, 2);
                        if (length == 0 && text.Length == 0) break;
                        var start = oracle.PositionAt(offset);
                        var end = oracle.PositionAt(offset + length);
                        // Core positions inside a CRLF pair are not representable through ApplyEdits (clamped), so skip those offsets.
                        if (oracle.OffsetAt(start) != offset || oracle.OffsetAt(end) != offset + length) break;
                        oracle.ApplyEdits([new TextEdit(new TextRange(start, end), text)]);
                        doc.Buffer.Replace(offset, length, text);
                        Assert.Equal(eventsBefore + 1, events);
                        if (undo.Count < markDepth) markLost = true;
                        undo.Push(before);
                        redo.Clear();
                        break;
                    }
                    case 4: // undo
                        if (undo.Count == 0) break;
                        doc.Undo();
                        Assert.Equal(eventsBefore + 1, events); // a whole group undoes as ONE event
                        redo.Push(before);
                        oracle = new InMemoryTextDocument(DocUri, "plaintext", undo.Pop());
                        break;
                    case 5: // redo
                        if (redo.Count == 0) break;
                        doc.Redo();
                        Assert.Equal(eventsBefore + 1, events);
                        undo.Push(before);
                        oracle = new InMemoryTextDocument(DocUri, "plaintext", redo.Pop());
                        break;
                }

                Assert.True(failure is null, $"seed {seed} op {op}: {failure}");
                Assert.True(oracle.GetText() == doc.GetText(), $"seed {seed} op {op}: text differs\n oracle='{Escape(oracle.GetText())}'\n doc   ='{Escape(doc.GetText())}'");
                Assert.True(replay == doc.GetText(), $"seed {seed} op {op}: event replay differs from document");
                Assert.Equal(doc.CanUndo, undo.Count > 0);
                Assert.Equal(doc.CanRedo, redo.Count > 0);
                Assert.Equal(markLost || undo.Count != markDepth, doc.IsDirty);
                Assert.Equal(lastVersion, doc.Version);
                CompareStructure(seed, r, doc, oracle);
            }

            if (seed % 25 == 0)
            {
                crossThreadChecks++;
                // Non-owner threads read the published immutable snapshot: it must equal the live document exactly.
                var text = oracle.GetText();
                var other = OnOtherThread(() => (doc.GetText(), doc.LineCount, doc.Length, doc.Version, doc.GetLineText(0), doc.PositionAt(text.Length / 2), doc.OffsetAt(new TextPosition(1, 1))));
                Assert.Equal(text, other.Item1);
                Assert.Equal(oracle.LineCount, other.Item2);
                Assert.Equal(oracle.Length, other.Item3);
                Assert.Equal(doc.Version, other.Item4);
                Assert.Equal(oracle.GetLineText(0), other.Item5);
                Assert.Equal(oracle.PositionAt(text.Length / 2), other.Item6);
                Assert.Equal(oracle.OffsetAt(new TextPosition(1, 1)), other.Item7);
            }
        }

        Assert.True(totalOps > Sequences, "the fuzzer must actually perform operations");
        Assert.True(crossThreadChecks > 100);
    }

    private static T OnOtherThread<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                result = f();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("other-thread read failed", error);
        return result;
    }

    private static List<TextEdit> RandomEdits(Random r, InMemoryTextDocument oracle)
    {
        var count = r.Next(1, 5);
        var edits = new List<TextEdit>();
        var len = oracle.Length;
        var offsets = Enumerable.Range(0, count * 2).Select(_ => r.Next(0, len + 1)).OrderBy(x => x).ToArray();
        for (var i = 0; i < count; i++)
        {
            var s = offsets[i * 2];
            var e = r.Next(8) == 0 ? offsets[Math.Max(0, i * 2 - 1)] : offsets[i * 2 + 1]; // sometimes overlap the previous edit
            if (e < s) (s, e) = (e, s);
            var start = Jitter(r, oracle, oracle.PositionAt(s));
            var end = Jitter(r, oracle, oracle.PositionAt(e));
            // a reversed range (end before start) is undefined input for the oracle; clamp it to an empty range
            if (oracle.OffsetAt(end) < oracle.OffsetAt(start)) end = start;
            var text = RandomText(r, 2);
            // avoid no-ops: the oracle records them as changes, TextDocument elides them (documented difference)
            if (start == end && text.Length == 0) continue;
            if (oracle.OffsetAt(start) == oracle.OffsetAt(end) && text.Length == 0) continue;
            edits.Add(new TextEdit(new TextRange(start, end), text));
        }
        return edits;
    }

    // Occasionally produce positions beyond the line/document (must clamp identically to the oracle).
    private static TextPosition Jitter(Random r, InMemoryTextDocument d, TextPosition p) =>
        r.Next(12) switch
        {
            0 => new TextPosition(p.Line, p.Character + r.Next(1, 5)),
            1 => new TextPosition(Math.Max(0, p.Line - 1), p.Character),
            2 => new TextPosition(d.LineCount + 2, 0),
            _ => p,
        };

    private static void CompareStructure(int seed, Random r, EditorDocument doc, InMemoryTextDocument oracle)
    {
        Assert.True(oracle.LineCount == doc.LineCount, $"seed {seed}: LineCount {oracle.LineCount} vs {doc.LineCount}");
        Assert.Equal(oracle.Length, doc.Length);
        for (var i = 0; i < oracle.LineCount; i++)
            Assert.True(oracle.GetLineText(i) == doc.GetLineText(i), $"seed {seed}: line {i}");
        for (var k = 0; k < 4; k++)
        {
            var p = new TextPosition(r.Next(-1, oracle.LineCount + 2), r.Next(-1, 8));
            Assert.Equal(oracle.OffsetAt(p), doc.OffsetAt(p));
            Assert.Equal(oracle.Validate(p), doc.Validate(p));
            var off = r.Next(-1, oracle.Length + 2);
            Assert.Equal(oracle.PositionAt(off), doc.PositionAt(off));
        }
        var a = new TextPosition(r.Next(0, oracle.LineCount), r.Next(0, 6));
        var b = new TextPosition(r.Next(0, oracle.LineCount), r.Next(0, 6));
        if (a > b) (a, b) = (b, a);
        Assert.Equal(oracle.GetText(new TextRange(a, b)), doc.GetText(new TextRange(a, b)));
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
