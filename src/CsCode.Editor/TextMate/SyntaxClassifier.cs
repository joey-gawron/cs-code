using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;

namespace CsCode.Editor.TextMate;

/// <summary>
/// <see cref="ISyntaxClassificationService"/> over the editor's token store: the rows the TextMate or tree-sitter engine published for a
/// document (tree-sitter captures are mapped onto TextMate scopes, so one set of rules serves both), each scope stack reduced to a
/// <see cref="SyntaxTextKind"/> by <see cref="SyntaxScopeRules"/> once per interned stack. A capture copies the row references under one
/// lock; reading runs afterwards touches nothing shared but the interner's append-only table.
/// </summary>
internal sealed class SyntaxClassifier : ISyntaxClassificationService, IDisposable
{
    private readonly TokenizationManager _tokens;
    private readonly IDocumentManager _documents;
    private readonly Emitter<SyntaxClassificationChange> _changed = new();
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    // kind + 1 per interned scope-stack id (0 = not computed yet); replaced by a larger copy, never shrunk
    private byte[] _kinds = new byte[1024];

    public SyntaxClassifier(TokenizationManager tokens, IDocumentManager documents)
    {
        _tokens = tokens;
        _documents = documents;
        _subscription = tokens.TokenRowsChanged.Subscribe(e => _changed.Fire(new SyntaxClassificationChange(e.Uri, e.First, e.Last)));
    }

    public IEventSource<SyntaxClassificationChange> Changed => _changed;

    public SyntaxClassification Capture(Uri uri)
    {
        var document = _documents.Get(uri);
        if (document is EditorDocument { IsLargeFile: true }) return SyntaxClassification.Empty(SyntaxClassificationState.LargeFile);
        if (document is null) return SyntaxClassification.Empty(SyntaxClassificationState.Unavailable);
        if (!_tokens.HasGrammar(document.LanguageId)) return SyntaxClassification.Empty(SyntaxClassificationState.NoGrammar, document.LineCount);
        if (_tokens.SessionFor(document) is not { } session) return SyntaxClassification.Empty(SyntaxClassificationState.Unavailable, document.LineCount);
        return new Rows(this, session.Store.CopyRows());
    }

    /// <summary>The kind of an interned scope stack (computed once per id).</summary>
    private SyntaxTextKind KindOf(int scopeId)
    {
        var kinds = Volatile.Read(ref _kinds);
        if (scopeId < kinds.Length && kinds[scopeId] != 0) return (SyntaxTextKind)(kinds[scopeId] - 1);
        var kind = scopeId == 0 ? SyntaxTextKind.Code : SyntaxScopeRules.Classify(_tokens.Interner.Scopes(scopeId));
        lock (_gate)
        {
            kinds = _kinds;
            if (scopeId >= kinds.Length)
            {
                var bigger = new byte[Math.Max(scopeId + 1, kinds.Length * 2)];
                Array.Copy(kinds, bigger, kinds.Length);
                Volatile.Write(ref _kinds, kinds = bigger);
            }
            kinds[scopeId] = (byte)(kind + 1);
        }
        return kind;
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _changed.Clear();
    }

    private sealed class Rows(SyntaxClassifier owner, int[]?[] rows) : SyntaxClassification
    {
        public override SyntaxClassificationState State => SyntaxClassificationState.Tokenized;
        public override int LineCount => rows.Length;

        public override bool TryGetRuns(int line, List<SyntaxTextRun> runs)
        {
            runs.Clear();
            if ((uint)line >= (uint)rows.Length || rows[line] is not { Length: >= 2 } row) return false;
            for (var i = 0; i + 1 < row.Length; i += 2)
            {
                var start = i == 0 ? 0 : row[i];
                var kind = owner.KindOf(row[i + 1]);
                if (runs.Count > 0 && runs[^1].Kind == kind) continue;
                runs.Add(new SyntaxTextRun(start, kind));
            }
            return true;
        }
    }
}

/// <summary>
/// How a TextMate scope stack reads for features that look at text by kind (<see cref="SyntaxTextKind"/>). The innermost scope that says
/// something decides, as <see cref="Theming.TokenColorResolver.KindOf"/> decides comment/string for auto-closing, with what a spell checker
/// must not read as words taken out: escapes, placeholders, regular expressions, links, raw and code spans, the XML or JSDoc markup inside a
/// documentation comment (a string inside a comment is an attribute value such as <c>cref="..."</c>), the paths of includes and imports.
/// The root scope tells code (<c>source.*</c>) from prose (<c>text.*</c>); embedded code inside prose is code.
/// </summary>
public static class SyntaxScopeRules
{
    private static readonly string[] IgnoredScopes =
    [
        "constant.character.escape", "constant.other.placeholder", "constant.character.format", "constant.other.character-class",
        "string.regexp", "keyword.operator.quantifier.regexp", "markup.underline.link", "markup.inline.raw", "markup.raw", "markup.fenced_code",
        "meta.tag", "entity.name.tag", "entity.other.attribute-name", "meta.preprocessor.include", "string.quoted.other.lt-gt.include",
        "storage.type.class.jsdoc", "variable.other.jsdoc", "entity.name.type.instance.jsdoc", "meta.embedded.block.frontmatter",
    ];

    // an import's string names a module or path: words there are not prose
    private static readonly string[] ImportScopes = ["meta.import", "meta.export", "meta.preprocessor.include", "meta.module-reference"];

    private static readonly string[] CodeScopes = ["meta.embedded", "meta.interpolation", "meta.template.expression", "punctuation.section.interpolation"];

    public static SyntaxTextKind Classify(IReadOnlyList<string> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var s = scopes[i];
            foreach (var ignored in IgnoredScopes)
                if (Matches(s, ignored)) return SyntaxTextKind.Ignored;
            if (Matches(s, "comment")) return SyntaxTextKind.Comment;
            if (Matches(s, "string"))
                return Outside(scopes, i, "comment") || OutsideAny(scopes, i, ImportScopes) ? SyntaxTextKind.Ignored : SyntaxTextKind.String;
            foreach (var code in CodeScopes)
                if (Matches(s, code)) return SyntaxTextKind.Code;
            if (s.StartsWith("source.", StringComparison.Ordinal)) return SyntaxTextKind.Code;
            if (s.StartsWith("text.", StringComparison.Ordinal)) return SyntaxTextKind.Text;
        }
        return SyntaxTextKind.Code;
    }

    private static bool Outside(IReadOnlyList<string> scopes, int index, string selector)
    {
        for (var i = index - 1; i >= 0; i--)
            if (Matches(scopes[i], selector)) return true;
        return false;
    }

    private static bool OutsideAny(IReadOnlyList<string> scopes, int index, string[] selectors)
    {
        foreach (var selector in selectors)
            if (Outside(scopes, index, selector)) return true;
        return false;
    }

    /// <summary>TextMate selector matching: <c>comment</c> matches <c>comment</c> and <c>comment.line.double-slash.cs</c>, not <c>comments</c>.</summary>
    private static bool Matches(string scope, string selector) =>
        scope.Length >= selector.Length && scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');
}
