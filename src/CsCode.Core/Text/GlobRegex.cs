using System.Buffers;

namespace CsCode.Core.Text;

/// <summary>
/// A linear-time matcher for the small regular-expression subset that glob translators emit (Core's <see cref="Glob"/>, Platform's .gitignore and
/// .editorconfig readers, the Editor's .editorconfig reader): literal characters and escapes, <c>.</c>, character classes with ranges and <c>^</c>
/// negation, groups <c>(?:…)</c> and <c>(…)</c>, alternation <c>|</c>, the quantifiers <c>*</c>, <c>+</c>, <c>?</c> (a lazy suffix is accepted), a
/// leading <c>^</c> and a trailing <c>$</c>. Anything else (back-references, look-arounds, <c>\d</c>/<c>\w</c>, counted repetition…) is refused by
/// <see cref="Compile"/> with an <see cref="ArgumentException"/>.
/// <para>
/// Why not <see cref="System.Text.RegularExpressions.Regex"/> (lsp-core-3, T38): a backtracking regex is polynomial or worse on patterns such as
/// <c>**/**/**/…/x</c>, and a timeout only turns that into a 250 ms stall per call that silently does not match; the <c>NonBacktracking</c> engine is
/// linear but keeps ~150 KB of lazily built automaton per pattern once it has matched. This compiles to a Thompson NFA and simulates it (a Pike VM
/// without captures): O(pattern × text) for every pattern, nothing retained but the compiled program (a few hundred bytes), and no allocation per
/// match (the state lists live on the stack, or in pooled arrays for very long patterns). A required literal prefix / suffix rejects most paths
/// before the simulation starts. Case-sensitive and culture-invariant, like the regexes it replaces. Immutable and thread-safe.
/// </para>
/// </summary>
public sealed class GlobRegex
{
    /// <summary>Patterns longer than this are refused (a glob of several kilobytes is not a glob; it would only make matching slow).</summary>
    public const int MaxPatternLength = 8192;

    private const int StackStates = 256;

    private enum Op : byte { Char, Any, Class, Split, Jump, Match }

    private readonly Op[] _op;
    private readonly char[] _ch;
    private readonly int[] _x;
    private readonly int[] _y;
    private readonly CharClass[] _classes;
    private readonly bool _anchoredStart;
    private readonly bool _anchoredEnd;
    private readonly bool _never;
    private readonly string _prefix;
    private readonly string _suffix;

    // Bit-parallel form, used when the program has at most 63 consuming states (every glob in practice): each consuming state (and Match) is one
    // bit; _follow[b] is the epsilon closure after consuming state b, _start the closure of the entry; ASCII characters are mapped to equivalence
    // classes whose masks say which states accept them. One step is then a few word operations per active state instead of a list walk.
    private readonly ulong[]? _follow;
    private readonly int[]? _pcOfBit;
    private readonly ulong _start;
    private readonly ulong _matchMask;
    private readonly byte[]? _asciiClass;
    private readonly ulong[]? _classMasks;

    /// <summary>A pattern that matches nothing (what callers use for a pattern they could not compile).</summary>
    public static GlobRegex Never { get; } = new("(?!)");

    /// <summary>The pattern as given to <see cref="Compile"/>.</summary>
    public string Pattern { get; }

    private GlobRegex(string never)
    {
        Pattern = never;
        _never = true;
        _op = [Op.Match];
        _ch = [default];
        _x = [0];
        _y = [0];
        _classes = [];
        _prefix = _suffix = "";
    }

    private GlobRegex(string pattern, Builder b, bool anchoredStart, bool anchoredEnd, string prefix, string suffix)
    {
        Pattern = pattern;
        _op = [.. b.Ops];
        _ch = [.. b.Chars];
        _x = [.. b.X];
        _y = [.. b.Y];
        _classes = [.. b.Classes];
        _anchoredStart = anchoredStart;
        _anchoredEnd = anchoredEnd;
        _prefix = prefix;
        _suffix = suffix;
        (_follow, _pcOfBit, _start, _matchMask, _asciiClass, _classMasks) = BuildBitParallel();
    }

