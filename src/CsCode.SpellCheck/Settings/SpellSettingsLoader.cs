using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;

namespace CsCode.SpellCheck.Settings;

/// <summary>
/// Builds <see cref="SpellSettings"/> the way Code Spell Checker (cspell) does in VS Code, so a project's existing configuration keeps working:
/// <list type="bullet">
/// <item>The word lists (<c>cSpell.words</c>, <c>cSpell.userWords</c>, <c>cSpell.ignoreWords</c>, <c>cSpell.flagWords</c>) are merged from
/// EVERY settings file — user, the folder's <c>.vscode/settings.json</c> and <c>.cscode/settings.json</c> — not taken from the top layer
/// only, as cspell merges them; plus the merged value the configuration service hands out.</item>
/// <item>A project's <c>cspell.json</c> / <c>.cspell.json</c> / <c>cspell.config.json</c> (also under <c>.vscode/</c>): its <c>words</c>,
/// <c>ignoreWords</c>, <c>flagWords</c>, <c>ignorePaths</c> and <c>ignoreRegExpList</c> are added, word-list files named by
/// <c>dictionaryDefinitions</c> + <c>dictionaries</c> are read, and its <c>language</c>, <c>enabled</c> and <c>minWordLength</c> apply where
/// the settings keep their defaults. YAML configs are not read (no YAML reader in CsCode): they are reported in <see cref="SpellSettings.Problems"/>.</item>
/// <item>Everything else comes from the merged settings.</item>
/// </list>
/// File I/O: call off the UI thread.
/// </summary>
public static class SpellSettingsLoader
{
    public const string Enabled = "cSpell.enabled";
    public const string Language = "cSpell.language";
    public const string Words = "cSpell.words";
    public const string UserWords = "cSpell.userWords";
    public const string IgnoreWords = "cSpell.ignoreWords";
    public const string FlagWords = "cSpell.flagWords";
    public const string EnabledLanguageIds = "cSpell.enabledLanguageIds";
    public const string EnabledFileTypes = "cSpell.enabledFileTypes";
    public const string EnableFiletypes = "cSpell.enableFiletypes";
    public const string IgnorePaths = "cSpell.ignorePaths";
    public const string IgnoreRegExpList = "cSpell.ignoreRegExpList";
    public const string MinWordLength = "cSpell.minWordLength";
    public const string DiagnosticLevel = "cSpell.diagnosticLevel";
    public const string MaxNumberOfProblems = "cSpell.maxNumberOfProblems";
    public const string MaxDuplicateProblems = "cSpell.maxDuplicateProblems";
    public const string CheckLimit = "cSpell.checkLimit";
    public const string NumSuggestions = "cSpell.numSuggestions";
    public const string CheckIdentifiers = "cscode.spellCheck.checkIdentifiers";
    public const string ProseLanguagesKey = "cscode.spellCheck.proseLanguages";
    public const string Delay = "cscode.spellCheck.delay";

    /// <summary>cspell's <c>enabledLanguageIds</c> default, plus the languages CsCode knows that cspell's list predates.</summary>
    public static readonly string[] DefaultLanguageIds =
    [
        "asciidoc", "c", "clojure", "cmake", "coffeescript", "cpp", "csharp", "css", "dart", "dockerfile", "elixir", "erlang", "fsharp",
        "git-commit", "go", "graphql", "handlebars", "haskell", "html", "jade", "java", "javascript", "javascriptreact", "json", "jsonc",
        "jupyter", "kotlin", "latex", "less", "lua", "makefile", "markdown", "nix", "objective-c", "objective-cpp", "ocaml", "org", "perl",
        "php", "plaintext", "powershell", "protobuf", "pug", "python", "r", "razor", "restructuredtext", "ruby", "rust", "scala", "scminput",
        "scss", "shellscript", "sql", "svelte", "swift", "terraform", "text", "toml", "typescript", "typescriptreact", "vb", "vue", "xml",
        "yaml", "yml", "zig",
    ];

    /// <summary>cspell's <c>ignorePaths</c> default.</summary>
    public static readonly string[] DefaultIgnorePaths = ["package-lock.json", "node_modules", "vscode-extension", ".git/objects", ".vscode-insiders"];

    /// <summary>Languages read whole when no grammar says what is code (plain text, org, AsciiDoc, the Source Control input).</summary>
    public static readonly string[] DefaultProseLanguages = ["plaintext", "text", "markdown", "git-commit", "org", "asciidoc", "scminput"];

