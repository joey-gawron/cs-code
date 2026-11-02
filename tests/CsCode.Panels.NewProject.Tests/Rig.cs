using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Projects;
using CsCode.Core.Text;
using CsCode.Panels.NewProject;
using CsCode.ProjectTemplates.Engine;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;

[assembly: AvaloniaTestApplication(typeof(CsCode.Panels.NewProject.Tests.TestAppBuilder))]

namespace CsCode.Panels.NewProject.Tests;

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

/// <summary>Runs every post inline on the calling thread.</summary>
internal sealed class ImmediateUiDispatcher : UiDispatcherBase
{
    public override bool CheckAccess() => true;
    protected override void PostCore(Action action, UiPriority priority) => action();
}

/// <summary>A generator that writes one file (or fails, or waits to be cancelled), with a tool that is or is not "installed".</summary>
internal sealed class FakeGenerator(string id, int order, bool available = true, IReadOnlyList<ProjectOption>? options = null) : IProjectGenerator
{
    public bool Fail { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public IReadOnlyList<string> ExtraFiles { get; set; } = [];
    public ConcurrentQueue<ProjectRequest> Requests { get; } = new();

    public string Id => id;
    public string DisplayName => id.ToUpperInvariant();
    public string Description => id + " project";
    public string? IconKey => "folder";
    public string? LanguageId => null;
    public int Order => order;
    public ValueTask<IReadOnlyList<ProjectOption>> GetOptionsAsync(CancellationToken ct) => ValueTask.FromResult(options ?? []);
    public ValueTask<ProjectToolCheck> CheckToolsAsync(CancellationToken ct) =>
        ValueTask.FromResult(available ? ProjectToolCheck.Available(id + " 1.0") : ProjectToolCheck.Missing(id + " was not found", "Install it with: brew install " + id));
    public string? ValidateName(string name) => name.Contains(' ', StringComparison.Ordinal) ? "No spaces." : ProjectNames.Folder(name);

    public async Task<ProjectResult> GenerateAsync(ProjectRequest request, IProjectGenerationProgress progress, CancellationToken ct)
    {
        Requests.Enqueue(request);
        progress.Step("Writing main.txt");
        progress.Output("tool says hello");
        await File.WriteAllTextAsync(Path.Combine(request.Directory, "main.txt"), "hi", ct);
        foreach (var extra in ExtraFiles)
        {
            var path = Path.Combine(request.Directory, extra);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "", ct);
        }
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (Fail) throw new ProjectGenerationException("fake tool exited with code 3");
        return new ProjectResult(request.Directory, "main.txt");
    }
}

internal sealed class FakeWorkspace : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    public List<Uri> Opened { get; } = [];
    public IReadOnlyList<Uri> Folders { get; set; } = [];
    public string? Name => null;
    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
    {
        Opened.Add(folder);
        Folders = [folder];
        return ValueTask.CompletedTask;
    }
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public IFileSystem FileSystem => throw new NotSupportedException();
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders { get; set; } = [];
    public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeNavigation : INavigationService
{
    private readonly Emitter<IEditorDocument?> _changed = new();
    public List<Uri> Opened { get; } = [];
    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        Opened.Add(uri);
        return ValueTask.FromResult<IEditorDocument?>(null);
    }
    public IEditorDocument? ActiveDocument => null;
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _changed;
    public IReadOnlyList<IEditorDocument> OpenDocuments => [];
    public ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default) => ValueTask.FromResult(false);
}

internal sealed class FakeViews : IViewActivator
{
    public ConcurrentQueue<string> Shown { get; } = new();
    public ConcurrentQueue<string> Closed { get; } = new();
    public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) { Shown.Enqueue(viewId); return ValueTask.FromResult(true); }
    public ValueTask<bool> CloseViewAsync(string viewId, CancellationToken ct = default) { Closed.Enqueue(viewId); return ValueTask.FromResult(true); }
}

/// <summary>State storage that round-trips through JSON, as Platform's does.</summary>
internal sealed class JsonStateStorage : IStateStorage
{
    private readonly Dictionary<string, string> _values = [];
    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _values[key] = JsonSerializer.Serialize(value);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _values.Remove(key);
    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>Everything the page uses, faked, with its home folder in a temp folder.</summary>
internal sealed class Rig : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-newproject-");
    public FakeWorkspace Workspace { get; } = new();
    public FakeNavigation Navigation { get; } = new();
    public FakeViews Views { get; } = new();
    public RecordingWindowService Window { get; } = new();
    public TestCommandService Commands { get; } = new();
    public TestConfigurationService Configuration { get; } = new();
    public InMemoryOutputService Output { get; } = new();
    public JsonStateStorage State { get; } = new();
    public FakePythonEnvironments Python { get; } = new();

    public NewProjectServices Services(params IProjectGenerator[] generators) =>
        new NewProjectServices([.. generators], new ProjectCreator(new ToolRunner()), new ImmediateUiDispatcher(), Configuration, State, Workspace, Window, Commands,
            Navigation, Output, Views, new ProjectFollowUps(Python)) { Home = Temp.Path };

    public void Dispose() => Temp.Dispose();
}
