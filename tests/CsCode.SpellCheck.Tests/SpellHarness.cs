using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.TestDoubles;

namespace CsCode.SpellCheck.Tests;

/// <summary>The checker and its workbench glue over Core's real registries and the shared fakes; checks run with no delay.</summary>
internal sealed class SpellHarness : IDisposable
{
    public DocumentManager Documents { get; } = new();
    public DiagnosticService Diagnostics { get; } = new();
    public TestConfigurationService Config { get; } = new();
    public FakeClassifier Classifier { get; } = new();
    public LanguageFeatureRegistry Features { get; } = new();
    public TestCommandService Commands { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public InMemoryStatusBarService Status { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public SpellCheckService Service { get; }
    public SpellCheckHost Host { get; }
    private readonly IDisposable _started;
    private readonly List<IDisposable> _registrations = [];

    public SpellHarness(Action<TestConfigurationService>? configure = null, ISyntaxClassificationService? classifier = null)
    {
        Config.Set("cscode.spellCheck.delay", 0);
        configure?.Invoke(Config);
        Navigation = new RecordingNavigationService(Documents);
        Service = new SpellCheckService(Documents, Diagnostics, Config, classifier: classifier ?? Classifier);
        Host = new SpellCheckHost(Service, Config, Commands, Features, Status, Window, workspace: null, new FakeLanguages(), () => Navigation);
        _started = Host.Start();
    }

    /// <summary>Opens a document; with <paramref name="classify"/> the fake classifier says "// comments" and "strings" as the editor would.</summary>
    public InMemoryTextDocument Open(string text, string languageId = "csharp", string name = "a.cs", bool classify = true)
    {
        var doc = new InMemoryTextDocument(new Uri("file:///work/" + name), languageId, text);
        if (classify) Classifier.Set(doc.Uri, FakeClassification.SlashComments(text), notify: false);
        _registrations.Add(Documents.Register(doc));
        return doc;
    }

    /// <summary>Applies an edit; the fake classifier is told the new text's runs (what the editor's tokeniser does right after).</summary>
    public void Edit(InMemoryTextDocument doc, TextEdit edit)
    {
        Assert.True(doc.ApplyEdits([edit]));
        Classifier.Set(doc.Uri, FakeClassification.SlashComments(doc.GetText()));
    }

    /// <summary>The published result for the document's current version that satisfies <paramref name="until"/>.</summary>
    public async Task<SpellCheckResult> ResultAsync(ITextDocument doc, Func<SpellCheckResult, bool>? until = null, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (Service.ResultFor(doc.Uri) is { } r && r.Version == doc.Version && (until is null || until(r))) return r;
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"No matching spell result for {doc.Uri} v{doc.Version}; last: {Describe(Service.ResultFor(doc.Uri))}");
            await Task.Delay(10);
        }
    }

    public static string Describe(SpellCheckResult? r) =>
        r is null ? "none" : $"v{r.Version} {r.State} [{string.Join(", ", r.Issues.Select(i => i.Word))}]";

    public IReadOnlyList<Diagnostic> DiagnosticsOf(ITextDocument doc) => Diagnostics.Get(doc.Uri);

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
        _started.Dispose();
        Host.Dispose();
    }
}

/// <summary>Comment syntax per language, as the editor's language service has it.</summary>
internal sealed class FakeLanguages : ILanguageService
{
    public IReadOnlyList<LanguageInfo> Languages => [];
    public string DetectLanguageId(Uri uri, string? firstLine = null) => "plaintext";
    public LanguageInfo? Get(string languageId) => null;

    public LanguageConfiguration GetConfiguration(string languageId) => languageId switch
    {
        "csharp" or "javascript" or "jsonc" => new LanguageConfiguration { LineComment = "//", BlockComment = ("/*", "*/") },
        "python" or "shellscript" => new LanguageConfiguration { LineComment = "#" },
        "markdown" or "html" => new LanguageConfiguration { BlockComment = ("<!--", "-->") },
        _ => new LanguageConfiguration(),
    };

    public IEventSource<string> LanguageConfigurationChanged => EventSource.Never<string>();
    public IEventSource<IReadOnlyList<LanguageInfo>> LanguagesChanged => EventSource.Never<IReadOnlyList<LanguageInfo>>();
}
