using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.TreeSitter.Queries;

namespace CsCode.Editor.TreeSitter.Highlighting;

/// <summary>A node of the syntax tree as character offsets [<paramref name="Start"/>, <paramref name="End"/>) and its node type.</summary>
public readonly record struct SyntaxSpan(int Start, int End, string Type);

/// <summary>
/// A definition found in the tree: <paramref name="Kind"/> is the capture's last part (<c>@definition.function</c> → "function"), the name is
/// [<paramref name="NameStart"/>, <paramref name="NameEnd"/>), the whole definition [<paramref name="Start"/>, <paramref name="End"/>).
/// </summary>
public readonly record struct SyntaxSymbol(string Kind, int NameStart, int NameEnd, int Start, int End);

/// <summary>
/// The STRUCTURAL side of a document's tree (docs/TREE-SITTER.md, "Structure"): what Expand/Shrink Selection, folding, the outline and
/// breadcrumbs without a language server, and sticky scroll (through folding) read. Same confinement as the rest of the class (the syntax lane), the
/// same flight-recorder scope and the same query guard (<see cref="MaxQueryTime"/>). All results are character offsets into the text of the
/// current tree; the caller passes that text (it must be the tree's) and maps offsets to lines.
/// </summary>
public sealed unsafe partial class SyntaxDocument
{
    /// <summary>Most results a structural query returns (a pathological file cannot hand the editor an unbounded list).</summary>
    public const int MaxStructuralResults = 20_000;

    /// <summary>
    /// The named nodes that contain [<paramref name="start"/>, <paramref name="end"/>), smallest first, each strictly larger than the one before:
    /// "parent node" for Expand Selection. For an empty range (a caret) at the end of a word, the word to its left counts (as VS Code does).
    /// Empty without a tree.
    /// </summary>
    public IReadOnlyList<SyntaxSpan> EnclosingNodes(ITextInput text, int start, int end)
    {
        ThrowIfDisposed();
        var result = new List<SyntaxSpan>();
        if (_tree == IntPtr.Zero) return result;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (start == end && start > 0 && IsWordChar(CharAt(text, start - 1)) && (start == text.Length || !IsWordChar(CharAt(text, start)))) start = end = start - 1;
        using (Record(Language))
        {
            var root = TsApi.ts_tree_root_node(_tree);
            var node = TsApi.ts_node_descendant_for_byte_range(root, (uint)start * 2, (uint)Math.Max(start, end) * 2);
            var lastStart = -1;
            var lastEnd = -1;
            for (; !node.IsNull && result.Count < 256; node = TsApi.ts_node_parent(node))
            {
                if (TsApi.ts_node_is_named(node) == 0) continue;
                var ns = (int)(TsApi.ts_node_start_byte(node) / 2);
                var ne = (int)Math.Min(TsApi.ts_node_end_byte(node) / 2, (uint)text.Length);
                if (ns > start || ne < end) continue; // a node beside a zero-width range at a boundary
                if (ns == lastStart && ne == lastEnd) continue; // a wrapper node with the same extent adds no step
                result.Add(new SyntaxSpan(ns, ne, Language.SymbolName(TsApi.ts_node_symbol(node), node)));
                lastStart = ns;
                lastEnd = ne;
            }
        }
        return result;
    }

