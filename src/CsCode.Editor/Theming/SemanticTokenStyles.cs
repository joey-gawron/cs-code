using CsCode.Core.Theming;

namespace CsCode.Editor.Theming;

/// <summary>
/// The style semantic highlighting gives one token: each property null when nothing sets it (the grammar's colour or style then stays).
/// </summary>
public sealed record SemanticStyle(Rgba? Foreground, bool? Bold = null, bool? Italic = null, bool? Underline = null, bool? Strikethrough = null)
{
    public bool IsEmpty => Foreground is null && Bold is null && Italic is null && Underline is null && Strikethrough is null;

    /// <summary>A TextMate/theme <c>fontStyle</c> string sets all four flags (VS Code's <c>TokenStyle.fromSettings</c>); null sets none.</summary>
    internal static SemanticStyle From(Rgba? foreground, string? fontStyle)
    {
        if (fontStyle is null) return new SemanticStyle(foreground);
        var parts = fontStyle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new SemanticStyle(foreground, parts.Contains("bold"), parts.Contains("italic"), parts.Contains("underline"), parts.Contains("strikethrough"));
    }
}

/// <summary>
/// Resolves the style of a semantic token (type, modifiers, language) under a colour theme, with VS Code's rules
/// (<c>tokenClassificationRegistry.ts</c>, <c>colorThemeData.getTokenStyle</c>):
/// <list type="number">
/// <item>Rules are selectors <c>type.modifier1.modifier2:language</c> (<c>*</c> for any type). A selector matches when its type is the token's
/// type or one of its super-types (<c>class</c>, <c>struct</c>, <c>interface</c>, <c>enum</c>, <c>typeParameter</c> are types), the token has
/// all its modifiers, and the language (when given) is the document's. Score: 100 minus the super-type distance for a typed selector, +100
/// per modifier, +10 for a language.</item>
/// <item>Two rule sets compete property by property (foreground, bold, italic, underline, strikethrough), the higher score winning and the theme
/// winning ties: the built-in defaults, which map each standard type (and a few type.modifier pairs) to TextMate scopes that are looked up in the
/// theme's <c>tokenColors</c> (the first scope a theme rule matches; no match leaves the property unset), then the theme's
/// <c>semanticTokenColors</c>.</item>
/// </list>
/// Pure and thread-safe after construction; <see cref="Resolve"/> allocates, so callers cache per (type, modifiers).
/// </summary>
public sealed class SemanticTokenStyleResolver
{
    private sealed record Selector(string Type, string[] Modifiers, string? Language);
    private sealed record Rule(Selector Selector, SemanticStyle Style);

    /// <summary>VS Code's default type → scope fallbacks, then its type.modifier defaults. Alternatives are tried in order.</summary>
    internal static readonly IReadOnlyList<(string Selector, string[] Scopes)> Defaults =
    [
        ("comment", ["comment"]),
        ("string", ["string"]),
        ("keyword", ["keyword.control"]),
        ("number", ["constant.numeric"]),
        ("regexp", ["constant.regexp"]),
        ("operator", ["keyword.operator"]),
        ("namespace", ["entity.name.namespace"]),
        ("type", ["entity.name.type", "support.type"]),
        ("struct", ["entity.name.type.struct"]),
        ("class", ["entity.name.type.class", "support.class"]),
        ("interface", ["entity.name.type.interface"]),
        ("enum", ["entity.name.type.enum"]),
        ("typeParameter", ["entity.name.type.parameter"]),
        ("function", ["entity.name.function", "support.function"]),
        ("member", ["entity.name.function.member", "support.function"]),
        ("method", ["entity.name.function.member", "support.function"]),
        ("macro", ["entity.name.function.preprocessor"]),
        ("variable", ["variable.other.readwrite", "entity.name.variable"]),
        ("parameter", ["variable.parameter"]),
        ("property", ["variable.other.property"]),
        ("enumMember", ["variable.other.enummember"]),
        ("event", ["variable.other.event"]),
        ("decorator", ["entity.name.decorator", "entity.name.function"]),
        ("variable.readonly", ["variable.other.constant"]),
        ("property.readonly", ["variable.other.constant.property"]),
        ("type.defaultLibrary", ["support.type"]),
        ("class.defaultLibrary", ["support.class"]),
        ("interface.defaultLibrary", ["support.class"]),
        ("variable.defaultLibrary", ["support.variable", "support.other.variable"]),
        ("variable.defaultLibrary.readonly", ["support.constant"]),
        ("property.defaultLibrary", ["support.variable.property"]),
        ("property.defaultLibrary.readonly", ["support.constant.property"]),
        ("function.defaultLibrary", ["support.function"]),
        ("member.defaultLibrary", ["support.function"]),
    ];

