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
    };
}
