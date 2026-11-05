using System.Text;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>
/// The lines of one file a block of results needs (its matching lines and their context), read once off the UI thread: an open document from
/// memory (what the engine searched), any other file through <see cref="IFileSystem"/> decoded the way the engine decoded it. The engine's previews
/// are trimmed and clipped, so the full lines come from the file itself.
/// </summary>
internal sealed class FileLines
{
    private readonly Dictionary<int, string> _lines;

    private FileLines(Dictionary<int, string> lines, int lineCount)
    {
        _lines = lines;
        LineCount = lineCount;
    }

    /// <summary>
    /// The file's line count, not counting the empty "line" after a final line break (a context line must be a line of the file, as ripgrep's
    /// are in VS Code). Lines past it are never shown.
    /// </summary>
    public int LineCount { get; }

    public string? this[int line] => _lines.GetValueOrDefault(line);

    public static FileLines Empty { get; } = new(new Dictionary<int, string>(), 0);

    /// <summary>The <paramref name="wanted"/> lines (sorted, 0-based) of <paramref name="file"/>; null when the file cannot be read any more.</summary>
    public static async Task<FileLines?> ReadAsync(FileMatches file, IReadOnlyList<int> wanted, IDocumentManager? documents, IFileSystem? fileSystem, CancellationToken ct)
    {
        if (documents?.Get(file.Uri) is { } doc)
        {
            // An open document: the text the engine searched (a dirty buffer is never the disk copy). Its snapshot reads are thread-safe.
            var count = doc.LineCount;
            if (count > 1 && doc.GetLineText(count - 1).Length == 0) count--; // the text ends with a line break
            var lines = new Dictionary<int, string>(wanted.Count);
            foreach (var l in wanted)
                if (l >= 0 && l < count) lines[l] = doc.GetLineText(l);
            return new FileLines(lines, count);
        }
        if (fileSystem is null) return null;
        byte[] bytes;
        try { bytes = await fileSystem.ReadFileAsync(file.Uri, ct).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
        return Extract(Decode(bytes, file.Encoding), wanted);
    }

    /// <summary>Decodes like the engine: its encoding name when it reported one, else BOM, else UTF-8 (invalid bytes replaced).</summary>
    internal static string Decode(byte[] bytes, string? encoding)
    {
        ReadOnlySpan<byte> b = bytes;
        if (encoding == "latin1") return Encoding.Latin1.GetString(b);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(b[3..]);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(b[2..]);
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(b[2..]);
        return Encoding.UTF8.GetString(b);
    }

    /// <summary>Picks the <paramref name="wanted"/> (sorted) lines out of <paramref name="text"/>; lines end at \n, \r\n or \r as in the engine.</summary>
    internal static FileLines Extract(string text, IReadOnlyList<int> wanted)
    {
        var lines = new Dictionary<int, string>(wanted.Count);
        var span = text.AsSpan();
        var line = 0;
        var w = 0;
        var pos = 0;
        while (true)
        {
            var rest = span[pos..];
            var idx = rest.IndexOfAny('\r', '\n');
            var end = idx < 0 ? rest.Length : idx;
            while (w < wanted.Count && wanted[w] < line) w++;
            if (w < wanted.Count && wanted[w] == line) lines[line] = new string(rest[..end]);
            if (idx < 0) break;
            pos += idx + 1;
            if (rest[idx] == '\r' && idx + 1 < rest.Length && rest[idx + 1] == '\n') pos++;
            line++;
            if (w >= wanted.Count) { line += CountBreaks(span[pos..]); break; } // nothing more is wanted: just count the remaining lines
        }
        var count = line + 1;
        if (count > 1 && span.Length > 0 && span[^1] is '\n' or '\r') count--; // the text ends with a line break: no empty last line
        lines.Remove(count);
        return new FileLines(lines, count);
    }

    private static int CountBreaks(ReadOnlySpan<char> s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n') n++;
            else if (s[i] == '\r')
            {
                n++;
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
        }
        return n;
    }
}

/// <summary>One file's results as lines of a search editor (without the path line), and how many matches they show.</summary>
internal sealed record ResultBlock(IReadOnlyList<string> Lines, int Matches);

/// <summary>Turns one file's matches into its block: matching lines (<c>  N: text</c>) and context lines (<c>  N  text</c>).</summary>
internal static class ResultBlockBuilder
{
    /// <summary>The 0-based lines the block of <paramref name="file"/> shows, sorted: every line a match covers plus <paramref name="context"/> lines around them.</summary>
    public static IReadOnlyList<int> LinesOf(FileMatches file, int context)
    {
        var set = new SortedSet<int>();
        foreach (var m in file.Matches)
        {
            for (var l = Math.Max(0, m.Range.Start.Line - context); l <= m.Range.End.Line + context; l++) set.Add(l);
        }
        return [.. set];
    }

