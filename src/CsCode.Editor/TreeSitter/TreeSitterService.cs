using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Isolation;
using CsCode.Editor.TreeSitter.Native;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// The tree-sitter engine's shared state, one per <see cref="TokenizationManager"/> (docs/TREE-SITTER.md):
/// <list type="bullet">
/// <item>the setting <c>editor.syntaxEngine</c> (<c>auto</c> | <c>textmate</c> | <c>treesitter</c>, global or per <c>"[language]"</c>; the old
/// <c>editor.experimental.treeSitter</c> is still read as an alias): <c>auto</c> colours with tree-sitter exactly the languages TextMate has no
/// grammar for, so C# and every other TextMate language stay on TextMate unless the user opts in;</item>
/// <item>the <b>syntax lane</b>: a dedicated thread of its own for every tree-sitter object (parsers, trees, cursors), apart from the editor
/// worker, so that a grammar that hangs stops tree-sitter and nothing else; a watchdog sees a native call that does not return within
/// <see cref="HangThreshold"/>, quarantines that grammar, moves tree-sitter to a fresh lane and hands the documents back (TextMate for the
/// quarantined grammar's languages);</item>
/// <item>crash isolation: a flight recorder (<see cref="FlightRecorder"/>) per process, and the quarantine (<see cref="GrammarQuarantine"/>)
/// that refuses a grammar build after it took the process down twice (or hung once);</item>
/// <item>the grammar registry (the app's bundle first) and the scope-stack adapter onto the manager's <see cref="ScopeInterner"/>.</item>
/// </list>
/// TextMate stays the fallback for every failure (no runtime, no grammar, ABI or digest mismatch, query that does not compile, quarantine):
/// it is logged once per grammar and the document is coloured by TextMate (or stays plain when TextMate has no grammar either).
///
/// Threading: the setting is read on any thread. <see cref="Registry"/>, <see cref="Stacks"/> and everything they return belong to the
/// syntax lane (<see cref="Lane"/>).
/// </summary>
internal sealed class TreeSitterService : IDisposable
{
    public const string SettingKey = "editor.syntaxEngine";

    /// <summary>The prototype's boolean setting, read as an alias: <c>true</c> = <c>treesitter</c>, <c>false</c> = <c>auto</c>.</summary>
    public const string LegacySettingKey = "editor.experimental.treeSitter";

    public const string Auto = "auto";
    public const string TextMate = "textmate";
    public const string TreeSitter = "treesitter";

