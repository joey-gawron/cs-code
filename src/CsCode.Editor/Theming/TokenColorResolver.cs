using CsCode.Core.Theming;

namespace CsCode.Editor.Theming;

/// <summary>Resolved style of a TextMate scope stack.</summary>
public readonly record struct ResolvedTokenStyle(Rgba? Foreground, Rgba? Background, bool Bold, bool Italic, bool Underline, bool Strikethrough)
{
    public bool IsPlain => Foreground is null && Background is null && !Bold && !Italic && !Underline && !Strikethrough;
}

/// <summary>
/// Resolves the colour/style a theme's <c>tokenColors</c> give a TextMate scope stack (outermost scope first, e.g.
/// <c>["source.cs", "meta.method.cs", "entity.name.function.cs"]</c>), with the semantics of vscode-textmate:
/// <list type="number">
/// <item>The stack is folded outermost → innermost; at each scope, every rule whose selector matches applies, least specific first, and a
/// property a rule sets overrides what the enclosing scopes produced (so the innermost scope wins, and a rule that sets only a font style
/// keeps the outer colour).</item>
/// <item>A selector matches a scope when it equals it or is a dot-prefix of it (<c>comment</c> matches <c>comment.line.double-slash</c>).</item>
/// <item>Specificity at one scope: the rule whose target has more dot-separated segments wins; then the one with longer parent selectors; then
/// more parent selectors; then the later rule in the theme file.</item>
/// <item>A selector <c>"a b c"</c> has target <c>c</c> and requires ancestors <c>b</c> then <c>a</c> somewhere above it, in that order (not
/// necessarily adjacent).</item>
/// <item>A rule with no scope is the theme's global default for ordinary text.</item>
/// </list>
/// Pure and thread-safe after construction.
/// </summary>
public sealed class TokenColorResolver
{
    private sealed record Rule(int Depth, string[] Parents, Rgba? Foreground, Rgba? Background, string? FontStyle, int Index);

    private readonly Dictionary<string, List<Rule>> _byTarget = new(StringComparer.Ordinal);
    private readonly Rgba _defaultForeground;
    private readonly Rgba? _defaultBackground;
    private readonly Rgba? _globalForeground;
    private readonly Rgba? _globalBackground;
    private readonly int _globalStyle;

    private const int Bold = 1, Italic = 2, Underline = 4, Strikethrough = 8;

    public TokenColorResolver(ColorTheme theme, Rgba defaultForeground, Rgba? defaultBackground = null)
    {
        _defaultForeground = defaultForeground;
        _defaultBackground = defaultBackground;
        var index = 0;
        foreach (var rule in theme.TokenColors)
        {
            index++;
            var fg = Rgba.TryParse(rule.Foreground, out var f) ? f : (Rgba?)null;
            var bg = Rgba.TryParse(rule.Background, out var b) ? b : (Rgba?)null;
            if (rule.Scopes.Count == 0)
            {
                // global default: later ones override earlier ones, property by property
                if (fg is not null) _globalForeground = fg;
                if (bg is not null) _globalBackground = bg;
                if (rule.FontStyle is not null) _globalStyle = ParseStyle(rule.FontStyle);
                continue;
            }
            foreach (var selector in rule.Scopes)
            {
                var parts = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var target = parts[^1];
                var parents = parts.Length > 1 ? parts[..^1].Reverse().ToArray() : []; // innermost parent first
                var r = new Rule(target.Count(c => c == '.') + 1, parents, fg, bg, rule.FontStyle, index);
                if (!_byTarget.TryGetValue(target, out var list)) _byTarget[target] = list = [];
                list.Add(r);
            }
        }
    }

    public ResolvedTokenStyle Resolve(IReadOnlyList<string> scopeStack)
    {
        Rgba? fg = _globalForeground ?? _defaultForeground;
        Rgba? bg = _globalBackground ?? _defaultBackground;
        var style = _globalStyle;
        var candidates = new List<Rule>(8);

        for (var i = 0; i < scopeStack.Count; i++)
        {
            var scope = scopeStack[i];
            candidates.Clear();
            // every prefix of the scope at a '.' boundary, and the whole scope
            for (var end = scope.IndexOf('.'); ; end = scope.IndexOf('.', end + 1))
            {
                var prefix = end < 0 ? scope : scope[..end];
                if (_byTarget.TryGetValue(prefix, out var rules))
                    foreach (var rule in rules)
                        if (ParentsMatch(rule.Parents, scopeStack, i)) candidates.Add(rule);
                if (end < 0) break;
            }
            if (candidates.Count == 0) continue;
            candidates.Sort(Ascending);
            foreach (var r in candidates)
            {
                if (r.Foreground is { } f) fg = f;
                if (r.Background is { } b) bg = b;
                if (r.FontStyle is { } fs) style = ParseStyle(fs); // "" (reset) clears inherited styles
            }
        }
        return new ResolvedTokenStyle(fg, bg, (style & Bold) != 0, (style & Italic) != 0, (style & Underline) != 0, (style & Strikethrough) != 0);
    }

