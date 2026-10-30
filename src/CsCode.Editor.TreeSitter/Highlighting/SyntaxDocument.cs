using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.TreeSitter.Queries;

namespace CsCode.Editor.TreeSitter.Highlighting;

/// <summary>
/// The tree-sitter state of ONE document: its parser, its current syntax tree, and the highlighter over it.
///
/// Threading: tree-sitter parsers and trees are not thread-safe, so a <see cref="SyntaxDocument"/> is CONFINED to one thread (the editor
/// worker) — every member except <see cref="RequestCancel"/> must be called there; nothing is copied between threads. The UI thread only
/// records edits (elsewhere) and may call <see cref="RequestCancel"/>, which flips a native flag the running parse polls.
///
/// Parsing is incremental (edits are applied to the old tree with <see cref="ApplyEdit"/>, then <see cref="Parse"/> reuses it) and
/// SLICED: <see cref="Parse"/> stops at the deadline it is given (tree-sitter's progress callback) and returns <see cref="ParseOutcome.Halted"/>;
/// calling it again with the same text resumes where it stopped. An edit or a different text abandons a halted parse (stale parses are
/// cancelled, never finished). Text is read straight from the caller's snapshot through a small native buffer (UTF-16LE), so a 100 MB
/// document is never copied into one big string.
///
/// Highlighting paints scope-stack ids per character into a <see cref="PaintBuffer"/>: captures sorted outer-before-inner, a capture
/// paints over chars whose current paint has a priority ≤ its own (Neovim semantics: later patterns and inner nodes win; <c>#set! priority</c>
/// overrides), and a capture nested in another gets the outer one's stack as parent (<c>string.quoted</c> > <c>constant.character.escape</c>),
/// like TextMate's nested scopes. Injections (<c>injections.scm</c>: fenced code in Markdown, script/style in HTML, …) are parsed with
/// included ranges and painted as layers on top, recursively up to <see cref="MaxInjectionDepth"/>.
/// </summary>
public sealed unsafe partial class SyntaxDocument : IDisposable
{
    public const int InputBufferChars = 8 * 1024;
    public const int MaxInjectionDepth = 4;
    public const int MaxInjectionSitesPerCall = 400;

    /// <summary>Longest <c>@injection.language</c> text considered (a language name; anything longer is not one).</summary>
    public const int MaxInjectionLanguageChars = 128;

    /// <summary>Largest injected region (characters, all its ranges) that is parsed: a bigger one stays in the host's colours.</summary>
    public const int MaxInjectionChars = 1 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct ParseControl
    {
        public long Deadline;
        public int Cancel;
    }

    private sealed class InputState
    {
        public ITextInput Text = null!;
        public char* Buffer;
    }

    private record struct CaptureSpan(int Start, int End, int Key, int Priority, int Pattern, int Seq);

    private sealed class InjectionSite
    {
        public required LanguageBundle Language;
        public readonly List<TSRange> Ranges = new();
        public int Start = int.MaxValue;
        public int End;
        public int Pattern;
        public bool Combined;
    }

    private static readonly Comparison<CaptureSpan> CaptureOrder = static (a, b) =>
        a.Start != b.Start ? a.Start.CompareTo(b.Start)
        : a.End != b.End ? b.End.CompareTo(a.End)
        : a.Pattern != b.Pattern ? a.Pattern.CompareTo(b.Pattern)
        : a.Seq.CompareTo(b.Seq);

    private static readonly char* EmptyInput = (char*)NativeMemory.AllocZeroed(16);

    private readonly GrammarRegistry _registry;
    private readonly List<CaptureSpan>[] _captures = new List<CaptureSpan>[MaxInjectionDepth + 1];
    private readonly List<InjectionSite>[] _sites = new List<InjectionSite>[MaxInjectionDepth + 1];
    private readonly Dictionary<(string, int, int, int), IntPtr> _injectionTrees = new();
    private readonly List<(int Start, int End)> _changed = new();
    private int[] _stackOf = new int[64];
    private int[] _parentOf = new int[64];
    private char[] _scratchA = new char[256];
    private char[] _scratchB = new char[256];
    private IntPtr _parser;
    private IntPtr _tree;
    private IntPtr _cursor;
    // The parse-control blocks (deadline + cancel flag; the document's own parse, then injection parses) live in a PINNED managed array, not
    // native memory: RequestCancel may run on the UI thread concurrently with Dispose on the worker, and writing into a still-reachable pinned
    // array can never be a use-after-free.
    private readonly long[] _controlStorage = GC.AllocateArray<long>(4, pinned: true);
    private ParseControl* _control;
    private ParseControl* _injectionControl;
    // per injected language: the smallest region (chars) whose own parse ran out of time (the whole MaxQueryTime) in this document; regions at
    // least that big are not tried again
    private readonly Dictionary<string, int> _slowInjections = new(StringComparer.Ordinal);
    // The query guard: the cursor's options (progress callback + payload) and its deadline, pinned for the cursor's whole iteration.
    private readonly long[] _queryStorage = GC.AllocateArray<long>(4, pinned: true);
    private TSQueryCursorOptions* _queryOptions;
    private long* _queryDeadline;
    private InputState? _input;
    private GCHandle _inputHandle;
    private bool _parsing;
    private ITextInput? _text;
    private int _layerCounter;
    private int _injectionSitesThisCall;
    private bool _disposed;
    private readonly Isolation.FlightRecorder? _recorder;

