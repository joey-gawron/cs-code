using System.Text;
using System.Text.RegularExpressions;
using CsCode.Editor.TreeSitter.Native;

namespace CsCode.Editor.TreeSitter.Queries;

/// <summary>
/// A compiled tree-sitter query (<c>highlights.scm</c>, <c>injections.scm</c>) plus what the C library leaves to its caller: the
/// predicates (<c>#eq?</c>, <c>#match?</c>, <c>#lua-match?</c>, <c>#any-of?</c>, <c>#contains?</c>, <c>#has-ancestor?</c>,
/// <c>#has-parent?</c>, <c>#kind-eq?</c> and their <c>not-</c>/<c>any-</c> forms) and the directives (<c>#set!</c>, <c>#offset!</c>), parsed
/// once per pattern. Immutable after construction and therefore shareable between threads (tree-sitter queries are; only cursors are not).
/// </summary>
public sealed unsafe class TsQuery : IDisposable
{
    private IntPtr _handle;

    private TsQuery(IntPtr handle, string[] captureNames, PatternInfo[] patterns, IReadOnlyList<string> warnings)
    {
        _handle = handle;
        CaptureNames = captureNames;
        Patterns = patterns;
        Warnings = warnings;
    }

    internal IntPtr Handle => _handle;

    public IReadOnlyList<string> CaptureNames { get; }

    internal PatternInfo[] Patterns { get; }

    /// <summary>Predicates the engine does not evaluate (kept as "always true"), untranslatable Lua patterns, etc.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public int PatternCount => Patterns.Length;

    /// <summary>Compiles <paramref name="source"/> for <paramref name="language"/>. Returns null and an error ("NodeType at line 12: ...") when it does not compile.</summary>
    public static TsQuery? Compile(TsLanguage language, string source, out string? error)
    {
        error = null;
        var utf8 = Encoding.UTF8.GetBytes(source);
        uint errorOffset;
        TSQueryError errorType;
        IntPtr handle;
        fixed (byte* p = utf8)
        {
            handle = TsApi.ts_query_new(language.Handle, p, (uint)utf8.Length, &errorOffset, &errorType);
        }
        if (handle == IntPtr.Zero)
        {
            var offset = (int)Math.Min(errorOffset, (uint)utf8.Length);
            var prefix = Encoding.UTF8.GetString(utf8, 0, offset);
            var line = prefix.Count(c => c == '\n') + 1;
            var lineStart = prefix.LastIndexOf('\n') + 1;
            var text = source.AsSpan(lineStart, Math.Min(80, source.Length - lineStart));
            var nl = text.IndexOf('\n');
            error = $"{errorType} error at line {line}: {(nl >= 0 ? text[..nl] : text).ToString().Trim()}";
            return null;
        }

        var captureCount = TsApi.ts_query_capture_count(handle);
        var names = new string[captureCount];
        for (uint i = 0; i < captureCount; i++)
        {
            uint len;
            var s = TsApi.ts_query_capture_name_for_id(handle, i, &len);
            names[i] = TsApi.Utf8(s, len);
        }
        var stringCount = TsApi.ts_query_string_count(handle);
        var strings = new string[stringCount];
        for (uint i = 0; i < stringCount; i++)
        {
            uint len;
            var s = TsApi.ts_query_string_value_for_id(handle, i, &len);
            strings[i] = TsApi.Utf8(s, len);
        }

        var warnings = new List<string>();
        var patternCount = TsApi.ts_query_pattern_count(handle);
        var patterns = new PatternInfo[patternCount];
        for (uint p = 0; p < patternCount; p++)
        {
            uint stepCount;
            var steps = TsApi.ts_query_predicates_for_pattern(handle, p, &stepCount);
            patterns[p] = PatternInfo.Parse(new ReadOnlySpan<TSQueryPredicateStep>(steps, (int)stepCount), names, strings, warnings);
        }
        return new TsQuery(handle, names, patterns, warnings);
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) TsApi.ts_query_delete(h);
    }
}

internal enum PredicateKind : byte
{
    Eq,
    Match,
    AnyOf,
    Contains,
    HasAncestor,
    HasParent,
    KindEq,
}

/// <summary>One parsed predicate. <see cref="Any"/>: for a quantified capture, ONE node satisfying it is enough (default: all nodes).</summary>
internal sealed record Predicate(PredicateKind Kind, bool Negated, bool Any, int Capture, int OtherCapture, string[] Strings, Regex? Regex, HashSet<string>? Set);

