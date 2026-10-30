using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Core.Text;

/// <summary>
/// VS Code–style glob matching (<c>**</c>, <c>*</c>, <c>?</c>, <c>{a,b}</c>, <c>[abc]</c>) used by document selectors,
/// files.exclude/watcherExclude and contribution patterns. Compiled regexes are cached in a BOUNDED cache.
/// </summary>
public static class Glob
{
    private const int MaxCached = 256;
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    /// <summary>
    /// Bound on one match. The translated globs have no nested quantifiers, so a match takes microseconds; the timeout only caps a pathological
    /// pattern (many <c>**/</c> in a row against a long path), which then does not match.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Matches a path against a pattern. Patterns without '/' match the file name only; patterns with '/' match the
    /// full (forward-slash) path, anchored at any directory boundary unless they start with '/'.
    /// </summary>
    public static bool IsMatch(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var p = pattern.Replace('\\', '/'); // the same instance when there is no backslash: no allocation on the hot path
        var normalized = path.Replace('\\', '/');
        var target = p.Contains('/') ? normalized.AsSpan() : normalized.AsSpan(normalized.LastIndexOf('/') + 1);
        try { return GetRegex(p).IsMatch(target); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Regex GetRegex(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var rx)) return rx;
        // Compiled backtracking, not NonBacktracking: a NonBacktracking regex keeps ~150 KB of symbolic-automaton caches once it has matched
        // (measured in the app with 20 documents: the ~40 cached globs of the language-server selectors held 6 MB; docs/MEMORY.md). The
        // interpreter would keep a few KB but was 10x slower on path globs; compiled is both small and 2-3x faster than NonBacktracking was.
        rx = new Regex(ToRegex(pattern), RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
        if (Cache.Count >= MaxCached) Cache.Clear(); // crude but bounded: globs are few and cheap to rebuild
        Cache[pattern] = rx;
        return rx;
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
