using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;

namespace CsCode.Editor.Features;

/// <summary>
/// VS Code's default document colour provider (<c>editor.defaultColorDecorators</c>): finds CSS colours — <c>#rgb</c>, <c>#rgba</c>,
/// <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>/<c>rgba()</c>, <c>hsl()</c>/<c>hsla()</c> — in the text of any document, and offers
/// <c>rgb()</c>, <c>hsl()</c> and hex presentations of a picked colour. It is the colour decorators' own fallback, not registered in the
/// language-feature registry: with <c>auto</c> it serves documents no language server finds colours in.
/// <para>Threading: the scan reads the document's immutable snapshot on the editor worker, in slices of about 64K characters or 6 ms, so a
/// big file never holds the worker (or anything else) for long; cancellation is checked between slices.</para>
/// </summary>
internal sealed class DefaultColorProvider(EditorWorker worker) : IDocumentColorProvider
{
    /// <summary>At most this many colours per document (the decorators' own limit is lower: <c>editor.colorDecoratorsLimit</c>).</summary>
    public const int MaxColors = 10_000;
    private const int SliceChars = 64 * 1024;
    private static readonly TimeSpan SliceTime = TimeSpan.FromMilliseconds(6);

    public async ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct)
    {
        if (document is not EditorDocument editor) return Scan(document.GetText(), document, ct); // a plain text document: already off the UI thread
        var snapshot = editor.Snapshot();
        var found = new List<ColorFormats.Found>();
        var line = 0;
        while (line < snapshot.LineCount && found.Count < MaxColors)
        {
            var from = line;
            line = await worker.RunAsync(WorkPriority.Background, token => ScanSlice(snapshot, from, found, token), ct).ConfigureAwait(false);
        }
        return found.Select(f => new ColorInformation(new TextRange(snapshot.PositionAt(f.Start), snapshot.PositionAt(f.Start + f.Length)), f.R, f.G, f.B, f.A)).ToArray();
    }

    /// <summary>Scans whole lines from <paramref name="line"/> for about one slice; returns the next line to scan.</summary>
    private static int ScanSlice(DocumentSnapshot snapshot, int line, List<ColorFormats.Found> found, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (line < snapshot.LineCount && found.Count < MaxColors && sw.Elapsed < SliceTime)
        {
            ct.ThrowIfCancellationRequested();
            var start = snapshot.LineStart(line);
            var end = line;
            while (end + 1 < snapshot.LineCount && snapshot.LineStart(end + 1) - start < SliceChars) end++;
            var stop = end + 1 < snapshot.LineCount ? snapshot.LineStart(end + 1) : snapshot.Length;
            ColorFormats.Find(snapshot.GetText(start, stop - start), MaxColors, found, start);
            line = end + 1;
        }
        return line;
    }

    private static IReadOnlyList<ColorInformation> Scan(string text, ITextDocument document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var found = new List<ColorFormats.Found>();
        ColorFormats.Find(text, MaxColors, found);
        return found.Select(f => new ColorInformation(new TextRange(document.PositionAt(f.Start), document.PositionAt(f.Start + f.Length)), f.R, f.G, f.B, f.A)).ToArray();
    }

    public ValueTask<IReadOnlyList<ColorPresentation>?> ProvideColorPresentationsAsync(ITextDocument document, ColorInformation color, CancellationToken ct)
    {
        // the case of the hex written now is kept (#FF0000 stays upper case)
        var current = color.Range.IsEmpty ? null : document.GetText(color.Range);
        var labels = ColorFormats.Presentations(color.Red, color.Green, color.Blue, color.Alpha, ColorFormats.IsUpperHex(current));
        return ValueTask.FromResult<IReadOnlyList<ColorPresentation>?>([.. labels.Select(l => new ColorPresentation(l, new TextEdit(color.Range, l)))]);
    }
}