    private (ulong[]?, int[]?, ulong, ulong, byte[]?, ulong[]?) BuildBitParallel()
    {
        var n = _op.Length;
        var bitOf = new int[n];
        var pcs = new List<int>();
        for (var pc = 0; pc < n; pc++)
        {
            bitOf[pc] = -1;
            if (_op[pc] is Op.Char or Op.Any or Op.Class or Op.Match)
            {
                if (pcs.Count == 64) return default;
                bitOf[pc] = pcs.Count;
                pcs.Add(pc);
            }
        }

        var visited = new bool[n];
        var stack = new Stack<int>();
        ulong Closure(int from)
        {
            Array.Clear(visited);
            ulong mask = 0;
            stack.Push(from);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                if (p >= n || visited[p]) continue;
                visited[p] = true;
                switch (_op[p])
                {
                    case Op.Jump: stack.Push(_x[p]); break;
                    case Op.Split: stack.Push(_x[p]); stack.Push(_y[p]); break;
                    default: mask |= 1UL << bitOf[p]; break;
                }
            }
            return mask;
        }

        var follow = new ulong[pcs.Count];
        ulong matchMask = 0;
        for (var b = 0; b < pcs.Count; b++)
        {
            if (_op[pcs[b]] == Op.Match) matchMask |= 1UL << b;
            else follow[b] = Closure(pcs[b] + 1);
        }

