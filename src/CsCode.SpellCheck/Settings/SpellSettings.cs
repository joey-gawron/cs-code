using System.Collections.Frozen;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;

namespace CsCode.SpellCheck.Settings;

/// <summary>
/// Everything that decides what is checked and how, read from cspell's VS Code settings (user, workspace, folder), a project's cspell config
/// file and CsCode's own <c>cscode.spellCheck.*</c> settings (<see cref="SpellSettingsLoader"/>). Immutable: a check reads one instance
/// from start to end; a change makes a new one.
/// </summary>
public sealed record SpellSettings
{
    public static readonly SpellSettings Default = new();

    public bool Enabled { get; init; } = true;

    /// <summary><c>cSpell.language</c> split at commas: <c>"en,de"</c> → en, de.</summary>
    public IReadOnlyList<string> Languages { get; init; } = ["en"];

    /// <summary>Words accepted everywhere (<c>cSpell.words</c>, <c>cSpell.userWords</c>, the config file's <c>words</c> and word lists), any case.</summary>
    public FrozenSet<string> Words { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Words never reported (<c>cSpell.ignoreWords</c>, <c>ignoreWords</c>), any case.</summary>
    public FrozenSet<string> IgnoreWords { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Words always reported (<c>cSpell.flagWords</c>, <c>flagWords</c>), any case; <c>"hte->the"</c> gives the suggestion.</summary>
    public FrozenDictionary<string, IReadOnlyList<string>> FlagWords { get; init; } = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>The language ids checked (<c>cSpell.enabledLanguageIds</c>, adjusted by <c>cSpell.enabledFileTypes</c> / <c>enableFiletypes</c>).</summary>
    public FrozenSet<string> EnabledLanguages { get; init; } = SpellSettingsLoader.DefaultLanguageIds.ToFrozenSet(StringComparer.Ordinal);

    /// <summary><c>"*"</c> was enabled: every language is checked except <see cref="DisabledLanguages"/>.</summary>
    public bool AllLanguages { get; init; }

    public FrozenSet<string> DisabledLanguages { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Glob patterns (gitignore-like, relative to a workspace folder) of files not checked.</summary>
    public IReadOnlyList<string> IgnorePaths { get; init; } = SpellSettingsLoader.DefaultIgnorePaths;

    /// <summary>Text matched by these is not checked (<c>cSpell.ignoreRegExpList</c>, <c>ignoreRegExpList</c>).</summary>
    public IReadOnlyList<Regex> IgnoreRegExps { get; init; } = [];

    public int MinWordLength { get; init; } = 4;
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Information;
    public int MaxNumberOfProblems { get; init; } = 100;
    public int MaxDuplicateProblems { get; init; } = 20;

    /// <summary><c>cSpell.checkLimit</c>: only the first this many kilobytes of a file are checked.</summary>
    public int CheckLimitKb { get; init; } = 500;

    public int NumSuggestions { get; init; } = 5;

    /// <summary><c>cscode.spellCheck.checkIdentifiers</c>: code (identifiers, split into words) is checked as well as comments and strings.</summary>
    public bool CheckIdentifiers { get; init; }

    /// <summary>Languages whose whole text is read as prose when the editor has no tokens for them (<c>cscode.spellCheck.proseLanguages</c>).</summary>
    public FrozenSet<string> ProseLanguages { get; init; } = SpellSettingsLoader.DefaultProseLanguages.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Delay between the last edit and the check, in milliseconds (<c>cscode.spellCheck.delay</c>).</summary>
    public int DelayMs { get; init; } = 400;

    /// <summary>The project's cspell config files that exist (JSON ones first): Add to Workspace Dictionary writes the first JSON one.</summary>
    public IReadOnlyList<string> ProjectConfigFiles { get; init; } = [];

    /// <summary>What could not be used (a YAML config, a bad pattern, a language without a dictionary): shown in the status bar's tooltip.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool IsLanguageEnabled(string languageId) =>
        !DisabledLanguages.Contains(languageId) && (AllLanguages || EnabledLanguages.Contains(languageId));

    /// <summary>The word is accepted by the user's or the project's word lists, or ignored.</summary>
    public bool IsAccepted(string word) => Words.Contains(word) || IgnoreWords.Contains(word);
}
