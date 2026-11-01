using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>How the source is parsed: VS Code's <c>markdown.preview.breaks</c> and <c>markdown.preview.linkify</c>.</summary>
public sealed record MarkdownPreviewOptions(bool Breaks = false, bool Linkify = true)
{
    public static readonly MarkdownPreviewOptions Default = new();
}

/// <summary>
/// Identity of a top-level block for reuse between two parses: its kind, the length of its source text and a 64-bit hash of it. Two blocks with
/// the same key render the same controls, so an update after a keystroke rebuilds only the block that changed (the text itself is not kept: a
/// key costs 24 bytes whatever the block's size).
/// </summary>
public readonly record struct PreviewBlockKey(Type Kind, int Length, ulong Hash);

/// <summary>One top-level block of a parsed document: the Markdig node, the source lines it spans (0-based, inclusive) and its reuse key.</summary>
public sealed class PreviewBlock
{
    internal PreviewBlock(Block block, int startLine, int endLine, PreviewBlockKey key)
    {
        Block = block;
        StartLine = startLine;
        EndLine = endLine;
        Key = key;
    }

    /// <summary>The Markdig node. Never mutated after the parse: the worker hands the whole tree to the UI thread, which only reads it.</summary>
    public Block Block { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public PreviewBlockKey Key { get; }
    public int LineCount => EndLine - StartLine + 1;
}

/// <summary>
/// The result of one parse (worker lane): the top-level blocks in source order, the TextMate spans of every fenced or indented code block, and the
/// heading anchors (GitHub's ids: <c>## Getting started</c> is <c>#getting-started</c>). Immutable once published.
/// </summary>
public sealed class PreviewDocument
{
    internal PreviewDocument(int version, IReadOnlyList<PreviewBlock> blocks, IReadOnlyDictionary<LeafBlock, IReadOnlyList<CodeSpan>> codeSpans,
        IReadOnlyDictionary<string, int> anchors, int lineCount, bool truncated)
    {
        Version = version;
        Blocks = blocks;
        CodeSpans = codeSpans;
        Anchors = anchors;
        LineCount = lineCount;
        Truncated = truncated;
    }

    public static readonly PreviewDocument Empty = new(0, [], new Dictionary<LeafBlock, IReadOnlyList<CodeSpan>>(), new Dictionary<string, int>(), 0, false);

    /// <summary>The source document's version this was parsed from.</summary>
    public int Version { get; }
    public IReadOnlyList<PreviewBlock> Blocks { get; }
    public IReadOnlyDictionary<LeafBlock, IReadOnlyList<CodeSpan>> CodeSpans { get; }
    /// <summary>Heading id (without '#') → index of the top-level block that holds the heading.</summary>
    public IReadOnlyDictionary<string, int> Anchors { get; }
    public int LineCount { get; }
    /// <summary>True when the source was longer than <see cref="MarkdownPreviewParser.MaxLength"/> and only its start was rendered.</summary>
    public bool Truncated { get; }

    /// <summary>The block that shows <paramref name="line"/> (0-based): the last block starting at or before it; -1 when there is none.</summary>
    public int BlockIndexAtLine(double line)
    {
        int lo = 0, hi = Blocks.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Blocks[mid].StartLine <= line) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 && Blocks.Count > 0 ? 0 : found;
    }
}

/// <summary>Colours the code of a fenced block (<paramref name="info"/>: its info string's first word; null for an indented block).</summary>
public delegate IReadOnlyList<CodeSpan>? CodeColorizer(string? info, string code, CancellationToken ct);

/// <summary>
/// Parses Markdown into a <see cref="PreviewDocument"/> with Markdig: CommonMark plus GitHub's extensions (pipe tables, task lists,
/// strikethrough, autolinks, heading ids) and precise source positions. Pure and thread-safe; meant for the editor worker. Code blocks are
/// coloured through a <see cref="CodeColorizer"/>; the spans of a block whose key the previous document already had are reused instead.
/// </summary>
public static class MarkdownPreviewParser
{
    /// <summary>Longer sources are cut (with a note in the preview): 4 M characters is a 4-8 MB string, far beyond any real README.</summary>
    public const int MaxLength = 4_000_000;

    private static readonly MarkdownPipeline[] Pipelines = new MarkdownPipeline[4];

