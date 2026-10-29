using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Panels.Search;
using CsCode.Platform;
using CsCode.Platform.Commands;
using CsCode.Platform.Configuration;
using CsCode.Platform.Context;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.Keybindings;
using CsCode.Ui;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Search.Preview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<PreviewApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

/// <summary>Hosts the panels' <see cref="IToolWindow"/>s in tabs, the way the shell will host them, and implements <see cref="IViewActivator"/> over the tabs.</summary>
public sealed class PreviewApp : Application, IViewActivator
{
    private TabControl _tabs = null!;
    private readonly Dictionary<string, TabItem> _tabByView = new();
    private TextBlock _status = null!;
    private ServiceProvider _sp = null!;
    private string _workspace = "";
    private bool _readOnlyWorkspace;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var i = Array.IndexOf(args, "--folder");
            _readOnlyWorkspace = i >= 0 && i + 1 < args.Length;
            _workspace = _readOnlyWorkspace ? Path.GetFullPath(args[i + 1]) : CreateSampleWorkspace();
            desktop.MainWindow = BuildWindow();
            desktop.Exit += (_, _) => _sp.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static string CreateSampleWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cscode-search-preview-" + Guid.NewGuid().ToString("N")[..8]);
        void W(string rel, string text) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        W("src/Program.cs", "using MyApp.Services;\n\npublic static class Program\n{\n    public static void Main(string[] args)\n    {\n        var service = new UserService();\n        System.Console.WriteLine(service.FindUser(args[0]).Name);\n    }\n}\n");
        W("src/Services/UserService.cs", "namespace MyApp.Services;\n\npublic class UserService\n{\n    public User FindUser(string name) => _repository.Find(name);\n\n    public IReadOnlyList<User> AllUsers() => _repository.All();\n}\n");
        W("src/Services/UserServiceTests.cs", "public class UserServiceTests\n{\n    public void Finds_a_user() => new UserService().FindUser(\"ada\");\n}\n");
        W("docs/architecture.md", "# Architecture\n\nThe UserService talks to the repository. See also: UserService docs.\n");
        W("README.md", "# MyApp\n\nUses UserService for lookups.\n");
        W(".gitignore", "bin/\nobj/\n");
        W("bin/ignored.txt", "UserService in an ignored folder\n");
        return dir;
    }

    private Window BuildWindow()
    {
        // ---- real Platform services, rooted in a throw-away CSCODE_HOME so the user's own settings are never touched
        var home = Path.Combine(Path.GetTempPath(), "cscode-preview-home-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(n => n == AppPaths.HomeEnvironmentVariable ? home : null);
        var lane = new IoLane("preview-io");
        var fs = new LocalFileSystem(lane);
        var config = new ConfigurationService(paths, lane);
        var themes = new PreviewThemes();
        var ui = AvaloniaUiDispatcher.Instance;
        var applier = new ThemeApplier(this, themes, ui, config);
        var context = new ContextKeyService(config);
        var commands = new CommandService();
        var keys = new KeybindingService(context, paths, lane, null, config);

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(new PreviewWorkspace(new Uri(_workspace.TrimEnd('/') + "/"), fs));
        services.AddSingleton<IDocumentManager>(new DocumentManager());
        services.AddSingleton<IConfigurationService>(config);
        services.AddSingleton<ICommandService>(commands);
        services.AddSingleton<IKeybindingService>(keys);
        services.AddSingleton<IContextKeyService>(context);
        services.AddSingleton<IStateStorage>(new MemoryState());
        services.AddSingleton<IUiDispatcher>(ui);
        services.AddSingleton<IViewActivator>(this);
        services.AddSingleton<IWindowService>(new PreviewWindows(m => Dispatcher.UIThread.Post(() => _status.Text = m)));
        services.AddSingleton<INavigationService>(new PreviewNavigation(_readOnlyWorkspace, m => Dispatcher.UIThread.Post(() => _status.Text = m)));
        services.AddCsCodeSearchPanels();
        _sp = services.BuildServiceProvider();

        // enumerating the tool windows registers the commands and the search.* settings (that is the integration contract)
        var tools = _sp.GetServices<IToolWindow>().ToList();

        _tabs = new TabControl { Padding = new Thickness(0) };
        foreach (var t in tools)
        {
            var tab = new TabItem { Header = t.Title, Content = t.CreateView(_sp) };
            _tabByView[t.Id] = tab;
            _tabs.Items.Add(tab);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 6) };
        Button B(string text, string command, params object?[] args)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4) };
            b.Click += async (_, _) => await commands.ExecuteAsync(command, args);
            return b;
        }
        buttons.Children.Add(B("Find in Files  (Ctrl+Shift+F)", "workbench.action.findInFiles"));
        buttons.Children.Add(B("Replace in Files  (Ctrl+Shift+H)", "workbench.action.replaceInFiles"));
        buttons.Children.Add(B("Settings", "workbench.action.openSettings"));
        buttons.Children.Add(B("Keyboard Shortcuts", "workbench.action.openGlobalKeybindings"));
        buttons.Children.Add(new TextBlock { Text = (_readOnlyWorkspace ? "folder (read-only): " : "sample workspace: ") + _workspace, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(10, 0) });

        _status = new TextBlock { Text = "Ready. Commands go through the real CommandService; the tabs stand in for the shell's tool windows.", Margin = new Thickness(10, 3), FontSize = 11 };
        var root = new DockPanel { Background = (IBrush?)Resources["vsc:editor.background"] };
        var bottom = new Border { Background = (IBrush?)Resources["vsc:statusBar.background"], Child = _status };
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Top);
        root.Children.Add(bottom);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);

        var window = new Window { Title = "CsCode panels preview: Find in Files, Settings, Keyboard Shortcuts", Width = 1100, Height = 760, Content = root };
        window.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) { await commands.ExecuteAsync("workbench.action.findInFiles"); e.Handled = true; }
            else if (e.Key == Key.H && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) { await commands.ExecuteAsync("workbench.action.replaceInFiles"); e.Handled = true; }
        };
        // Optional self-driving for demos and screenshots: query DIR, replace TEXT, tab settings or keys (all given with a double-dash prefix).
        var cli = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? [];
        string? Arg(string name) { var k = Array.IndexOf(cli, "--" + name); return k >= 0 && k + 1 < cli.Length ? cli[k + 1] : null; }
        if (Arg("query") is { } q)
        {
            var replace = Arg("replace");
            DispatcherTimer.RunOnce(async () =>
            {
                var request = System.Text.Json.JsonSerializer.SerializeToElement(new { query = q, replace, triggerSearch = true });
                await commands.ExecuteAsync(replace is null ? "workbench.action.findInFiles" : "workbench.action.replaceInFiles", request);
            }, TimeSpan.FromMilliseconds(800));
        }
        if (Arg("tab") is { } wanted)
            DispatcherTimer.RunOnce(async () => await commands.ExecuteAsync(wanted == "keys" ? "workbench.action.openGlobalKeybindings" : "workbench.action.openSettings"), TimeSpan.FromMilliseconds(800));
        _ = applier; // keeps the theme applier (and so the vsc: brushes) alive for the window's lifetime
        return window;
    }

    /// <summary>The shell's job in the real app: reveal the view. Here: select its tab.</summary>
    public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default)
    {
        if (!_tabByView.TryGetValue(viewId, out var tab)) return ValueTask.FromResult(false);
        Dispatcher.UIThread.Post(() => _tabs.SelectedItem = tab);
        return ValueTask.FromResult(true);
    }
}

