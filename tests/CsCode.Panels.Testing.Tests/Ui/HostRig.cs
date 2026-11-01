using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Testing.Engine;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Testing.Tests.Ui;

/// <summary>Runs every post inline on the calling thread.</summary>
internal sealed class ImmediateUiDispatcher : UiDispatcherBase
{
    public override bool CheckAccess() => true;
    protected override void PostCore(Action action, UiPriority priority) => action();
}

internal sealed class FakeWorkspace(params string[] folders) : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    public IReadOnlyList<Uri> Folders { get; set; } = [.. folders.Select(f => new Uri(f.TrimEnd('/') + "/"))];
    public string? Name => "w";
    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];
    public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>An editor document over an in-memory one (the navigation service's active document).</summary>
internal sealed class FakeEditorDocument(InMemoryTextDocument inner) : IEditorDocument
{
    public Uri Uri => inner.Uri;
    public string LanguageId => inner.LanguageId;
    public int Version => inner.Version;
    public bool IsDirty => false;
    public int Length => inner.Length;
    public int LineCount => inner.LineCount;
    public string GetText() => inner.GetText();
    public string GetText(TextRange range) => inner.GetText(range);
    public string GetLineText(int line) => inner.GetLineText(line);
    public int OffsetAt(TextPosition position) => inner.OffsetAt(position);
    public TextPosition PositionAt(int offset) => inner.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => inner.Validate(position);
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => inner.ApplyEdits(edits);
    public IEventSource<TextDocumentChangedEventArgs> Changed => inner.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => inner.LanguageIdChanged;
    public bool IsReadOnly => false;
    public bool IsUntitled => false;
    public string EncodingName => "utf-8";
    public string EndOfLine => "\n";
    public void SetLanguageId(string languageId) { }
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IEventSource<IEditorDocument> Saved => EventSource.Never<IEditorDocument>();
    public IEventSource<IEditorDocument> DirtyChanged => EventSource.Never<IEditorDocument>();
}

/// <summary>A <see cref="TestingHost"/> over fakes and a <see cref="FakeProvider"/> with one project of three C# tests in /w/calc.</summary>
internal sealed class HostRig : IDisposable
{
    public const string MathFile = "/w/calc/MathTests.cs";

    public const string MathSource = """
        namespace Calc;
        public class MathTests
        {
            [Fact]
            public void Adds() { }

            [Fact]
            public void Subtracts() { }
        }
        """;

    public HostRig(IUiDispatcher? ui = null)
    {
        Provider = new FakeProvider
        {
            Scan = text => [.. CsCode.Testing.Providers.DotNet.CSharpTestScanner.Scan(text).Select(f => new SourceTest(f.FullName, f.Line, f.Method))],
        };
        Project = Provider.AddProject("Calc.Tests", "/w/calc",
            FakeProvider.Test("Calc.MathTests.Adds", MathFile, 4),
            FakeProvider.Test("Calc.MathTests.Subtracts", MathFile, 7),
            FakeProvider.Test("Calc.Other.Later", "/w/calc/Other.cs", 3, skip: "later"));
        Provider.Outcome = id => id.EndsWith("Subtracts", StringComparison.Ordinal)
            ? new TestResult(id, TestOutcome.Failed, TimeSpan.FromMilliseconds(4), "Assert.Equal() Failure", "at Calc.MathTests.Subtracts() in /w/calc/MathTests.cs:line 8", "printed", new TestLocation(MathFile, 7))
            : new TestResult(id, TestOutcome.Passed, TimeSpan.FromMilliseconds(2));
        var services = new ServiceCollection();
        services.AddSingleton<IUiDispatcher>(ui ?? new ImmediateUiDispatcher());
        services.AddSingleton<ICommandService>(Commands);
        services.AddSingleton<IConfigurationService>(Config);
        services.AddSingleton<IContextKeyService>(Keys);
        services.AddSingleton<IOutputService>(Output);
        services.AddSingleton<IDiagnosticService>(Diagnostics);
        services.AddSingleton<ILanguageFeatureRegistry>(Features);
        services.AddSingleton<IWindowService>(Window);
        services.AddSingleton<IWorkspaceService>(Workspace);
        services.AddSingleton<IDocumentManager>(Documents);
        services.AddSingleton<INavigationService>(Navigation = new RecordingNavigationService(Documents));
        services.AddSingleton<IViewActivator>(Views);
        Services = services.BuildServiceProvider();
        Host = new TestingHost(Services, [Provider]);
    }

    public FakeProvider Provider { get; }
    public TestProject Project { get; }
    public TestCommandService Commands { get; } = new();
    public TestConfigurationService Config { get; } = new();
    public TestContextKeyService Keys { get; } = new();
    public InMemoryOutputService Output { get; } = new();
    public DiagnosticService Diagnostics { get; } = new();
    public LanguageFeatureRegistry Features { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public FakeWorkspace Workspace { get; } = new("/w");
    public DocumentManager Documents { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public RecordingViews Views { get; } = new();
    public ServiceProvider Services { get; }
    public TestingHost Host { get; }

    /// <summary>Asks for discovery (nothing is discovered before tests are needed) and waits (pumping nothing: the dispatcher is inline) until it has filled the tree.</summary>
    public async Task DiscoveredAsync()
    {
        _ = Host.EnsureDiscoveredAsync();
        for (var i = 0; i < 500 && (Host.Controller.Tree.TestCount == 0 || Host.Controller.IsDiscovering); i++) await Task.Delay(5);
    }

    public void Dispose()
    {
        Host.Dispose();
        Services.Dispose();
    }
}

internal sealed class RecordingViews : IViewActivator
{
    public List<string> Shown { get; } = [];
    public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default)
    {
        Shown.Add(viewId);
        return ValueTask.FromResult(true);
    }
}
