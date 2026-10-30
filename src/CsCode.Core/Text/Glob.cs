using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Core.Text;

/// <summary>
/// VS Code–style glob matching (<c>**</c>, <c>*</c>, <c>?</c>, <c>{a,b}</c>, <c>[abc]</c>) used by document selectors,
/// files.exclude/watcherExclude and contribution patterns. Compiled matchers are cached in a BOUNDED cache.
/// </summary>
public static class Glob
{
    private const int MaxCached = 256;
    private static readonly ConcurrentDictionary<string, GlobRegex> Cache = new();

    /// <summary>
    /// Matches a path against a pattern. Patterns without '/' match the file name only; patterns with '/' match the
    /// full (forward-slash) path, anchored at any directory boundary unless they start with '/'. Never throws: patterns come from language
    /// servers and workspace settings, and a malformed one (an unterminated or reversed class such as <c>[z-a]</c>) simply matches nothing.
    /// </summary>
    public static bool IsMatch(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var p = pattern.Replace('\\', '/'); // the same instance when there is no backslash: no allocation on the hot path
        var normalized = path.Replace('\\', '/');
        var target = p.Contains('/') ? normalized.AsSpan() : normalized.AsSpan(normalized.LastIndexOf('/') + 1);
        return GetMatcher(p).IsMatch(target);
    }

    private static GlobRegex GetMatcher(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var matcher)) return matcher;
        // lsp-core-3 / T38: a linear-time NFA (GlobRegex), not a Regex. The compiled backtracking regex used here before was polynomial on patterns
        // such as **/**/**/…/x (a server's document selector cost 250 ms per completion request until its timeout made it silently fail), and a
        // NonBacktracking regex keeps ~150 KB of automaton caches per pattern once it has matched (the ~40 language-server selector globs held 6 MB;
        // docs/MEMORY.md). A pattern that cannot be compiled ([z-a]) is cached as one that never matches instead of throwing into the caller.
        matcher = GlobRegex.CompileOrNever(ToRegex(pattern));
        if (Cache.Count >= MaxCached) Cache.Clear(); // crude but bounded: globs are few and cheap to rebuild
        Cache[pattern] = matcher;
        return matcher;
    }

    internal static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var anchoredAtRoot = pattern.StartsWith('/');
        if (pattern.Contains('/') && !anchoredAtRoot && !pattern.StartsWith("**", StringComparison.Ordinal))
            sb.Append("(?:.*/)?");

        var braceDepth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '{': braceDepth++; sb.Append("(?:"); break;
                case '}' when braceDepth > 0: braceDepth--; sb.Append(')'); break;
                case ',' when braceDepth > 0: sb.Append('|'); break;
                case '[':
                {
                    var end = pattern.IndexOf(']', i + 1);
                    if (end < 0) { sb.Append(@"\["); break; }
                    var body = pattern[(i + 1)..end];
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    sb.Append('[').Append(body.Replace(@"\", @"\\")).Append(']');
                    i = end;
                    break;
                }
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        while (braceDepth-- > 0) sb.Append(')');
        return sb.Append('$').ToString();
    }
}