    /// <summary>
    /// The block text. <paramref name="lines"/> null (the file could not be read again): the engine's previews stand in for the matching lines.
    /// With context, an empty line separates runs of lines that are not adjacent (VS Code does the same).
    /// </summary>
    public static ResultBlock Build(FileMatches file, int context, FileLines? lines)
    {
        // match lines and the first match column on each (long lines are clipped around it)
        var matchLines = new SortedDictionary<int, int>();
        foreach (var m in file.Matches)
        {
            var endLine = Math.Max(m.Range.Start.Line, m.Range.End.Line);
            if (m.Range.End.Line > m.Range.Start.Line && m.Range.End.Character == 0) endLine--; // a match ending at a line break does not show the next line
            for (var l = m.Range.Start.Line; l <= endLine; l++)
            {
                var col = l == m.Range.Start.Line ? m.Range.Start.Character : 0;
                if (!matchLines.TryGetValue(l, out var existing) || col < existing) matchLines[l] = col;
            }
        }

        var shown = new SortedDictionary<int, bool>(); // line -> is a match line
        foreach (var l in matchLines.Keys) shown[l] = true;
        if (lines is not null && context > 0)
        {
            foreach (var l in matchLines.Keys)
                for (var c = Math.Max(0, l - context); c <= l + context; c++)
                    if (c < lines.LineCount && !shown.ContainsKey(c)) shown[c] = false;
        }

        var width = shown.Count == 0 ? 1 : (shown.Keys.Max() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        var previews = lines is null ? file.Matches.GroupBy(m => m.Line).ToDictionary(g => g.Key, g => g.First().Preview) : null;
        var output = new List<string>(shown.Count + 4);
        var last = -2;
        foreach (var (line, isMatch) in shown)
        {
            var text = lines?[line] ?? previews?.GetValueOrDefault(line);
            if (text is null) continue;
            if (context > 0 && last >= 0 && line != last + 1) output.Add("");
            output.Add(SearchEditorFormat.ResultLineText(line + 1, width, isMatch, SearchEditorFormat.ClipLine(text, isMatch ? matchLines[line] : null)));
            last = line;
        }
        return new ResultBlock(output, file.Matches.Count);
    }
}

/// <summary>
/// Orders the files of a search editor's results like VS Code (by workspace folder, then path segment by segment, ignoring case) and keeps the line
/// count of every block written so far, so a block that arrives later can be inserted where it belongs. Owned by one run's worker.
/// </summary>
internal sealed class ResultLayout
{
    /// <summary>A file's block in the editor: path line, result lines, one empty line.</summary>
    internal sealed class Block(Uri uri, int folder, string[] segments, string label)
    {
        public Uri Uri { get; } = uri;
        public int Folder { get; } = folder;
        public string[] Segments { get; } = segments;
        public string Label { get; } = label;
        public int Lines { get; set; }
        public int Matches { get; set; }
    }

    private readonly IReadOnlyList<Uri> _folders;
    private readonly List<Block> _blocks = new();            // applied, sorted
    private readonly Dictionary<Uri, Block> _byUri = new();
    private readonly List<(Block Block, List<string> Lines)> _pendingNew = new();
    private readonly Dictionary<Block, List<string>> _pendingMore = new();
    private int _pendingChars;

    public ResultLayout(IReadOnlyList<Uri> folders) => _folders = folders;

    public int Files => _byUri.Count;
    public int Matches { get; private set; }
    public int PendingChars => _pendingChars;
    public bool HasPending => _pendingNew.Count > 0 || _pendingMore.Count > 0;

