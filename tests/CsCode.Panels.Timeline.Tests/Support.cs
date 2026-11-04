using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;

[assembly: AvaloniaTestApplication(typeof(CsCode.Panels.Timeline.Tests.TestAppBuilder))]

namespace CsCode.Panels.Timeline.Tests;

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

/// <summary>A clock the test moves by hand (merge window, relative times).</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// An <see cref="IFileSystem"/> that passes every call to an inner one, after an optional gate: while <see cref="Hold"/> is set nothing reaches
/// the disk (the "does not block the caller" tests), and the thread each call was made on is recorded.
/// </summary>
public sealed class GatedFileSystem(IFileSystem inner) : IFileSystem
{
    public TaskCompletionSource? Hold { get; set; }
    public int Calls;

    private async Task Gate(CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Hold is { } hold) await hold.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) { await Gate(ct); return await inner.StatAsync(uri, ct); }
    public async ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) { await Gate(ct); return await inner.ReadDirectoryAsync(uri, ct); }
    public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) { await Gate(ct); return await inner.ReadFileAsync(uri, ct); }
    public async ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) { await Gate(ct); await inner.WriteFileAsync(uri, content, createParents, ct); }
    public async ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) { await Gate(ct); await inner.CreateDirectoryAsync(uri, ct); }
    public async ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) { await Gate(ct); await inner.DeleteAsync(uri, recursive, useTrash, ct); }
    public async ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { await Gate(ct); await inner.RenameAsync(source, target, overwrite, ct); }
    public async ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { await Gate(ct); await inner.CopyAsync(source, target, overwrite, ct); }
}

/// <summary>An editor document over an in-memory one whose save writes the file through the file system and reports it, as the editor's does.</summary>
public sealed class FakeEditorDocument(InMemoryTextDocument inner, IFileSystem fs, IDocumentManager documents) : IEditorDocument
{
    private int _savedVersion = inner.Version;
    public int ApplyCount;
    public Uri Uri => inner.Uri;
    public string LanguageId => inner.LanguageId;
    public int Version => inner.Version;
    public bool IsDirty => inner.Version != _savedVersion;
    public int Length => inner.Length;
    public int LineCount => inner.LineCount;
    public string GetText() => inner.GetText();
    public string GetText(TextRange range) => inner.GetText(range);
    public string GetLineText(int line) => inner.GetLineText(line);
    public int OffsetAt(TextPosition position) => inner.OffsetAt(position);
    public TextPosition PositionAt(int offset) => inner.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => inner.Validate(position);
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) { Interlocked.Increment(ref ApplyCount); return inner.ApplyEdits(edits); }
    public IEventSource<TextDocumentChangedEventArgs> Changed => inner.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => inner.LanguageIdChanged;
    public bool IsReadOnly => false;
    public bool IsUntitled => false;
    public string EncodingName => "utf8";
    public string EndOfLine => "\n";
    public void SetLanguageId(string languageId) { }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await fs.WriteFileAsync(Uri, Encoding.UTF8.GetBytes(GetText()), true, ct);
        _savedVersion = Version;
        documents.NotifySaved(this);
    }

    public Task SaveAsAsync(Uri target, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IEventSource<IEditorDocument> Saved => EventSource.Never<IEditorDocument>();
    public IEventSource<IEditorDocument> DirtyChanged => EventSource.Never<IEditorDocument>();
}

public sealed class RecordingDiffEditor : IDiffEditorService
{
    public List<DiffEditorRequest> Requests { get; } = [];
    public ValueTask OpenDiffAsync(DiffEditorRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingViewActivator : IViewActivator
{
    public List<string> Shown { get; } = [];
    public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) { Shown.Add(viewId); return ValueTask.FromResult(true); }
}

/// <summary>
/// The local history service over the real Platform file system (its own I/O lane) in temp directories: a workspace folder and the history
/// home. <see cref="SaveAsync"/> writes a file and reports its save the way the editor does.
/// </summary>
public sealed class HistoryRig : IDisposable
{
    private readonly IoLane _lane = new("test.history.io", 2);
    private readonly TempDirectory _dir = new("cscode-history-");
    private readonly Dictionary<string, IDisposable> _open = new();