/// <summary>
/// A directive that rewrites a capture's TEXT (Neovim's <c>#gsub!</c> and <c>#trim!</c>), applied in query order. The highlighter uses the text of
/// one capture only: <c>@injection.language</c> (Nix's <c>/* bash */ ''…''</c>, HTML's <c>&lt;script type="text/javascript"&gt;</c>).
/// </summary>
/// <param name="Pattern">For <c>gsub!</c>: the Lua pattern, translated WITH its captures; null for <c>trim!</c>.</param>
/// <param name="Replacement">For <c>gsub!</c>: the Lua replacement (<c>%1</c>…<c>%9</c>, <c>%0</c> the whole match, <c>%%</c> a percent sign).</param>
internal sealed record TextDirective(Regex? Pattern, string Replacement)
{
    public string Apply(string text)
    {
        if (Pattern is null) return text.Trim(); // trim!: the language name without the whitespace around it
        try
        {
            return Pattern.Replace(text, m => Expand(m, Replacement));
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>Lua's <c>string.gsub</c> replacement string for one match.</summary>
    private static string Expand(Match m, string replacement)
    {
        if (!replacement.Contains('%', StringComparison.Ordinal)) return replacement;
        var sb = new StringBuilder(replacement.Length + 16);
        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];
            if (c != '%' || i + 1 >= replacement.Length)
            {
                sb.Append(c);
                continue;
            }
            var n = replacement[++i];
            if (char.IsAsciiDigit(n))
            {
                var index = n - '0';
                // Lua: %1 in a pattern without captures is the whole match
                if (index == 0 || (index == 1 && m.Groups.Count == 1)) sb.Append(m.Value);
                else if (index < m.Groups.Count) sb.Append(m.Groups[index].Value);
            }
            else sb.Append(n); // %% and any other escaped character: the character itself
        }
        return sb.ToString();
    }
}

/// <summary>Per-pattern metadata: predicates, <c>#set!</c> properties (pattern-wide and per capture), <c>#offset!</c> adjustments and text directives.</summary>
internal sealed class PatternInfo
{
    public static readonly PatternInfo Empty = new([], null, null, null);

    public PatternInfo(Predicate[] predicates, Dictionary<string, string?>? properties, Dictionary<(int Capture, string Key), string?>? captureProperties, Dictionary<int, int[]>? offsets,
        Dictionary<int, List<TextDirective>>? textDirectives = null)
    {
        Predicates = predicates;
        Properties = properties;
        CaptureProperties = captureProperties;
        Offsets = offsets;
        TextDirectives = textDirectives;
        Priority = properties is not null && properties.TryGetValue("priority", out var p) && int.TryParse(p, out var v) ? v : DefaultPriority;
    }

    /// <summary>capture → its <c>#gsub!</c>/<c>#trim!</c> directives, in query order.</summary>
    public Dictionary<int, List<TextDirective>>? TextDirectives { get; }

    /// <summary><paramref name="text"/> of capture <paramref name="capture"/> after this pattern's text directives (unchanged when it has none).</summary>
    public string CaptureText(int capture, string text)
    {
        if (TextDirectives is null || !TextDirectives.TryGetValue(capture, out var directives)) return text;
        foreach (var d in directives) text = d.Apply(text);
        return text;
    }

    public const int DefaultPriority = 100;

    public Predicate[] Predicates { get; }
    public Dictionary<string, string?>? Properties { get; }
    public Dictionary<(int Capture, string Key), string?>? CaptureProperties { get; }
    /// <summary>capture → [startRow, startCol, endRow, endCol] deltas (<c>#offset!</c>), columns in characters.</summary>
    public Dictionary<int, int[]>? Offsets { get; }
    public int Priority { get; }

    public string? Property(string key) => Properties is not null && Properties.TryGetValue(key, out var v) ? v : null;
    public bool HasProperty(string key) => Properties?.ContainsKey(key) == true;

