using CsCode.Core.Lifecycle;

namespace CsCode.Core.Languages;

/// <summary>Parsed language-configuration.json (comments, brackets, auto-closing, folding, word pattern, indentation).</summary>
public sealed record LanguageConfiguration
{
    public string? LineComment { get; init; }
    public (string Open, string Close)? BlockComment { get; init; }
    public IReadOnlyList<(string Open, string Close)> Brackets { get; init; } = [];
    public IReadOnlyList<AutoClosingPair> AutoClosingPairs { get; init; } = [];
    public IReadOnlyList<(string Open, string Close)> SurroundingPairs { get; init; } = [];
    public string? WordPattern { get; init; }
    public string? FoldingMarkersStart { get; init; }
    public string? FoldingMarkersEnd { get; init; }
    public string? IncreaseIndentPattern { get; init; }
    public string? DecreaseIndentPattern { get; init; }
    public string? IndentNextLinePattern { get; init; }
    public string? UnIndentedLinePattern { get; init; }
    public IReadOnlyList<OnEnterRule> OnEnterRules { get; init; } = [];
    public bool OffsideFolding { get; init; }
}

public sealed record AutoClosingPair(string Open, string Close, IReadOnlyList<string>? NotIn = null);
public sealed record OnEnterRule(string BeforeText, string? AfterText, string Action /* none|indent|outdent|indentOutdent */, string? AppendText = null, int RemoveText = 0);

public sealed record LanguageInfo(string Id, string DisplayName, IReadOnlyList<string> Extensions, IReadOnlyList<string> Filenames);

/// <summary>
/// Maps files to language ids and exposes language configuration. Backed by IContributionRegistry (+ built-ins).
/// Implemented by CsCode.Editor; consumed by the shell (status bar language picker) and editor.
/// </summary>
public interface ILanguageService
{
    IReadOnlyList<LanguageInfo> Languages { get; }
    /// <summary>Detects by filename pattern, file extension, then first-line regex. Returns "plaintext" when unknown.</summary>
    string DetectLanguageId(Uri uri, string? firstLine = null);
    LanguageInfo? Get(string languageId);
    LanguageConfiguration GetConfiguration(string languageId);
    IEventSource<string> LanguageConfigurationChanged { get; }
    IEventSource<IReadOnlyList<LanguageInfo>> LanguagesChanged { get; }
}
