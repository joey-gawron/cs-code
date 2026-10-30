using CsCode.Core.Diagnostics;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Fallbacks;
using CsCode.Editor.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Editor;

/// <summary>
/// Everything the editor consumes from the rest of the app, resolved ONCE from DI. All Core services are optional here: when the
/// Platform module (file system, watcher, configuration…) is not registered the editor falls back to small local
/// implementations so the module is usable and testable standalone. Nothing in here is ever a concrete type of another module.
/// </summary>
public sealed class EditorServices : IDisposable
{
    private readonly DedicatedLane _ownedIoLane;
    private readonly Lazy<INavigationService?> _navigation;
    private readonly Lazy<IDebugService?> _debug;

    internal EditorServices(IServiceProvider? sp, IUiThread ui, DedicatedLane? io = null)
    {
        Ui = ui;
        Provider = sp;
        _ownedIoLane = io ?? new DedicatedLane("cscode-editor-io", 1, ThreadPriority.Normal);
        Io = _ownedIoLane;

        T? Opt<T>() where T : class => sp?.GetService<T>();

        FileSystem = Opt<IFileSystem>() ?? new LocalFileSystem(Io);
        FileWatcher = Opt<IFileWatcher>() ?? new FswFileWatcher();
        Config = Opt<IConfigurationService>() ?? new InMemoryConfiguration();
        Window = Opt<IWindowService>();
        Documents = Opt<IDocumentManager>() ?? new DocumentManager();
        Features = Opt<ILanguageFeatureRegistry>() ?? new LanguageFeatureRegistry();
        Diagnostics = Opt<IDiagnosticService>() ?? new DiagnosticService();
        Contributions = Opt<IContributionRegistry>() ?? new ContributionRegistry();
        Commands = Opt<ICommandService>();
        ContextKeys = Opt<IContextKeyService>();
        Clipboard = Opt<IClipboardService>();
        Keybindings = Opt<IKeybindingService>();
        // Resolved on first use, not here: the shell's navigation service needs the document well, which needs the editor view factory,
        // which needs this object; the debug service needs navigation. Resolving them eagerly is a DI cycle that never finishes.
        _navigation = new Lazy<INavigationService?>(Opt<INavigationService>);
        Memory = Opt<IMemoryMonitor>();
        _debug = new Lazy<IDebugService?>(Opt<IDebugService>);
        AppPaths = Opt<IAppPaths>();
        EditorConfig = Opt<IEditorConfigSource>() ?? new EditorConfigResolver(FileSystem);
        LoggerFactory = Opt<ILoggerFactory>() ?? NullLoggerFactory.Instance;
    }

    public IServiceProvider? Provider { get; }
    public IUiThread Ui { get; }
    /// <summary>Dedicated I/O thread (never the shared pool, never the UI thread).</summary>
    public DedicatedLane Io { get; }
    public IFileSystem FileSystem { get; }
    public IFileWatcher FileWatcher { get; }
    public IConfigurationService Config { get; }
    public IWindowService? Window { get; }
    public IDocumentManager Documents { get; }
    public ILanguageFeatureRegistry Features { get; }
    public IDiagnosticService Diagnostics { get; }
    public IContributionRegistry Contributions { get; }
    public ICommandService? Commands { get; }
    public IContextKeyService? ContextKeys { get; }
    /// <summary>The shell's clipboard (system clipboard, PRIMARY, ring); null when standalone (the editor then uses its window's clipboard).</summary>
    public IClipboardService? Clipboard { get; }
    public IKeybindingService? Keybindings { get; }
    public INavigationService? Navigation => _navigation.Value;
    public IMemoryMonitor? Memory { get; }
    public IDebugService? Debug => _debug.Value;
    public IAppPaths? AppPaths { get; }
    public IEditorConfigSource EditorConfig { get; }
    public ILoggerFactory LoggerFactory { get; }

    public void Dispose() => _ownedIoLane.Dispose();
}