    public static PatternInfo Parse(ReadOnlySpan<TSQueryPredicateStep> steps, string[] captures, string[] strings, List<string> warnings)
    {
        if (steps.Length == 0) return Empty;
        var predicates = new List<Predicate>();
        Dictionary<string, string?>? props = null;
        Dictionary<(int, string), string?>? captureProps = null;
        Dictionary<int, int[]>? offsets = null;
        Dictionary<int, List<TextDirective>>? textDirectives = null;
        void AddDirective(int capture, TextDirective directive)
        {
            textDirectives ??= new();
            if (!textDirectives.TryGetValue(capture, out var list)) textDirectives[capture] = list = new();
            list.Add(directive);
        }
        var args = new List<TSQueryPredicateStep>();
        var i = 0;
        while (i < steps.Length)
        {
            args.Clear();
            while (i < steps.Length && steps[i].Type != TSQueryPredicateStepType.Done) args.Add(steps[i++]);
            i++; // the Done step
            if (args.Count == 0 || args[0].Type != TSQueryPredicateStepType.String) continue;
            var op = strings[args[0].ValueId];
            var rest = args.Skip(1).ToArray();
            string Str(TSQueryPredicateStep s) => s.Type == TSQueryPredicateStepType.String ? strings[s.ValueId] : captures[s.ValueId];

            if (op.EndsWith('!'))
            {
                switch (op)
                {
                    case "set!":
                        if (rest.Length >= 1 && rest[0].Type == TSQueryPredicateStepType.Capture)
                        {
                            if (rest.Length >= 2)
                            {
                                captureProps ??= new();
                                captureProps[((int)rest[0].ValueId, Str(rest[1]))] = rest.Length >= 3 ? Str(rest[2]) : null;
                            }
                        }
                        else if (rest.Length >= 1)
                        {
                            props ??= new(StringComparer.Ordinal);
                            props[Str(rest[0])] = rest.Length >= 2 ? Str(rest[1]) : null;
                        }
                        break;
                    case "offset!":
                        if (rest.Length >= 5 && rest[0].Type == TSQueryPredicateStepType.Capture)
                        {
                            var d = new int[4];
                            for (var k = 0; k < 4; k++) _ = int.TryParse(Str(rest[k + 1]), out d[k]);
                            offsets ??= new();
                            offsets[(int)rest[0].ValueId] = d;
                        }
                        break;
                    case "gsub!":
                        // #gsub! @capture "lua pattern" "replacement" rewrites the capture's text (Neovim); the highlighter reads @injection.language's
                        if (rest.Length >= 3 && rest[0].Type == TSQueryPredicateStepType.Capture)
                        {
                            var source = Str(rest[1]);
                            if (LuaPattern.CompileWithCaptures(source) is { } regex) AddDirective((int)rest[0].ValueId, new TextDirective(regex, Str(rest[2])));
                            else warnings.Add($"#gsub! \"{source}\" could not be translated (the text is left as it is)");
                        }
                        break;
                    case "trim!":
                        // Neovim trims the capture's RANGE; for the one capture whose text is used (a language name) that is its text without
                        // the whitespace around it
                        if (rest.Length >= 1 && rest[0].Type == TSQueryPredicateStepType.Capture) AddDirective((int)rest[0].ValueId, new TextDirective(null, ""));
                        break;
                    default:
                        // select-adjacent!, inject-language!, downcase!... only change text/metadata the highlighter does not use
                        break;
                }
                continue;
            }

            var negated = false;
            var any = false;
            var name = op;
            if (name.StartsWith("not-", StringComparison.Ordinal))
            {
                negated = true;
                name = name[4..];
            }
            if (name.StartsWith("any-", StringComparison.Ordinal) && name != "any-of?")
            {
                any = true;
                name = name[4..];
            }
            var capture = rest.Length > 0 && rest[0].Type == TSQueryPredicateStepType.Capture ? (int)rest[0].ValueId : -1;
            var stringArgs = rest.Skip(1).Where(s => s.Type == TSQueryPredicateStepType.String).Select(s => strings[s.ValueId]).ToArray();
            switch (name)
            {
                case "eq?":
                    var other = rest.Length > 1 && rest[1].Type == TSQueryPredicateStepType.Capture ? (int)rest[1].ValueId : -1;
                    predicates.Add(new Predicate(PredicateKind.Eq, negated, any, capture, other, stringArgs, null, null));
                    break;
                case "match?":
                case "vim-match?":
                    predicates.Add(Regexp(PredicateKind.Match, negated, any, capture, stringArgs, isLua: false, warnings, op));
                    break;
                case "lua-match?":
                    predicates.Add(Regexp(PredicateKind.Match, negated, any, capture, stringArgs, isLua: true, warnings, op));
                    break;
                case "any-of?":
                    predicates.Add(new Predicate(PredicateKind.AnyOf, negated, any, capture, -1, stringArgs, null, new HashSet<string>(stringArgs, StringComparer.Ordinal)));
                    break;
                case "contains?":
                    predicates.Add(new Predicate(PredicateKind.Contains, negated, any, capture, -1, stringArgs, null, null));
                    break;
                case "has-ancestor?":
                    predicates.Add(new Predicate(PredicateKind.HasAncestor, negated, any, capture, -1, stringArgs, null, new HashSet<string>(stringArgs, StringComparer.Ordinal)));
                    break;
                case "has-parent?":
                    predicates.Add(new Predicate(PredicateKind.HasParent, negated, any, capture, -1, stringArgs, null, new HashSet<string>(stringArgs, StringComparer.Ordinal)));
                    break;
                case "kind-eq?":
                    predicates.Add(new Predicate(PredicateKind.KindEq, negated, any, capture, -1, stringArgs, null, new HashSet<string>(stringArgs, StringComparer.Ordinal)));
                    break;
                default:
                    warnings.Add($"predicate #{op} is not evaluated (treated as true)");
                    break;
            }
        }
        return new PatternInfo(predicates.ToArray(), props, captureProps, offsets, textDirectives);
    }

    private static Predicate Regexp(PredicateKind kind, bool negated, bool any, int capture, string[] args, bool isLua, List<string> warnings, string op)
    {
        Regex? regex = null;
        var source = args.Length > 0 ? args[0] : string.Empty;
        try
        {
            regex = isLua
                ? LuaPattern.Compile(source)
                : new Regex(source, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            regex = null;
        }
        if (regex is null) warnings.Add($"#{op} \"{source}\" could not be translated (the pattern never matches)");
        return new Predicate(kind, negated, any, capture, -1, args, regex, null);
    }
}