// ----------------------------------------------------------------------------------------------- small fakes for what Platform does not provide yet

internal sealed class PreviewThemes : IColorThemeService
{
    private readonly Emitter<ColorTheme> _changed = new();
    public ColorTheme Current { get; private set; } = VsThemes.Dark;
    public IReadOnlyList<ColorTheme> Available => VsThemes.All;
    public IEventSource<ColorTheme> Changed => _changed;
    public IDisposable Register(ColorTheme theme) => Disposable.Empty;
    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default) { Current = VsThemes.All.First(t => t.Id == themeId); _changed.Fire(Current); return ValueTask.CompletedTask; }
    public Rgba GetColor(string key) => Current.TryGetColor(key, out var c) ? c : new Rgba(128, 128, 128);
}

internal sealed class MemoryState : IStateStorage
{
    private readonly Dictionary<string, string> _d = new();
    public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => _d.TryGetValue(key, out var j) ? System.Text.Json.JsonSerializer.Deserialize<T>(j) : default;
    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) => _d[key] = System.Text.Json.JsonSerializer.Serialize(value);
    public void Remove(string key, StorageScope scope = StorageScope.Application) => _d.Remove(key);
    public Task FlushAsync() => Task.CompletedTask;
}

/// <summary>A workspace over one folder with the real <see cref="LocalFileSystem"/>; file listing skips VCS/build folders (the real WorkspaceService is not in this base).</summary>
internal sealed class PreviewWorkspace(Uri folder, IFileSystem fs) : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
    public IReadOnlyList<Uri> Folders { get; } = [folder];
    public string? Name => Path.GetFileName(folder.LocalPath.TrimEnd('/'));
    public IFileSystem FileSystem => fs;
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
    public ValueTask OpenFolderAsync(Uri f, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        var skip = new HashSet<string> { ".git", "bin", "obj", "node_modules", ".scratch" };
        var stack = new Stack<string>();
        stack.Push(folder.LocalPath);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir).Where(d => !skip.Contains(Path.GetFileName(d)))) stack.Push(d);
            foreach (var f in Directory.EnumerateFiles(dir)) yield return new Uri(f);
        }
    }
}

