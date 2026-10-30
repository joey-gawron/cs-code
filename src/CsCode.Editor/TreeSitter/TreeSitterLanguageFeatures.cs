using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.Editor.TreeSitter.Highlighting;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// The structural features the syntax tree gives without a language server (docs/TREE-SITTER.md, "Structure"): Expand/Shrink Selection
/// (<see cref="ISelectionRangeProvider"/>: the enclosing nodes), folding (<see cref="IFoldingRangeProvider"/>: the grammar's <c>folds.scm</c>) and
/// the outline/breadcrumbs (<see cref="IDocumentSymbolProvider"/>: CsCode's <c>symbols.scm</c>, the grammar's <c>tags.scm</c> or its
/// <c>locals.scm</c>). Registered once with <see cref="DocumentSelector.All"/>, whose score (5) ranks it after any language server's providers
/// (10), so a server that offers the feature wins; for selection and symbols it is also SILENT while another provider of the same feature exists
/// for the document, so consumers that merge every provider (Go to Symbol in File) never see an entry twice (folding answers anyway: the editor
/// merges folding providers by start line, VS Code's rule), and it is silent for every document tree-sitter does not colour (it answers null at
/// once, without leaving the caller's thread): the editor's own fallbacks (indentation folding, the word/bracket expander) apply.
///
/// Threading: the tree belongs to the syntax lane, so every answer is computed there (<see cref="EditorWorker.RunAsync{T}"/>) from the tree of
/// the document's current version, waiting briefly (at most <see cref="TreeWait"/>) while a parse is still under way; the caller only awaits.
/// </summary>
internal sealed partial class TreeSitterLanguageFeatures(TokenizationManager manager, ILanguageFeatureRegistry registry)
    : ISelectionRangeProvider, IFoldingRangeProvider, IDocumentSymbolProvider
{
    /// <summary>How long a request waits for the tree of the document's current version (a first parse of a big file takes longer: null then).</summary>
    internal static TimeSpan TreeWait { get; set; } = TimeSpan.FromSeconds(2);

    private enum Outcome { Ready, NotReady, NoEngine }

    [GeneratedRegex(@"^(?:end\w*\b.*|[\]\)\}>;,]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CloserLine();

    // ---- selection ----

    public async ValueTask<IReadOnlyList<SelectionRange>?> ProvideSelectionRangesAsync(ITextDocument document, IReadOnlyList<TextPosition> positions, CancellationToken ct)
    {
        if (Others<ISelectionRangeProvider>(document)) return null;
        return await OnTreeAsync(document, ct, (syntax, text) =>
        {
            var snapshot = text.Snapshot;
            var result = new List<SelectionRange>(positions.Count);
            foreach (var p in positions)
            {
                var offset = snapshot.OffsetAt(p);
                SelectionRange? chain = null;
                var spans = syntax.EnclosingNodes(text, offset, offset);
                for (var i = spans.Count - 1; i >= 0; i--) chain = new SelectionRange(ToRange(snapshot, spans[i].Start, spans[i].End), chain);
                result.Add(chain ?? new SelectionRange(new TextRange(p, p)));
            }
            return (IReadOnlyList<SelectionRange>)result;
        }).ConfigureAwait(false);
    }

    // ---- folding ----

    /// <summary>
    /// Not silent while a server offers folding too: the editor merges every provider's ranges (VS Code's rule, the server's first on a shared
    /// start line), so the tree still folds when the server answers nothing (starting, failed) and fills in what it leaves out.
    /// </summary>
    public async ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct)
    {
        return await OnTreeAsync(document, ct, (syntax, text) =>
        {
            if (syntax.FoldNodes(text) is not { } nodes) return null; // no folds.scm: the editor's indentation folding stays
            return (IReadOnlyList<FoldingRange>?)Folds(nodes, text.Snapshot);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Node spans → VS Code-style folds: the first line stays visible, the region ends on the node's last non-blank line, and a last line that is
    /// only a closer (<c>}</c>, <c>)</c>, <c>end</c>…) stays visible like the first (as the editor's indentation folding does). One fold per start
    /// line (the largest). Capped at <see cref="Features.FoldComputer.MaxRanges"/>.
    /// </summary>
    internal static List<FoldingRange> Folds(IReadOnlyList<SyntaxSpan> nodes, DocumentSnapshot snapshot)
    {
        var byStart = new Dictionary<int, FoldingRange>();
        foreach (var n in nodes)
        {
            if (n.End <= n.Start) continue;
            var startLine = snapshot.PositionAt(n.Start).Line;
            var endLine = snapshot.PositionAt(Math.Max(n.Start, n.End - 1)).Line;
            while (endLine > startLine && string.IsNullOrWhiteSpace(snapshot.GetLineText(endLine))) endLine--;
            if (endLine > startLine && CloserLine().IsMatch(snapshot.GetLineText(endLine).Trim())) endLine--;
            if (endLine <= startLine) continue;
            var kind = n.Type.Contains("comment", StringComparison.Ordinal) ? FoldingRangeKind.Comment
                : n.Type.Contains("import", StringComparison.Ordinal) || n.Type is "use_declaration" or "using_directive" or "preproc_include" ? FoldingRangeKind.Imports
                : (FoldingRangeKind?)null;
            if (!byStart.TryGetValue(startLine, out var existing) || existing.EndLine < endLine) byStart[startLine] = new FoldingRange(startLine, endLine, kind);
        }
        var result = byStart.Values.OrderBy(f => f.StartLine).ToList();
        if (result.Count > Features.FoldComputer.MaxRanges) result.RemoveRange(Features.FoldComputer.MaxRanges, result.Count - Features.FoldComputer.MaxRanges);
        return result;
    }

    // ---- outline / breadcrumbs ----

    public async ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct)
    {
        if (Others<IDocumentSymbolProvider>(document)) return null;
        return await OnTreeAsync(document, ct, (syntax, text) =>
            syntax.SymbolNodes(text) is { } symbols ? (IReadOnlyList<DocumentSymbol>?)Symbols(symbols, text.Snapshot) : null).ConfigureAwait(false);
    }

    /// <summary>Flat definitions (document order) → the symbol tree, nested by containment.</summary>
    internal static List<DocumentSymbol> Symbols(IReadOnlyList<SyntaxSymbol> flat, DocumentSnapshot snapshot)
    {
        var roots = new List<DocumentSymbol>();
        // (end offset, children list of the open symbol, its index in its parent's list, the parent list)
        var open = new Stack<(int End, List<DocumentSymbol> Children, List<DocumentSymbol> Parent, int Index)>();
        foreach (var s in flat)
        {
            while (open.Count > 0 && s.Start >= open.Peek().End) Close(open);
            var name = NameOf(snapshot, s);
            if (name.Length == 0) continue;
            var symbol = new DocumentSymbol(name, KindOf(s.Kind), ToRange(snapshot, s.Start, s.End), ToRange(snapshot, s.NameStart, s.NameEnd));
            var siblings = open.Count > 0 ? open.Peek().Children : roots;
            siblings.Add(symbol);
            open.Push((s.End, new List<DocumentSymbol>(), siblings, siblings.Count - 1));
        }
        while (open.Count > 0) Close(open);
        return roots;

        static void Close(Stack<(int End, List<DocumentSymbol> Children, List<DocumentSymbol> Parent, int Index)> open)
        {
            var (_, children, parent, index) = open.Pop();
            if (children.Count > 0) parent[index] = parent[index] with { Children = children };
        }
    }

    private static string NameOf(DocumentSnapshot snapshot, SyntaxSymbol s)
    {
        var length = Math.Clamp(s.NameEnd - s.NameStart, 0, 400);
        var raw = snapshot.GetText(s.NameStart, length);
        var newline = raw.IndexOfAny(['\r', '\n']);
        if (newline >= 0) raw = raw[..newline];
        raw = raw.Trim();
        return raw.Length > 120 ? raw[..120] + "…" : raw;
    }

    internal static SymbolKind KindOf(string kind) => kind switch
    {
        "function" or "macro" or "label" or "rpc" => SymbolKind.Function,
        "method" => SymbolKind.Method,
        "constructor" => SymbolKind.Constructor,
        "class" or "type" or "implementation" or "impl" => SymbolKind.Class,
        "interface" or "trait" or "service" => SymbolKind.Interface,
        "module" or "block" => SymbolKind.Module,
        "namespace" => SymbolKind.Namespace,
        "package" => SymbolKind.Package,
        "struct" or "union" or "message" or "record" => SymbolKind.Struct,
        "enum" => SymbolKind.Enum,
        "enum_member" or "enumerator" => SymbolKind.EnumMember,
        "field" => SymbolKind.Field,
        "property" => SymbolKind.Property,
        "constant" => SymbolKind.Constant,
        "variable" or "var" => SymbolKind.Variable,
        "heading" or "section" => SymbolKind.String, // what VS Code's Markdown outline uses
        "table" or "object" => SymbolKind.Object,
        "key" => SymbolKind.Key,
        _ => SymbolKind.Variable,
    };

    // ---- plumbing ----

    private bool Others<T>(ITextDocument document) where T : class, ILanguageProvider =>
        registry.GetProviders<T>(document).Any(p => !ReferenceEquals(p, this));

    private static TextRange ToRange(DocumentSnapshot snapshot, int start, int end) => new(snapshot.PositionAt(start), snapshot.PositionAt(Math.Max(start, end)));

    /// <summary>
    /// Runs <paramref name="compute"/> on the syntax lane with the tree of the document's current version. Null at once for a document tree-sitter
    /// does not colour; null after <see cref="TreeWait"/> when no tree of that version arrives (a parse still under way, or tree-sitter gave up).
    /// </summary>
    private async ValueTask<T?> OnTreeAsync<T>(ITextDocument document, CancellationToken ct, Func<SyntaxDocument, SnapshotTextInput, T?> compute) where T : class
    {
        if (manager.SessionFor(document) is not { TreeSitterWanted: true } session) return null;
        var version = document.Version;
        var answered = false;
        try
        {
            var deadline = DateTime.UtcNow + TreeWait;
            var delay = 10;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!session.TreeSitterWanted) return null;
                EditorWorker lane;
                try
                {
                    lane = manager.TreeSitter.Lane;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;
                (Outcome, T?) answer;
                try
                {
                    // bounded: a lane that is shutting down (or was given up after a hang) never runs the job
                    answer = await lane.RunAsync(WorkPriority.Interactive, _ =>
                    {
                        if (session.TreeSitter is not { } engine || !session.IsCurrentEngine(engine)) return (Outcome.NoEngine, default(T));
                        if (engine.TreeVersion < version || engine.TreeText is not { } text) return (Outcome.NotReady, default(T));
                        return (Outcome.Ready, compute(engine.Syntax, text));
                    }, ct).WaitAsync(remaining, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
                var (outcome, value) = answer;
                if (outcome == Outcome.Ready)
                {
                    answered = true;
                    return value;
                }
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 100);
            }
        }
        finally
        {
            if (!answered) MissedTree(session, version);
        }
    }

    /// <summary>
    /// The request ended without the tree (too slow a first parse, a busy lane, the caller gave up): the session says so when the tree lands
    /// (<see cref="TokenizationManager.SyntaxTreeArrived"/>), and folding asks again then instead of keeping its fallback until the next edit. A
    /// tree that landed meanwhile is caught by the check posted to the lane behind it.
    /// </summary>
    private void MissedTree(TokenizationSession session, int version)
    {
        if (!session.TreeSitterWanted) return;
        session.StructureMissedTree();
        try
        {
            manager.TreeSitter.Lane.Post(WorkPriority.Background, null, _ => session.CheckTreeArrived(version));
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }
}
