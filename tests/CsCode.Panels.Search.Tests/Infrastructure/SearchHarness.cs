using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.TestDoubles;

namespace CsCode.Panels.Search.Tests;

/// <summary>A temp workspace plus an engine over it; collects everything a session delivers.</summary>
public sealed class SearchHarness : IDisposable
{
    public TempDirectory Dir { get; } = new("cscode-search-");
    public TestWorkspace Workspace { get; }
    public DocumentManager Documents { get; } = new();
    public TestConfigurationService Config { get; } = new();
    public SearchEngine Engine { get; }

    public SearchHarness(SearchLimits? limits = null, ISearchFileSource? source = null)
    {
        Workspace = new TestWorkspace(Dir.Uri);
        Engine = new SearchEngine(Workspace, Documents, Config, source, limits);
    }

    public Uri UriOf(string relative) => new(Dir.Uri, relative);

    public string Write(string relative, string content) => Dir.WriteFile(relative, content);

    public string WriteBytes(string relative, byte[] bytes)
    {
        var full = Dir.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public async Task<SearchOutcomeData> RunAsync(SearchQuery query, int timeoutMs = 20_000)
    {
        using var session = Engine.Start(query);
        return await Collect(session, timeoutMs);
    }

    public static async Task<SearchOutcomeData> Collect(SearchSession session, int timeoutMs = 20_000)
    {
        var files = new List<FileMatches>();
        var batches = new List<SearchBatch>();
        var gate = new object();
        using var sub = session.Batches.Subscribe(b => { lock (gate) { batches.Add(b); files.AddRange(b.Files); } });
        var done = await session.Result.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        lock (gate) return new SearchOutcomeData(done, files.ToList(), batches.ToList());
    }

    public void Dispose()
    {
        Engine.Dispose();
        Dir.Dispose();
    }
}

public sealed record SearchOutcomeData(SearchCompletion Completion, IReadOnlyList<FileMatches> Files, IReadOnlyList<SearchBatch> Batches)
{
    public SearchOutcome Outcome => Completion.Outcome;
    public SearchStats Stats => Completion.Stats;
    public int MatchCount => Files.Sum(f => f.Matches.Count);
    public IReadOnlyList<string> FileNames => Files.Select(f => Path.GetFileName(f.Uri.LocalPath)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    public FileMatches File(string name) => Files.Single(f => Path.GetFileName(f.Uri.LocalPath) == name);
    public bool Has(string name) => Files.Any(f => Path.GetFileName(f.Uri.LocalPath) == name);
}