    /// <summary>The project config file names cspell looks for in a folder, in its order (JSON only: see the class remarks).</summary>
    public static readonly string[] ProjectConfigNames =
    [
        ".cspell.json", "cspell.json", ".cSpell.json", "cSpell.json", "cspell.config.json",
        ".vscode/cspell.json", ".vscode/cSpell.json", ".vscode/.cspell.json",
    ];

    public static readonly string[] YamlConfigNames = ["cspell.yaml", "cspell.yml", ".cspell.yaml", ".cspell.yml", "cspell.config.yaml", "cspell.config.yml"];

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The settings declared to the configuration service (defaults and descriptions for the Settings editor).</summary>
    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        Declare(Enabled, true, "Enable the spell checker (Code Spell Checker's setting; CsCode's checker is built in)."),
        Declare(Language, "en", "The dictionaries to use, comma separated: \"en\" (bundled en_US), \"en-GB\", \"de\"... Dictionaries other than en_US are .dic/.aff pairs in {UserDataDir}/dictionaries."),
        Declare(Words, Array.Empty<string>(), "Words to accept (merged from user, workspace and folder settings and the project's cspell.json)."),
        Declare(UserWords, Array.Empty<string>(), "Words to accept (user settings; Code Spell Checker's older setting)."),
        Declare(IgnoreWords, Array.Empty<string>(), "Words never reported."),
        Declare(FlagWords, Array.Empty<string>(), "Words always reported as forbidden; \"hte->the\" also offers the replacement."),
        Declare(EnabledLanguageIds, DefaultLanguageIds, "The language ids that are checked."),
        Declare(EnabledFileTypes, new Dictionary<string, bool>(), "Enable or disable checking per language id: { \"*\": true, \"json\": false }; the older array form [\"*\", \"!json\"] is read too."),
        Declare(IgnorePaths, DefaultIgnorePaths, "Glob patterns of files not to check (relative to the workspace folder)."),
        Declare(IgnoreRegExpList, Array.Empty<string>(), "Regular expressions (\"/pattern/flags\") of text not to check, or pattern names: Urls, Email, HexValues, UUID, Base64."),
        Declare(MinWordLength, 4, "Shorter words are not checked."),
        Declare(DiagnosticLevel, "Information", "Severity of spelling diagnostics: Error, Warning, Information or Hint."),
        Declare(MaxNumberOfProblems, 100, "At most this many spelling problems are reported per file."),
        Declare(MaxDuplicateProblems, 20, "At most this many problems are reported for one word in a file."),
        Declare(CheckLimit, 500, "Only the first this many kilobytes of a file are checked."),
        Declare(NumSuggestions, 5, "How many spelling suggestions a quick fix offers."),
        Declare(CheckIdentifiers, false, "Also check identifiers (split into their words), not only comments, strings and prose. Off by default: identifiers are abbreviated by design and come from libraries you cannot rename."),
        Declare(ProseLanguagesKey, DefaultProseLanguages, "Languages whose whole text is checked when the editor has no grammar for them."),
        Declare(Delay, 400, "Milliseconds of quiet after an edit before the file is checked again."),
    ];

    /// <summary>One settings file's contribution (null when the file is absent or unreadable).</summary>
    public sealed record FileLayer(string Path, JsonElement Root);

    /// <summary>
    /// Reads every source and builds the settings. <paramref name="folders"/> are the workspace folders; <paramref name="fileSystem"/> reads them
    /// (the workspace's, so a remote folder works); the user settings file is a local path.
    /// </summary>
    public static async Task<SpellSettings> LoadAsync(IConfigurationService config, IReadOnlyList<Uri> folders, IFileSystem? fileSystem, CancellationToken ct = default)
    {
        var settingsFiles = new List<JsonElement>();
        if (ReadLocalJson(config.UserSettingsPath) is { } user) settingsFiles.Add(user);
        var projects = new List<FileLayer>();
        var problems = new List<string>();
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in (string[])[".vscode/settings.json", ".cscode/settings.json"])
                if (await ReadJsonAsync(fileSystem, Combine(folder, name), ct).ConfigureAwait(false) is { } layer) settingsFiles.Add(layer);
            var found = false;
            foreach (var name in ProjectConfigNames)
            {
                var uri = Combine(folder, name);
                if (await ReadJsonAsync(fileSystem, uri, ct).ConfigureAwait(false) is not { } root) continue;
                projects.Add(new FileLayer(uri.IsFile ? uri.LocalPath : uri.ToString(), root));
                found = true;
                break; // cspell uses the first config it finds in a folder
            }
            if (!found)
                foreach (var name in YamlConfigNames)
                    if (await ExistsAsync(fileSystem, Combine(folder, name), ct).ConfigureAwait(false))
                    {
                        problems.Add($"{name} is not read (YAML cspell configs are not supported; use cspell.json)");
                        break;
                    }
        }
        var wordFiles = new List<string>();
        foreach (var project in projects)
            foreach (var file in DictionaryDefinitionFiles(project))
                if (await ReadTextAsync(fileSystem, file, ct).ConfigureAwait(false) is { } text) wordFiles.Add(text);
        return Build(config, settingsFiles, projects, wordFiles, problems);
    }

    /// <summary>The pure part of <see cref="LoadAsync"/> (tests build settings from JSON without files).</summary>
    public static SpellSettings Build(IConfigurationService config, IReadOnlyList<JsonElement> settingsFiles, IReadOnlyList<FileLayer> projects,
        IReadOnlyList<string> wordFileTexts, List<string>? problems = null)
    {
        problems ??= [];
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void AddAll(HashSet<string> set, IEnumerable<string> items)
        {
            foreach (var w in items)
            {
                var t = w.Trim();
                if (t.Length > 0) set.Add(t);
            }
        }
        void AddFlags(IEnumerable<string> items)
        {
            foreach (var raw in items)
            {
                // "hte->the" or "hte:the" (cspell's forms), several suggestions separated by commas
                var arrow = raw.IndexOf("->", StringComparison.Ordinal);
                var colon = arrow < 0 ? raw.IndexOf(':') : -1;
                var cut = arrow >= 0 ? arrow : colon;
                var word = (cut >= 0 ? raw[..cut] : raw).Trim();
                if (word.Length == 0) continue;
                if (!flags.TryGetValue(word, out var list)) flags[word] = list = [];
                if (cut >= 0)
                    foreach (var s in raw[(cut + (arrow >= 0 ? 2 : 1))..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!list.Contains(s, StringComparer.Ordinal)) list.Add(s);
            }
        }

        // the merged value first (covers any layer this loader does not read itself), then every settings file
        AddAll(words, StringList(config.GetRaw(Words)));
        AddAll(words, StringList(config.GetRaw(UserWords)));
        AddAll(ignore, StringList(config.GetRaw(IgnoreWords)));
        AddFlags(StringList(config.GetRaw(FlagWords)));
        foreach (var file in settingsFiles)
        {
            AddAll(words, StringList(Find(file, Words)));
            AddAll(words, StringList(Find(file, UserWords)));
            AddAll(ignore, StringList(Find(file, IgnoreWords)));
            AddFlags(StringList(Find(file, FlagWords)));
        }

        var ignorePaths = new List<string>(StringList(config.GetRaw(IgnorePaths)) is { Count: > 0 } configured ? configured : DefaultIgnorePaths);
        var regexSources = new List<string>(StringList(config.GetRaw(IgnoreRegExpList)));
        var language = config.Get(Language, "en");
        var enabled = config.Get(Enabled, true);
        var minWordLength = config.Get(MinWordLength, 4);
        foreach (var project in projects)
        {
            var root = project.Root;
            if (root.ValueKind != JsonValueKind.Object) continue;
            AddAll(words, StringList(Prop(root, "words")));
            AddAll(ignore, StringList(Prop(root, "ignoreWords")));
            AddFlags(StringList(Prop(root, "flagWords")));
            ignorePaths.AddRange(StringList(Prop(root, "ignorePaths")));
            regexSources.AddRange(StringList(Prop(root, "ignoreRegExpList")));
            // the project's scalars apply where the settings keep their defaults
            if (language == "en" && Prop(root, "language") is { ValueKind: JsonValueKind.String } lang && lang.GetString() is { Length: > 0 } l) language = l;
            if (enabled && Prop(root, "enabled") is { ValueKind: JsonValueKind.False }) enabled = false;
            if (minWordLength == 4 && Prop(root, "minWordLength") is { ValueKind: JsonValueKind.Number } mwl && mwl.TryGetInt32(out var m)) minWordLength = m;
        }
        foreach (var text in wordFileTexts)
            foreach (var line in text.Split('\n'))
            {
                var w = line.Trim();
                if (w.Length > 0 && w[0] != '#') words.Add(w.TrimStart('+', '~'));
            }

        var (enabledLanguages, all, disabled) = LanguageFilter(config);
        var proseLanguages = StringList(config.GetRaw(ProseLanguagesKey));
        return new SpellSettings
        {
            Enabled = enabled,
            Languages = language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Words = words.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            IgnoreWords = ignore.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            FlagWords = flags.ToFrozenDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase),
            EnabledLanguages = enabledLanguages.ToFrozenSet(StringComparer.Ordinal),
            AllLanguages = all,
            DisabledLanguages = disabled.ToFrozenSet(StringComparer.Ordinal),
            IgnorePaths = ignorePaths.Distinct(StringComparer.Ordinal).ToArray(),
            IgnoreRegExps = CompilePatterns(regexSources, problems),
            MinWordLength = Math.Max(1, minWordLength),
            Severity = ParseSeverity(config.Get(DiagnosticLevel, "Information")),
            MaxNumberOfProblems = Math.Max(1, config.Get(MaxNumberOfProblems, 100)),
            MaxDuplicateProblems = Math.Max(1, config.Get(MaxDuplicateProblems, 20)),
            CheckLimitKb = Math.Max(1, config.Get(CheckLimit, 500)),
            NumSuggestions = Math.Clamp(config.Get(NumSuggestions, 5), 1, 20),
            CheckIdentifiers = config.Get(CheckIdentifiers, false),
            ProseLanguages = (proseLanguages.Count > 0 ? proseLanguages : DefaultProseLanguages).ToFrozenSet(StringComparer.Ordinal),
            DelayMs = Math.Clamp(config.Get(Delay, 400), 0, 10_000),
            ProjectConfigFiles = projects.Select(p => p.Path).ToArray(),
            Problems = problems.ToArray(),
        };
    }

    // ---- language filter ----

    private static (HashSet<string> Enabled, bool All, HashSet<string> Disabled) LanguageFilter(IConfigurationService config)
    {
        var ids = StringList(config.GetRaw(EnabledLanguageIds));
        var enabled = new HashSet<string>(ids.Count > 0 ? ids : DefaultLanguageIds, StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var all = false;
        void Apply(string id, bool on)
        {
            if (id == "*")
            {
                all = on;
                if (!on) enabled.Clear();
                return;
            }
            if (on)
            {
                enabled.Add(id);
                disabled.Remove(id);
            }
            else
            {
                enabled.Remove(id);
                disabled.Add(id);
            }
        }
        foreach (var key in (string[])[EnableFiletypes, EnabledFileTypes])
        {
            switch (config.GetRaw(key))
            {
                case { ValueKind: JsonValueKind.Array } array:
                    foreach (var item in array.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                            Apply(s.TrimStart('!'), !s.StartsWith('!'));
                    break;
                case { ValueKind: JsonValueKind.Object } map:
                    // "*" first, so { "*": true, "json": false } means "everything but JSON" whatever the order
                    if (map.TryGetProperty("*", out var star) && star.ValueKind is JsonValueKind.True or JsonValueKind.False) Apply("*", star.ValueKind == JsonValueKind.True);
                    foreach (var p in map.EnumerateObject())
                        if (p.Name != "*" && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) Apply(p.Name, p.Value.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        return (enabled, all, disabled);
    }

    // ---- patterns ----

    /// <summary>cspell's pattern names CsCode knows, as .NET expressions.</summary>
    private static readonly Dictionary<string, string> NamedPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Urls"] = @"(?:https?|ftp|file)://[^\s""'<>()\[\]{}`]+",
        ["Email"] = @"\b[\w.+-]+@[\w-]+\.[\w.-]+\b",
        ["HexValues"] = @"\b0[xX][0-9a-fA-F_]+\b|#[0-9a-fA-F]{3,8}\b",
        ["HexDigits"] = @"\b[0-9a-fA-F]{6,}\b",
        ["UUID"] = @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        ["Base64"] = @"(?<![\w/+])(?:[A-Za-z0-9+/]{4}){10,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?(?![\w/+])",
        ["CommitHash"] = @"\b(?=[0-9a-f]*\d)[0-9a-f]{7,40}\b",
        ["SpellCheckerDisable"] = @"(?:cspell|spell-?checker)\s*:\s*disable[\s\S]*?(?:(?:cspell|spell-?checker)\s*:\s*enable|$(?![\s\S]))",
    };

    /// <summary>
    /// <c>"/pattern/flags"</c> (JavaScript's form: <c>i</c>, <c>m</c>, <c>s</c>, <c>u</c>, <c>x</c>, <c>g</c>, <c>y</c>; <c>g</c> and <c>y</c>
    /// change nothing here), a pattern name, or a bare expression. A pattern .NET rejects is reported and skipped; every match runs with a
    /// 250 ms timeout.
    /// </summary>
    public static IReadOnlyList<Regex> CompilePatterns(IEnumerable<string> sources, List<string>? problems = null)
    {
        var result = new List<Regex>();
        foreach (var source in sources.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var pattern = source;
            var options = RegexOptions.CultureInvariant;
            if (NamedPatterns.TryGetValue(source.Trim(), out var named)) pattern = named;
            else if (source.Length >= 2 && source[0] == '/' && source.LastIndexOf('/') is var close and > 0)
            {
                pattern = source[1..close];
                foreach (var f in source[(close + 1)..])
                    options |= f switch
                    {
                        'i' => RegexOptions.IgnoreCase,
                        'm' => RegexOptions.Multiline,
                        's' => RegexOptions.Singleline,
                        'x' => RegexOptions.IgnorePatternWhitespace,
                        _ => RegexOptions.None,
                    };
            }
            try { result.Add(new Regex(pattern, options, RegexTimeout)); }
            catch (ArgumentException ex) { problems?.Add($"ignoreRegExpList: {source} is not a valid pattern ({ex.Message})"); }
        }
        return result;
    }

    private static DiagnosticSeverity ParseSeverity(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        "hint" => DiagnosticSeverity.Hint,
        _ => DiagnosticSeverity.Information,
    };

    // ---- JSON ----

    /// <summary>A key of a settings file, flat (<c>"cSpell.words"</c>) or nested (<c>{"cSpell": {"words": …}}</c>).</summary>
    public static JsonElement? Find(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty(key, out var flat)) return flat;
        var dot = key.IndexOf('.');
        if (dot < 0) return null;
        return root.TryGetProperty(key[..dot], out var section) ? Find(section, key[(dot + 1)..]) : null;
    }

    private static JsonElement? Prop(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) ? v : null;

    public static IReadOnlyList<string> StringList(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array) return value is { ValueKind: JsonValueKind.String } s && s.GetString() is { } one ? [one] : [];
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } str) list.Add(str);
        return list;
    }

    /// <summary>Parses JSON with comments and trailing commas; null when it is not JSON.</summary>
    public static JsonElement? ParseJsonc(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Jsonc);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ReadLocalJson(string path)
    {
        try { return File.Exists(path) ? ParseJsonc(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<JsonElement?> ReadJsonAsync(IFileSystem? fs, Uri uri, CancellationToken ct) =>
        await ReadTextAsync(fs, uri, ct).ConfigureAwait(false) is { } text ? ParseJsonc(text) : null;

    private static async Task<string?> ReadTextAsync(IFileSystem? fs, Uri uri, CancellationToken ct)
    {
        try
        {
            if (fs is null)
                return uri.IsFile && File.Exists(uri.LocalPath) ? await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false) : null;
            if (await fs.StatAsync(uri, ct).ConfigureAwait(false) is null) return null;
            var bytes = await fs.ReadFileAsync(uri, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<bool> ExistsAsync(IFileSystem? fs, Uri uri, CancellationToken ct)
    {
        try
        {
            if (fs is null) return uri.IsFile && File.Exists(uri.LocalPath);
            return await fs.StatAsync(uri, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The word-list files a cspell config names: <c>dictionaryDefinitions</c> entries that its <c>dictionaries</c> list (or that say <c>addWords</c>).</summary>
    private static IEnumerable<Uri> DictionaryDefinitionFiles(FileLayer project)
    {
        if (Prop(project.Root, "dictionaryDefinitions") is not { ValueKind: JsonValueKind.Array } defs) yield break;
        var used = new HashSet<string>(StringList(Prop(project.Root, "dictionaries")), StringComparer.OrdinalIgnoreCase);
        var baseUri = project.Path.Contains("://", StringComparison.Ordinal) ? new Uri(project.Path) : new Uri(project.Path);
        foreach (var def in defs.EnumerateArray())
        {
            if (def.ValueKind != JsonValueKind.Object) continue;
            var name = Prop(def, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
            var path = Prop(def, "path") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
            var addWords = Prop(def, "addWords") is { ValueKind: JsonValueKind.True };
            if (string.IsNullOrEmpty(path) || (!addWords && (name is null || !used.Contains(name)))) continue;
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".dic", StringComparison.OrdinalIgnoreCase)) continue;
            Uri target;
            try { target = new Uri(baseUri, path.Replace('\\', '/')); }
            catch (UriFormatException) { continue; }
            yield return target;
        }
    }

    private static Uri Combine(Uri folder, string relative)
    {
        var s = folder.ToString();
        return new Uri(s.EndsWith('/') ? s + relative : s + "/" + relative);
    }

    private static ConfigurationProperty Declare<T>(string key, T value, string description) =>
        new(key, JsonSerializer.SerializeToElement(value), description);
}