    public HistoryRig(bool start = true)
    {
        Directory.CreateDirectory(Workspace);
        Local = new LocalFileSystem(_lane);
        Fs = new GatedFileSystem(Local);
        WorkspaceService = new FakeWorkspace(Fs, new Uri(Workspace + "/"));
        if (start) Start();
    }

    public string Workspace => _dir.Combine("ws");
    public string Home => _dir.Combine("user", LocalHistoryService.HistoryFolderName);
    public LocalFileSystem Local { get; }
    public GatedFileSystem Fs { get; }
    public DocumentManager Documents { get; } = new();
    public TestConfigurationService Config { get; } = new();
    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    public FakeWorkspace WorkspaceService { get; }
    public LocalHistoryService History { get; private set; } = null!;

    /// <summary>A (new) service over the same folders: a restart.</summary>
    public LocalHistoryService Start()
    {
        History = new LocalHistoryService(Fs, Home, Config, Documents, WorkspaceService, Time);
        return History;
    }

    public void Restart()
    {
        History.Dispose();
        Start();
    }

    public string PathOf(string relative) => System.IO.Path.Combine(Workspace, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public Uri UriOf(string relative) => new(PathOf(relative));

    public void Write(string relative, string content)
    {
        var full = PathOf(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Writes the file and reports a save of its document (registered on first use), then waits for the history to settle.</summary>
    public async Task SaveAsync(string relative, string content, bool wait = true)
    {
        Write(relative, content);
        var doc = Document(relative);
        Documents.NotifySaved(doc);
        if (wait) await History.WhenIdleAsync();
    }

    public ITextDocument Document(string relative)
    {
        var uri = UriOf(relative);
        if (Documents.Get(uri) is { } doc) return doc;
        var created = new InMemoryTextDocument(uri, "plaintext", File.Exists(PathOf(relative)) ? File.ReadAllText(PathOf(relative)) : "");
        _open[relative] = Documents.Register(created);
        return created;
    }

    /// <summary>Opens <paramref name="relative"/> in an "editor" (replacing the plain document a save registered).</summary>
    public FakeEditorDocument EditorDocument(string relative)
    {
        var uri = UriOf(relative);
        if (Documents.Get(uri) is FakeEditorDocument open) return open;
        if (_open.Remove(relative, out var plain)) plain.Dispose();
        var doc = new FakeEditorDocument(new InMemoryTextDocument(uri, "plaintext", File.ReadAllText(PathOf(relative))), Fs, Documents);
        _open[relative] = Documents.Register(doc);
        return doc;
    }

    /// <summary>Closes the document of <paramref name="relative"/> (the file is then "not open").</summary>
    public void Close(string relative)
    {
        if (_open.Remove(relative, out var registration)) registration.Dispose();
    }

    public async Task<IReadOnlyList<string>> ContentsAsync(string relative)
    {
        var entries = await History.GetEntriesAsync(UriOf(relative));
        var texts = new List<string>();
        foreach (var e in entries) texts.Add(Encoding.UTF8.GetString(await History.ReadAsync(e) ?? []));
        return texts;
    }

    public void Dispose()
    {
        History.Dispose();
        foreach (var r in _open.Values) r.Dispose();
        _lane.Dispose();
        _dir.Dispose();
    }
}

public sealed class FakeWorkspace(IFileSystem fs, params Uri[] folders) : IWorkspaceService
{
    public IReadOnlyList<Uri> Folders { get; } = folders;
    public string? Name => "test";
    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => EventSource.Never<WorkspaceFoldersChangedEventArgs>();
    public IFileSystem FileSystem => fs;
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];
    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because ?? "condition not met in time");
            await Task.Delay(20);
        }
    }
}