    /// <summary>
    /// The nodes the grammar's <c>folds.scm</c> marks foldable (<c>@fold</c>), in document order, at most <see cref="MaxStructuralResults"/>.
    /// Null when the grammar has no folds query (the caller keeps its own folding).
    /// </summary>
    public IReadOnlyList<SyntaxSpan>? FoldNodes(ITextInput text)
    {
        ThrowIfDisposed();
        if (Language.Query("folds", out _) is not { } query) return null;
        var result = new List<SyntaxSpan>();
        if (_tree == IntPtr.Zero) return result;
        var fold = IndexOfCapture(query, "fold");
        if (fold < 0) return result;
        RunQuery(query, text, 0, text.Length, (in TSQueryMatch m, PatternInfo pattern) =>
        {
            // a quantified capture ((import_statement)+ @fold) is ONE fold over all its nodes
            SyntaxSpan? span = null;
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                if ((int)cap.Index != fold) continue;
                var s = Span(cap.Node, text.Length);
                span = span is { } a ? a with { Start = Math.Min(a.Start, s.Start), End = Math.Max(a.End, s.End) } : s;
            }
            if (span is { } found) result.Add(found);
            return result.Count < MaxStructuralResults;
        });
        result.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        return result;
    }

    /// <summary>
    /// The definitions in the document for an outline: from the first of these queries the grammar has — CsCode's own <c>symbols.scm</c>, the
    /// grammar's <c>tags.scm</c> (both in the tags format: <c>@name</c> inside <c>@definition.&lt;kind&gt;</c>), or nvim-treesitter's
    /// <c>locals.scm</c> (<c>@local.definition.&lt;kind&gt;</c> on the name; the definition is the name's parent node, and only kinds an outline
    /// shows are kept). In document order; empty without a tree; null when the grammar has none of the three.
    /// </summary>
    public IReadOnlyList<SyntaxSymbol>? SymbolNodes(ITextInput text) => SymbolNodes(text, SymbolSources);

    /// <summary>The query kinds <see cref="SymbolNodes(ITextInput)"/> tries, in order.</summary>
    public static readonly string[] SymbolSources = ["symbols", "tags", "locals"];

    /// <summary><see cref="SymbolNodes(ITextInput)"/> from the first of <paramref name="sources"/> the grammar has (tests pick one).</summary>
    public IReadOnlyList<SyntaxSymbol>? SymbolNodes(ITextInput text, IReadOnlyList<string> sources)
    {
        ThrowIfDisposed();
        var result = new List<SyntaxSymbol>();
        TsQuery? query = null;
        string? source = null;
        foreach (var kind in sources)
        {
            if (Language.Query(kind, out _) is not { } q) continue;
            query = q;
            source = kind;
            break;
        }
        if (query is null) return null;
        if (source == "locals") CollectLocals(query, text, result);
        else CollectTags(query, text, result);
        result.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        // the same definition matched by two patterns (e.g. a generic and a specific one) is listed once
        for (var i = result.Count - 1; i > 0; i--)
            if (result[i].Start == result[i - 1].Start && result[i].End == result[i - 1].End && result[i].NameStart == result[i - 1].NameStart) result.RemoveAt(i);
        return result;
    }

    /// <summary>The query kind of the syntax text objects (nvim-treesitter-textobjects' <c>textobjects.scm</c>, bundled per grammar).</summary>
    public const string TextObjectsQuery = "textobjects";

    /// <summary>
    /// The ranges the grammar's <c>textobjects.scm</c> captures as <paramref name="capture"/> (<c>function.outer</c>, <c>class.inner</c>,
    /// <c>parameter.outer</c>…; Vim's <c>af</c>, <c>ic</c>, <c>aa</c>), in document order (by start, larger first), duplicates removed, at most
    /// <see cref="MaxStructuralResults"/>. As nvim-treesitter-textobjects reads them: every node one match captures under that name belongs to ONE
    /// range, from the first to the last (<c>"," @parameter.outer . (_) @parameter.inner @parameter.outer</c> is the argument with the comma
    /// before it), after the pattern's <c>#offset!</c>. Null when the grammar has no textobjects query; empty without a tree.
    /// </summary>
    public IReadOnlyList<SyntaxSpan>? TextObjectRanges(ITextInput text, string capture)
    {
        ThrowIfDisposed();
        if (Language.Query(TextObjectsQuery, out _) is not { } query) return null;
        var result = new List<SyntaxSpan>();
        if (_tree == IntPtr.Zero) return result;
        var index = IndexOfCapture(query, capture);
        if (index < 0) return result;
        RunQuery(query, text, 0, text.Length, (in TSQueryMatch m, PatternInfo pattern) =>
        {
            int start = int.MaxValue, end = -1;
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                if ((int)cap.Index != index) continue;
                var cs = (int)(TsApi.ts_node_start_byte(cap.Node) / 2);
                var ce = (int)Math.Min(TsApi.ts_node_end_byte(cap.Node) / 2, (uint)text.Length);
                if (pattern.Offsets is { } offsets && offsets.TryGetValue(index, out var d)) (cs, ce) = Offset(cs, ce, d);
                start = Math.Min(start, cs);
                end = Math.Max(end, ce);
            }
            if (end > start) result.Add(new SyntaxSpan(start, end, capture));
            return result.Count < MaxStructuralResults;
        });
        result.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        for (var i = result.Count - 1; i > 0; i--)
            if (result[i].Start == result[i - 1].Start && result[i].End == result[i - 1].End) result.RemoveAt(i);
        return result;
    }

    /// <summary>Kinds of <c>locals.scm</c> definitions an outline shows (variables, parameters, fields and imports are left out).</summary>
    private static readonly HashSet<string> OutlineLocalKinds = new(StringComparer.Ordinal)
    {
        "function", "method", "type", "class", "interface", "namespace", "module", "macro", "enum", "constructor", "struct", "trait", "impl",
    };

    private void CollectTags(TsQuery query, ITextInput text, List<SyntaxSymbol> result)
    {
        if (_tree == IntPtr.Zero) return;
        var names = query.CaptureNames;
        var nameIndex = IndexOfCapture(query, "name");
        var kinds = new string?[names.Count];
        for (var i = 0; i < names.Count; i++)
            if (names[i].StartsWith("definition.", StringComparison.Ordinal)) kinds[i] = names[i]["definition.".Length..];
        RunQuery(query, text, 0, text.Length, (in TSQueryMatch m, PatternInfo pattern) =>
        {
            SyntaxSpan? name = null, definition = null;
            string? kind = null;
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                if ((int)cap.Index == nameIndex)
                {
                    // several @name nodes (a block's type and labels) make one name from the first to the last
                    var s = Span(cap.Node, text.Length);
                    name = name is { } a ? a with { Start = Math.Min(a.Start, s.Start), End = Math.Max(a.End, s.End) } : s;
                }
                else if (kinds[cap.Index] is { } kd)
                {
                    kind = kd;
                    definition = Span(cap.Node, text.Length);
                }
            }
            if (kind is not null && name is { } n && definition is { } d)
                result.Add(new SyntaxSymbol(kind, n.Start, n.End, Math.Min(d.Start, n.Start), Math.Max(d.End, n.End)));
            return result.Count < MaxStructuralResults;
        });
    }

    private void CollectLocals(TsQuery query, ITextInput text, List<SyntaxSymbol> result)
    {
        if (_tree == IntPtr.Zero) return;
        var names = query.CaptureNames;
        var kinds = new string?[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            if (!names[i].StartsWith("local.definition.", StringComparison.Ordinal)) continue;
            var kind = names[i]["local.definition.".Length..];
            if (OutlineLocalKinds.Contains(kind)) kinds[i] = kind;
        }
        RunQuery(query, text, 0, text.Length, (in TSQueryMatch m, PatternInfo pattern) =>
        {
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                if (kinds[cap.Index] is not { } kind) continue;
                var name = Span(cap.Node, text.Length);
                var parent = TsApi.ts_node_parent(cap.Node);
                var definition = parent.IsNull ? name : Span(parent, text.Length);
                result.Add(new SyntaxSymbol(kind, name.Start, name.End, definition.Start, definition.End));
            }
            return result.Count < MaxStructuralResults;
        });
    }

    // ---- shared query plumbing ----

    private delegate bool MatchVisitor(in TSQueryMatch match, PatternInfo pattern);

    /// <summary>Runs <paramref name="query"/> over [start, end) of the tree, predicates evaluated, until <paramref name="visit"/> says stop or the guard does.</summary>
    private void RunQuery(TsQuery query, ITextInput text, int start, int end, MatchVisitor visit)
    {
        _text = text;
        StartQueryClock();
        try
        {
            using (Record(Language))
            {
                Exec(query, TsApi.ts_tree_root_node(_tree), start, end);
                TSQueryMatch m;
                while (TsApi.ts_query_cursor_next_match(_cursor, &m) != 0)
                {
                    var pattern = query.Patterns[m.PatternIndex];
                    if (pattern.Predicates.Length > 0 && !Evaluate(Language, pattern, m)) continue;
                    if (!visit(m, pattern)) break;
                }
            }
        }
        finally
        {
            _text = null;
            StopQueryClock();
        }
    }

    private static int IndexOfCapture(TsQuery query, string name)
    {
        for (var i = 0; i < query.CaptureNames.Count; i++)
            if (query.CaptureNames[i] == name) return i;
        return -1;
    }

    private SyntaxSpan Span(TSNode node, int textLength) =>
        new((int)(TsApi.ts_node_start_byte(node) / 2), (int)Math.Min(TsApi.ts_node_end_byte(node) / 2, (uint)textLength), Language.SymbolName(TsApi.ts_node_symbol(node), node));

    private static char CharAt(ITextInput text, int offset)
    {
        Span<char> c = stackalloc char[1];
        text.CopyTo(offset, c);
        return c[0];
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