    internal static MarkdownPipeline Pipeline(MarkdownPreviewOptions options)
    {
        var index = (options.Breaks ? 1 : 0) | (options.Linkify ? 2 : 0);
        if (Volatile.Read(ref Pipelines[index]) is { } built) return built;
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseTaskLists()
            .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UsePreciseSourceLocation();
        if (options.Linkify) builder.UseAutoLinks();
        if (options.Breaks) builder.UseSoftlineBreakAsHardlineBreak();
        var pipeline = builder.Build();
        Volatile.Write(ref Pipelines[index], pipeline);
        return pipeline;
    }

    public static PreviewDocument Parse(string text, int version, MarkdownPreviewOptions? options = null, CodeColorizer? colorize = null,
        PreviewDocument? previous = null, CancellationToken ct = default)
    {
        options ??= MarkdownPreviewOptions.Default;
        var truncated = text.Length > MaxLength;
        if (truncated) text = text[..MaxLength];
        var lineStarts = LineStarts(text);
        var doc = Markdig.Markdown.Parse(text, Pipeline(options));
        ct.ThrowIfCancellationRequested();

        // Spans the previous document already computed, by block key: an edit elsewhere does not re-tokenise a 200-line code block.
        Dictionary<PreviewBlockKey, IReadOnlyList<CodeSpan>>? reusable = null;
        if (previous is not null && colorize is not null)
        {
            foreach (var b in previous.Blocks)
                if (b.Block is LeafBlock leaf && previous.CodeSpans.TryGetValue(leaf, out var s)) (reusable ??= new())[b.Key] = s;
        }

        var blocks = new List<PreviewBlock>(doc.Count);
        var spans = new Dictionary<LeafBlock, IReadOnlyList<CodeSpan>>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in doc)
        {
            ct.ThrowIfCancellationRequested();
            if (block is LinkReferenceDefinitionGroup or BlankLineBlock) continue; // definitions are resolved into the links; nothing to show
            var start = Math.Clamp(block.Span.Start, 0, text.Length);
            var end = Math.Clamp(block.Span.End, start - 1, text.Length - 1);
            var length = end - start + 1;
            var key = new PreviewBlockKey(block.GetType(), length, Hash(text.AsSpan(start, Math.Max(0, length))));
            var startLine = LineOf(lineStarts, start);
            var endLine = Math.Max(startLine, LineOf(lineStarts, Math.Max(start, end)));
            var item = new PreviewBlock(block, startLine, endLine, key);
            var index = blocks.Count;
            blocks.Add(item);
            Collect(block, index, anchors, spans, colorize, reusable, key, ct);
        }
        return new PreviewDocument(version, blocks, spans, anchors, lineStarts.Length, truncated);
    }

    private static void Collect(Block block, int index, Dictionary<string, int> anchors, Dictionary<LeafBlock, IReadOnlyList<CodeSpan>> spans,
        CodeColorizer? colorize, Dictionary<PreviewBlockKey, IReadOnlyList<CodeSpan>>? reusable, PreviewBlockKey topKey, CancellationToken ct)
    {
        switch (block)
        {
            case HeadingBlock h when h.GetAttributes().Id is { Length: > 0 } id:
                anchors.TryAdd(id, index);
                break;
            case CodeBlock code when colorize is not null:
            {
                // A top-level code block can take the previous parse's spans; nested ones (in lists, quotes) are cheap to redo.
                if (block.Parent is MarkdownDocument && reusable is not null && reusable.TryGetValue(topKey, out var known))
                {
                    spans[code] = known;
                    break;
                }
                var info = code is FencedCodeBlock f ? FirstWord(f.Info) : null;
                if (colorize(info, CodeText(code), ct) is { Count: > 0 } colored) spans[code] = colored;
                break;
            }
            case ContainerBlock container:
                foreach (var child in container) Collect(child, index, anchors, spans, colorize, reusable, topKey, ct);
                break;
        }
    }

    /// <summary>The code of a fenced or indented block, lines joined with '\n' (no trailing newline).</summary>
    public static string CodeText(LeafBlock code)
    {
        var lines = code.Lines;
        if (lines.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var slice = lines.Lines[i].Slice;
            sb.Append(slice.AsSpan());
        }
        return sb.ToString();
    }

    private static string? FirstWord(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        var trimmed = info.Trim();
        var space = trimmed.IndexOfAny([' ', '\t', '{']);
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n') starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    private static int LineOf(int[] starts, int offset)
    {
        var i = Array.BinarySearch(starts, offset);
        return i >= 0 ? i : ~i - 1;
    }

    // FNV-1a, 64 bit: stable, allocation-free; a collision only costs a stale block until the next edit of it.
    private static ulong Hash(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
