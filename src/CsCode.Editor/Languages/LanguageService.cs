using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Languages;

/// <summary>
/// <see cref="ILanguageService"/>: maps files to language ids and serves language configurations.
/// Sources: the built-in VS Code languages (<see cref="BuiltinLanguages"/>) and <see cref="LanguageContribution"/>s in the
/// <see cref="IContributionRegistry"/> (the user's <c>languages.json</c>, loaded by the editor's user-content loader). User
/// contributions are consulted before built-ins; the index is rebuilt (immutable swap, safe from any thread) whenever they change.
/// Detection order: <c>files.associations</c> → exact filename → filename pattern → longest extension → first-line regex → plaintext.
/// </summary>
public sealed class LanguageService : ILanguageService, IDisposable
{
    private sealed class Entry(string id)
    {
        public string Id { get; } = id;
        public List<string> Aliases { get; } = [];
        public List<string> Extensions { get; } = [];
        public List<string> Filenames { get; } = [];
        public List<string> Patterns { get; } = [];
        public string? FirstLine { get; set; }
        public string? ConfigPath { get; set; }
    }

    private sealed class Index
    {
        public required IReadOnlyList<LanguageInfo> Languages { get; init; }
        public required Dictionary<string, LanguageInfo> ById { get; init; }
        public required Dictionary<string, string> Filenames { get; init; }
        public required (string Pattern, string Id)[] Patterns { get; init; }
        public required Dictionary<string, string> Extensions { get; init; }
        public required (Regex Rx, string Id)[] FirstLines { get; init; }
        public required Dictionary<string, string?> ConfigPaths { get; init; }
    }

    private readonly EditorServices _svc;
    private readonly DisposableStore _subscriptions = new();
    private readonly Emitter<string> _configChanged = new();
    private readonly Emitter<IReadOnlyList<LanguageInfo>> _languagesChanged = new();
    private readonly ConcurrentDictionary<string, ExtendedLanguageConfiguration> _configs = new();
    private readonly ConcurrentDictionary<string, byte> _loading = new();
    private readonly ILogger _log;
    private Index _index;

    public LanguageService(EditorServices svc)
    {
        _svc = svc;
        _log = svc.LoggerFactory.CreateLogger<LanguageService>();
        _index = BuildIndex();
        _subscriptions.Add(svc.Contributions.Of<LanguageContribution>().Changed.Subscribe(_ => OnContributionsChanged()));
    }

    public IReadOnlyList<LanguageInfo> Languages => Volatile.Read(ref _index).Languages;
    public IEventSource<string> LanguageConfigurationChanged => _configChanged;
    public IEventSource<IReadOnlyList<LanguageInfo>> LanguagesChanged => _languagesChanged;

    public LanguageInfo? Get(string languageId) => Volatile.Read(ref _index).ById.GetValueOrDefault(languageId);

    // ---- detection ----

    public string DetectLanguageId(Uri uri, string? firstLine = null)
    {
        var index = Volatile.Read(ref _index);
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

        // 1. user's files.associations (glob -> language id), in the order the user wrote them
        if (_svc.Config.GetRaw("files.associations") is { ValueKind: JsonValueKind.Object } assoc)
            foreach (var p in assoc.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } id && Glob.IsMatch(p.Name, path))
                    return id;

        // 2. exact file name   3. file name pattern
        if (index.Filenames.TryGetValue(name.ToLowerInvariant(), out var byName)) return byName;
        foreach (var (pattern, id) in index.Patterns)
            if (Glob.IsMatch(pattern, path)) return id;

        // 4. extension: the LONGEST matching suffix wins (".d.ts" beats ".ts"); dotfiles match as a whole (".gitignore")
        var lower = name.ToLowerInvariant();
        for (var dot = lower.IndexOf('.'); dot >= 0; dot = lower.IndexOf('.', dot + 1))
            if (index.Extensions.TryGetValue(lower[dot..], out var byExt)) return byExt;

