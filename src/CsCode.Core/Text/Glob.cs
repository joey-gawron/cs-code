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
    /// Matches a path against a pattern. Patterns without '/' match the file name only; patterns with '/' match the
    /// full (forward-slash) path, anchored at any directory boundary unless they start with '/'.
    /// </summary>
    public static bool IsMatch(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var p = pattern.Replace('\\', '/');
        var normalized = path.Replace('\\', '/');
        var target = p.Contains('/') ? normalized : normalized[(normalized.LastIndexOf('/') + 1)..];
        return GetRegex(p).IsMatch(target);
    }

    private static Regex GetRegex(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var rx)) return rx;
        rx = new Regex(ToRegex(pattern), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
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