    /// <summary>
    /// What the theme's rules give ONE scope on its own (no enclosing scopes): the foreground and font style set by the rules that match it,
    /// most specific last, each null when no matching rule sets it; null when no rule matches at all. Semantic highlighting uses it for VS
    /// Code's token-type → TextMate-scope fallback, where only a matching rule may colour a token (the grammar's colour stays otherwise).
    /// </summary>
    public (Rgba? Foreground, string? FontStyle)? MatchScope(string scope)
    {
        var candidates = new List<Rule>(4);
        for (var end = scope.IndexOf('.'); ; end = scope.IndexOf('.', end + 1))
        {
            var prefix = end < 0 ? scope : scope[..end];
            if (_byTarget.TryGetValue(prefix, out var rules))
                foreach (var rule in rules)
                    if (rule.Parents.Length == 0) candidates.Add(rule);
            if (end < 0) break;
        }
        if (candidates.Count == 0) return null;
        candidates.Sort(Ascending);
        Rgba? fg = null;
        string? style = null;
        foreach (var r in candidates)
        {
            if (r.Foreground is { } f) fg = f;
            if (r.FontStyle is { } fs) style = fs;
        }
        return (fg, style);
    }

    /// <summary>Least specific first, so applying in order lets the most specific rule have the last word.</summary>
    private static int Ascending(Rule a, Rule b)
    {
        if (a.Depth != b.Depth) return a.Depth.CompareTo(b.Depth);
        var n = Math.Min(a.Parents.Length, b.Parents.Length);
        for (var i = 0; i < n; i++)
        {
            var d = a.Parents[i].Length.CompareTo(b.Parents[i].Length);
            if (d != 0) return d;
        }
        if (a.Parents.Length != b.Parents.Length) return a.Parents.Length.CompareTo(b.Parents.Length);
        return a.Index.CompareTo(b.Index);
    }

    private static bool ParentsMatch(string[] parents, IReadOnlyList<string> stack, int index)
    {
        if (parents.Length == 0) return true;
        var p = 0;
        for (var a = index - 1; a >= 0; a--)
            if (ScopeMatches(stack[a], parents[p]) && ++p == parents.Length) return true;
        return false;
    }

    private static bool ScopeMatches(string scope, string selector) =>
        scope.Length >= selector.Length && scope.StartsWith(selector, StringComparison.Ordinal) && (scope.Length == selector.Length || scope[selector.Length] == '.');

    private static int ParseStyle(string fontStyle)
    {
        var flags = 0;
        foreach (var part in fontStyle.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            flags |= part switch { "bold" => Bold, "italic" => Italic, "underline" => Underline, "strikethrough" => Strikethrough, _ => 0 };
        return flags;
    }

    /// <summary>
    /// The coarse token class TextMate assigns: comment, string, regex or other — the innermost matching scope decides, and
    /// <c>meta.embedded</c> resets to other. Auto-closing (<c>notIn: ["string", "comment"]</c>) and bracket matching depend on it.
    /// </summary>
    public static TokenKind KindOf(IReadOnlyList<string> scopeStack)
    {
        for (var i = scopeStack.Count - 1; i >= 0; i--)
        {
            var s = scopeStack[i];
            if (ScopeMatches(s, "comment")) return TokenKind.Comment;
            if (ScopeMatches(s, "string")) return TokenKind.String;
            if (ScopeMatches(s, "regex")) return TokenKind.Regex;
            if (ScopeMatches(s, "meta.embedded")) return TokenKind.Other;
        }
        return TokenKind.Other;
    }
}

public enum TokenKind : byte { Other = 0, Comment = 1, String = 2, Regex = 3 }
