using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Testing.Engine;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Testing;

/// <summary>
/// The editor's test glyphs (<see cref="ITestGutterProvider"/>): a document's text is scanned by the providers of its language (off the UI
/// thread: the editor calls providers through a worker) and each test found is matched to a test of the explorer's tree. The tree belongs to
/// the UI thread, so matching reads an immutable index (file → tests with their states) rebuilt on the UI thread after the tree changes,
/// coalesced; then <see cref="StatesChanged"/> asks every editor to refresh.
/// </summary>
public sealed class TestGutterProvider : ITestGutterProvider, IDisposable
{
    internal sealed record Entry(string TestId, string ProjectKey, string ProviderId, TestOutcome Outcome, string? Message, TestLocation? Location);

    /// <summary>A test of a document, with its tree node (UI thread only).</summary>
    internal sealed record DocumentTest(int Line, TestNode Node);

    private readonly TestingHost _host;
    private readonly IUiDispatcher _ui;
    private readonly Emitter<Uri?> _changed = new();
    private readonly Emitter<Uri?> _testsChanged = new();
    private volatile Dictionary<string, Entry[]> _index = new(PathComparer);
    private bool _disposed;

    internal static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    internal TestGutterProvider(TestingHost host, IUiDispatcher ui)
    {
        _host = host;
        _ui = ui;
    }

    public IEventSource<Uri?> StatesChanged => _changed;

    /// <summary>
    /// Fires (after <see cref="StatesChanged"/>) only when a rebuild changed which tests a file has or where they are, not for outcome-only
    /// changes: the run/debug code lenses depend on nothing else, and refreshing them asks every code lens provider (the server's too) again.
    /// </summary>
    internal IEventSource<Uri?> TestsChanged => _testsChanged;

    /// <summary>The tree changed: rebuild the index (coalesced, background priority) and tell the editors.</summary>
    internal void Invalidate() => _ui.PostCoalesced("testing.gutterIndex", Rebuild, UiPriority.Background);

    internal void Rebuild()
    {
        if (_disposed) return;
        var map = new Dictionary<string, List<Entry>>(PathComparer);
        foreach (var t in _host.Controller.Tree.AllTests())
        {
            if (t.Location is not { } loc || t.TestId is not { } id) continue;
            if (!map.TryGetValue(loc.FilePath, out var list)) map[loc.FilePath] = list = [];
            list.Add(new Entry(id, t.Project.Key, t.Project.ProviderId, t.Outcome, t.Message, loc));
        }
        var old = _index;
        _index = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), PathComparer);
        _changed.Fire(null);
        if (!SameTests(old, _index)) _testsChanged.Fire(null);
    }

    /// <summary>Whether two indexes have the same tests at the same places (outcomes and messages ignored).</summary>
    internal static bool SameTests(Dictionary<string, Entry[]> a, Dictionary<string, Entry[]> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (file, x) in a)
        {
            if (!b.TryGetValue(file, out var y) || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
                if (x[i].TestId != y[i].TestId || x[i].ProviderId != y[i].ProviderId || x[i].ProjectKey != y[i].ProjectKey || x[i].Location != y[i].Location) return false;
        }
        return true;
    }

    public ValueTask<IReadOnlyList<TestGutterItem>?> ProvideTestsAsync(ITextDocument document, CancellationToken ct)
    {
        if (!document.Uri.IsFile) return ValueTask.FromResult<IReadOnlyList<TestGutterItem>?>(null);
        var path = document.Uri.LocalPath;
        if (!_index.TryGetValue(path, out var entries) || entries.Length == 0) return ValueTask.FromResult<IReadOnlyList<TestGutterItem>?>(null);
        var text = document.GetText();
        ct.ThrowIfCancellationRequested();
        var items = new List<TestGutterItem>();
        foreach (var provider in _host.Controller.Providers.Where(p => p.LanguageIds.Contains(document.LanguageId)))
        {
            var own = entries.Where(e => e.ProviderId == provider.Id).ToList();
            if (own.Count == 0) continue;
            foreach (var source in provider.ScanSource(path, text))
            {
                var match = own.FirstOrDefault(e => provider.Matches(new DiscoveredTest(e.TestId, e.TestId, [], e.Location), path, source));
                if (match is null) continue;
                items.Add(new TestGutterItem(source.Line, match.TestId, source.Label, ToState(match.Outcome), match.Message));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<TestGutterItem>?>(items.Count == 0 ? null : items);
    }

    internal static TestGutterState ToState(TestOutcome o) => o switch
    {
        TestOutcome.Passed => TestGutterState.Passed,
        TestOutcome.Failed => TestGutterState.Failed,
        TestOutcome.Skipped => TestGutterState.Skipped,
        TestOutcome.Running => TestGutterState.Running,
        TestOutcome.Queued => TestGutterState.Queued,
        _ => TestGutterState.None,
    };

    /// <summary>UI thread: the tests the document declares, with their nodes (Run Test at Cursor / in Current File).</summary>
    internal IReadOnlyList<DocumentTest> TestsIn(ITextDocument document)
    {
        _ui.AssertUiThread();
        if (!document.Uri.IsFile) return [];
        var path = document.Uri.LocalPath;
        var text = document.GetText();
        var result = new List<DocumentTest>();
        var inFile = _host.Controller.Tree.AllTests().Where(t => t.Location is { } l && PathComparer.Equals(l.FilePath, path)).ToList();
        foreach (var provider in _host.Controller.Providers.Where(p => p.LanguageIds.Contains(document.LanguageId)))
            foreach (var source in provider.ScanSource(path, text))
                if (inFile.FirstOrDefault(t => t.Project.ProviderId == provider.Id && provider.Matches(new DiscoveredTest(t.TestId!, t.Label, [], t.Location), path, source)) is { } node)
                    result.Add(new DocumentTest(source.Line, node));
        return result;
    }

    public ValueTask RunAsync(string testId, bool debug, CancellationToken ct = default) =>
        new(_ui.InvokeAsync(async () =>
        {
            if (_host.Controller.Tree.FindTest(testId) is { } node) await _host.RunNodesAsync([node], debug).ConfigureAwait(true);
        }).Unwrap());

    public ValueTask RevealAsync(string testId, CancellationToken ct = default) =>
        new(_ui.InvokeAsync(async () =>
        {
            await _host.ShowViewAsync().ConfigureAwait(true);
            if (_host.Controller.Tree.FindTest(testId) is { } node) _host.LiveExplorer?.Reveal(node);
        }).Unwrap());

    public void Dispose()
    {
        _disposed = true;
        _changed.Clear();
        _testsChanged.Clear();
    }
}
