using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Platform.Workspace;

/// <summary>
/// One parsed <c>.gitignore</c> (or <c>.git/info/exclude</c>). Semantics follow gitignore(5):
/// blank lines and <c>#</c> comments are skipped · trailing unescaped spaces are dropped · <c>!</c> negates · a trailing <c>/</c> matches directories only ·
/// a pattern with a <c>/</c> at the start or in the middle is anchored to the file's directory, otherwise it matches at any depth ·
/// <c>*</c> and <c>?</c> never cross a <c>/</c> · <c>**</c> as a leading <c>**/</c>, a trailing <c>/**</c> or a middle <c>/**/</c> spans directories ·
/// <c>[a-z]</c> classes · backslash escapes · the LAST matching rule wins.
/// "A parent directory that is excluded cannot be re-included" is what the walker implements by not descending into excluded directories.
/// </summary>
internal sealed class GitIgnoreFile
{
    private readonly record struct Rule(bool Negate, bool DirOnly, string? Literal, Regex? Pattern);

    private readonly Rule[] _rules;

    private GitIgnoreFile(Rule[] rules) => _rules = rules;

    public int RuleCount => _rules.Length;

    public static GitIgnoreFile Parse(string text)
    {
        var rules = new List<Rule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;

            // trailing spaces are insignificant unless escaped with a backslash
            while (line.Length > 0 && line[^1] == ' ' && !(line.Length >= 2 && line[^2] == '\\')) line = line[..^1];
            if (line.Length == 0) continue;

            var negate = false;
            if (line[0] == '!') { negate = true; line = line[1..]; }
            if (line.Length == 0) continue;

            var dirOnly = false;
            if (line.EndsWith('/')) { dirOnly = true; line = line.TrimEnd('/'); }
            if (line.Length == 0) continue;

            var anchored = line.Contains('/');
            if (line[0] == '/') line = line[1..];
            if (line.Length == 0) continue;

            if (!anchored && IsPlain(line))
            {
                rules.Add(new Rule(negate, dirOnly, Unescape(line), null)); // a bare name: compare the last path segment, no regex needed
                continue;
            }

            try
            {
                var rx = new Regex(ToRegex(line, anchored), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                rules.Add(new Rule(negate, dirOnly, null, rx));
            }
            catch (ArgumentException) { /* a pattern we cannot express: ignore that one line, as git ignores malformed ones */ }
            catch (NotSupportedException) { }
        }
        return new GitIgnoreFile([.. rules]);
    }

    private static bool IsPlain(string p) => p.IndexOfAny(['*', '?', '[', '\\']) < 0;

    private static string Unescape(string p) => p.Replace("\\", "", StringComparison.Ordinal);

    private static string ToRegex(string pattern, bool anchored)
    {
        var sb = new StringBuilder(anchored ? "^" : "^(?:.*/)?");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < pattern.Length) { sb.Append(Regex.Escape(pattern[i + 1].ToString())); i++; }
                    else sb.Append(@"\\");
                    break;

                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        var atStart = i == 0;
                        var afterSlash = i > 0 && pattern[i - 1] == '/';
                        var beforeSlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                        var atEnd = i + 2 == pattern.Length;
                        if (atStart && beforeSlash) { sb.Append("(?:.*/)?"); i += 2; }                 // "**/x"   : any leading directories
                        else if (afterSlash && beforeSlash) { sb.Append("(?:.*/)?"); i += 2; }          // "a/**/b" : zero or more directories
                        else if (afterSlash && atEnd) { sb.Length--; sb.Append("/.+"); i += 1; }        // "a/**"   : everything inside a (not a itself)
                        else { sb.Append("[^/]*"); i += 1; }                                             // anywhere else "**" is just "*"
                    }
                    else sb.Append("[^/]*");
                    break;

                case '?': sb.Append("[^/]"); break;

                case '[':
                {
                    var j = i + 1;
                    var negated = j < pattern.Length && pattern[j] is '!' or '^';
                    if (negated) j++;
                    if (j < pattern.Length && pattern[j] == ']') j++; // a ']' right after '[' (or '[!') is a literal member
                    while (j < pattern.Length && pattern[j] != ']') j++;
                    if (j >= pattern.Length) { sb.Append(@"\["); break; } // no closing bracket: literal '['
                    var body = pattern[(i + 1 + (negated ? 1 : 0))..j].Replace(@"\", @"\\", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal);
                    sb.Append(negated ? "[^/" : "[").Append(body).Append(']');
                    i = j;
                    break;
                }

                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.Append('$').ToString();
    }

    /// <summary>Does any rule decide this path? <paramref name="ignored"/> is the verdict of the LAST matching rule (negations un-ignore).</summary>
    /// <param name="relPath">Path relative to this file's directory, '/' separated, no leading slash.</param>
    public bool TryMatch(string relPath, bool isDir, out bool ignored)
    {
        for (var i = _rules.Length - 1; i >= 0; i--)
        {
            ref readonly var r = ref _rules[i];
            if (r.DirOnly && !isDir) continue;
            if (r.Literal is not null ? NameEquals(relPath, r.Literal) : r.Pattern!.IsMatch(relPath))
            {
                ignored = !r.Negate;
                return true;
            }
        }
        ignored = false;
        return false;
    }

    private static bool NameEquals(string rel, string name)
    {
        if (rel.Length < name.Length || !rel.EndsWith(name, StringComparison.Ordinal)) return false;
        return rel.Length == name.Length || rel[rel.Length - name.Length - 1] == '/';
    }
}

/// <summary>The ignore files in effect for a directory: its own, then its ancestors' (nearest wins). Immutable, so siblings share their parents' nodes.</summary>
internal sealed class IgnoreChain(GitIgnoreFile file, string baseRel, IgnoreChain? parent)
{
    public GitIgnoreFile File { get; } = file;
    /// <summary>The directory this file lives in, relative to the walk root ("" for the root itself).</summary>
    public string BaseRel { get; } = baseRel;
    public IgnoreChain? Parent { get; } = parent;

    /// <summary><paramref name="relFromRoot"/> is the entry's path relative to the walk root.</summary>
    public static bool IsIgnored(IgnoreChain? chain, string relFromRoot, bool isDir)
    {
        for (var node = chain; node is not null; node = node.Parent)
        {
            var candidate = node.BaseRel.Length == 0 ? relFromRoot : relFromRoot[(node.BaseRel.Length + 1)..];
            if (node.File.TryMatch(candidate, isDir, out var ignored)) return ignored; // the nearest file with an opinion decides
        }
        return false;
    }
}
