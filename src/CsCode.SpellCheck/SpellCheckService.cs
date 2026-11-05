using System.Diagnostics;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.SpellCheck.Engine;
using CsCode.SpellCheck.Settings;
using CsCode.SpellCheck.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeCantSpell.Hunspell;

namespace CsCode.SpellCheck;

/// <summary>The last published check of a document.</summary>
public sealed record SpellCheckResult(Uri Uri, int Version, IReadOnlyList<SpellIssue> Issues, bool Truncated, SpellDocumentState State);

/// <summary>Why a document is (not) checked: the status bar says it.</summary>
public enum SpellDocumentState
{
    Checked,
    Disabled,
    LanguageNotEnabled,
    IgnoredPath,
    LargeFile,
    /// <summary>Code the editor has no tokens for yet (no view showed it), or a language without a grammar that is not prose.</summary>
    NoTokens,
}

/// <summary>
/// The spell checker: watches the open documents (<see cref="IDocumentManager"/>), checks each one on its own lane after
/// <see cref="SpellSettings.DelayMs"/> of quiet, and publishes the misspelt words as diagnostics (owner and source <see cref="Owner"/>) so the
/// editor underlines them, the Error List lists them and the overview ruler marks them.
/// <list type="bullet">
/// <item>Threading: every event handler only bumps a generation and (re)starts a timer; the timer posts the check to <see cref="SpellLane"/>,
/// which reads an immutable text and token capture, checks it in slices of at most 4 ms, and drops the run as soon as a newer generation
/// exists (latest wins). A result is published only for the version it was computed for.</item>
/// <item>Memory: the dictionaries load on the first check (on the lane) and are <see cref="IMemoryTrimmable"/>; per document only the
/// last result (at most <see cref="SpellSettings.MaxNumberOfProblems"/> issues) and a timer are kept, and both go when the document closes.</item>
/// <item>Large-file documents, disabled languages and ignored paths are not checked (their diagnostics are cleared).</item>
/// </list>
/// </summary>
public sealed class SpellCheckService : IMemoryTrimmable, IDisposable
{
    /// <summary>Diagnostic owner and source: cspell's name, so messages read as they do in VS Code.</summary>
    public const string Owner = "cSpell";

    /// <summary>The longest one slice of a check runs on the lane (tests make it zero: one line per slice).</summary>
    public TimeSpan SliceBudget { get; set; } = TimeSpan.FromMilliseconds(4);

    private readonly IDocumentManager _documents;
    private readonly IDiagnosticService _diagnostics;
    private readonly IConfigurationService _config;
    private readonly IWorkspaceService? _workspace;
    private readonly IAppPaths? _paths;
    private readonly ISyntaxClassificationService? _classifier;
    private readonly IFileWatcher? _watcher;
    private readonly IMemoryMonitor? _memory;
    private readonly ILogger _log;
    private readonly SpellLane _lane = new();
    private readonly DisposableStore _subscriptions = new();
    private DisposableStore _fileWatches = new();
    private readonly object _gate = new();
    private readonly Dictionary<Uri, DocState> _docs = new();
    private readonly Emitter<Uri> _resultsChanged = new();
    private readonly Emitter<SpellSettings> _settingsChanged = new();
    private readonly Timer _settingsTimer;
    private readonly Dictionary<string, (DateTime Stamp, WordList List)> _loadedLists = new(StringComparer.Ordinal); // lane only
    private readonly TaskCompletionSource _firstSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SpellSettings _settings = SpellSettings.Default;
    private SpellDictionarySet? _dictionaries; // lane only
    private string? _dictionaryKey; // lane only
    private volatile bool _dictionaryFolderChanged;
    private volatile bool _settingsReady;
    private int _settingsGeneration;
    private bool _started;
    private volatile bool _disposed;

