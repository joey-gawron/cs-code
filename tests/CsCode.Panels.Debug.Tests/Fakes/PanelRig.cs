using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>Round-trips values through JSON like the real storage does; the workspace scope is per <see cref="Workspace"/> (switched like the real one).</summary>
public sealed class InMemoryStateStorage : IStateStorage
{
    private readonly Dictionary<(string, StorageScope, string), string> _values = new();

    /// <summary>The folder whose workspace-scope values are visible (the real storage swaps the scope when the folder changes).</summary>
    public string Workspace { get; set; } = "default";

    private (string, StorageScope, string) Key(string key, StorageScope scope) => (key, scope, scope == StorageScope.Workspace ? Workspace : "");

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _values.TryGetValue(Key(key, scope), out var json) ? JsonSerializer.Deserialize<T>(json, CoreJson.Options) : default;

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _values[Key(key, scope)] = JsonSerializer.Serialize(value, CoreJson.Options);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove(Key(key, scope));
    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>
/// Folder switching like the Platform's WorkspaceService: the state storage's workspace scope is swapped first (it is an IWorkspaceBound
/// there), then <see cref="FoldersChanged"/> fires. Nothing else of the workspace is used by the panels.
/// </summary>
public sealed class FakeWorkspaceService(InMemoryStateStorage state) : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    private List<Uri> _folders = new();

    public IReadOnlyList<Uri> Folders => _folders;
    public string? Name => _folders.Count > 0 ? _folders[0].Segments[^1].TrimEnd('/') : null;
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public int SubscriberCount => _changed.SubscriberCount;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];
    public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => throw new NotSupportedException();

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => Apply([folder]);

    /// <summary>Adds a second root (the primary folder, and with it the workspace state, stays the same).</summary>
    public ValueTask AddFolderAsync(Uri folder) => Apply([.. _folders, folder]);

    public ValueTask CloseFolderAsync(CancellationToken ct = default) => Apply([]);

    private ValueTask Apply(List<Uri> folders)
    {
        var removed = _folders.Except(folders).ToList();
        var added = folders.Except(_folders).ToList();
        _folders = folders;
        state.Workspace = folders.Count > 0 ? folders[0].AbsoluteUri : "default";
        _changed.Fire(new WorkspaceFoldersChangedEventArgs(added, removed));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Everything a panel view-model needs, wired to fakes, plus helpers to drive a stop.</summary>
public sealed class PanelRig : IDisposable
{
    public FakeDebugService Debug { get; } = new();
    public FakeDebugSession Session { get; }
    public TestWindowService Window { get; } = new();
    public DocumentManager Documents { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public InMemoryStateStorage State { get; } = new();
    public FakeWorkspaceService Workspace { get; }
    public TestContextKeyService ContextKeys { get; } = new();
    public IUiDispatcher Ui { get; }
    public DebugPanelServices Services { get; }

    public PanelRig(bool withSession = true, IUiDispatcher? ui = null, FakeDebugSession? session = null)
    {
        Ui = ui ?? AvaloniaUiDispatcher.Instance;
        Navigation = new RecordingNavigationService(Documents);
        Workspace = new FakeWorkspaceService(State);
        Session = session ?? new FakeDebugSession().WithDefaultScript();
        Services = new DebugPanelServices(Debug, Ui, Debug, Debug, Navigation, Window, null, ContextKeys, State, Documents, Workspace);
        if (withSession) Debug.AddSession(Session);
    }

    /// <summary>Stops at <paramref name="frameIndex"/> and publishes the stop location (the real service does that a moment after <c>stopped</c>).</summary>
    public void StopAt(int frameIndex = 0, string reason = "breakpoint", string? description = null, string? text = null) => Debug.StopAt(Session, reason, frameIndex, 1, description, text);

    /// <summary>The debuggee resumes (step/continue): handles die, the service clears the stop location.</summary>
    public void Resume()
    {
        Session.Resume();
        Debug.SetStopLocation(null);
    }

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(4);
        }
        if (!condition()) throw new TimeoutException("Condition not reached in time" + (what is null ? "" : ": " + what));
    }

    public void Dispose() => Services.Dispose();
}