        // ASCII equivalence classes: characters that the same consuming states accept share a mask.
        var asciiClass = new byte[128];
        var masks = new List<ulong>();
        for (var c = 0; c < 128; c++)
        {
            ulong mask = 0;
            for (var b = 0; b < pcs.Count; b++)
                if (Accepts(pcs[b], (char)c)) mask |= 1UL << b;
            var index = masks.IndexOf(mask);
            if (index < 0) { index = masks.Count; masks.Add(mask); }
            asciiClass[c] = (byte)index; // at most 65 distinct masks are possible here in practice; 128 at most in theory, which fits a byte
        }
        return (follow, [.. pcs], Closure(0), matchMask, asciiClass, [.. masks]);
    }

    private bool Accepts(int pc, char c) => _op[pc] switch
    {
        Op.Char => _ch[pc] == c,
        Op.Any => c != '\n',
        Op.Class => _classes[_x[pc]].Contains(c),
        _ => false,
    };

    private bool RunBitParallel(ReadOnlySpan<char> text)
    {
        var follow = _follow!;
        var asciiClass = _asciiClass!;
        var classMasks = _classMasks!;
        var current = _start;
        foreach (var c in text)
        {
            if (!_anchoredStart) current |= _start;
            if ((current & _matchMask) != 0 && !_anchoredEnd) return true;
            var accepting = current & (c < 128 ? classMasks[asciiClass[c]] : NonAsciiMask(c));
            ulong next = 0;
            while (accepting != 0)
            {
                next |= follow[System.Numerics.BitOperations.TrailingZeroCount(accepting)];
                accepting &= accepting - 1;
            }
            current = next;
            if (current == 0 && _anchoredStart) return false;
        }
        if (!_anchoredStart) current |= _start;
        return (current & _matchMask) != 0;
    }

    private ulong NonAsciiMask(char c)
    {
        var pcs = _pcOfBit!;
        ulong mask = 0;
        for (var b = 0; b < pcs.Length; b++)
            if (Accepts(pcs[b], c)) mask |= 1UL << b;
        return mask;
    }

    /// <summary>Compiles <paramref name="pattern"/>; <see cref="ArgumentException"/> when it is malformed or outside the supported subset.</summary>
    public static GlobRegex Compile(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > MaxPatternLength) throw new ArgumentException($"Pattern longer than {MaxPatternLength} characters", nameof(pattern));
        if (pattern == "(?!)") return Never;

        var start = 0;
        var end = pattern.Length;
        var anchoredStart = end > 0 && pattern[0] == '^';
        if (anchoredStart) start = 1;
        var anchoredEnd = false;
        if (end > start && pattern[end - 1] == '$')
        {
            var backslashes = 0;
            for (var i = end - 2; i >= start && pattern[i] == '\\'; i--) backslashes++;
            if (backslashes % 2 == 0) { anchoredEnd = true; end--; }
        }

        var parser = new Parser(pattern, start, end);
        var ast = parser.ParseAll();
        // In a regex "^a|b$" the anchors bind to one branch each; stripping them would mean "^(a|b)$". No translator emits that: refuse it.
        if ((anchoredStart || anchoredEnd) && parser.TopLevelAlternation)
            throw new ArgumentException($"An anchored top-level alternation is not supported (group it): \"{pattern}\"", nameof(pattern));
        var builder = new Builder();
        builder.Emit(ast, parser.Classes);
        builder.Add(Op.Match);
        var (prefix, suffix) = LiteralEnds(ast);
        return new GlobRegex(pattern, builder, anchoredStart, anchoredEnd, anchoredStart ? prefix : "", anchoredEnd ? suffix : "");
    }

    /// <summary>Compiles, or returns <see cref="Never"/> when the pattern cannot be compiled.</summary>
    public static GlobRegex CompileOrNever(string pattern)
    {
        try { return Compile(pattern); }
        catch (ArgumentException) { return Never; }
    }

    public bool IsMatch(string text) => IsMatch(text.AsSpan());

    public bool IsMatch(ReadOnlySpan<char> text)
    {
        if (_never) return false;
        if (_prefix.Length > 0 && !text.StartsWith(_prefix, StringComparison.Ordinal)) return false;
        if (_suffix.Length > 0 && !text.EndsWith(_suffix, StringComparison.Ordinal)) return false;
        if (_follow is not null) return RunBitParallel(text);

        // current list, next list, per-state marks, and the closure stack (each marked state pushes at most two successors: 2n + 1 entries)
        var n = _op.Length;
        var size = 5 * n + 1;
        int[]? rented = null;
        var buffer = n <= StackStates ? stackalloc int[size] : (rented = ArrayPool<int>.Shared.Rent(size)).AsSpan(0, size);
        try
        {
            return Run(text, buffer[..n], buffer.Slice(n, n), buffer.Slice(2 * n, n), buffer[(3 * n)..]);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    private bool Run(ReadOnlySpan<char> text, Span<int> current, Span<int> next, Span<int> marks, Span<int> stack)
    {
        marks.Clear();
        var generation = 1;
        var count = 0;
        var matched = Add(current, ref count, 0, generation, marks, stack);
        for (var i = 0; i < text.Length; i++)
        {
            if (!_anchoredStart) matched |= Add(current, ref count, 0, generation, marks, stack); // a match may start anywhere
            if (matched && !_anchoredEnd) return true;
            if (count == 0) return false;

            var c = text[i];
            generation++;
            var nextCount = 0;
            var nextMatched = false;
            for (var k = 0; k < count; k++)
            {
                var pc = current[k];
                var take = _op[pc] switch
                {
                    Op.Char => _ch[pc] == c,
                    Op.Any => c != '\n',
                    Op.Class => _classes[_x[pc]].Contains(c),
                    _ => false,
                };
                if (take) nextMatched |= Add(next, ref nextCount, pc + 1, generation, marks, stack);
            }
            var swap = current;
            current = next;
            next = swap;
            count = nextCount;
            matched = nextMatched;
        }
        if (!_anchoredStart) matched |= Add(current, ref count, 0, generation, marks, stack);
        return matched;
    }

    /// <summary>Adds <paramref name="pc"/> and everything reachable from it without input to <paramref name="list"/>; true when that reaches Match.</summary>
    private bool Add(Span<int> list, ref int count, int pc, int generation, Span<int> marks, Span<int> stack)
    {
        var matched = false;
        var top = 0;
        stack[top++] = pc;
        while (top > 0)
        {
            var p = stack[--top];
            if (marks[p] == generation) continue;
            marks[p] = generation;
            switch (_op[p])
            {
                case Op.Jump:
                    stack[top++] = _x[p];
                    break;
                case Op.Split:
                    stack[top++] = _y[p];
                    stack[top++] = _x[p];
                    break;
                case Op.Match:
                    matched = true;
                    break;
                default:
                    list[count++] = p;
                    break;
            }
        }
        return matched;
    }

    private static (string Prefix, string Suffix) LiteralEnds(Node ast)
    {
        var items = ast is Node.Cat cat ? cat.Items : [ast];
        var prefix = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            if (item is not Node.Lit lit) break;
            prefix.Append(lit.C);
        }
        var suffix = new System.Text.StringBuilder();
        for (var i = items.Count - 1; i >= 0 && items[i] is Node.Lit lit; i--) suffix.Insert(0, lit.C);
        return (prefix.ToString(), suffix.ToString());
    }

    // ---- AST ---------------------------------------------------------------------------------------------------------------------------

    private abstract record Node
    {
        public sealed record Lit(char C) : Node;
        public sealed record AnyChar : Node;
        public sealed record Class(int Index) : Node;
        public sealed record Cat(List<Node> Items) : Node;
        public sealed record Alt(List<Node> Branches) : Node;
        public sealed record Repeat(Node Body, char Kind) : Node;
        public sealed record Empty : Node;
    }

    private readonly record struct CharClass(char[] Ranges, bool Negated)
    {
        public bool Contains(char c)
        {
            var hit = false;
            for (var i = 0; i < Ranges.Length && !hit; i += 2) hit = c >= Ranges[i] && c <= Ranges[i + 1];
            return hit != Negated;
        }
    }

    private sealed class Parser(string pattern, int start, int end)
    {
        private int _i = start;
        private int _depth;
        public List<CharClass> Classes { get; } = new();

        /// <summary>A '|' outside every group was seen.</summary>
        public bool TopLevelAlternation { get; private set; }

        public Node ParseAll()
        {
            var node = ParseAlt();
            if (_i < end) throw Error(pattern[_i] == ')' ? "Too many )'s" : "Unexpected character");
            return node;
        }

        private ArgumentException Error(string message) => new($"{message} at offset {_i} in glob pattern \"{pattern}\"", nameof(pattern));

        private Node ParseAlt()
        {
            var first = ParseCat();
            if (_i >= end || pattern[_i] != '|') return first;
            var branches = new List<Node> { first };
            if (_depth == 0) TopLevelAlternation = true;
            while (_i < end && pattern[_i] == '|')
            {
                _i++;
                branches.Add(ParseCat());
            }
            return new Node.Alt(branches);
        }

        private Node ParseCat()
        {
            var items = new List<Node>();
            while (_i < end && pattern[_i] is not ('|' or ')')) items.Add(ParseRepeat());
            return items.Count switch { 0 => new Node.Empty(), 1 => items[0], _ => new Node.Cat(items) };
        }

        private Node ParseRepeat()
        {
            var atom = ParseAtom();
            while (_i < end && pattern[_i] is '*' or '+' or '?')
            {
                atom = new Node.Repeat(atom, pattern[_i++]);
                if (_i < end && pattern[_i] == '?') _i++; // lazy: the same language, and IsMatch only asks whether there is a match
            }
            if (_i < end && pattern[_i] == '{' && IsCountedRepetition()) throw Error("Counted repetition is not supported");
            return atom;
        }

        private bool IsCountedRepetition()
        {
            var j = _i + 1;
            var digits = 0;
            while (j < end && char.IsAsciiDigit(pattern[j])) { j++; digits++; }
            if (digits == 0) return false;
            if (j < end && pattern[j] == ',') { j++; while (j < end && char.IsAsciiDigit(pattern[j])) j++; }
            return j < end && pattern[j] == '}';
        }

        private Node ParseAtom()
        {
            var c = pattern[_i];
            switch (c)
            {
                case '(':
                {
                    _i++;
                    if (_i < end && pattern[_i] == '?')
                    {
                        if (_i + 1 < end && pattern[_i + 1] == ':') _i += 2;
                        else throw Error("Only (?:…) groups are supported");
                    }
                    _depth++;
                    var inner = ParseAlt();
                    _depth--;
                    if (_i >= end || pattern[_i] != ')') throw Error("Not enough )'s");
                    _i++;
                    return inner;
                }
                case '[':
                    return ParseClass();
                case '.':
                    _i++;
                    return new Node.AnyChar();
                case '\\':
                    _i++;
                    return new Node.Lit(Escape(inClass: false));
                case '*' or '+' or '?':
                    throw Error("Quantifier following nothing");
                case '^' or '$':
                    throw Error("Anchors are only supported at the start and the end");
                default:
                    _i++;
                    return new Node.Lit(c);
            }
        }

        private char Escape(bool inClass)
        {
            if (_i >= end) throw Error("Illegal \\ at end of pattern");
            var c = pattern[_i++];
            switch (c)
            {
                case 't': return '\t';
                case 'n': return '\n';
                case 'r': return '\r';
                case 'f': return '\f';
                case 'v': return '\v';
                case 'e': return '\u001B';
                case 'a': return '\u0007';
                case 'b' when inClass: return '\b';
            }
            if (char.IsAsciiLetterOrDigit(c)) throw Error($"Unsupported escape \\{c}");
            return c; // escaped punctuation, space, '#'…: the character itself
        }

        private Node ParseClass()
        {
            _i++; // '['
            var negated = _i < end && pattern[_i] == '^';
            if (negated) _i++;
            var ranges = new List<char>();
            var first = true;
            while (true)
            {
                if (_i >= end) throw Error("Unterminated [] set");
                var c = pattern[_i];
                if (c == ']' && !first) { _i++; break; }
                first = false;
                if (c == '-' && _i + 1 < end && pattern[_i + 1] == '[') throw Error("Character class subtraction is not supported");
                var lo = Member();
                var hi = lo;
                if (_i + 1 < end && pattern[_i] == '-' && pattern[_i + 1] != ']')
                {
                    _i++;
                    if (pattern[_i] == '[') throw Error("Character class subtraction is not supported");
                    hi = Member();
                    if (hi < lo) throw Error("[x-y] range in reverse order");
                }
                ranges.Add(lo);
                ranges.Add(hi);
            }
            Classes.Add(new CharClass([.. ranges], negated));
            return new Node.Class(Classes.Count - 1);
        }

        private char Member()
        {
            var c = pattern[_i++];
            return c == '\\' ? Escape(inClass: true) : c;
        }
    }

    private sealed class Builder
    {
        public List<Op> Ops { get; } = new();
        public List<char> Chars { get; } = new();
        public List<int> X { get; } = new();
        public List<int> Y { get; } = new();
        public List<CharClass> Classes { get; } = new();

        private int Pc => Ops.Count;

        public int Add(Op op, char c = default, int x = 0, int y = 0)
        {
            Ops.Add(op);
            Chars.Add(c);
            X.Add(x);
            Y.Add(y);
            return Ops.Count - 1;
        }

        public void Emit(Node node, List<CharClass> classes)
        {
            switch (node)
            {
                case Node.Lit lit:
                    Add(Op.Char, lit.C);
                    break;
                case Node.AnyChar:
                    Add(Op.Any);
                    break;
                case Node.Class cls:
                    Classes.Add(classes[cls.Index]);
                    Add(Op.Class, x: Classes.Count - 1);
                    break;
                case Node.Cat cat:
                    foreach (var item in cat.Items) Emit(item, classes);
                    break;
                case Node.Empty:
                    break;
                case Node.Alt alt:
                {
                    var jumps = new List<int>();
                    for (var k = 0; k < alt.Branches.Count - 1; k++)
                    {
                        var split = Add(Op.Split);
                        X[split] = Pc;
                        Emit(alt.Branches[k], classes);
                        jumps.Add(Add(Op.Jump));
                        Y[split] = Pc;
                    }
                    Emit(alt.Branches[^1], classes);
                    foreach (var j in jumps) X[j] = Pc;
                    break;
                }
                case Node.Repeat { Kind: '*' } star:
                {
                    var split = Add(Op.Split);
                    X[split] = Pc;
                    Emit(star.Body, classes);
                    Add(Op.Jump, x: split);
                    Y[split] = Pc;
                    break;
                }
                case Node.Repeat { Kind: '+' } plus:
                {
                    var loop = Pc;
                    Emit(plus.Body, classes);
                    var split = Add(Op.Split, x: loop);
                    Y[split] = Pc;
                    break;
                }
                case Node.Repeat optional: // '?'
                {
                    var split = Add(Op.Split);
                    X[split] = Pc;
                    Emit(optional.Body, classes);
                    Y[split] = Pc;
                    break;
                }
            }
        }
    }
}