    public SpellCheckService(IDocumentManager documents, IDiagnosticService diagnostics, IConfigurationService config, IWorkspaceService? workspace = null,
        IAppPaths? paths = null, ISyntaxClassificationService? classifier = null, IFileWatcher? watcher = null, IMemoryMonitor? memory = null,
        ILoggerFactory? loggerFactory = null)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _config = config;
        _workspace = workspace;
        _paths = paths;
        _classifier = classifier;
        _watcher = watcher ?? workspace?.Watcher;
        _memory = memory;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SpellCheckService>();
        _settingsTimer = new Timer(static s => ((SpellCheckService)s!).ReloadSettingsNow(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public SpellLane Lane => _lane;

    /// <summary>The settings in force (immutable).</summary>
    public SpellSettings Settings => Volatile.Read(ref _settings);

    /// <summary>Fires on the lane when a document's result was published (or cleared).</summary>
    public IEventSource<Uri> ResultsChanged => _resultsChanged;

    /// <summary>Fires (any thread) when new settings are in force.</summary>
    public IEventSource<SpellSettings> SettingsChanged => _settingsChanged;

    /// <summary>The user dictionary folder: <c>{UserDataDir}/dictionaries</c>.</summary>
    public string? DictionaryFolder => _paths is null ? null : Path.Combine(_paths.UserDataDir, "dictionaries");

    /// <summary>The first dictionary load's duration (milliseconds), for the status bar tooltip and docs/SPELLCHECK.md; null until loaded.</summary>
    public double? LastDictionaryLoadMs { get; private set; }

    /// <summary>Completes when the settings have been read once (tests; checks wait for it on their own).</summary>
    public Task SettingsReady => _firstSettings.Task;

    /// <summary>Subscribes to documents, settings, the editor's tokens and the dictionary folder. Idempotent; disposing the result stops it.</summary>
    public IDisposable Start()
    {
        lock (_gate)
        {
            if (_started) return Disposable.Empty;
            _started = true;
        }
        _subscriptions.Add(_config.RegisterProperties(SpellSettingsLoader.Properties));
        _subscriptions.Add(_documents.Opened.Subscribe(e => Track(e.Document)));
        _subscriptions.Add(_documents.Closed.Subscribe(e => Untrack(e.Document)));
        _subscriptions.Add(_documents.Changed.Subscribe(e => Schedule(e.Document.Uri, Settings.DelayMs)));
        _subscriptions.Add(_documents.LanguageChanged.Subscribe(e => Schedule(e.Document.Uri, 0)));
        if (_classifier is not null) _subscriptions.Add(_classifier.Changed.Subscribe(e => Schedule(e.Uri, Settings.DelayMs)));
        _subscriptions.Add(_config.Changed.Subscribe(e =>
        {
            if (e.Affects("cSpell") || e.Affects("cscode.spellCheck") || e.Affects(SettingKeys.EditorLargeFileOptimizationsLimit)) ScheduleSettingsReload();
        }));
        if (_workspace is not null) _subscriptions.Add(_workspace.FoldersChanged.Subscribe(_ =>
        {
            WatchFiles();
            ScheduleSettingsReload();
        }));
        if (_memory is not null) _subscriptions.Add(_memory.Register(this));
        foreach (var doc in _documents.Documents) Track(doc);
        WatchFiles();
        ScheduleSettingsReload(immediate: true);
        return Disposable.Create(Dispose);
    }

    // ---- documents ----

    private sealed class DocState(ITextDocument document) : IDisposable
    {
        public ITextDocument Document { get; } = document;
        public Timer? Timer;
        public int Generation;
        public SpellCheckResult? Result;
        public bool Disposed;

        public void Dispose()
        {
            Disposed = true;
            Timer?.Dispose();
            Timer = null;
        }
    }

    private void Track(ITextDocument document)
    {
        if (_disposed) return;
        DocState state;
        lock (_gate)
        {
            if (_docs.ContainsKey(document.Uri)) return;
            _docs[document.Uri] = state = new DocState(document);
        }
        state.Timer = new Timer(static s =>
        {
            var (owner, st) = ((SpellCheckService, DocState))s!;
            owner.OnTimer(st);
        }, (this, state), Timeout.Infinite, Timeout.Infinite);
        Schedule(state, 0);
    }

    private void Untrack(ITextDocument document)
    {
        DocState? state;
        lock (_gate)
        {
            if (!_docs.Remove(document.Uri, out state)) return;
        }
        lock (state) state.Dispose();
        _diagnostics.Clear(document.Uri, Owner);
        _resultsChanged.Fire(document.Uri);
    }

    /// <summary>Checks <paramref name="uri"/> after <paramref name="delayMs"/> (any thread; costs a timer change). Latest wins.</summary>
    public void Schedule(Uri uri, int delayMs)
    {
        DocState? state;
        lock (_gate) state = _docs.GetValueOrDefault(uri);
        if (state is not null) Schedule(state, delayMs);
    }

    /// <summary>Checks every open document again (settings, dictionaries or the user's words changed).</summary>
    public void ScheduleAll(int delayMs = 0)
    {
        DocState[] all;
        lock (_gate) all = _docs.Values.ToArray();
        foreach (var s in all) Schedule(s, delayMs);
    }

    private static void Schedule(DocState state, int delayMs)
    {
        lock (state)
        {
            if (state.Disposed) return;
            state.Generation++;
            state.Timer?.Change(Math.Max(0, delayMs), Timeout.Infinite);
        }
    }

    private void OnTimer(DocState state)
    {
        int generation;
        lock (state)
        {
            if (state.Disposed) return;
            generation = state.Generation;
        }
        _lane.Run(() =>
        {
            try
            {
                Begin(state, generation);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a document or token source that failed under us: this check is lost, the next change checks again
                _log.LogWarning(ex, "Spell check of {Uri} failed", state.Document.Uri);
            }
        });
    }

    private static bool Current(DocState state, int generation)
    {
        lock (state) return !state.Disposed && state.Generation == generation;
    }

    // ---- checking (lane) ----

    private void Begin(DocState state, int generation)
    {
        if (_disposed || !Current(state, generation)) return;
        if (!_settingsReady) return; // the first settings load checks every document when it lands
        var settings = Settings;
        var doc = state.Document;
        var version = doc.Version;
        var verdict = Decide(doc, settings, out var classification);
        if (verdict != SpellDocumentState.Checked)
        {
            Publish(state, generation, new SpellCheckResult(doc.Uri, version, [], false, verdict), settings);
            return;
        }
        SpellDictionarySet dictionaries;
        try
        {
            dictionaries = EnsureDictionaries(settings);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            _log.LogWarning(ex, "Loading the spell checker's dictionaries failed");
            return;
        }
        if (!Current(state, generation)) return;
        string text;
        try
        {
            text = doc.GetText();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return; // the document went away under us
        }
        if (doc.Version != version) return; // an edit landed meanwhile; its Changed event scheduled the next check
        var prose = settings.ProseLanguages.Contains(doc.LanguageId);
        var run = new CheckRun(text, classification, prose, settings, dictionaries);
        Continue(state, generation, run, version, settings);
    }

    private void Continue(DocState state, int generation, CheckRun run, int version, SpellSettings settings)
    {
        if (_disposed || !Current(state, generation)) return;
        bool done;
        try
        {
            done = run.Step(SliceBudget, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Spell check of {Uri} failed", state.Document.Uri);
            return;
        }
        if (!done)
        {
            // yield the lane between slices: other documents (and a newer generation of this one) get their turn
            _lane.Run(() => Continue(state, generation, run, version, settings));
            return;
        }
        Publish(state, generation, new SpellCheckResult(state.Document.Uri, version, run.Issues, run.Truncated, SpellDocumentState.Checked), settings);
    }

    private SpellDocumentState Decide(ITextDocument doc, SpellSettings settings, out SyntaxClassification classification)
    {
        classification = SyntaxClassification.Empty(SyntaxClassificationState.Unavailable);
        if (!settings.Enabled) return SpellDocumentState.Disabled;
        if (!settings.IsLanguageEnabled(doc.LanguageId)) return SpellDocumentState.LanguageNotEnabled;
        if (IsIgnoredPath(doc.Uri, settings)) return SpellDocumentState.IgnoredPath;
        if (doc.Length > LargeFileLimit()) return SpellDocumentState.LargeFile;
        classification = _classifier?.Capture(doc.Uri) ?? classification;
        if (classification.State == SyntaxClassificationState.LargeFile) return SpellDocumentState.LargeFile;
        if (classification.State != SyntaxClassificationState.Tokenized && !settings.ProseLanguages.Contains(doc.LanguageId)) return SpellDocumentState.NoTokens;
        return SpellDocumentState.Checked;
    }

    /// <summary>The editor's large-file threshold (<c>cscode.editor.largeFileThresholdBytes</c>, 5 MB): such documents get no language feature.</summary>
    private long LargeFileLimit() =>
        _config.GetRaw(SettingKeys.EditorLargeFileOptimizationsLimit) is { ValueKind: System.Text.Json.JsonValueKind.Number } n && n.TryGetInt64(out var limit) && limit > 0
            ? limit
            : 5L * 1024 * 1024;

    private void Publish(DocState state, int generation, SpellCheckResult result, SpellSettings settings)
    {
        lock (state)
        {
            if (state.Disposed || state.Generation != generation) return;
            if (state.Document.Version != result.Version) return; // computed for older text: the newer check publishes
            state.Result = result;
        }
        var diagnostics = new Diagnostic[result.Issues.Count];
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var issue = result.Issues[i];
            diagnostics[i] = new Diagnostic(new TextRange(new TextPosition(issue.Line, issue.Start), new TextPosition(issue.Line, issue.Start + issue.Length)),
                issue.Message, settings.Severity, Source: Owner);
        }
        _diagnostics.Set(result.Uri, Owner, diagnostics, result.Version);
        _resultsChanged.Fire(result.Uri);
    }

    /// <summary>The last published result of <paramref name="uri"/> (null: not checked yet, or not open).</summary>
    public SpellCheckResult? ResultFor(Uri uri)
    {
        DocState? state;
        lock (_gate) state = _docs.GetValueOrDefault(uri);
        if (state is null) return null;
        lock (state) return state.Result;
    }

    /// <summary>True when <paramref name="uri"/> is open and tracked.</summary>
    public bool IsTracked(Uri uri)
    {
        lock (_gate) return _docs.ContainsKey(uri);
    }

    internal int TrackedCount
    {
        get { lock (_gate) return _docs.Count; }
    }

    /// <summary>The per-document state object (leak tests).</summary>
    internal object? StateFor(Uri uri)
    {
        lock (_gate) return _docs.GetValueOrDefault(uri);
    }

    private bool IsIgnoredPath(Uri uri, SpellSettings settings)
    {
        if (settings.IgnorePaths.Count == 0 || _workspace is null) return false;
        foreach (var folder in _workspace.Folders)
        {
            var root = folder.ToString().TrimEnd('/') + "/";
            var target = uri.ToString();
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Uri.UnescapeDataString(target[root.Length..]);
            foreach (var pattern in settings.IgnorePaths)
                if (PathMatches(pattern, relative)) return true;
        }
        return false;
    }

    /// <summary>gitignore-like: a pattern without '/' matches any path segment (a folder or the file); one with '/' the relative path or a folder prefix of it.</summary>
    internal static bool PathMatches(string pattern, string relative)
    {
        var p = pattern.Trim().Replace('\\', '/');
        if (p.Length == 0 || p.StartsWith('#')) return false;
        p = p.TrimStart('/');
        if (!p.Contains('/'))
        {
            foreach (var segment in relative.Split('/'))
                if (Glob.IsMatch(p, segment)) return true;
            return false;
        }
        if (Glob.IsMatch(p, relative)) return true;
        return Glob.IsMatch(p.TrimEnd('/') + "/**", relative);
    }

    // ---- dictionaries (lane) ----

    private SpellDictionarySet EnsureDictionaries(SpellSettings settings)
    {
        var key = string.Join(',', settings.Languages);
        if (_dictionaries is { } current && _dictionaryKey == key && !_dictionaryFolderChanged) return current;
        _dictionaryFolderChanged = false;
        var started = Stopwatch.GetTimestamp();
        var available = DictionaryCatalog.Discover(DictionaryFolder);
        var (chosen, missing) = DictionaryCatalog.Choose(available, settings.Languages);
        var lists = new List<WordList>();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in chosen)
        {
            var id = source.DicPath ?? source.Name;
            keep.Add(id);
            if (!_loadedLists.TryGetValue(id, out var loaded) || loaded.Stamp != source.Stamp)
            {
                try
                {
                    loaded = (source.Stamp, DictionaryCatalog.Load(source));
                    _loadedLists[id] = loaded;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException)
                {
                    _log.LogWarning(ex, "The dictionary {Dictionary} could not be loaded", source.Name);
                    continue;
                }
            }
            lists.Add(loaded.List);
        }
        foreach (var stale in _loadedLists.Keys.Where(k => !keep.Contains(k)).ToArray()) _loadedLists.Remove(stale);
        var set = new SpellDictionarySet(chosen, lists, DictionaryCatalog.LoadSoftwareTerms(), missing);
        _dictionaries = set;
        _dictionaryKey = key;
        LastDictionaryLoadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _log.LogInformation("Spell checker dictionaries {Dictionaries} loaded in {Ms:F0} ms", string.Join(", ", chosen.Select(c => c.Name)), LastDictionaryLoadMs);
        return set;
    }

    /// <summary>The dictionaries in force, loading them if needed (on the lane).</summary>
    public Task<SpellDictionarySet> GetDictionariesAsync() => _lane.Run(() => EnsureDictionaries(Settings));

    private readonly Emitter<bool> _textsChanged = new();

    /// <summary>Fires (any thread) when new settings or dictionaries are in force: texts checked by <see cref="CheckTextAsync"/> should be checked again.</summary>
    public IEventSource<bool> TextsChanged => _textsChanged;

    /// <summary>
    /// The misspelt words of a text that is no open document (the commit message box), read whole as prose of <paramref name="languageId"/>,
    /// on the lane; offsets are into <paramref name="text"/>. Empty when spell checking is off or the language is not enabled.
    /// </summary>
    public Task<IReadOnlyList<SpellingIssue>> CheckTextAsync(string text, string languageId, CancellationToken ct = default) => _lane.Run<IReadOnlyList<SpellingIssue>>(() =>
    {
        var settings = Settings;
        if (!settings.Enabled || !settings.IsLanguageEnabled(languageId) || text.Length == 0) return [];
        var run = new CheckRun(text, SyntaxClassification.Empty(SyntaxClassificationState.NoGrammar), prose: true, settings, EnsureDictionaries(settings));
        while (!run.Step(TimeSpan.MaxValue, ct)) { }
        if (run.Issues.Count == 0) return [];
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\n' or '\r') lineStarts.Add(i + 1);
        }
        return run.Issues.Select(issue => new SpellingIssue(lineStarts[issue.Line] + issue.Start, issue.Length, issue.Word, issue.Message)).ToArray();
    }, ct);

