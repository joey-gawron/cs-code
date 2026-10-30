using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.ProfileHost;

/// <summary>
/// The GC matrix's synthetic workload (MUST 3): an editor's allocation and retention pattern without the UI, deterministic (fixed seed, fixed
/// amount of work, so CPU time compares across configurations). Per step: (1) documents of varied sizes (1 KB … 2 MB; up to 40 open, the oldest
/// closed) edited a few times each through <see cref="InMemoryTextDocument"/>; (2) a token-state-like graph per open document (an object per line
/// holding a small token array and a parent state, rebuilt from the first edited line on, as a tokenizer does); (3) many small short-lived strings
/// (line splits, number formatting, concatenations); (4) UI-like churn: event args, closures, small lists and diagnostics published and cleared,
/// as a frame's worth of view-model updates.
/// </summary>
public static class SyntheticWorkload
{
    private sealed class LineState(LineState? parent, int[] tokens)
    {
        public LineState? Parent { get; } = parent;
        public int[] Tokens { get; } = tokens;
    }

    private sealed class OpenDoc(InMemoryTextDocument doc)
    {
        public InMemoryTextDocument Doc { get; } = doc;
        public List<LineState> States { get; } = new();
    }

    public static long Run(int steps, int seed = 42)
    {
        var random = new Random(seed);
        var open = new LinkedList<OpenDoc>();
        var diagnostics = new DiagnosticService();
        var emitter = new Emitter<TextDocumentChangedEventArgs>();
        long checksum = 0;
        using var sub = emitter.Subscribe(e => checksum += e.Version);
        int[] sizes = [1024, 8 * 1024, 64 * 1024, 256 * 1024, 2 * 1024 * 1024];
        for (var step = 0; step < steps; step++)
        {
            // (1) open a document every few steps; keep at most 40
            if (step % 5 == 0)
            {
                var size = sizes[random.Next(sizes.Length) % (step % 50 == 0 ? sizes.Length : sizes.Length - 1)];
                var doc = new InMemoryTextDocument(new Uri($"file:///synthetic/{step}.cs"), "csharp", Text(size, random));
                var entry = new OpenDoc(doc);
                Tokenize(entry, 0, random);
                open.AddLast(entry);
                if (open.Count > 40) open.RemoveFirst();
            }
            // edits to a random open document, then re-tokenize from the edited line
            if (open.Count > 0)
            {
                var target = open.ElementAt(random.Next(open.Count));
                for (var e = 0; e < 3; e++)
                {
                    var line = random.Next(target.Doc.LineCount);
                    target.Doc.ApplyEdits([TextEdit.Insert(new TextPosition(line, 0), "x" + step + " ")]);
                    Tokenize(target, line, random);
                }
                emitter.Fire(new TextDocumentChangedEventArgs(target.Doc, target.Doc.Version, []));
            }
            // (3) short-lived strings
            for (var i = 0; i < 2000; i++)
            {
                var s = string.Concat("item", i.ToString(System.Globalization.CultureInfo.InvariantCulture), ":", (step * i).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
                checksum += s.Length;
            }
            if (open.First is { } first)
                foreach (var part in first.Value.Doc.GetLineText(step % first.Value.Doc.LineCount).Split(' ')) checksum += part.Length;
            // (4) UI-like churn: a frame's worth of small objects, closures and a diagnostics publish/clear
            var items = new List<object>(64);
            for (var i = 0; i < 64; i++)
            {
                var captured = i;
                Func<int> f = () => captured + step;
                items.Add(new KeyValuePair<string, Func<int>>("row" + i, f));
            }
            checksum += items.Count;
            var uri = new Uri($"file:///synthetic/{step % 20}.cs");
            diagnostics.Set(uri, "synthetic", [new Diagnostic(TextRange.At(step % 100, 0), "message " + step, DiagnosticSeverity.Warning)]);
            if (step % 20 == 19) diagnostics.Clear("synthetic");
        }
        return checksum;
    }

    private static void Tokenize(OpenDoc doc, int fromLine, Random random)
    {
        var lines = doc.Doc.LineCount;
        if (doc.States.Count > fromLine) doc.States.RemoveRange(fromLine, doc.States.Count - fromLine);
        var parent = fromLine > 0 && doc.States.Count >= fromLine ? doc.States[fromLine - 1] : null;
        // a tokenizer only re-tokenizes what is visible plus a bit: at most 400 lines per pass
        for (var line = fromLine; line < Math.Min(lines, fromLine + 400); line++)
        {
            var tokens = new int[2 * (1 + random.Next(8))];
            for (var t = 0; t < tokens.Length; t++) tokens[t] = random.Next(1000);
            parent = new LineState(parent, tokens);
            doc.States.Add(parent);
        }
    }

    private static string Text(int size, Random random)
    {
        var sb = new System.Text.StringBuilder(size + 100);
        while (sb.Length < size)
        {
            sb.Append("    public int Field").Append(random.Next(100_000)).Append(" = ").Append(random.Next()).Append("; // comment text here\n");
        }
        return sb.ToString();
    }
}
