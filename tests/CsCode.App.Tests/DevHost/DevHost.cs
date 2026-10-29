using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor;
using CsCode.TestDoubles;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.App.Tests.Hosting;

/// <summary>
/// Composes the REAL shell (<see cref="AppServices.Build"/> + <see cref="MainWindow"/>) over in-memory fakes for the modules that are being written
/// concurrently (Platform, Editor, Dap...). This is how the shell is run, tested and screenshotted until those modules are merged.
/// Every service can be replaced through <see cref="Configure"/>.
/// </summary>
public sealed class DevHost : IDisposable
{
    public TempDirectory Workspace { get; } = new("cscode-devhost-");
    /// <summary>Where the shell's crash handler writes in this host (never the user's real log directory).</summary>
    public TempDirectory CrashLogs { get; } = new("cscode-devhost-logs-");
    public ServiceProvider Services { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public TestCommandService Commands { get; } = new();
    public SimpleContextKeys Keys { get; } = new();
    public TestConfigurationService Config { get; } = new();
    public FakeKeybindingService Bindings { get; }
    public FakeWorkspaceService WorkspaceService { get; }
    public InMemoryStateStorage State { get; } = new();
    public FakeColorThemeService Themes { get; } = new();
    public FakeEditorViewFactory Editors { get; }
    public FakeDebugService Debug { get; } = new();
    public DocumentManager Documents { get; } = new();
    public string[] Args { get; init; } = [];

    public DevHost()
    {
        Bindings = new FakeKeybindingService(Keys);
        WorkspaceService = new FakeWorkspaceService();
        Editors = new FakeEditorViewFactory(Documents) { TextFor = SampleText };
        SeedKeymap();
    }

    private static string SampleText(Uri uri) => uri.AbsolutePath.EndsWith(".cs", StringComparison.Ordinal)
        ? "using System;\n\nnamespace Demo;\n\npublic static class Program\n{\n    public static int Main(string[] args)\n    {\n        Console.WriteLine(\"Hello, CsCode\");\n        return 0;\n    }\n}\n"
        : "# CsCode demo\n\nSample content.\n";

    private void SeedKeymap()
    {
        foreach (var (cmd, chord) in new[]
        {
            (KnownCommands.CommandPalette, "ctrl+shift+p"), (KnownCommands.QuickOpen, "ctrl+t"), (KnownCommands.OpenFolder, "ctrl+shift+o"), (KnownCommands.OpenFile, "ctrl+o"),
            (KnownCommands.NewUntitledFile, "ctrl+n"), (KnownCommands.Save, "ctrl+s"), (KnownCommands.SaveAll, "ctrl+shift+s"), (KnownCommands.CloseActiveEditor, "ctrl+f4"),
            (KnownCommands.Find, "ctrl+f"), (KnownCommands.GotoDefinition, "f12"), (KnownCommands.FindReferences, "shift+f12"), (KnownCommands.Rename, "f2"),
            (KnownCommands.DebugStart, "f5"), (KnownCommands.DebugRun, "ctrl+f5"), (KnownCommands.DebugStop, "shift+f5"), (KnownCommands.DebugStepOver, "f10"),
            (KnownCommands.DebugStepInto, "f11"), (KnownCommands.DebugStepOut, "shift+f11"), (KnownCommands.DebugToggleBreakpoint, "f9"), (KnownCommands.ToggleTerminal, "ctrl+`"),
            ("workbench.view.explorer", "ctrl+alt+l"), ("workbench.action.gotoLine", "ctrl+g"), (KnownCommands.Quit, "alt+f4"),
        })
            Bindings.Add(cmd, chord);
        Bindings.Add("editor.action.addCommentLine", "ctrl+k ctrl+c");
        Bindings.Add("editor.action.removeCommentLine", "ctrl+k ctrl+u");
        Bindings.Add(KnownCommands.FormatDocument, "ctrl+k ctrl+d", "editorTextFocus");
    }

    /// <summary>Registers no-op handlers for commands normally owned by other modules, so their menu items are enabled in screenshots.</summary>
    public void RegisterModuleCommands(params string[] ids)
    {
        foreach (var id in ids)
            if (!Commands.Has(id)) Commands.Register(id, _ => ValueTask.FromResult<object?>(null), new CommandMetadata(id, id));
    }

    public Action<IServiceCollection>? Configure { get; init; }

    /// <summary>Builds the provider and the window (not shown). Must be called on the Avalonia UI thread.</summary>
    public DevHost Build()
    {
        Services = AppServices.Build(Args, ApplyTo);
        Window = new MainWindow(Services);
        return this;
    }

    /// <summary>Replaces the module services with this host's fakes (also used as <c>App.ConfigureServices</c> for a real, non-headless window).</summary>
    public void ApplyTo(IServiceCollection services)
    {
        Replace<ICommandService>(services, Commands);
        Replace<IContextKeyService>(services, Keys);
        Replace<IConfigurationService>(services, Config);
        Replace<IKeybindingService>(services, Bindings);
        Replace<IWorkspaceService>(services, WorkspaceService);
        Replace<IStateStorage>(services, State);
        Replace<IColorThemeService>(services, Themes);
        Replace<IEditorViewFactory>(services, Editors);
        Replace<IDocumentManager>(services, Documents);
        Replace<IDebugService>(services, Debug);
        Replace(services, new Startup.CrashHandler(CrashLogs.Path));
        Configure?.Invoke(services);
    }

    private static void Replace<T>(IServiceCollection services, T instance) where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    /// <summary>Creates a small realistic project on disk (the workspace the explorer shows).</summary>
    public void SeedWorkspace()
    {
        Workspace.WriteFile("src/Demo.App/Program.cs", SampleText(new Uri("file:///x.cs")));
        Workspace.WriteFile("src/Demo.App/Demo.App.csproj", "<Project />");
        Workspace.WriteFile("src/Demo.App/Services/GreetingService.cs", "// service");
        Workspace.WriteFile("src/Demo.App/Services/Logger.cs", "// logger");
        Workspace.WriteFile("src/Demo.Core/Models/Person.cs", "// model");
        Workspace.WriteFile("tests/Demo.Tests/ProgramTests.cs", "// tests");
        Workspace.WriteFile("docs/README.md", "# Demo");
        Workspace.WriteFile(".editorconfig", "root = true");
        Workspace.WriteFile("Demo.slnx", "<Solution />");
        Workspace.WriteFile("LICENSE", "MIT");
    }

    /// <summary>Pumps the dispatcher until <paramref name="condition"/> holds (headless tests: continuations run through the UI loop).</summary>
    public static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>Shows the window and waits for the deferred startup (layout restore, command-line targets, welcome page).</summary>
    public async Task ShowAsync(int width = 1280, int height = 800)
    {
        Window.Width = width;
        Window.Height = height;
        Window.Show();
        await WaitFor(() => Window.IsVisible);
        // The shell does its I/O after the first frame; give it a few dispatcher rounds.
        for (var i = 0; i < 30; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    public static string ScreenshotDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        var shots = Path.Combine(root, ".scratch", "screenshots");
        Directory.CreateDirectory(shots);
        return shots;
    }

    /// <summary>Renders the window and writes a PNG into <c>.scratch/screenshots</c> (git-ignored). Returns the path.</summary>
    public string Capture(string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(ScreenshotDirectory(), name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    public void Dispose()
    {
        Window?.CloseWithoutPrompt();
        Services?.Dispose();
        Workspace.Dispose();
        CrashLogs.Dispose();
    }
}
