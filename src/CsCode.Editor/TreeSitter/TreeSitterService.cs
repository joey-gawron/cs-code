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

    /// <summary>
    /// Lane only. The registry, created on first use ON THE LANE together with everything that touches files: the quarantine, the crash
    /// records of earlier processes, this process's flight recorder, the runtime library and the bundle (none of it on the UI thread).
    /// </summary>
    public GrammarRegistry Registry
    {
        get
        {
            var state = State;
            return state.Registry ??= CreateRegistry();
        }
    }

    /// <summary>Lane only. Interns tree-sitter scope stacks into the same ids TextMate tokens use.</summary>
    public IScopeStacks Stacks => State.Stacks;

    /// <summary>The thread the registry (and the file and native-library work with it) was last created on (tests: never the UI thread).</summary>
    internal int RegistryCreatedOnThread { get; private set; }

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

    /// <summary>Cheap (a thread): this may run on the UI thread, the first time a document is scheduled for tree-sitter.</summary>
    private LaneState CreateLane()
    {
        var lane = new EditorWorker("cscode-syntax-lane", ThreadPriority.Normal);
        lane.OnError = ex => _log.LogError(ex, "tree-sitter job failed");
        return new LaneState(lane, new InternerScopeStacks(Interner));
    }

    /// <summary>Where the flight recorder and the quarantine live: the test override, else <c>{UserDataDir}/tree-sitter</c>; null without either.</summary>
    private string? IsolationDirectory =>
        IsolationDirectoryOverride ?? (_services.AppPaths?.UserDataDir is { } d ? Path.Combine(d, "tree-sitter") : null);

    /// <summary>Tests: runs (on the lane) while the isolation state is set up, i.e. while its files are read and written.</summary>
    internal Action? IsolationSetupHookForTests { get; set; }

    /// <summary>Lane only (it reads and writes files and loads the native runtime).</summary>
    private GrammarRegistry CreateRegistry()
    {
        RegistryCreatedOnThread = Environment.CurrentManagedThreadId;
        var userDir = _services.AppPaths?.UserDataDir is { } d ? Path.Combine(d, "tree-sitter") : null;
        GrammarQuarantine? quarantine;
        FlightRecorder? recorder;
        lock (_gate)
        {
            quarantine = _quarantine;
            recorder = _recorder;
        }
        if (quarantine is null)
        {
            // The file and process work happens OUTSIDE _gate: the UI thread takes that lock for every schedule of a tree-sitter document (Lane),
            // exactly while the first one lays out, and must never wait for this I/O. The results are published under the lock afterwards.
            IsolationSetupHookForTests?.Invoke();
            var isolationDir = IsolationDirectory;
            var newQuarantine = new GrammarQuarantine(isolationDir is null ? null : Path.Combine(isolationDir, "quarantine.json"));
            FlightRecorder newRecorder;
            if (isolationDir is not null)
            {
                // processes that died inside a grammar since the last start (their flight files are left behind), then this process's own recorder
                foreach (var (grammar, identity) in FlightRecorder.CollectCrashes(Path.Combine(isolationDir, "flight")))
                {
                    _log.LogWarning("an earlier CsCode process ended while running the tree-sitter grammar {Grammar} ({Identity})", grammar, identity);
                    if (newQuarantine.RecordCrash(grammar, identity)) Tell($"CsCode closed unexpectedly while colouring with the tree-sitter grammar '{grammar}', more than once. Files that need it are coloured by TextMate now (Clear Quarantined Tree-sitter Grammars tries it again).");
                }
                newRecorder = FlightRecorder.Open(Path.Combine(isolationDir, "flight"));
            }
            else newRecorder = FlightRecorder.InMemory();
            var generation = newQuarantine.Generation; // (a stat: before the lock)
            var lost = false;
            lock (_gate)
            {
                if (_quarantine is null && !_disposed)
                {
                    _quarantine = newQuarantine;
                    _recorder = newRecorder;
                    _quarantineGeneration = generation;
                    _watchdog ??= new Timer(_ => Watch(), null, WatchdogPeriod, WatchdogPeriod);
                }
                else lost = true; // set up meanwhile (or shut down): keep the published state
                quarantine = _quarantine;
                recorder = _recorder;
            }
            if (lost) newRecorder.Dispose();
        }
        TsRuntime.Load(userDir is null ? null : [userDir, Path.Combine(userDir, "lib")]);
        var locator = LocatorOverride ?? GrammarLocator.CreateDefault(userDir is null ? null : Path.Combine(userDir, "grammars"));
        return new GrammarRegistry(locator, TreeSitterLanguages.RootScopeFor) { Quarantine = quarantine, Recorder = recorder };
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

    /// <summary>
    /// Forgets every quarantined grammar (they are tried again on the next document that needs them), in this process and, through the file,
    /// in every other one. Any thread; the file work never runs on the caller's. The task completes once the file is cleared (tests).
    /// </summary>
    public Task ClearQuarantine()
    {
        LaneState? state;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            state = _lane;
        }
        if (state is null)
        {
            // no lane yet (nothing on tree-sitter in this process so far, so nothing has loaded the quarantine): clear the file itself, so the
            // first tree-sitter document reads it clean
            return Task.Run(ClearQuarantineFile);
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Lane.Post(WorkPriority.Interactive, null, _ =>
        {
            try
            {
                ClearQuarantineFile(); // a file write: on the lane, not the caller's (UI) thread
                state.Registry?.ForgetFailures();
                ForgetReports();
            }
            finally
            {
                done.TrySetResult();
            }
            LaneReplaced?.Invoke(); // documents re-evaluate their engine
        });
        return done.Task;
    }

    /// <summary>Clears the quarantine: the loaded one, or (before the engine first loaded it, its registry not created yet) the file directly.</summary>
    private void ClearQuarantineFile()
    {
        GrammarQuarantine? quarantine;
        lock (_gate) quarantine = _quarantine;
        if (quarantine is not null)
        {
            quarantine.Clear();
            return;
        }
        if (IsolationDirectory is { } dir) new GrammarQuarantine(Path.Combine(dir, "quarantine.json")).Clear();
    }

    // ---- the watchdog ----

    private int _watching;
    private int _quarantineGeneration; // the quarantine's clear count this process last acted on

    private void Watch()
    {
        // one check at a time (timer callbacks overlap when one is slow: the file work below)
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        try
        {
            WatchOnce();
        }
        finally
        {
            Volatile.Write(ref _watching, 0);
        }
    }

    private void WatchOnce()
    {
        FlightRecorder? recorder;
        GrammarQuarantine? quarantine;
        lock (_gate)
        {
            if (_disposed) return;
            recorder = _recorder;
            quarantine = _quarantine;
        }
        if (quarantine is not null) HonourClearElsewhere(quarantine);
        if (recorder is null) return;
        var (inside, grammar, identity, elapsed) = recorder.Current;
        if (!inside || elapsed < HangThreshold || grammar is null) return;
        // The file work (the quarantine's read and write, the new recorder's file and mapping) is done OUTSIDE _gate, which the UI thread takes
        // to schedule tree-sitter documents. The hang is recorded BEFORE the lane is replaced: no new lane can load the grammar before it lands.
        quarantine?.RecordHang(grammar, identity ?? "", $"a call did not return for {elapsed.TotalSeconds:0} s");
        var fresh = FlightRecorderAfterHang();
        LaneState? hung;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_recorder, recorder)) hung = null;
            else
            {
                hung = _lane;
                _lane = null; // the next use creates a fresh lane; everything on the hung one is abandoned (never touched again)
                // the hung thread may still write into the old recorder: it stays mapped (and its file behind) until the process exits
                _recorder = fresh;
                fresh = null;
            }
        }
        if (fresh is not null)
        {
            fresh.Dispose(); // shut down meanwhile
            return;
        }
        Interlocked.Increment(ref _abandonedLanes);
        ForgetReports();
        _log.LogError("the tree-sitter grammar {Grammar} has been running for {Seconds:0} s without returning: quarantined; tree-sitter moved to a new thread", grammar, elapsed.TotalSeconds);
        hung?.Lane.Dispose(); // stops taking jobs; the stuck job itself cannot be stopped (native code)
        Tell($"The tree-sitter grammar '{grammar}' stopped responding. CsCode colours those files with TextMate now; the stuck thread keeps using one CPU core until CsCode is restarted.");
        LaneReplaced?.Invoke();
    }

    /// <summary>
    /// Watchdog thread, once a tick (a stat of the quarantine file; it is read only when it changed). When another process sharing the file
    /// cleared it (Clear Quarantined Tree-sitter Grammars there), the grammars this process refused are forgotten and the documents re-evaluate
    /// their engine, as this process's own clear does: without it, open documents stayed on TextMate until this process ran the command itself
    /// or restarted. (This process's own clear finds nothing left to forget by the time its lane job runs.)
    /// </summary>
    private void HonourClearElsewhere(GrammarQuarantine quarantine)
    {
        var generation = quarantine.Generation;
        if (Interlocked.Exchange(ref _quarantineGeneration, generation) == generation) return;
        LaneState? state;
        lock (_gate) state = _disposed ? null : _lane;
        state?.Lane.Post(WorkPriority.Interactive, null, _ =>
        {
            if (state.Registry?.ForgetQuarantined() != true) return; // nothing refused here: no document to move back
            ForgetReports();
            LaneReplaced?.Invoke(); // documents re-evaluate their engine
        });
    }

    private FlightRecorder FlightRecorderAfterHang()
    {
        var dir = IsolationDirectory;
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
            state.Registry?.Dispose();
            recorder?.Dispose();
            state.Lane.Dispose();
        });
    }

    /// <summary>One syntax lane and what lives on it (the registry is created there, on first use).</summary>
    private sealed class LaneState(EditorWorker lane, InternerScopeStacks stacks)
    {
        public EditorWorker Lane { get; } = lane;
        public InternerScopeStacks Stacks { get; } = stacks;
        public GrammarRegistry? Registry { get; set; }
    }

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
