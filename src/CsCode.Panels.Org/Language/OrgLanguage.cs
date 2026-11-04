using System.Text.RegularExpressions;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Panels.Org.Engine;

namespace CsCode.Panels.Org.Language;

/// <summary>
/// The <c>org</c> language: detection (<c>.org</c>), the TextMate grammar and language configuration shipped next to the module (contributed
/// as files, as a user's own grammar would be), and the outline of a file: folding by heading (a heading folds its subtree, as org's TAB),
/// property and other drawers, <c>#+BEGIN_…</c> blocks, the headings as document symbols (breadcrumbs, Go to Symbol, sticky scroll), and
/// semantic tokens for the TODO states of each heading as its file defines them (<c>#+TODO:</c>, else <c>org.todoKeywords</c>): to-do states as
/// keywords (the grammar's TODO colour), done states as comments (its DONE colour), so a file's own states are coloured too.
/// </summary>
public sealed class OrgLanguage : IFoldingRangeProvider, IDocumentSymbolProvider, ISemanticTokensProvider
{
    public const string LanguageId = "org";
    public const string ScopeName = "source.org";

    private static readonly Regex DrawerStartRe = new(@"^[ \t]*:([\w\-]+):[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DrawerEndRe = new(@"^[ \t]*:END:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockStartRe = new(@"^[ \t]*#\+begin_(\w+)", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockEndRe = new(@"^[ \t]*#\+end_(\w+)", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<OrgTodoKeywords> _keywords;

    public OrgLanguage(Func<OrgTodoKeywords> keywords) => _keywords = keywords;

    /// <summary>Where the grammar and the language configuration are: the <c>Org</c> folder next to the module's assembly.</summary>
    public static string ResourceDirectory => Path.Combine(AppContext.BaseDirectory, "Org");

    public static LanguageContribution Language(string directory) =>
        new(LanguageId, ["Org", "org-mode"], [".org", ".org_archive"], [], [], ConfigurationPath: Path.Combine(directory, "org.language-configuration.json"));

    public SemanticTokensLegend Legend { get; } = new(["keyword", "comment"], []);

    public ValueTask<SemanticTokens?> ProvideSemanticTokensAsync(ITextDocument document, CancellationToken ct) =>
        ValueTask.FromResult<SemanticTokens?>(KeywordTokens(OrgDocument.Parse(document.GetText(), _keywords())));

    /// <summary>The TODO state of each heading as a token (LSP encoding): type 0 (keyword) for a to-do state, 1 (comment) for a done state.</summary>
    public static SemanticTokens KeywordTokens(OrgDocument doc)
    {
        var data = new List<uint>();
        var previousLine = 0;
        foreach (var h in doc.Headings)
        {
            if (h.Keyword is not { } kw) continue;
            data.Add((uint)(h.Line - previousLine));
            data.Add((uint)h.KeywordColumn);
            data.Add((uint)kw.Length);
            data.Add(h.IsDone ? 1u : 0u);
            data.Add(0);
            previousLine = h.Line;
        }
        return new SemanticTokens(data);
    }

    public static GrammarContribution Grammar(string directory) => new(LanguageId, ScopeName, Path.Combine(directory, "org.tmLanguage.json"));

    public ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<FoldingRange>?>(FoldingRanges(OrgDocument.Parse(document.GetText(), _keywords())));

    public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<DocumentSymbol>?>(Symbols(OrgDocument.Parse(document.GetText(), _keywords())));

    /// <summary>Each heading's subtree (to its last line of text), drawers and blocks.</summary>
    public static IReadOnlyList<FoldingRange> FoldingRanges(OrgDocument doc)
    {
        var ranges = new List<FoldingRange>();
        foreach (var h in doc.Headings)
        {
            var last = LastTextLine(doc, h.Line, h.SubtreeEnd);
            if (last > h.Line) ranges.Add(new FoldingRange(h.Line, last));
        }
        int? drawer = null;
        string? block = null;
        var blockStart = 0;
        for (var i = 0; i < doc.ContentLineCount; i++)
        {
            var line = doc.Lines[i];
            if (block is not null)
            {
                if (BlockEndRe.Match(line) is { Success: true } e && e.Groups[1].Value.Equals(block, StringComparison.OrdinalIgnoreCase))
                {
                    if (i > blockStart) ranges.Add(new FoldingRange(blockStart, i, FoldingRangeKind.Region));
                    block = null;
                }
                continue;
            }
            if (drawer is { } d)
            {
                if (DrawerEndRe.IsMatch(line)) { if (i > d) ranges.Add(new FoldingRange(d, i, FoldingRangeKind.Region)); drawer = null; }
                else if (line.Length > 0 && line[0] == '*') drawer = null; // a heading ends an unclosed drawer
                continue;
            }
            if (BlockStartRe.Match(line) is { Success: true } b) { block = b.Groups[1].Value; blockStart = i; }
            else if (DrawerStartRe.IsMatch(line) && !DrawerEndRe.IsMatch(line)) drawer = i;
        }
        return ranges;
    }

    private static int LastTextLine(OrgDocument doc, int from, int end)
    {
        var last = end - 1;
        while (last > from && string.IsNullOrWhiteSpace(doc.Lines[last])) last--;
        return last;
    }

    /// <summary>The headings as a tree of symbols: name = title, detail = TODO state, priority and tags.</summary>
    public static IReadOnlyList<DocumentSymbol> Symbols(OrgDocument doc)
    {
        var index = 0;
        return Children(null, int.MaxValue);

        List<DocumentSymbol> Children(OrgHeading? parent, int end)
        {
            var list = new List<DocumentSymbol>();
            while (index < doc.Headings.Count && doc.Headings[index].Line < end && ReferenceEquals(doc.Headings[index].Parent, parent))
            {
                var h = doc.Headings[index++];
                var children = Children(h, h.SubtreeEnd);
                var last = LastTextLine(doc, h.Line, h.SubtreeEnd);
                var detail = string.Join(' ', new[] { h.Keyword, h.Priority is { } p ? $"[#{p}]" : null, h.Tags.Count > 0 ? ":" + string.Join(':', h.Tags) + ":" : null }.OfType<string>());
                var name = h.Title.Length > 0 ? h.Title : "(untitled)";
                list.Add(new DocumentSymbol(
                    name,
                    SymbolKind.String,
                    new TextRange(new TextPosition(h.Line, 0), new TextPosition(last, doc.Lines[last].Length)),
                    new TextRange(new TextPosition(h.Line, h.TitleColumn), new TextPosition(h.Line, Math.Max(h.TitleColumn, h.TitleEnd))),
                    detail.Length > 0 ? detail : null,
                    children.Count > 0 ? children : null));
            }
            return list;
        }
    }
}
