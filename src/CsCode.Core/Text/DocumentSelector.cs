namespace CsCode.Core.Text;

/// <summary>Same semantics as vscode.DocumentFilter / LSP DocumentFilter. Null fields match anything.</summary>
public sealed record DocumentFilter(string? Language = null, string? Scheme = null, string? Pattern = null)
{
    /// <summary>0 = no match; higher = more specific (language 10, scheme 10, pattern 5; wildcard-only 5).</summary>
    public int Score(Uri uri, string languageId)
    {
        var score = 0;
        if (Scheme is not null)
        {
            if (Scheme == "*") score = Math.Max(score, 3);
            else if (string.Equals(Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)) score = 10;
            else return 0;
        }
        if (Language is not null)
        {
            if (Language == "*") score = Math.Max(score, 5);
            else if (string.Equals(Language, languageId, StringComparison.Ordinal)) score = 10;
            else return 0;
        }
        if (Pattern is not null)
        {
            if (!Glob.IsMatch(Pattern, uri.IsFile ? uri.LocalPath : uri.AbsolutePath)) return 0;
            score = Math.Max(score, 5);
        }
        return score == 0 && Scheme is null && Language is null && Pattern is null ? 5 : score;
    }
}

public sealed record DocumentSelector(IReadOnlyList<DocumentFilter> Filters)
{
    public static DocumentSelector ForLanguage(string languageId) => new([new DocumentFilter(Language: languageId)]);
    public static DocumentSelector ForLanguages(params string[] languageIds) =>
        new(languageIds.Select(l => new DocumentFilter(Language: l)).ToArray());
    public static readonly DocumentSelector All = new([new DocumentFilter(Language: "*")]);

    public int Score(Uri uri, string languageId)
    {
        // A loop, not LINQ's Max: this runs for every provider on every completion/hover/diagnostic lookup.
        var best = 0;
        for (var i = 0; i < Filters.Count; i++) best = Math.Max(best, Filters[i].Score(uri, languageId));
        return best;
    }

    public int Score(ITextDocument doc) => Score(doc.Uri, doc.LanguageId);
}
