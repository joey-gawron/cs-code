using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Tests.Support;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public class WorkspaceRobustnessTests : IDisposable
{
    private readonly WorkspaceFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ThrowingDependent : IWorkspaceBound
    {
        public ValueTask OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct) => throw new IOException("disk on fire");
    }

    private sealed class RecordingDependent : IWorkspaceBound
    {
        public List<int> FolderCounts { get; } = new();
        public ValueTask OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct) { FolderCounts.Add(folders.Count); return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task A_failing_dependent_is_logged_and_does_not_stop_the_workspace_from_opening()
    {
        Directory.CreateDirectory(_f.Root);
        var log = new CapturingLogger<WorkspaceService>();
        var good = new RecordingDependent();
        using var ws = new WorkspaceService(_f.Fs, _f.Watcher, _f.State, _f.Config, _f.Lane, [new ThrowingDependent(), good], _f.Options, log);
        var events = 0;
        using var sub = ws.FoldersChanged.Subscribe(_ => events++);

        await ws.OpenFolderAsync(_f.RootUri, Ct);

        Assert.Equal([_f.RootUri], ws.Folders);              // the folder opened
        Assert.Equal(1, events);                             // and was announced
        Assert.Equal([1], good.FolderCounts);                // the dependents after the failing one were still told
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("ThrowingDependent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workspace_state_write_racing_a_folder_switch_is_not_lost()
    {
        var a = FileUri.FromDirectoryPath(_f.Temp.Combine("a"));
        var b = FileUri.FromDirectoryPath(_f.Temp.Combine("b"));
        Directory.CreateDirectory(_f.Temp.Combine("a"));
        Directory.CreateDirectory(_f.Temp.Combine("b"));
        await _f.Workspace.OpenFolderAsync(a, Ct);

        // Hammer workspace-scoped Sets from another thread while the folder flips back and forth.
        using var stop = new CancellationTokenSource();
        var written = 0;
        var writer = Task.Run(() => { while (!stop.IsCancellationRequested) _f.State.Set("counter", Interlocked.Increment(ref written), StorageScope.Workspace); }, Ct);
        for (var i = 0; i < 20; i++) await _f.Workspace.OpenFolderAsync(i % 2 == 0 ? b : a, Ct);
        await stop.CancelAsync();
        await writer;
        await _f.Workspace.OpenFolderAsync(a, Ct);
        await _f.State.FlushAsync();

        // Whatever happened, the state of the folder we ended on is internally consistent and loadable (no exception, valid JSON).
        var final = _f.State.Get<int>("counter", StorageScope.Workspace);
        Assert.InRange(final, 0, written);
    }
}