        // 5. first line (shebangs, <?xml, …)
        if (!string.IsNullOrEmpty(firstLine))
            foreach (var (rx, id) in index.FirstLines)
            {
                try
                {
                    if (rx.IsMatch(firstLine)) return id;
                }
                catch (RegexMatchTimeoutException)
                {
                    // a pathological contributed pattern must not stall opening a file: skip it
                }
            }
        return "plaintext";
    }

    // ---- configuration ----

    public LanguageConfiguration GetConfiguration(string languageId) => GetExtended(languageId).Base;

    /// <summary>Configuration plus editor-only extras, with empty bracket/pair lists filled from the defaults so editing always works.</summary>
    internal ExtendedLanguageConfiguration GetExtended(string languageId)
    {
        if (_configs.TryGetValue(languageId, out var cached)) return cached;
        var index = Volatile.Read(ref _index);
        var path = index.ConfigPaths.GetValueOrDefault(languageId);
        ExtendedLanguageConfiguration? parsed = null;
        if (path is null) parsed = WithDefaults(new ExtendedLanguageConfiguration(new LanguageConfiguration(), null));
        else if (path.StartsWith(BuiltinLanguages.BuiltinPathPrefix, StringComparison.Ordinal))
        {
            var text = BuiltinLanguages.ReadText(path[BuiltinLanguages.BuiltinPathPrefix.Length..]);
            parsed = text is not null && LanguageConfigurationParser.TryParse(text, out var ok) ? WithDefaults(ok) : WithDefaults(new ExtendedLanguageConfiguration(new LanguageConfiguration(), null));
        }
        if (parsed is not null) return _configs.GetOrAdd(languageId, parsed);

        // A user file: loading it is I/O, which never happens on the caller's (possibly UI) thread. Serve defaults until it arrives.
        if (_loading.TryAdd(languageId, 0)) _ = LoadFileConfigurationAsync(languageId, path!); // path is non-null here: the null case returned above
        return WithDefaults(new ExtendedLanguageConfiguration(new LanguageConfiguration(), null));
    }

    private static ExtendedLanguageConfiguration WithDefaults(ExtendedLanguageConfiguration c)
    {
        var b = c.Base;
        if (b.Brackets.Count > 0 && b.AutoClosingPairs.Count > 0 && b.SurroundingPairs.Count > 0) return c;
        var d = LanguageConfigurationParser.Default;
        return c with
        {
            Base = b with
            {
                Brackets = b.Brackets.Count > 0 ? b.Brackets : d.Brackets,
                AutoClosingPairs = b.AutoClosingPairs.Count > 0 ? b.AutoClosingPairs : d.AutoClosingPairs,
                SurroundingPairs = b.SurroundingPairs.Count > 0 ? b.SurroundingPairs : d.SurroundingPairs,
            },
        };
    }

    private async Task LoadFileConfigurationAsync(string languageId, string path)
    {
        try
        {
            var bytes = await _svc.FileSystem.ReadFileAsync(new Uri(path)).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(bytes);
            if (!LanguageConfigurationParser.TryParse(text, out var parsed))
            {
                _log.LogWarning("Language configuration {Path} is not valid JSON; using defaults", path);
                parsed = new ExtendedLanguageConfiguration(new LanguageConfiguration(), null);
            }
            _configs[languageId] = WithDefaults(parsed);
            _configChanged.Fire(languageId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read language configuration {Path}", path);
            _configs[languageId] = WithDefaults(new ExtendedLanguageConfiguration(new LanguageConfiguration(), null));
        }
        finally
        {
            _loading.TryRemove(languageId, out _);
        }
    }

    // ---- live updates ----

    private void OnContributionsChanged()
    {
        var loaded = _configs.Keys.ToArray();
        Volatile.Write(ref _index, BuildIndex());
        _configs.Clear();
        _languagesChanged.Fire(Languages);
        foreach (var id in loaded) _configChanged.Fire(id);
    }

    private Index BuildIndex()
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        void Merge(LanguageContribution c)
        {
            if (!entries.TryGetValue(c.Id, out var e)) entries[c.Id] = e = new Entry(c.Id);
            AddAll(e.Aliases, c.Aliases);
            AddAll(e.Extensions, c.Extensions);
            AddAll(e.Filenames, c.Filenames);
            AddAll(e.Patterns, c.FilenamePatterns);
            if (c.FirstLine is not null) e.FirstLine = c.FirstLine;
            if (c.ConfigurationPath is not null) e.ConfigPath = c.ConfigurationPath;
        }

        foreach (var c in BuiltinLanguages.Catalog.Languages) Merge(c);
        var contributed = _svc.Contributions.Of<LanguageContribution>().Items;
        foreach (var c in contributed) Merge(c.Value);

        var filenames = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(string, string)>();
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstLines = new List<(Regex, string)>();
        var infos = new List<LanguageInfo>();
        var byId = new Dictionary<string, LanguageInfo>(StringComparer.Ordinal);
        var configs = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Built-ins first so that user contributions (merged later) overwrite them in the lookup tables.
        var builtinIds = BuiltinLanguages.Catalog.Languages.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var e in entries.Values.OrderBy(e => builtinIds.Contains(e.Id) ? 0 : 1).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            foreach (var f in e.Filenames) filenames[f.ToLowerInvariant()] = e.Id;
            foreach (var p in e.Patterns) patterns.Add((p, e.Id));
            foreach (var x in e.Extensions) extensions[(x.StartsWith('.') ? x : "." + x).ToLowerInvariant()] = e.Id;
            if (e.FirstLine is not null)
            {
                try
                {
                    firstLines.Add((new Regex(e.FirstLine, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), e.Id));
                }
                catch (ArgumentException)
                {
                    // invalid firstLine regex in a contribution: that rule is ignored
                }
            }
            configs[e.Id] = e.ConfigPath;
            if (e.Aliases.Count > 0)
            {
                var info = new LanguageInfo(e.Id, e.Aliases[0], e.Extensions.ToArray(), e.Filenames.ToArray(), null);
                infos.Add(info);
                byId[e.Id] = info;
            }
            else byId[e.Id] = new LanguageInfo(e.Id, e.Id, e.Extensions.ToArray(), e.Filenames.ToArray(), null);
        }

        // Later (user) patterns must win over earlier (built-in) ones: reverse so the first match in the list is the most specific source.
        patterns.Reverse();
        firstLines.Reverse();
        return new Index
        {
            Languages = infos.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            ById = byId,
            Filenames = filenames,
            Patterns = patterns.ToArray(),
            Extensions = extensions,
            FirstLines = firstLines.ToArray(),
            ConfigPaths = configs,
        };
    }

    private static void AddAll(List<string> target, IReadOnlyList<string>? items)
    {
        if (items is null) return;
        foreach (var i in items)
            if (!target.Contains(i, StringComparer.OrdinalIgnoreCase)) target.Add(i);
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _configChanged.Clear();
        _languagesChanged.Clear();
    }
}
