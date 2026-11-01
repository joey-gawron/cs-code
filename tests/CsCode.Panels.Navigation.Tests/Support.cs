using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;
using Microsoft.Extensions.DependencyInjection;

[assembly: AvaloniaTestApplication(typeof(CsCode.Panels.Navigation.Tests.TestAppBuilder))]

namespace CsCode.Panels.Navigation.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithCodeFonts();
}

/// <summary>A navigation service that records every open with its preview / focus flags.</summary>
internal sealed class RecordingNavigation : INavigationService
{
    public List<(Uri Uri, TextRange? Range, bool Preview, bool PreserveFocus)> Opened { get; } = [];
    public IEditorDocument? ActiveDocument { get; set; }
    public TextPosition? ActiveCaret { get; set; }
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => EventSource.Never<IEditorDocument?>();
    public IReadOnlyList<IEditorDocument> OpenDocuments => [];

    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        Opened.Add((uri, selection, preview, preserveFocus));
        return ValueTask.FromResult<IEditorDocument?>(null);
    }

    public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default) => ValueTask.FromResult(false);
}

internal sealed class RecordingViewActivator : IViewActivator
{
    public List<string> Shown { get; } = [];
    public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default)
    {
        Shown.Add(viewId);
        return ValueTask.FromResult(true);
    }
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

/// <summary>
/// A call graph served the way a language server serves it: prepare answers the function whose name is under the position, incoming /
/// outgoing answer from the graph, every item carries opaque <see cref="HierarchyItem.Data"/> that must come back unchanged (the protocol's
/// rule; a call with an item that lost it throws). Records which items were expanded and on which thread; an optional gate holds answers.
/// </summary>
internal sealed class FakeCallGraph : ICallHierarchyProvider
{
    public static readonly Uri FileA = new("file:///w/a.cs");
    public static readonly Uri FileB = new("file:///w/b.cs");

    private readonly Dictionary<string, HierarchyItem> _items = new(StringComparer.Ordinal);
    // caller -> (callee, ranges of the calls inside the caller)
    private readonly List<(string Caller, string Callee, TextRange[] Ranges)> _calls = [];

    public ConcurrentQueue<(string Item, string Direction, int ThreadId)> Requests { get; } = new();
    public int PrepareCount;
    public Func<string, Task>? Gate { get; set; }
    public TextPosition? LastPreparePosition { get; private set; }

    public HierarchyItem Add(string name, Uri uri, int line, SymbolKind kind = SymbolKind.Method, string? detail = null)
    {
        var item = new HierarchyItem(name, kind, uri, TextRange.FromLines(line, 0, line + 3, 1), TextRange.FromLines(line, 4, line, 4 + name.Length), detail,
            JsonSerializer.SerializeToElement(new { id = name }));
        _items[name] = item;
        return item;
    }

    public void Call(string caller, string callee, params TextRange[] ranges) => _calls.Add((caller, callee, ranges));

    public HierarchyItem Item(string name) => _items[name];

    public async ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCount);
        LastPreparePosition = position;
        Requests.Enqueue(("prepare", "prepare", Environment.CurrentManagedThreadId));
        if (Gate is { } g) await g("prepare").WaitAsync(ct).ConfigureAwait(false);
        var hit = _items.Values.FirstOrDefault(i => i.Uri == document.Uri && i.SelectionRange.Start.Line == position.Line);
        return hit is null ? [] : [hit];
    }

    public async ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct)
    {
        var name = Check(item);
        Requests.Enqueue((name, "incoming", Environment.CurrentManagedThreadId));
        if (Gate is { } g) await g(name).WaitAsync(ct).ConfigureAwait(false);
        return _calls.Where(c => c.Callee == name).Select(c => new CallHierarchyIncomingCall(_items[c.Caller], c.Ranges)).ToList();
    }

    public async ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct)
    {
        var name = Check(item);
        Requests.Enqueue((name, "outgoing", Environment.CurrentManagedThreadId));
        if (Gate is { } g) await g(name).WaitAsync(ct).ConfigureAwait(false);
        return _calls.Where(c => c.Caller == name).Select(c => new CallHierarchyOutgoingCall(_items[c.Callee], c.Ranges)).ToList();
    }

    private string Check(HierarchyItem item)
    {
        if (item.Data is not { } data || data.GetProperty("id").GetString() != item.Name) throw new InvalidOperationException("the item's data did not come back unchanged");
        return item.Name;
    }
}

