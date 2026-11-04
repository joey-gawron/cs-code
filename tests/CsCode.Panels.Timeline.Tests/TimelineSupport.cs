using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.History;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Timeline.Tests;

public sealed class MemoryState : IStateStorage
{
    private readonly ConcurrentDictionary<(string, StorageScope), JsonElement> _values = new();
    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => _values.TryGetValue((key, scope), out var v) ? v.Deserialize<T>(CoreJson.Options) : default;
    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
    {
        if (value is null) _values.TryRemove((key, scope), out _);
        else _values[(key, scope)] = JsonSerializer.SerializeToElement(value, CoreJson.Options);
    }
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.TryRemove((key, scope), out _);
    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>A navigation service whose active document the test sets (and which raises the change, as the shell's does).</summary>
public sealed class TestNavigation(IDocumentManager documents) : INavigationService
{
    private readonly Emitter<IEditorDocument?> _active = new();
    public List<Uri> Opened { get; } = [];
    public IEditorDocument? ActiveDocument { get; private set; }
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
    public IReadOnlyList<IEditorDocument> OpenDocuments => documents.Documents.OfType<IEditorDocument>().ToArray();

    public void SetActive(IEditorDocument? doc)
    {
        ActiveDocument = doc;
        _active.Fire(doc);
    }

    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        lock (Opened) Opened.Add(uri);
        return ValueTask.FromResult(documents.Get(uri) as IEditorDocument);
    }

    public ValueTask<bool> ApplyWorkspaceEditAsync(Core.Languages.WorkspaceEdit edit, string? label = null, CancellationToken ct = default) => ValueTask.FromResult(false);
}

/// <summary>A provider serving a fixed list of items newest first, in pages of the size asked for (cursor = how many came before).</summary>
public sealed class PagedProvider(string id, string label, Uri resource, IReadOnlyList<(string Id, DateTimeOffset At)> items) : ITimelineProvider
{
    private readonly Emitter<Uri?> _changed = new();
    public List<string?> Cursors { get; } = [];
    public string Id => id;
    public string Label => label;
    public IEventSource<Uri?> Changed => _changed;
    public bool Handles(Uri r) => r == resource;
    public void Fire(Uri? uri) => _changed.Fire(uri);

    public ValueTask<TimelinePage> GetTimelineAsync(Uri r, string? cursor, int limit, CancellationToken ct = default)
    {
        lock (Cursors) Cursors.Add(cursor);
        var skip = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = items.OrderByDescending(i => i.At).Skip(skip).Take(limit).Select(i => new TimelineItem(id, i.Id, r, i.At, i.Id)).ToArray();
        return ValueTask.FromResult(new TimelinePage(page, skip + limit < items.Count ? (skip + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    public ValueTask<TimelineContent?> GetContentAsync(TimelineItem item, CancellationToken ct = default) => ValueTask.FromResult<TimelineContent?>(new TimelineContent($"{item.Id}", Text: "text of " + item.Id));
    public ValueTask<TimelineContent?> GetPreviousContentAsync(TimelineItem item, CancellationToken ct = default) => ValueTask.FromResult<TimelineContent?>(new TimelineContent($"{item.Id}^", Text: "before " + item.Id));
}

/// <summary>The Timeline host and window over a <see cref="HistoryRig"/> and recording fakes, composed through <c>AddCsCodeTimeline()</c>.</summary>
public sealed class TimelineRig : IDisposable
{
    public TimelineRig(Action<IServiceCollection>? configure = null)
    {
        History = new HistoryRig();
        Navigation = new TestNavigation(History.Documents);
        var services = new ServiceCollection();
        services.AddSingleton(History.History);
        services.AddSingleton<IFileSystem>(History.Fs);
        services.AddSingleton<IDocumentManager>(History.Documents);
        services.AddSingleton<IConfigurationService>(History.Config);
        services.AddSingleton<IWorkspaceService>(History.WorkspaceService);
        services.AddSingleton<TimeProvider>(History.Time);
        services.AddSingleton<ICommandService>(Commands);
        services.AddSingleton<IWindowService>(Window);
        services.AddSingleton<INavigationService>(Navigation);
        services.AddSingleton<IDiffEditorService>(Diff);
        services.AddSingleton<IViewActivator>(Views);
        services.AddSingleton<IStateStorage>(State);
        configure?.Invoke(services);
        services.AddCsCodeTimeline();
        Provider = services.BuildServiceProvider();
        Host = Provider.GetRequiredService<TimelineHost>();
    }

    public HistoryRig History { get; }
    public TestNavigation Navigation { get; }
    public TestCommandService Commands { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public RecordingDiffEditor Diff { get; } = new();
    public RecordingViewActivator Views { get; } = new();
    public MemoryState State { get; } = new();
    public ServiceProvider Provider { get; }
    public TimelineHost Host { get; }

    /// <summary>Opens <paramref name="relative"/> as the active editor document.</summary>
    public FakeEditorDocument Activate(string relative)
    {
        var doc = History.EditorDocument(relative);
        Navigation.SetActive(doc);
        return doc;
    }

    public void Dispose()
    {
        Provider.Dispose();
        History.Dispose();
    }
}