    /// <summary>Queues <paramref name="block"/> for the next chunk: a new file's block, or more lines for a file that already has one.</summary>
    public void Add(Uri uri, ResultBlock block)
    {
        if (block.Lines.Count == 0) return;
        Matches += block.Matches;
        foreach (var l in block.Lines) _pendingChars += l.Length + 1;
        if (_byUri.TryGetValue(uri, out var existing))
        {
            existing.Matches += block.Matches;
            var pendingNew = _pendingNew.FindIndex(p => ReferenceEquals(p.Block, existing));
            if (pendingNew >= 0) _pendingNew[pendingNew].Lines.AddRange(block.Lines);
            else
            {
                if (!_pendingMore.TryGetValue(existing, out var more)) _pendingMore[existing] = more = new List<string>();
                more.AddRange(block.Lines);
            }
            return;
        }
        var label = SearchResultPaths.LabelFor(uri, _folders);
        var (folder, segments) = SortKey(uri);
        var b = new Block(uri, folder, segments, label) { Matches = block.Matches };
        _byUri[uri] = b;
        _pendingNew.Add((b, [.. block.Lines]));
        _pendingChars += label.Length + 3;
    }

    /// <summary>
    /// The queued blocks as inserts against the text written so far (0-based lines counted from the first block's line; several inserts at the
    /// same line are merged in order), then marks them written.
    /// </summary>
    public IReadOnlyList<(int Line, string Text)> TakeInserts()
    {
        if (!HasPending) return [];
        // where each written block starts
        var starts = new int[_blocks.Count + 1];
        for (var i = 0; i < _blocks.Count; i++) starts[i + 1] = starts[i] + _blocks[i].Lines;

        var inserts = new SortedDictionary<int, StringBuilder>();
        void At(int line, string text)
        {
            if (!inserts.TryGetValue(line, out var sb)) inserts[line] = sb = new StringBuilder();
            sb.Append(text);
        }

        // more lines for a written block: before its trailing empty line
        foreach (var (block, more) in _pendingMore)
        {
            var index = _blocks.IndexOf(block);
            if (index < 0) continue;
            At(starts[index] + block.Lines - 1, Join(more));
            block.Lines += more.Count;
        }

        // new blocks: at the start of the first written block that sorts after them (merged in sort order when several land on one line)
        _pendingNew.Sort((a, b) => Compare(a.Block, b.Block));
        foreach (var (block, lines) in _pendingNew)
        {
            var index = UpperBound(block);
            block.Lines = lines.Count + 2;
            At(starts[index], block.Label + ":\n" + Join(lines) + "\n");
        }
        foreach (var (block, _) in _pendingNew) _blocks.Insert(UpperBound(block), block);
        _pendingNew.Clear();
        _pendingMore.Clear();
        _pendingChars = 0;
        return inserts.Select(kv => (kv.Key, kv.Value.ToString())).ToArray();
    }

    /// <summary>The blocks in document order (tests).</summary>
    internal IReadOnlyList<Block> Blocks => _blocks;

    private static string Join(List<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    /// <summary>The index of the first written block that sorts after <paramref name="block"/>.</summary>
    private int UpperBound(Block block)
    {
        int lo = 0, hi = _blocks.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Compare(_blocks[mid], block) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private (int Folder, string[] Segments) SortKey(Uri uri)
    {
        for (var i = 0; i < _folders.Count; i++)
            if (SearchResultPaths.Relative(_folders[i], uri) is { } rel) return (i, rel.Split('/'));
        var path = uri.IsFile ? uri.LocalPath.Replace('\\', '/') : uri.ToString();
        return (_folders.Count, path.Split('/'));
    }

    /// <summary>VS Code's default order: folder, then the paths compared segment by segment (case-insensitive, then ordinal to be total).</summary>
    internal static int Compare(Block a, Block b)
    {
        var c = a.Folder.CompareTo(b.Folder);
        if (c != 0) return c;
        var n = Math.Min(a.Segments.Length, b.Segments.Length);
        for (var i = 0; i < n; i++)
        {
            c = string.Compare(a.Segments[i], b.Segments[i], StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.CompareOrdinal(a.Segments[i], b.Segments[i]);
            if (c != 0) return c;
        }
        c = a.Segments.Length.CompareTo(b.Segments.Length);
        return c != 0 ? c : string.CompareOrdinal(a.Uri.ToString(), b.Uri.ToString());
    }
}