    /// <summary>Spelling suggestions for <paramref name="word"/> (on the lane): the dictionaries', then near user words and software terms, case matched.</summary>
    public Task<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken ct = default) => _lane.Run<IReadOnlyList<string>>(() =>
    {
        var settings = Settings;
        var max = settings.NumSuggestions;
        var result = new List<string>();
        void Add(string s)
        {
            var cased = CaseMatch.Apply(word, s);
            if (result.Count < max && !string.Equals(cased, word, StringComparison.Ordinal) && !result.Contains(cased, StringComparer.Ordinal)) result.Add(cased);
        }
        if (settings.FlagWords.TryGetValue(word, out var replacements))
            foreach (var r in replacements) Add(r);
        var dictionaries = EnsureDictionaries(settings);
        foreach (var s in dictionaries.Suggest(word, max)) Add(s);
        foreach (var w in settings.Words.Where(w => EditDistance.Distance(w, word, 2) <= 2).OrderBy(w => EditDistance.Distance(w, word, 2)).Take(max)) Add(w);
        foreach (var t in dictionaries.NearTerms(word)) Add(t);
        return result;
    }, ct);

    // ---- settings ----

    private void ScheduleSettingsReload(bool immediate = false)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _settingsGeneration);
        try { _settingsTimer.Change(immediate ? 0 : 200, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void ReloadSettingsNow() => _ = ReloadSettingsAsync();

    private async Task ReloadSettingsAsync()
    {
        var generation = Volatile.Read(ref _settingsGeneration);
        SpellSettings next;
        try
        {
            next = await SpellSettingsLoader.LoadAsync(_config, _workspace?.Folders ?? [], _workspace?.FileSystem).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Reading the spell checker's settings failed");
            next = Settings;
        }
        if (_disposed || generation != Volatile.Read(ref _settingsGeneration)) return; // a newer reload is coming
        ApplySettings(next);
    }

    private void ApplySettings(SpellSettings next)
    {
        Volatile.Write(ref _settings, next);
        _settingsReady = true;
        _firstSettings.TrySetResult();
        _settingsChanged.Fire(next);
        _textsChanged.Fire(true);
        ScheduleAll(0);
    }

    /// <summary>
    /// Accepts <paramref name="words"/> at once (in memory: every open document is checked again now) while the settings write lands; the
    /// reload that follows the write keeps them.
    /// </summary>
    internal void AcceptNow(IEnumerable<string> words)
    {
        var current = Settings;
        var merged = current.Words.Concat(words).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplySettings(current with { Words = System.Collections.Frozen.FrozenSet.ToFrozenSet(merged, StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>Forbids <paramref name="word"/> at once (Vim's <c>zw</c>), as <see cref="AcceptNow"/> does for accepted words.</summary>
    internal void FlagNow(string word)
    {
        var current = Settings;
        var flags = current.FlagWords.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        flags.TryAdd(word, []);
        var words = current.Words.Where(w => !string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        ApplySettings(current with
        {
            FlagWords = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(flags, StringComparer.OrdinalIgnoreCase),
            Words = System.Collections.Frozen.FrozenSet.ToFrozenSet(words, StringComparer.OrdinalIgnoreCase),
        });
    }

    /// <summary>Re-reads the settings now and waits for them to be in force (commands after a write; tests).</summary>
    public async Task ReloadSettingsAndWaitAsync()
    {
        Interlocked.Increment(ref _settingsGeneration);
        await ReloadSettingsAsync().ConfigureAwait(false);
    }

    // ---- watching files ----

    private void WatchFiles()
    {
        var watches = new DisposableStore();
        Interlocked.Exchange(ref _fileWatches, watches).Dispose();
        if (_watcher is null || _disposed) return;
        void Watch(string directory, Func<string, bool> relevant, Action onChange)
        {
            try
            {
                watches.Add(_watcher.Watch(new Uri(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar), false, null, changes =>
                {
                    if (changes.Any(c => relevant(Path.GetFileName(c.Uri.LocalPath)))) onChange();
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
            {
                _log.LogDebug(ex, "Cannot watch {Directory}", directory);
            }
        }
        if (DictionaryFolder is { } dictionaries)
            Watch(dictionaries, n => n.EndsWith(".dic", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".aff", StringComparison.OrdinalIgnoreCase), () =>
            {
                _dictionaryFolderChanged = true;
                _textsChanged.Fire(true);
                ScheduleAll(0);
            });
        // the user file: a word added there while the workspace also sets cSpell.words changes no merged value, so the settings service is silent
        if (Path.GetDirectoryName(_config.UserSettingsPath) is { Length: > 0 } userDir && Path.IsPathRooted(userDir))
            Watch(userDir, n => n.Equals(Path.GetFileName(_config.UserSettingsPath), StringComparison.OrdinalIgnoreCase), () => ScheduleSettingsReload());
        if (_workspace is null) return;
        var names = SpellSettingsLoader.ProjectConfigNames.Concat(SpellSettingsLoader.YamlConfigNames).Select(Path.GetFileName).Append("settings.json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _workspace.Folders.Where(f => f.IsFile))
            foreach (var dir in (string[])["", ".vscode", ".cscode"])
                Watch(Path.Combine(folder.LocalPath, dir), n => names.Contains(n), () => ScheduleSettingsReload());
    }

    // ---- IMemoryTrimmable ----

    public string Name => "Spell checker dictionaries";

    public long EstimatedBytes => Volatile.Read(ref _dictionaries)?.EstimatedBytes ?? 0;

    public long Trim(MemoryTrimLevel level)
    {
        if (level == MemoryTrimLevel.Idle || _disposed) return 0;
        var current = Volatile.Read(ref _dictionaries);
        if (current is null) return 0;
        if (level == MemoryTrimLevel.Moderate)
        {
            var cached = current.CachedVerdicts * 64L;
            _lane.Run(current.ClearCache);
            return cached;
        }
        // aggressive: the word lists go; the next check loads them again (tens of milliseconds, on the lane)
        _lane.Run(() =>
        {
            _dictionaries = null;
            _dictionaryKey = null;
            _loadedLists.Clear();
        });
        return current.EstimatedBytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        _fileWatches.Dispose();
        _settingsTimer.Dispose();
        DocState[] all;
        lock (_gate)
        {
            all = _docs.Values.ToArray();
            _docs.Clear();
        }
        foreach (var s in all) lock (s) s.Dispose();
        _diagnostics.Clear(Owner);
        _resultsChanged.Clear();
        _settingsChanged.Clear();
        _textsChanged.Clear();
        _lane.Run(() =>
        {
            _dictionaries = null;
            _loadedLists.Clear();
        });
        _lane.Dispose();
    }
}