internal sealed class PreviewWindows(Action<string> status) : IWindowService
{
    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
    {
        status($"[{severity}] {message.Replace('\n', ' ')}");
        return ValueTask.FromResult(actions?.FirstOrDefault(a => !a.IsCloseAffordance)?.Title); // the preview auto-confirms the first action ("Replace")
    }
    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<QuickPickItem?>(null);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);
    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
    public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => await work(new NoProgress(), CancellationToken.None);
    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) { status(text); return Disposable.Empty; }
    public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(false);
    public ValueTask CopyToClipboardAsync(string text) { status("copied to clipboard: " + text); return ValueTask.CompletedTask; }
    private sealed class NoProgress : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
}

/// <summary>"Opening" a match only reports it in the status bar. Edits are applied to disk in the sample workspace and refused for a user-supplied --folder.</summary>
internal sealed class PreviewNavigation(bool readOnly, Action<string> status) : INavigationService
{
    private readonly Emitter<IEditorDocument?> _active = new();
    public IEditorDocument? ActiveDocument => null;
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
    public IReadOnlyList<IEditorDocument> OpenDocuments => [];

    public ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        status($"open{(preview ? " (preview)" : "")}: {(uri.IsFile ? uri.LocalPath : uri)}{(selection is { } s ? $"  at {s.Start.Line + 1}:{s.Start.Character + 1}" : "")}");
        return ValueTask.FromResult<IEditorDocument?>(null);
    }

    public async ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default)
    {
        if (readOnly) { status("preview: edits are disabled for a user-supplied --folder"); return false; }
        foreach (var (uri, edits) in edit.Changes)
        {
            var doc = new InMemoryTextDocument(uri, "plaintext", await File.ReadAllTextAsync(uri.LocalPath, ct));
            if (!doc.ApplyEdits(edits)) return false;
            await File.WriteAllTextAsync(uri.LocalPath, doc.GetText(), ct);
        }
        status($"applied {edit.Changes.Sum(c => c.Value.Count)} edit(s) in {edit.Changes.Count} file(s)");
        return true;
    }
}