    /// <summary>
    /// The longest one <see cref="Highlight"/> (or structural query) may run its queries before the cursor is stopped (the query guard: a
    /// pathological tree or query cannot occupy the lane). A viewport normally takes 1–30 ms. Settable for tests.
    /// </summary>
    public static TimeSpan MaxQueryTime { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>True when the last <see cref="Highlight"/> (or query) was stopped by <see cref="MaxQueryTime"/> (its result is partial).</summary>
    public bool LastQueryTimedOut { get; private set; }

    /// <summary>
    /// Tests only: runs inside the flight recorder's scope just before a parse enters native code, with the grammar's name, so a test can stand
    /// in for a grammar that never returns (a real one would have to be built with an endless loop in its scanner).
    /// </summary>
    internal static Action<string>? HangHookForTests { get; set; }

    /// <summary>Tests: runs on the lane right before an injection parse starts (its language, its size in chars), e.g. to make it run out of time.</summary>
    internal static Action<string, long>? InjectionParseHookForTests { get; set; }

    public SyntaxDocument(GrammarRegistry registry, LanguageBundle language)
    {
        _registry = registry;
        Language = language;
        _parser = TsApi.ts_parser_new();
        if (TsApi.ts_parser_set_language(_parser, language.Language.Handle) == 0)
        {
            TsApi.ts_parser_delete(_parser);
            _parser = IntPtr.Zero;
            throw new InvalidOperationException($"tree-sitter refused grammar {language.Language}");
        }
        _cursor = TsApi.ts_query_cursor_new();
        // Bounds the query cursor's in-progress match state on pathological input (the default is unlimited).
        TsApi.ts_query_cursor_set_match_limit(_cursor, 2048);
        _control = (ParseControl*)Unsafe.AsPointer(ref _controlStorage[0]);
        _injectionControl = (ParseControl*)Unsafe.AsPointer(ref _controlStorage[2]);
        _queryDeadline = (long*)Unsafe.AsPointer(ref _queryStorage[0]);
        _queryOptions = (TSQueryCursorOptions*)Unsafe.AsPointer(ref _queryStorage[2]);
        _queryOptions->Payload = _queryDeadline;
        _queryOptions->ProgressCallback = &QueryProgress;
        *_queryDeadline = long.MaxValue;
        _recorder = registry.Recorder;
        for (var i = 0; i <= MaxInjectionDepth; i++)
        {
            _captures[i] = new List<CaptureSpan>(256);
            _sites[i] = new List<InjectionSite>();
        }
    }

    public LanguageBundle Language { get; }

    public bool HasTree => _tree != IntPtr.Zero;

    /// <summary>Incremented each time a parse completes (a new tree).</summary>
    public int Generation { get; private set; }

    /// <summary>True while a sliced parse is halted half-way (call <see cref="Parse"/> again to continue).</summary>
    public bool IsParsing => _parsing;

    /// <summary>
    /// Character ranges whose syntax changed in the last completed parse (from <c>ts_tree_get_changed_ranges</c>); the whole text after the
    /// first parse. The edited text itself is NOT included (the caller knows it).
    /// </summary>
    public IReadOnlyList<(int Start, int End)> ChangedRanges => _changed;

    /// <summary>Total time spent inside tree-sitter parse calls for the current/last parse (all slices).</summary>
    public TimeSpan LastParseTime { get; private set; }

    public int LastParseSlices { get; private set; }

    /// <summary>Halted parses that were thrown away because the text changed under them (stale parses cancelled, not finished).</summary>
    public int AbandonedParses { get; private set; }

    /// <summary>
    /// Approximate native bytes held by this document's tree and parser (tree-sitter has no per-tree accounting): visible node count × 140 B,
    /// calibrated against the counting allocator in benchmark B4 (100 K characters of C#: 3.5 MB counted, ≈ 25 k visible nodes).
    /// </summary>
    public long EstimatedTreeBytes => _tree == IntPtr.Zero ? 0 : TsApi.ts_node_descendant_count(TsApi.ts_tree_root_node(_tree)) * NativeBytesPerNode;

    internal const long NativeBytesPerNode = 140;

    public bool RootHasError => _tree != IntPtr.Zero && TsApi.ts_node_has_error(TsApi.ts_tree_root_node(_tree)) != 0;

    /// <summary>Any thread: asks a running parse slice to stop at its next progress check (an edit made it stale).</summary>
    public void RequestCancel()
    {
        Volatile.Write(ref ((ParseControl*)Unsafe.AsPointer(ref _controlStorage[0]))->Cancel, 1);
        Volatile.Write(ref ((ParseControl*)Unsafe.AsPointer(ref _controlStorage[2]))->Cancel, 1); // an injection parse of a highlight in flight
    }

    /// <summary>Injected regions whose parse was stopped by the highlight's time limit (tests, diagnostics).</summary>
    public int InjectionParsesStopped { get; private set; }

    /// <summary>Injected regions left for the next highlight because an earlier region of the same highlight had spent its time (tests, diagnostics).</summary>
    public int InjectionParsesDeferred { get; private set; }

    /// <summary>True when the last <see cref="Highlight"/> left injected regions for the next one (its clock was spent before it reached them).</summary>
    public bool LastHighlightDeferred { get; private set; }

    /// <summary>The smallest region of <paramref name="language"/> whose parse ran out of time in this document, or null (tests, diagnostics).</summary>
    internal int? SlowInjectionSize(string language) => _slowInjections.TryGetValue(language, out var chars) ? chars : null;

    /// <summary>Applies one edit to the current tree (in order, each against the text produced by the previous). Abandons a halted parse.</summary>
    public void ApplyEdit(in TextEditInfo e)
    {
        ThrowIfDisposed();
        AbandonParse();
        if (_tree == IntPtr.Zero) return; // nothing to keep in sync: the next parse is a full one
        var edit = new TSInputEdit
        {
            StartByte = (uint)e.StartOffset * 2,
            OldEndByte = (uint)e.OldEndOffset * 2,
            NewEndByte = (uint)e.NewEndOffset * 2,
            StartPoint = new TSPoint((uint)e.StartLine, (uint)e.StartCharacter * 2),
            OldEndPoint = new TSPoint((uint)e.OldEndLine, (uint)e.OldEndCharacter * 2),
            NewEndPoint = new TSPoint((uint)e.NewEndLine, (uint)e.NewEndCharacter * 2),
        };
        TsApi.ts_tree_edit(_tree, &edit);
        ClearInjectionTrees();
    }

    /// <summary>Forgets the tree (the next parse is a full parse), e.g. when edits were lost.</summary>
    public void DropTree()
    {
        ThrowIfDisposed();
        AbandonParse();
        if (_tree != IntPtr.Zero) TsApi.ts_tree_delete(_tree);
        _tree = IntPtr.Zero;
        ClearInjectionTrees();
    }

    /// <summary>
    /// Parses <paramref name="text"/> (incrementally from the edited tree), stopping at <paramref name="deadlineTimestamp"/>
    /// (<see cref="Stopwatch.GetTimestamp"/> units; <c>long.MaxValue</c> = no limit) or when <see cref="RequestCancel"/> is called.
    /// </summary>
    public ParseOutcome Parse(ITextInput text, long deadlineTimestamp)
    {
        ThrowIfDisposed();
        if (_parsing && !ReferenceEquals(_input!.Text, text)) AbandonParse(); // a different snapshot: the halted parse is stale
        if (!_parsing)
        {
            _input = new InputState { Text = text, Buffer = (char*)NativeMemory.Alloc(InputBufferChars * sizeof(char)) };
            _inputHandle = GCHandle.Alloc(_input);
            _parsing = true;
            LastParseTime = TimeSpan.Zero;
            LastParseSlices = 0;
        }
        _control->Deadline = deadlineTimestamp;
        Volatile.Write(ref _control->Cancel, 0);
        var input = new TSInput
        {
            Payload = (void*)GCHandle.ToIntPtr(_inputHandle),
            Read = &ReadInput,
            Encoding = TSInputEncoding.Utf16LE,
            Decode = null,
        };
        var options = new TSParseOptions { Payload = _control, ProgressCallback = &Progress };
        var started = Stopwatch.GetTimestamp();
        IntPtr tree;
        using (Record(Language))
        {
            HangHookForTests?.Invoke(Language.Name);
            tree = TsApi.ts_parser_parse_with_options(_parser, _tree, input, options);
        }
        LastParseTime += Stopwatch.GetElapsedTime(started);
        LastParseSlices++;
        if (tree == IntPtr.Zero) return ParseOutcome.Halted;

        FreeInput();
        _parsing = false;
        _changed.Clear();
        if (_tree != IntPtr.Zero)
        {
            uint count;
            var ranges = TsApi.ts_tree_get_changed_ranges(_tree, tree, &count);
            for (var i = 0; i < count; i++) _changed.Add(((int)(ranges[i].StartByte / 2), (int)Math.Min(ranges[i].EndByte / 2, (uint)text.Length)));
            TsRuntime.FreeOwned(ranges);
            TsApi.ts_tree_delete(_tree);
        }
        else _changed.Add((0, text.Length));
        _tree = tree;
        Generation++;
        ClearInjectionTrees();
        return ParseOutcome.Completed;
    }

    /// <summary>The tree as an S-expression (tests, diagnostics).</summary>
    public string? TreeString()
    {
        ThrowIfDisposed();
        if (_tree == IntPtr.Zero) return null;
        var s = TsApi.ts_node_string(TsApi.ts_tree_root_node(_tree));
        try
        {
            return TsApi.Utf8(s);
        }
        finally
        {
            TsRuntime.FreeOwned(s);
        }
    }

    /// <summary>
    /// Paints [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="text"/> (which must be the text of the current tree) into
    /// <paramref name="paint"/>. Returns the number of captures applied (all layers).
    /// </summary>
    public int Highlight(ITextInput text, int start, int end, IScopeStacks stacks, PaintBuffer paint)
    {
        ThrowIfDisposed();
        end = Math.Clamp(end, 0, text.Length);
        start = Math.Clamp(start, 0, end);
        var root = stacks.Root(Language.RootScopeKey);
        paint.Reset(start, end - start, root);
        LastQueryTimedOut = false;
        LastHighlightDeferred = false;
        if (_tree == IntPtr.Zero || end <= start) return 0;
        _text = text;
        _layerCounter = 0;
        _injectionSitesThisCall = 0;
        Volatile.Write(ref _injectionControl->Cancel, 0);
        StartQueryClock();
        try
        {
            using (Record(Language))
                return HighlightLayer(Language, TsApi.ts_tree_root_node(_tree), start, end, 0, stacks, paint, 0);
        }
        finally
        {
            _text = null;
            StopQueryClock();
        }
    }

    private Isolation.FlightRecorder.Scope Record(LanguageBundle language) => _recorder is null ? default : _recorder.Enter(language.FlightRecord);

    private void StartQueryClock()
    {
        _queryDeadline[1] = 0; // set by QueryProgress when it stops the cursor
        *_queryDeadline = Stopwatch.GetTimestamp() + (long)(MaxQueryTime.TotalSeconds * Stopwatch.Frequency);
    }

    private void StopQueryClock()
    {
        LastQueryTimedOut = _queryDeadline[1] != 0;
        *_queryDeadline = long.MaxValue;
    }

    /// <summary>Starts <paramref name="query"/> on the shared cursor over [start, end) of <paramref name="node"/>, under the query guard.</summary>
    private void Exec(TsQuery query, TSNode node, int start, int end)
    {
        TsApi.ts_query_cursor_set_byte_range(_cursor, (uint)start * 2, (uint)end * 2);
        TsApi.ts_query_cursor_exec_with_options(_cursor, query.Handle, node, _queryOptions);
    }

    // ---- layers ----

    private int HighlightLayer(LanguageBundle bundle, TSNode root, int start, int end, int mark, IScopeStacks stacks, PaintBuffer paint, int depth)
    {
        if (bundle.Highlights is not { } query) return 0;
        var caps = _captures[depth];
        caps.Clear();
        CollectCaptures(bundle, query, root, start, end, caps);
        caps.Sort(CaptureOrder);
        Paint(caps, mark, stacks, paint);
        var painted = caps.Count;

        if (bundle.Injections is not { } injections || depth >= MaxInjectionDepth) return painted;
        var sites = _sites[depth];
        sites.Clear();
        CollectInjections(bundle, injections, root, start, end, sites);
        foreach (var site in sites)
        {
            if (_injectionSitesThisCall++ >= MaxInjectionSitesPerCall) break;
            var tree = InjectedTree(site);
            if (tree == IntPtr.Zero) continue;
            var layer = ++_layerCounter;
            var sub = site.Language;
            foreach (var r in site.Ranges)
            {
                var rs = Math.Max(start, (int)(r.StartByte / 2));
                var re = Math.Min(end, (int)(r.EndByte / 2));
                if (re <= rs) continue;
                var hostStack = paint.Stack[rs - paint.Start];
                var subBase = sub.RootScope == bundle.RootScope ? hostStack : stacks.Push(hostStack, sub.RootScopeKey);
                for (var i = rs - paint.Start; i < re - paint.Start; i++)
                {
                    if (paint.Layer[i] != mark) continue; // a gap of the host (an injection inside an injection keeps its own chars)
                    paint.Layer[i] = layer;
                    paint.Base[i] = subBase;
                    paint.Stack[i] = subBase;
                    paint.Priority[i] = int.MinValue;
                    paint.Owner[i] = -1;
                }
            }
            painted += HighlightLayer(sub, TsApi.ts_tree_root_node(tree), Math.Max(start, site.Start), Math.Min(end, site.End), layer, stacks, paint, depth + 1);
        }
        return painted;
    }

    private void CollectCaptures(LanguageBundle bundle, TsQuery query, TSNode root, int start, int end, List<CaptureSpan> caps)
    {
        Exec(query, root, start, end);
        var keys = bundle.CaptureKeys;
        var seq = 0;
        TSQueryMatch m;
        while (TsApi.ts_query_cursor_next_match(_cursor, &m) != 0)
        {
            var pattern = query.Patterns[m.PatternIndex];
            if (pattern.Predicates.Length > 0 && !Evaluate(bundle, pattern, m)) continue;
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                var key = keys[cap.Index];
                if (key == LanguageBundle.SkipKey) continue;
                var cs = (int)(TsApi.ts_node_start_byte(cap.Node) / 2);
                var ce = (int)(TsApi.ts_node_end_byte(cap.Node) / 2);
                if (pattern.Offsets is { } offsets && offsets.TryGetValue((int)cap.Index, out var d)) (cs, ce) = Offset(cs, ce, d);
                cs = Math.Max(cs, start);
                ce = Math.Min(ce, end);
                if (ce <= cs) continue;
                caps.Add(new CaptureSpan(cs, ce, key, pattern.Priority, m.PatternIndex, seq++));
            }
        }
    }

