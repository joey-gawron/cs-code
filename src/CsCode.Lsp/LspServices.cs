using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Lsp;

/// <summary>
/// Every Core service the LSP module talks to. Only the first three are mandatory; the rest degrade gracefully when absent
/// (no window service => messages go to the Output channel/log only, and so on). All access to documents goes through these
/// interfaces, so the whole manager can run in a separate headless process later (no statics, no assumption of in-proc editors).
/// </summary>
public sealed record LspServices(IDocumentManager Documents, ILanguageFeatureRegistry Registry, IDiagnosticService Diagnostics)
{
    public IConfigurationService? Configuration { get; init; }
    public IWorkspaceService? Workspace { get; init; }
    public IWindowService? Window { get; init; }
    public IOutputService? Output { get; init; }
    public INavigationService? Navigation { get; init; }
    public IFileWatcher? Watcher { get; init; }
    public IFileSystem? FileSystem { get; init; }
    public ICommandService? Commands { get; init; }
    public IAppPaths? Paths { get; init; }
    public IMemoryMonitor? Memory { get; init; }
    public ILanguageService? Languages { get; init; }
    public ILoggerFactory? Loggers { get; init; }
    public ILspConnectionFactory? Connections { get; init; }
    /// <summary>Where library and generated documents are served from (go to definition into metadata, source-generated files).</summary>
    public IVirtualDocumentService? VirtualDocuments { get; init; }
    /// <summary>Per-workspace choices a server integration remembers (the solution Roslyn loads).</summary>
    public IStateStorage? State { get; init; }
    /// <summary>Server extensions' status-bar items (rust-analyzer's server status).</summary>
    public IStatusBarService? StatusBar { get; init; }
    /// <summary>Where server extensions put read-only results (Expand Macro); null: those commands say they cannot show them.</summary>
    public Extensions.ReadOnlyDocuments? ReadOnlyDocuments { get; init; }
    /// <summary>The debugger (rust-analyzer's Debug lens), resolved lazily: it is composed after the language servers.</summary>
    public Func<Core.Debugging.IDebugService?>? Debug { get; init; }
    /// <summary>Task terminals (rust-analyzer's Run lens runs cargo in one), resolved lazily: the terminal comes with the shell's tool windows.</summary>
    public Func<Core.Tasks.ITaskTerminalHost?>? TaskTerminals { get; init; }

    public static LspServices FromProvider(IServiceProvider sp) => new(
        sp.GetRequiredService<IDocumentManager>(),
        sp.GetRequiredService<ILanguageFeatureRegistry>(),
        sp.GetRequiredService<IDiagnosticService>())
    {
        Configuration = sp.GetService<IConfigurationService>(),
        Workspace = sp.GetService<IWorkspaceService>(),
        Window = sp.GetService<IWindowService>(),
        Output = sp.GetService<IOutputService>(),
        Navigation = sp.GetService<INavigationService>(),
        Watcher = sp.GetService<IFileWatcher>(),
        FileSystem = sp.GetService<IFileSystem>(),
        Commands = sp.GetService<ICommandService>(),
        Paths = sp.GetService<IAppPaths>(),
        Memory = sp.GetService<IMemoryMonitor>(),
        Languages = sp.GetService<ILanguageService>(),
        Loggers = sp.GetService<ILoggerFactory>(),
        Connections = sp.GetService<ILspConnectionFactory>(),
        VirtualDocuments = sp.GetService<IVirtualDocumentService>(),
        State = sp.GetService<IStateStorage>(),
        StatusBar = sp.GetService<IStatusBarService>(),
        ReadOnlyDocuments = sp.GetService<Extensions.ReadOnlyDocuments>(),
        Debug = () => sp.GetService<Core.Debugging.IDebugService>(),
        TaskTerminals = () => sp.GetService<Core.Tasks.ITaskTerminalHost>(),
    };
}