    /// <summary>A native call into a grammar that has not returned after this long is a hang (a parse slice is 6 ms; a query is capped at 1 s).</summary>
    internal static TimeSpan HangThreshold { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(1);

    private readonly EditorServices _services;
    private readonly Func<string, bool> _hasTextMateGrammar;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private LaneState? _lane;
    private Timer? _watchdog;
    private FlightRecorder? _recorder;
    private GrammarQuarantine? _quarantine;
    private int _abandonedLanes;
    private bool _disposed;

    /// <param name="hasTextMateGrammar">Whether TextMate can colour a language (a built-in or user grammar): the <c>auto</c> policy's question.</param>
    public TreeSitterService(EditorServices services, ScopeInterner interner, Func<string, bool> hasTextMateGrammar)
    {
        _services = services;
        Interner = interner;
        _hasTextMateGrammar = hasTextMateGrammar;
        _log = services.LoggerFactory.CreateLogger<TreeSitterService>();
    }

    public ScopeInterner Interner { get; }

    /// <summary>The setting's declaration (registered by the manager so the Settings panel lists it, as a choice of three).</summary>
    public static ConfigurationProperty Property => new(SettingKey, JsonSerializer.SerializeToElement(Auto),
        "Which engine colours code. auto: TextMate where it has a grammar, tree-sitter for the languages it has none for (TOML, Kotlin, Scala, " +
        "Zig, Protobuf, GraphQL, Haskell, Elixir, Erlang, OCaml, Vue, Svelte, Terraform, Nix, reStructuredText, assembly, CMake, CSV…). textmate: " +
        "TextMate only. treesitter: tree-sitter wherever CsCode has a grammar (C#, TypeScript, Rust, Python, Markdown, C/C++, HTML, CSS, Go, Java…), " +
        "which also gives syntax-exact folding, Expand Selection and an outline without a language server. Can be set per language: " +
        "\"[rust]\": { \"editor.syntaxEngine\": \"treesitter\" }.",
        JsonDocument.Parse("""
            {"type":"string","enum":["auto","textmate","treesitter"],"enumDescriptions":[
              "TextMate where it has a grammar; tree-sitter for the languages TextMate cannot colour.",
              "TextMate only (languages without a TextMate grammar stay uncoloured).",
              "Tree-sitter wherever CsCode bundles a grammar; TextMate elsewhere."]}
            """).RootElement.Clone());

    /// <summary>The setting for <paramref name="languageId"/>: its language section first, then the global value, the old boolean as an alias at each level.</summary>
    public string SettingFor(string languageId)
    {
        var config = _services.Config;
        if (!string.IsNullOrEmpty(languageId) && config.GetRaw($"[{languageId}]") is { ValueKind: JsonValueKind.Object } section)
        {
            if (TryGet(section, SettingKey, out var scoped) && Normalise(scoped) is { } value) return value;
            if (TryGet(section, LegacySettingKey, out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return legacy.ValueKind == JsonValueKind.True ? TreeSitter : Auto;
        }
        var global = config.GetRaw(SettingKey) is { } g ? Normalise(g) ?? Auto : Auto;
        // the alias only speaks when the new setting is at its default
        if (global == Auto && config.GetRaw(LegacySettingKey) is { ValueKind: JsonValueKind.True }) return TreeSitter;
        return global;
    }

    /// <summary>
    /// <paramref name="dottedKey"/> inside a language section, written either way: <c>"editor.syntaxEngine"</c> as one property, or nested
    /// (<c>{"editor": {"syntaxEngine": …}}</c>, which is how the settings service hands sections back: it expands dotted keys).
    /// </summary>
    private static bool TryGet(JsonElement section, string dottedKey, out JsonElement value)
    {
        if (section.TryGetProperty(dottedKey, out value)) return true;
        var current = section;
        foreach (var segment in dottedKey.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                value = default;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static string? Normalise(JsonElement e) =>
        e.ValueKind != JsonValueKind.String ? null
        : e.GetString()?.Trim().ToLowerInvariant() switch
        {
            Auto => Auto,
            TextMate => TextMate,
            TreeSitter or "tree-sitter" => TreeSitter,
            _ => null, // an unknown value: ignored (the level below decides)
        };

    /// <summary>True when tree-sitter should colour this document: the setting (or <c>auto</c>) picks it and CsCode has a grammar for the language.</summary>
    public bool IsEnabledFor(string languageId, Uri? uri = null)
    {
        if (TreeSitterLanguages.GrammarFor(languageId, uri) is null) return false;
        return SettingFor(languageId) switch
        {
            TreeSitter => true,
            TextMate => false,
            _ => !_hasTextMateGrammar(languageId),
        };
    }

    /// <summary>Tests: where grammars are looked up (set before the first document uses tree-sitter). Default: the bundle, the user's folder, the environment.</summary>
    internal GrammarLocator? LocatorOverride { get; set; }

    /// <summary>Tests: where the flight recorder and the quarantine live (default <c>{UserDataDir}/tree-sitter</c>; null without one).</summary>
    internal string? IsolationDirectoryOverride { get; set; }

    /// <summary>The syntax lane: the one thread every tree-sitter object belongs to. Created on first use; replaced after a hang.</summary>
    public EditorWorker Lane => State.Lane;

    /// <summary>Lane only. The registry (created, and the runtime loaded, on first use).</summary>
    public GrammarRegistry Registry => State.Registry;

    /// <summary>Lane only. Interns tree-sitter scope stacks into the same ids TextMate tokens use.</summary>
    public IScopeStacks Stacks => State.Stacks;

    /// <summary>How many lanes were given up because a grammar hung in them (tests, diagnostics).</summary>
    public int AbandonedLanes => Volatile.Read(ref _abandonedLanes);

    /// <summary>The quarantine (null until the engine is first used).</summary>
    internal GrammarQuarantine? Quarantine => _quarantine;

    /// <summary>Fires (on the watchdog's thread) after a hang moved tree-sitter to a new lane: every document must restart its engine.</summary>
    internal Action? LaneReplaced { get; set; }

    private LaneState State
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _lane ??= CreateLane();
            }
        }
    }

    /// <summary>True when <paramref name="lane"/> is the current syntax lane (an engine of an abandoned lane must never be touched again).</summary>
    public bool IsCurrent(EditorWorker lane)
    {
        lock (_gate) return ReferenceEquals(_lane?.Lane, lane);
    }

    private LaneState CreateLane()
    {
        var userDir = _services.AppPaths?.UserDataDir is { } d ? Path.Combine(d, "tree-sitter") : null;
        if (_quarantine is null)
        {
            var isolationDir = IsolationDirectoryOverride ?? userDir;
            _quarantine = new GrammarQuarantine(isolationDir is null ? null : Path.Combine(isolationDir, "quarantine.json"));
            if (isolationDir is not null)
            {
                // processes that died inside a grammar since the last start (their flight files are left behind), then this process's own recorder
                foreach (var (grammar, identity) in FlightRecorder.CollectCrashes(Path.Combine(isolationDir, "flight")))
                {
                    _log.LogWarning("an earlier CsCode process ended while running the tree-sitter grammar {Grammar} ({Identity})", grammar, identity);
                    if (_quarantine.RecordCrash(grammar, identity)) Tell($"CsCode closed unexpectedly while colouring with the tree-sitter grammar '{grammar}', more than once. Files that need it are coloured by TextMate now (Clear Quarantined Tree-sitter Grammars tries it again).");
                }
                _recorder = FlightRecorder.Open(Path.Combine(isolationDir, "flight"));
            }
            else _recorder = FlightRecorder.InMemory();
            _watchdog = new Timer(_ => Watch(), null, WatchdogPeriod, WatchdogPeriod);
        }
        TsRuntime.Load(userDir is null ? null : [userDir, Path.Combine(userDir, "lib")]);
        var locator = LocatorOverride ?? GrammarLocator.CreateDefault(userDir is null ? null : Path.Combine(userDir, "grammars"));
        var lane = new EditorWorker("cscode-syntax-lane", ThreadPriority.Normal);
        lane.OnError = ex => _log.LogError(ex, "tree-sitter job failed");
        var registry = new GrammarRegistry(locator, TreeSitterLanguages.RootScopeFor) { Quarantine = _quarantine, Recorder = _recorder };
        return new LaneState(lane, registry, new InternerScopeStacks(Interner));
    }

    /// <summary>Lane only. The grammar bundle for a document, or null (logged once) — the caller falls back to TextMate.</summary>
    public LanguageBundle? BundleFor(string languageId, Uri? uri = null)
    {
        if (TreeSitterLanguages.GrammarFor(languageId, uri) is not { } grammar) return null;
        var bundle = Registry.Get(grammar, out var error);
        if (bundle is null)
        {
            if (FirstReport(grammar)) _log.LogWarning("tree-sitter is enabled but not usable for {Language}: {Reason}. Using TextMate.", languageId, error);
        }
        else if (bundle.Diagnostics.Count > 0 && FirstReport(grammar))
            _log.LogInformation("tree-sitter grammar {Grammar}: {Notes}", grammar, string.Join("; ", bundle.Diagnostics));
        return bundle;
    }

    private bool FirstReport(string grammar)
    {
        lock (_reported) return _reported.Add(grammar);
    }

    private void ForgetReports()
    {
        lock (_reported) _reported.Clear();
    }

    /// <summary>Forgets every quarantined grammar (they are tried again on the next document that needs them). Any thread.</summary>
    public void ClearQuarantine()
    {
        _quarantine?.Clear();
        lock (_gate)
        {
            if (_lane is { } state) state.Lane.Post(WorkPriority.Interactive, null, _ => state.Registry.ForgetFailures());
        }
        ForgetReports();
        LaneReplaced?.Invoke(); // documents re-evaluate their engine
    }

    // ---- the watchdog ----

    private void Watch()
    {
        if (_recorder is not { } recorder) return;
        var (inside, grammar, identity, elapsed) = recorder.Current;
        if (!inside || elapsed < HangThreshold || grammar is null) return;
        LaneState? hung;
        lock (_gate)
        {
            if (_disposed) return;
            hung = _lane;
            _lane = null; // the next use creates a fresh lane; everything on the hung one is abandoned (never touched again)
            _recorder = null; // the hung thread may still write into the old recorder: it stays mapped (and its file behind) until the process exits
            _quarantine?.RecordHang(grammar, identity ?? "", $"a call did not return for {elapsed.TotalSeconds:0} s");
            _recorder = FlightRecorderAfterHang();
        }
        Interlocked.Increment(ref _abandonedLanes);
        ForgetReports();
        _log.LogError("the tree-sitter grammar {Grammar} has been running for {Seconds:0} s without returning: quarantined; tree-sitter moved to a new thread", grammar, elapsed.TotalSeconds);
        hung?.Lane.Dispose(); // stops taking jobs; the stuck job itself cannot be stopped (native code)
        Tell($"The tree-sitter grammar '{grammar}' stopped responding. CsCode colours those files with TextMate now; the stuck thread keeps using one CPU core until CsCode is restarted.");
        LaneReplaced?.Invoke();
    }

    private FlightRecorder FlightRecorderAfterHang()
    {
        var dir = IsolationDirectoryOverride ?? (_services.AppPaths?.UserDataDir is { } d ? Path.Combine(d, "tree-sitter") : null);
        // a second file for the new lane (the old one keeps "inside <hung grammar>": if the process is killed now, that is counted too)
        return dir is null ? FlightRecorder.InMemory() : FlightRecorder.Open(Path.Combine(dir, "flight"), "lane" + (_abandonedLanes + 2));
    }

    private void Tell(string message)
    {
        if (_services.Window is not { } window) return;
        _services.Ui.Post(() => _ = window.ShowMessageAsync(MessageSeverity.Warning, message).AsTask());
    }

    public void Dispose()
    {
        LaneState? state;
        FlightRecorder? recorder;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            state = _lane;
            _lane = null;
            recorder = _recorder;
            _recorder = null;
        }
        _watchdog?.Dispose();
        if (state is null)
        {
            recorder?.Dispose();
            return;
        }
        // the injection parsers and the recorder's writer belong to the lane: free them there, then stop it
        state.Lane.Post(WorkPriority.Interactive, null, _ =>
        {
            state.Registry.Dispose();
            recorder?.Dispose();
            state.Lane.Dispose();
        });
    }

    private sealed record LaneState(EditorWorker Lane, GrammarRegistry Registry, InternerScopeStacks Stacks);

    /// <summary>
    /// <see cref="IScopeStacks"/> over the editor's <see cref="ScopeInterner"/>: tree-sitter stacks become ordinary interned scope stacks, so the
    /// colorizer, the theme resolver, <c>KindAt</c> (auto-closing) and the bracket feature treat them exactly like TextMate's. Lane only;
    /// (parent, scope) → id is cached so the hot path is one dictionary lookup.
    /// </summary>
    private sealed class InternerScopeStacks(ScopeInterner interner) : IScopeStacks
    {
        private readonly Dictionary<long, int> _push = new();
        private readonly Dictionary<int, int> _roots = new();

        public int Root(int scopeKey)
        {
            if (_roots.TryGetValue(scopeKey, out var id)) return id;
            id = interner.Intern([ScopeNames.NameOf(scopeKey)]);
            _roots[scopeKey] = id;
            return id;
        }

        public int Push(int parentId, int scopeKey)
        {
            var key = ((long)parentId << 32) | (uint)scopeKey;
            if (_push.TryGetValue(key, out var id)) return id;
            var parent = interner.Scopes(parentId);
            var stack = new string[parent.Length + 1];
            parent.CopyTo(stack, 0);
            stack[^1] = ScopeNames.NameOf(scopeKey);
            id = interner.Intern(stack);
            _push[key] = id;
            return id;
        }
    }
}