    private void Paint(List<CaptureSpan> caps, int mark, IScopeStacks stacks, PaintBuffer paint)
    {
        if (_stackOf.Length < caps.Count)
        {
            _stackOf = new int[Math.Max(caps.Count, _stackOf.Length * 2)];
            _parentOf = new int[_stackOf.Length];
        }
        var origin = paint.Start;
        for (var ci = 0; ci < caps.Count; ci++)
        {
            var c = caps[ci];
            var s = c.Start - origin;
            var e = c.End - origin;
            // parent: the capture currently painting the first char, when it encloses this one (same node, other pattern = replace it)
            int parent;
            var o = paint.Layer[s] == mark ? paint.Owner[s] : -1;
            if (o >= 0 && caps[o].Start <= c.Start && caps[o].End >= c.End)
                parent = caps[o].Start == c.Start && caps[o].End == c.End ? _parentOf[o] : _stackOf[o];
            else parent = paint.Base[s];
            var stack = c.Key == LanguageBundle.NoneKey ? paint.Base[s] : stacks.Push(parent, c.Key);
            _parentOf[ci] = parent;
            _stackOf[ci] = stack;
            for (var i = s; i < e; i++)
            {
                if (paint.Layer[i] != mark || c.Priority < paint.Priority[i]) continue;
                paint.Stack[i] = stack;
                paint.Priority[i] = c.Priority;
                paint.Owner[i] = ci;
            }
        }
    }

