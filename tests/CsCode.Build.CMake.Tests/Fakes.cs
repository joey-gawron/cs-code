using Xunit;
using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Build.CMake;
using CsCode.Core;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Build.CMake.Tests;

/// <summary>A workspace with fixed local folders (only what the CMake service reads).</summary>
internal sealed class TestWorkspace(params string[] folders) : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    private List<Uri> _folders = [.. folders.Select(f => new Uri(f.TrimEnd('/') + "/"))];

    public IReadOnlyList<Uri> Folders => _folders;
    public string? Name => "test";
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];

    public void SetFolders(params string[] folders)
    {
        var old = _folders;
        _folders = [.. folders.Select(f => new Uri(f.TrimEnd('/') + "/"))];
        _changed.Fire(new WorkspaceFoldersChangedEventArgs(_folders.Except(old).ToList(), old.Except(_folders).ToList()));
    }

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => AsyncEnumerable.Empty<Uri>();
}

/// <summary>State storage in memory, serialised like Platform's (JSON) so a type that does not round-trip fails here too.</summary>
internal sealed class MemoryState : IStateStorage
{
    private readonly ConcurrentDictionary<(string, StorageScope), JsonElement> _values = new();

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) =>
        _values.TryGetValue((key, scope), out var v) ? v.Deserialize<T>(CoreJson.Options) : default;

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) =>
        _values[(key, scope)] = JsonSerializer.SerializeToElement(value, CoreJson.Options);

    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.TryRemove((key, scope), out _);

    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>Records the commands the service runs and answers each with scripted output and an exit code.</summary>
internal sealed class ScriptedProcesses : ICMakeProcesses
{
    private readonly object _gate = new();
    public List<CMakeCommand> Commands { get; } = [];

    /// <summary>Answers a command: its output lines and exit code (default: no output, 0). May write files (a fake File API reply).</summary>
    public Func<CMakeCommand, (IReadOnlyList<string> Lines, int Exit)> Answer { get; set; } = _ => ([], 0);

    /// <summary>When set, a run waits for it (cancellation tests).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<int?> RunAsync(CMakeCommand command, Action<IReadOnlyList<string>> onLines, CancellationToken ct)
    {
        lock (_gate) Commands.Add(command);
        if (Gate is { } gate)
        {
            try { await gate.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        var (lines, exit) = Answer(command);
        if (lines.Count > 0) onLines(lines);
        return exit;
    }

    public CMakeCommand[] Snapshot() { lock (_gate) return [.. Commands]; }
}
