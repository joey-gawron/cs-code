namespace CsCode.Panels.Search.Engine;

/// <summary>
/// One parsed <c>.gitignore</c>. Implements the parts of the format that matter for searching: comments, <c>!</c> negation,
/// trailing-<c>/</c> (directories only), leading/middle <c>/</c> anchoring, <c>*</c> <c>?</c> <c>[a-z]</c> and <c>**</c>, escapes.
/// The last matching rule wins. Not implemented: global ignore files and <c>.git/info/exclude</c>.
/// Matching is hand-written (no regex per rule) so a 500-rule file costs nothing to load.
/// </summary>
public sealed class IgnoreRules
{
    private sealed record Rule(string[] Segments, bool Negated, bool DirectoryOnly, bool Anchored);

    private readonly Rule[] _rules;

    private IgnoreRules(Rule[] rules) => _rules = rules;

    public int Count => _rules.Length;

    public static IgnoreRules Parse(string text)
    {
        var rules = new List<Rule>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // Trailing spaces are ignored unless escaped with a backslash.
            var end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\')) end--;
            line = line[..end];
            if (line.Length == 0 || line[0] == '#') continue;

            var negated = false;
            if (line[0] == '!') { negated = true; line = line[1..]; }
            else if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal)) line = line[1..];
            if (line.Length == 0) continue;

            var dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0) continue;

            var anchored = line.Contains('/');
            if (line.StartsWith('/')) line = line[1..];
            if (line.Length == 0) continue;

            var segs = line.Split('/', StringSplitOptions.RemoveEmptyEntries);
            rules.Add(new Rule(segs, negated, dirOnly, anchored));
        }
        return new IgnoreRules(rules.ToArray());
    }

    /// <summary>
    /// null = no rule matches; true = ignored; false = explicitly re-included by a <c>!</c> rule.
    /// <paramref name="relativePath"/> is relative to the folder holding the .gitignore, forward slashes, no leading slash.
    /// </summary>
    public bool? Evaluate(string relativePath, bool isDirectory)
    {
        if (_rules.Length == 0) return null;
        var path = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0) return null;
        for (var i = _rules.Length - 1; i >= 0; i--)
        {
            var r = _rules[i];
            if (r.DirectoryOnly && !isDirectory) continue;
            var hit = r.Anchored ? MatchSegments(r.Segments, 0, path, 0) : SegmentMatch(r.Segments[^1], path[^1]);
            if (hit) return !r.Negated;
        }
        return null;
    }

    private static bool MatchSegments(string[] pattern, int pi, string[] path, int si)
    {
        while (true)
        {
            if (pi == pattern.Length) return si == path.Length;
            if (pattern[pi] == "**")
            {
                if (pi == pattern.Length - 1) return si < path.Length; // "a/**" = everything inside a
                for (var k = si; k <= path.Length; k++)
                    if (MatchSegments(pattern, pi + 1, path, k)) return true;
                return false;
            }
            if (si == path.Length || !SegmentMatch(pattern[pi], path[si])) return false;
            pi++;
            si++;
        }
    }

    /// <summary>Wildcard match of one path segment: <c>*</c> (any run, not '/'), <c>?</c>, <c>[abc]</c>/<c>[!a-z]</c>, <c>\x</c> escape.</summary>
    internal static bool SegmentMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length)
            {
                var c = pattern[p];
                if (c == '*') { starP = ++p; starT = t; continue; }
                if (c == '?') { p++; t++; continue; }
                if (c == '[' && TryClass(pattern, p, text[t], out var classEnd, out var inClass))
                {
                    if (inClass) { p = classEnd; t++; continue; }
                }
                else
                {
                    if (c == '\\' && p + 1 < pattern.Length) c = pattern[++p];
                    if (c == text[t]) { p++; t++; continue; }
                }
            }
            if (starP < 0) return false;
            p = starP;
            t = ++starT;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>Parses the character class starting at <paramref name="start"/> ('['); false when it is not a well-formed class.</summary>
    private static bool TryClass(ReadOnlySpan<char> pattern, int start, char ch, out int end, out bool matched)
    {
        end = start;
        matched = false;
        var i = start + 1;
        var negate = false;
        if (i < pattern.Length && (pattern[i] == '!' || pattern[i] == '^')) { negate = true; i++; }
        var first = true;
        var any = false;
        while (i < pattern.Length && (pattern[i] != ']' || first))
        {
            first = false;
            var lo = pattern[i];
            if (lo == '\\' && i + 1 < pattern.Length) lo = pattern[++i];
            var hi = lo;
            if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']') { hi = pattern[i + 2]; i += 2; }
            if (ch >= lo && ch <= hi) any = true;
            i++;
        }
        if (i >= pattern.Length) return false; // unterminated: treat '[' literally
        end = i + 1;
        matched = any != negate;
        return true;
    }
}