    // ---- injections ----

    private void CollectInjections(LanguageBundle host, TsQuery query, TSNode root, int start, int end, List<InjectionSite> sites)
    {
        Exec(query, root, start, end);
        TSQueryMatch m;
        while (TsApi.ts_query_cursor_next_match(_cursor, &m) != 0)
        {
            var pattern = query.Patterns[m.PatternIndex];
            if (pattern.Predicates.Length > 0 && !Evaluate(host, pattern, m)) continue;
            string? languageName = pattern.Property("injection.language");
            if (pattern.HasProperty("injection.self") || pattern.HasProperty("injection.parent")) languageName = host.Name;
            var contentCount = 0;
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                var name = query.CaptureNames[(int)cap.Index];
                if (name == "injection.language")
                {
                    // document text (a fence's info string, an rst directive argument, a script's type): rewritten by the query's #gsub!/#trim!,
                    // then only ever used as a name of an INSTALLED grammar (Resolve and the registry refuse anything else before any lookup)
                    var raw = NodeText(cap.Node, ref _scratchA);
                    languageName = raw.Length > MaxInjectionLanguageChars ? null : pattern.CaptureText((int)cap.Index, raw.ToString());
                }
                else if (name == "injection.content") contentCount++;
            }
            if (contentCount == 0 || languageName is null) continue;
            if (InjectionLanguages.Resolve(languageName) is not { } grammar) continue;
            if (!_registry.IsKnown(grammar) || _registry.Get(grammar, out _) is not { } sub) continue;

            var combined = pattern.HasProperty("injection.combined");
            InjectionSite? site = null;
            if (combined)
                foreach (var existing in sites)
                    if (existing.Combined && existing.Pattern == m.PatternIndex && existing.Language == sub)
                    {
                        site = existing;
                        break;
                    }
            if (site is null)
            {
                site = new InjectionSite { Language = sub, Pattern = m.PatternIndex, Combined = combined };
                sites.Add(site);
            }
            var includeChildren = pattern.HasProperty("injection.include-children");
            for (var k = 0; k < m.CaptureCount; k++)
            {
                var cap = m.Captures[k];
                if (query.CaptureNames[(int)cap.Index] != "injection.content") continue;
                int[]? offset = null;
                pattern.Offsets?.TryGetValue((int)cap.Index, out offset);
                AddContentRanges(site, cap.Node, includeChildren, offset);
            }
            if (site.Ranges.Count == 0) sites.Remove(site);
        }
        // ranges of combined (or quantified) content may arrive out of document order: tree-sitter needs them sorted and disjoint
        foreach (var s in sites)
            if (s.Ranges.Count > 1) NormaliseRanges(s.Ranges);
    }

    private void AddContentRanges(InjectionSite site, TSNode node, bool includeChildren, int[]? offset)
    {
        var ns = (int)(TsApi.ts_node_start_byte(node) / 2);
        var ne = (int)(TsApi.ts_node_end_byte(node) / 2);
        if (offset is not null) (ns, ne) = Offset(ns, ne, offset);
        if (ne <= ns) return;
        if (includeChildren)
        {
            AddRange(site, ns, ne);
            return;
        }
        // everything of the node except its NAMED children (Neovim's rule, which its injection queries are written for; tree-sitter-highlight
        // also cuts out anonymous children, which would leave nothing of e.g. Markdown's (inline) nodes)
        var from = ns;
        var count = TsApi.ts_node_named_child_count(node);
        for (uint i = 0; i < count; i++)
        {
            var child = TsApi.ts_node_named_child(node, i);
            var cs = (int)(TsApi.ts_node_start_byte(child) / 2);
            var ce = (int)(TsApi.ts_node_end_byte(child) / 2);
            if (cs > from) AddRange(site, from, Math.Min(cs, ne));
            from = Math.Max(from, ce);
        }
        if (ne > from) AddRange(site, from, ne);
    }

    private void AddRange(InjectionSite site, int start, int end)
    {
        if (end <= start) return;
        site.Ranges.Add(new TSRange
        {
            StartByte = (uint)start * 2,
            EndByte = (uint)end * 2,
            StartPoint = PointAt(start),
            EndPoint = PointAt(end),
        });
        site.Start = Math.Min(site.Start, start);
        site.End = Math.Max(site.End, end);
    }

    private static void NormaliseRanges(List<TSRange> ranges)
    {
        ranges.Sort(static (a, b) => a.StartByte.CompareTo(b.StartByte));
        var w = 0;
        for (var r = 1; r < ranges.Count; r++)
        {
            if (ranges[r].StartByte < ranges[w].EndByte)
            {
                if (ranges[r].EndByte > ranges[w].EndByte)
                {
                    var merged = ranges[w];
                    merged.EndByte = ranges[r].EndByte;
                    merged.EndPoint = ranges[r].EndPoint;
                    ranges[w] = merged;
                }
            }
            else ranges[++w] = ranges[r];
        }
        ranges.RemoveRange(w + 1, ranges.Count - w - 1);
    }

    private IntPtr InjectedTree(InjectionSite site)
    {
        var key = (site.Language.Name, site.Start, site.End, site.Ranges.Count);
        if (_injectionTrees.TryGetValue(key, out var cached)) return cached;
        // The size of the region: one too big is not parsed at all, and one at least as big as a region of the same language that already ran
        // out of time in this document is not tried again (after every edit it would cost the whole time limit again).
        long chars = 0;
        foreach (var r in site.Ranges) chars += (r.EndByte - r.StartByte) / 2;
        if (chars > MaxInjectionChars || (_slowInjections.TryGetValue(site.Language.Name, out var slow) && chars >= slow))
        {
            _injectionTrees[key] = IntPtr.Zero;
            return IntPtr.Zero;
        }
        // Every layer of a highlight shares its clock. Once an earlier region spent it, a later one is not started (it would stop at its first
        // progress check and be taken for slow): it stays in the host's colours for this pass, which is partial, and the next highlight, with a
        // clock of its own, parses it (the slow region before it is not tried again).
        var started = Stopwatch.GetTimestamp();
        if (started >= *_queryDeadline)
        {
            _queryDeadline[1] = 1;
            InjectionParsesDeferred++;
            LastHighlightDeferred = true;
            return IntPtr.Zero;
        }
        var parser = _registry.InjectionParser(site.Language);
        if (parser == IntPtr.Zero) return IntPtr.Zero;
        var ranges = CollectionsMarshal.AsSpan(site.Ranges);
        IntPtr tree;
        fixed (TSRange* r = ranges)
        {
            if (TsApi.ts_parser_set_included_ranges(parser, r, (uint)ranges.Length) == 0) return IntPtr.Zero;
        }
        var state = new InputState { Text = _text!, Buffer = (char*)NativeMemory.Alloc(InputBufferChars * sizeof(char)) };
        var handle = GCHandle.Alloc(state);
        try
        {
            var input = new TSInput { Payload = (void*)GCHandle.ToIntPtr(handle), Read = &ReadInput, Encoding = TSInputEncoding.Utf16LE };
            // An injection parse is bounded: MaxQueryTime from its own start (far below the hang watchdog), and the cancel an edit requests.
            // Without a progress callback one slow block ran unbounded under the watchdog, which then quarantined a healthy grammar and
            // abandoned the lane. It is started only while the highlight's clock has time left (above), so a highlight takes at most about
            // twice the limit: its own clock, plus the one region that was still started before it ran out.
            _injectionControl->Deadline = started + (long)(MaxQueryTime.TotalSeconds * Stopwatch.Frequency);
            var options = new TSParseOptions { Payload = _injectionControl, ProgressCallback = &Progress };
            using (Record(site.Language))
            {
                InjectionParseHookForTests?.Invoke(site.Language.Name, chars);
                tree = TsApi.ts_parser_parse_with_options(parser, IntPtr.Zero, input, options);
            }
        }
        finally
        {
            handle.Free();
            NativeMemory.Free(state.Buffer);
            TsApi.ts_parser_reset(parser);
        }
        if (tree == IntPtr.Zero)
        {
            if (Volatile.Read(ref _injectionControl->Cancel) != 0) return IntPtr.Zero; // an edit made this highlight stale: nothing to remember
            // out of time: this highlight is partial (the query guard counts it), and the region stays in the host's colours
            _queryDeadline[1] = 1;
            InjectionParsesStopped++;
            // It had the whole limit to itself, so the region (its language, its size) is remembered as slow. A region the highlight did not
            // start because an earlier one spent the clock is not (above): it says nothing about itself, and the next highlight parses it.
            _slowInjections[site.Language.Name] = (int)Math.Min(chars, _slowInjections.GetValueOrDefault(site.Language.Name, int.MaxValue));
        }
        _injectionTrees[key] = tree;
        return tree;
    }

    private void ClearInjectionTrees()
    {
        foreach (var t in _injectionTrees.Values)
            if (t != IntPtr.Zero) TsApi.ts_tree_delete(t);
        _injectionTrees.Clear();
    }

    // ---- predicates ----

    private bool Evaluate(LanguageBundle bundle, PatternInfo pattern, in TSQueryMatch m)
    {
        foreach (var p in pattern.Predicates)
        {
            bool result;
            if (p.Kind == PredicateKind.Eq && p.OtherCapture >= 0)
            {
                var a = FirstNode(m, p.Capture);
                var b = FirstNode(m, p.OtherCapture);
                result = a.IsNull || b.IsNull || NodeText(a, ref _scratchA).SequenceEqual(NodeText(b, ref _scratchB));
            }
            else result = EvaluateNodes(bundle, p, m);
            if (result == p.Negated) return false;
        }
        return true;
    }

    private bool EvaluateNodes(LanguageBundle bundle, Predicate p, in TSQueryMatch m)
    {
        var seen = false;
        for (var k = 0; k < m.CaptureCount; k++)
        {
            var cap = m.Captures[k];
            if ((int)cap.Index != p.Capture) continue;
            seen = true;
            var ok = Test(bundle, p, cap.Node);
            if (p.Any && ok) return true;
            if (!p.Any && !ok) return false;
        }
        return !seen || !p.Any;
    }

    private bool Test(LanguageBundle bundle, Predicate p, TSNode node)
    {
        switch (p.Kind)
        {
            case PredicateKind.Eq:
                return p.Strings.Length > 0 && NodeText(node, ref _scratchA).SequenceEqual(p.Strings[0]);
            case PredicateKind.Match:
                if (p.Regex is null) return false;
                try
                {
                    return p.Regex.IsMatch(NodeText(node, ref _scratchA));
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    return false;
                }
            case PredicateKind.AnyOf:
                return p.Set!.GetAlternateLookup<ReadOnlySpan<char>>().Contains(NodeText(node, ref _scratchA));
            case PredicateKind.Contains:
                var text = NodeText(node, ref _scratchA);
                foreach (var s in p.Strings)
                    if (text.Contains(s.AsSpan(), StringComparison.Ordinal)) return true;
                return false;
            case PredicateKind.HasParent:
                var parent = TsApi.ts_node_parent(node);
                return !parent.IsNull && p.Set!.Contains(bundle.SymbolName(TsApi.ts_node_symbol(parent), parent));
            case PredicateKind.HasAncestor:
                for (var a = TsApi.ts_node_parent(node); !a.IsNull; a = TsApi.ts_node_parent(a))
                    if (p.Set!.Contains(bundle.SymbolName(TsApi.ts_node_symbol(a), a))) return true;
                return false;
            case PredicateKind.KindEq:
                return p.Set!.Contains(bundle.SymbolName(TsApi.ts_node_symbol(node), node));
            default:
                return true;
        }
    }

    private static TSNode FirstNode(in TSQueryMatch m, int capture)
    {
        for (var k = 0; k < m.CaptureCount; k++)
            if ((int)m.Captures[k].Index == capture) return m.Captures[k].Node;
        return default;
    }

    private ReadOnlySpan<char> NodeText(TSNode node, ref char[] scratch)
    {
        var text = _text!;
        var s = (int)(TsApi.ts_node_start_byte(node) / 2);
        var e = Math.Min((int)(TsApi.ts_node_end_byte(node) / 2), text.Length);
        var n = Math.Max(0, e - s);
        if (scratch.Length < n) scratch = new char[Math.Max(n, scratch.Length * 2)];
        var span = scratch.AsSpan(0, n);
        text.CopyTo(s, span);
        if (scratch.Length > 64 * 1024 && n <= 256) scratch = new char[256]; // do not keep a huge buffer after one huge node
        return span;
    }

    // ---- positions ----

    private (int Start, int End) Offset(int start, int end, int[] d)
    {
        var text = _text!;
        int Move(int offset, int rows, int cols)
        {
            if (rows == 0) return offset + cols;
            var line = text.LineOfOffset(offset);
            var column = offset - text.LineStart(line);
            var target = Math.Clamp(line + rows, 0, text.LineCount - 1);
            return text.LineStart(target) + column + cols;
        }
        var s = Math.Clamp(Move(start, d[0], d[1]), 0, text.Length);
        var e = Math.Clamp(Move(end, d[2], d[3]), 0, text.Length);
        return (s, Math.Max(s, e));
    }

    private TSPoint PointAt(int offset)
    {
        var text = _text!;
        var line = text.LineOfOffset(offset);
        return new TSPoint((uint)line, (uint)(offset - text.LineStart(line)) * 2);
    }

    // ---- native callbacks ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* ReadInput(void* payload, uint byteIndex, TSPoint position, uint* bytesRead)
    {
        try
        {
            var state = (InputState)GCHandle.FromIntPtr((IntPtr)payload).Target!;
            var offset = (int)(byteIndex / 2);
            var n = Math.Min(InputBufferChars, state.Text.Length - offset);
            if (n <= 0)
            {
                *bytesRead = 0;
                return (byte*)EmptyInput;
            }
            state.Text.CopyTo(offset, new Span<char>(state.Buffer, n));
            *bytesRead = (uint)n * 2;
            return (byte*)state.Buffer;
        }
        catch (Exception)
        {
            // an exception must never cross into native code: report end of input (the parse completes with what it has)
            *bytesRead = 0;
            return (byte*)EmptyInput;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Progress(TSParseState* state)
    {
        var control = (ParseControl*)state->Payload;
        return Volatile.Read(ref control->Cancel) != 0 || Stopwatch.GetTimestamp() >= control->Deadline ? (byte)1 : (byte)0;
    }

    /// <summary>The query guard: stops the cursor (and says so in the word after the deadline) once <see cref="MaxQueryTime"/> is spent.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte QueryProgress(TSQueryCursorState* state)
    {
        var control = (long*)state->Payload;
        if (Stopwatch.GetTimestamp() < control[0]) return 0;
        control[1] = 1;
        return 1;
    }

    // ---- lifetime ----

    private void AbandonParse()
    {
        if (!_parsing) return;
        AbandonedParses++;
        TsApi.ts_parser_reset(_parser);
        FreeInput();
        _parsing = false;
    }

    private void FreeInput()
    {
        if (_inputHandle.IsAllocated) _inputHandle.Free();
        if (_input is not null)
        {
            NativeMemory.Free(_input.Buffer);
            _input.Buffer = null;
            _input = null;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Frees the parser, tree, cursor and injection trees. Must run on the thread that owns the document (the editor worker).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AbandonParse();
        ClearInjectionTrees();
        if (_tree != IntPtr.Zero) TsApi.ts_tree_delete(_tree);
        _tree = IntPtr.Zero;
        if (_cursor != IntPtr.Zero) TsApi.ts_query_cursor_delete(_cursor);
        _cursor = IntPtr.Zero;
        if (_parser != IntPtr.Zero) TsApi.ts_parser_delete(_parser);
        _parser = IntPtr.Zero;
    }
}