/// <summary>A type graph: base types and derived types, same rules as <see cref="FakeCallGraph"/>.</summary>
internal sealed class FakeTypeGraph : ITypeHierarchyProvider
{
    private readonly Dictionary<string, HierarchyItem> _items = new(StringComparer.Ordinal);
    private readonly List<(string Derived, string Base)> _edges = [];
    public ConcurrentQueue<(string Item, string Direction)> Requests { get; } = new();

    public HierarchyItem Add(string name, int line, SymbolKind kind = SymbolKind.Class)
    {
        var item = new HierarchyItem(name, kind, FakeCallGraph.FileA, TextRange.FromLines(line, 0, line + 3, 1), TextRange.FromLines(line, 6, line, 6 + name.Length), null,
            JsonSerializer.SerializeToElement(new { id = name }));
        _items[name] = item;
        return item;
    }

    public void Derives(string derived, string @base) => _edges.Add((derived, @base));

    public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareTypeHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct)
    {
        var hit = _items.Values.FirstOrDefault(i => i.SelectionRange.Start.Line == position.Line);
        return ValueTask.FromResult<IReadOnlyList<HierarchyItem>?>(hit is null ? [] : [hit]);
    }

    public ValueTask<IReadOnlyList<HierarchyItem>?> SupertypesAsync(HierarchyItem item, CancellationToken ct)
    {
        Requests.Enqueue((item.Name, "super"));
        return ValueTask.FromResult<IReadOnlyList<HierarchyItem>?>(_edges.Where(e => e.Derived == item.Name).Select(e => _items[e.Base]).ToList());
    }

    public ValueTask<IReadOnlyList<HierarchyItem>?> SubtypesAsync(HierarchyItem item, CancellationToken ct)
    {
        Requests.Enqueue((item.Name, "sub"));
        return ValueTask.FromResult<IReadOnlyList<HierarchyItem>?>(_edges.Where(e => e.Base == item.Name).Select(e => _items[e.Derived]).ToList());
    }
}

/// <summary>A <see cref="HierarchyHost"/> over the real Core registries and recording fakes, with a.cs open as the active document.</summary>
internal sealed class Rig : IDisposable
{
    public LanguageFeatureRegistry Features { get; } = new();
    public DocumentManager Documents { get; } = new();
    public RecordingNavigation Navigation { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public TestCommandService Commands { get; } = new();
    public ContributionRegistry Contributions { get; } = new();
    public RecordingViewActivator Views { get; } = new();
    public ServiceProvider Provider { get; }
    public HierarchyHost Host { get; }
    public FakeEditorDocument Doc { get; }
    private readonly IDisposable _registration;

    public Rig()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILanguageFeatureRegistry>(Features);
        services.AddSingleton<IDocumentManager>(Documents);
        services.AddSingleton<INavigationService>(Navigation);
        services.AddSingleton<IWindowService>(Window);
        services.AddSingleton<ICommandService>(Commands);
        services.AddSingleton<IContributionRegistry>(Contributions);
        services.AddSingleton<IViewActivator>(Views);
        services.AddCsCodeNavigationPanels();
        Provider = services.BuildServiceProvider();
        Host = Provider.GetRequiredService<HierarchyHost>();
        var text = string.Join("\n", Enumerable.Range(0, 40).Select(i => "line " + i));
        Doc = new FakeEditorDocument(new InMemoryTextDocument(FakeCallGraph.FileA, "csharp", text));
        _registration = Documents.Register(Doc);
        Navigation.ActiveDocument = Doc;
    }

    /// <summary>main → run → (step, log); helper → log. main, run in a.cs, step and log in b.cs.</summary>
    public FakeCallGraph CallGraph()
    {
        var g = new FakeCallGraph();
        g.Add("Main", FakeCallGraph.FileA, 1);
        g.Add("Run", FakeCallGraph.FileA, 10, detail: "void Program.Run()");
        g.Add("Step", FakeCallGraph.FileB, 3);
        g.Add("Log", FakeCallGraph.FileB, 20);
        g.Add("Helper", FakeCallGraph.FileA, 30);
        g.Call("Main", "Run", TextRange.FromLines(2, 8, 2, 11));
        g.Call("Run", "Step", TextRange.FromLines(11, 8, 11, 12), TextRange.FromLines(12, 8, 12, 12));
        g.Call("Run", "Log", TextRange.FromLines(13, 8, 13, 11));
        g.Call("Helper", "Log", TextRange.FromLines(31, 8, 31, 11));
        Features.Register<ICallHierarchyProvider>(DocumentSelector.All, g);
        return g;
    }

    public void Dispose()
    {
        _registration.Dispose();
        Provider.Dispose();
    }
}
