using BenchmarkDotNet.Attributes;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.ProfileHost;

/// <summary>
/// MUST 6: allocations/op and ns/op of Core's hot paths with BenchmarkDotNet's MemoryDiagnoser (results in docs/MEMORY.md):
/// <c>InMemoryTextDocument.ApplyEdits</c>, <c>Glob.IsMatch</c>, <c>DocumentSelector.Score</c>, <c>LanguageFeatureRegistry.GetProviders</c>,
/// <c>Emitter.Fire</c>, <c>DiagnosticService.Set/GetAll</c>. Run: <c>tools/profile/bench.sh</c> (Release, a process per benchmark).
/// </summary>
[MemoryDiagnoser]
public class CoreBenchmarks
{
    private InMemoryTextDocument _small = null!, _large = null!;
    private TextEdit[] _oneEdit = null!;
    private readonly Uri _fileUri = new("file:///home/user/project/src/Services/DocumentService.cs");
    private readonly DocumentSelector _selector = new([new DocumentFilter(Language: "csharp"), new DocumentFilter(Pattern: "**/*.csx"), new DocumentFilter(Scheme: "file", Language: "fsharp")]);
    private LanguageFeatureRegistry _registry = null!;
    private InMemoryTextDocument _doc = null!;
    private readonly Emitter<int> _emitter = new();
    private DiagnosticService _diagnostics = null!;
    private Uri[] _uris = null!;
    private IReadOnlyList<Diagnostic> _payload = null!;
    private int _version;

    private sealed class Hover : IHoverProvider
    {
        public ValueTask<CsCode.Core.Languages.Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) => default;
    }

    private sealed class Completion : ICompletionProvider
    {
        public IReadOnlyList<string> TriggerCharacters => [];
        public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) => default;
    }

    [GlobalSetup]
    public void Setup()
    {
        var line = "    public int Field = 42; // some comment text\n";
        _small = new InMemoryTextDocument(new Uri("file:///small.cs"), "csharp", string.Concat(Enumerable.Repeat(line, 100)));
        _large = new InMemoryTextDocument(new Uri("file:///large.cs"), "csharp", string.Concat(Enumerable.Repeat(line, 20_000)));
        _oneEdit = [new TextEdit(TextRange.FromLines(50, 4, 50, 5), "x")]; // a one-character replacement: the document keeps its size
        _registry = new LanguageFeatureRegistry();
        foreach (var lang in new[] { "csharp", "typescript", "python", "rust", "go", "json", "markdown", "cpp" })
        {
            _registry.Register<IHoverProvider>(DocumentSelector.ForLanguage(lang), new Hover());
            _registry.Register<ICompletionProvider>(DocumentSelector.ForLanguage(lang), new Completion());
        }
        _registry.Register<IHoverProvider>(DocumentSelector.All, new Hover());
        _doc = new InMemoryTextDocument(_fileUri, "csharp");
        for (var i = 0; i < 8; i++) _emitter.Subscribe(_ => { });
        _diagnostics = new DiagnosticService();
        _uris = Enumerable.Range(0, 100).Select(i => new Uri($"file:///project/f{i}.cs")).ToArray();
        _payload = Enumerable.Range(0, 20).Select(i => new Diagnostic(TextRange.At(i, 0), "message " + i, DiagnosticSeverity.Warning)).ToArray();
        foreach (var u in _uris) _diagnostics.Set(u, "server", _payload);
    }

    [Benchmark]
    public bool ApplyEdits_small_document() => _small.ApplyEdits(_oneEdit);

    [Benchmark]
    public bool ApplyEdits_20k_lines() => _large.ApplyEdits(_oneEdit);

    [Benchmark]
    public bool Glob_IsMatch_file_name() => Glob.IsMatch("*.cs", "/home/user/project/src/Services/DocumentService.cs");

    [Benchmark]
    public bool Glob_IsMatch_path() => Glob.IsMatch("**/src/**/*.cs", "/home/user/project/src/Services/DocumentService.cs");

    [Benchmark]
    public int DocumentSelector_Score() => _selector.Score(_fileUri, "csharp");

    [Benchmark]
    public int Registry_GetProviders() => _registry.GetProviders<IHoverProvider>(_doc).Count;

    [Benchmark]
    public void Emitter_Fire_8_subscribers() => _emitter.Fire(1);

    [Benchmark]
    public void Diagnostics_Set() => _diagnostics.Set(_uris[_version++ % _uris.Length], "server", _payload);

    [Benchmark]
    public int Diagnostics_GetAll_2000() => _diagnostics.GetAll().Count;
}