    /// <summary>
    /// Super-types: VS Code's standard ones, plus the custom types well-known servers declare with a super-type in their VS Code extension
    /// (rust-analyzer), which CsCode does not run.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SuperTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["struct"] = "type", ["class"] = "type", ["interface"] = "type", ["enum"] = "type", ["typeParameter"] = "type",
        ["builtinType"] = "type", ["typeAlias"] = "type", ["union"] = "type",
        ["selfKeyword"] = "keyword", ["selfTypeKeyword"] = "keyword",
    };

    private readonly List<Rule> _defaults = [];
    private readonly List<Rule> _theme = [];

    public SemanticTokenStyleResolver(ColorTheme theme, TokenColorResolver textMate)
    {
        foreach (var (selector, scopes) in Defaults)
        {
            foreach (var scope in scopes)
            {
                if (textMate.MatchScope(scope) is not { } match) continue;
                var style = SemanticStyle.From(match.Foreground, match.FontStyle);
                if (!style.IsEmpty) _defaults.Add(new Rule(Parse(selector), style));
                break; // the first alternative the theme knows decides
            }
        }
        if (theme.SemanticTokenColors is { } rules)
            foreach (var (selector, rule) in rules)
            {
                var style = SemanticStyle.From(Rgba.TryParse(rule.Foreground, out var fg) ? fg : null, rule.FontStyle);
                if (!style.IsEmpty) _theme.Add(new Rule(Parse(selector), style));
            }
    }

    private static Selector Parse(string text)
    {
        string? language = null;
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            language = text[(colon + 1)..].Trim();
            text = text[..colon];
        }
        var parts = text.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return new Selector(parts.Length > 0 ? parts[0] : "*", parts.Length > 1 ? parts[1..] : [], string.IsNullOrEmpty(language) ? null : language);
    }

    /// <summary>The style for a token, or null when no rule sets anything (the grammar's colours stay).</summary>
    public SemanticStyle? Resolve(string type, IReadOnlyCollection<string> modifiers, string? language)
    {
        Rgba? fg = null;
        bool? bold = null, italic = null, underline = null, strike = null;
        int sFg = -1, sBold = -1, sItalic = -1, sUnderline = -1, sStrike = -1;

        void Apply(List<Rule> rules, bool themeWinsTies)
        {
            foreach (var rule in rules)
            {
                var score = Score(rule.Selector, type, modifiers, language);
                if (score < 0) continue;
                var st = rule.Style;
                bool Better(int current) => themeWinsTies ? score >= current : score > current;
                if (st.Foreground is { } f && Better(sFg)) { fg = f; sFg = score; }
                if (st.Bold is { } b && Better(sBold)) { bold = b; sBold = score; }
                if (st.Italic is { } i && Better(sItalic)) { italic = i; sItalic = score; }
                if (st.Underline is { } u && Better(sUnderline)) { underline = u; sUnderline = score; }
                if (st.Strikethrough is { } s && Better(sStrike)) { strike = s; sStrike = score; }
            }
        }

        Apply(_defaults, themeWinsTies: false);
        Apply(_theme, themeWinsTies: true);
        var result = new SemanticStyle(fg, bold, italic, underline, strike);
        return result.IsEmpty ? null : result;
    }

    private static int Score(Selector s, string type, IReadOnlyCollection<string> modifiers, string? language)
    {
        var score = 0;
        if (s.Language is not null)
        {
            if (!string.Equals(s.Language, language, StringComparison.Ordinal)) return -1;
            score += 10;
        }
        if (s.Type != "*")
        {
            var level = Level(type, s.Type);
            if (level < 0) return -1;
            score += 100 - level;
        }
        foreach (var m in s.Modifiers)
            if (!modifiers.Contains(m)) return -1;
        return score + s.Modifiers.Length * 100;
    }

    /// <summary>0 when <paramref name="type"/> is <paramref name="selectorType"/>, 1 for its super-type, …; -1 when unrelated.</summary>
    private static int Level(string type, string selectorType)
    {
        var level = 0;
        for (var t = type; t is not null; t = SuperTypes.GetValueOrDefault(t), level++)
        {
            if (string.Equals(t, selectorType, StringComparison.Ordinal)) return level;
            if (level > 8) break;
        }
        return -1;
    }
}
