using System.Text.RegularExpressions;

namespace CsCode.SpellCheck.Engine;

/// <summary>
/// cspell's in-document directives, written in a comment anywhere in the file (prefix <c>cspell:</c>, <c>cSpell:</c>, <c>spell-checker:</c>
/// or <c>spellchecker:</c>): <c>ignore w1 w2</c> and <c>words w1 w2</c> accept words in this file, <c>flagWords w</c> forbids one,
/// <c>disable</c> … <c>enable</c> turn checking off for the lines between, <c>disable-next-line</c> and <c>disable-line</c> for one line.
/// The "Ignore … in this file" quick fix writes an <c>ignore</c> directive.
/// </summary>
public sealed partial class InlineDirectives
{
    public static readonly InlineDirectives None = new([], [], []);

    private readonly HashSet<string> _words;
    private readonly HashSet<string> _flagged;
    private readonly (int First, int Last)[] _disabled; // line ranges, inclusive, sorted

    private InlineDirectives(HashSet<string> words, HashSet<string> flagged, (int, int)[] disabled)
    {
        _words = words;
        _flagged = flagged;
        _disabled = disabled;
    }

    public IReadOnlySet<string> Words => _words;
    public IReadOnlySet<string> FlaggedWords => _flagged;

    [GeneratedRegex(@"(?:cspell|spell-?checker)\s*:\s*(?<cmd>disable-next-line|disable-next|disable-line|disable|enable|ignore|words|word|flagWords|flag-words)\b(?<rest>[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Directive();

    /// <summary>
    /// The directives of <paramref name="text"/>; <paramref name="lineOf"/> maps an offset to its line. A cheap scan first: most files have none.
    /// </summary>
    public static InlineDirectives Parse(string text, Func<int, int> lineOf)
    {
        if (text.IndexOf("spell", StringComparison.OrdinalIgnoreCase) < 0) return None;
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new List<(int, int)>();
        int? disabledFrom = null;
        try
        {
            foreach (Match m in Directive().Matches(text))
            {
                var line = lineOf(m.Index);
                switch (m.Groups["cmd"].Value.ToLowerInvariant())
                {
                    case "ignore" or "words" or "word":
                        foreach (var w in WordsOf(m.Groups["rest"].Value)) words.Add(w);
                        break;
                    case "flagwords" or "flag-words":
                        foreach (var w in WordsOf(m.Groups["rest"].Value)) flagged.Add(w);
                        break;
                    case "disable-next-line" or "disable-next":
                        disabled.Add((line + 1, line + 1));
                        break;
                    case "disable-line":
                        disabled.Add((line, line));
                        break;
                    case "disable":
                        disabledFrom ??= line;
                        break;
                    case "enable":
                        if (disabledFrom is { } from) disabled.Add((from, line));
                        disabledFrom = null;
                        break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // a pathological file: the directives found so far apply
        }
        if (disabledFrom is { } open) disabled.Add((open, int.MaxValue));
        if (words.Count == 0 && flagged.Count == 0 && disabled.Count == 0) return None;
        return new InlineDirectives(words, flagged, disabled.OrderBy(r => r.Item1).ToArray());
    }

    public bool IsDisabled(int line)
    {
        foreach (var (first, last) in _disabled)
        {
            if (first > line) return false;
            if (line <= last) return true;
        }
        return false;
    }

    /// <summary>The words of a directive's argument: up to the end of the comment (<c>*/</c>, <c>--&gt;</c>), split at spaces and commas.</summary>
    private static IEnumerable<string> WordsOf(string rest)
    {
        foreach (var end in (string[])["*/", "-->", "#}", "%}", "\"\"\"", "'''"])
        {
            var cut = rest.IndexOf(end, StringComparison.Ordinal);
            if (cut >= 0) rest = rest[..cut];
        }
        foreach (var w in rest.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = w.Trim('"', '\'', '`');
            if (t.Length > 0) yield return t;
        }
    }
}
