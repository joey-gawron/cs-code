using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// rust-analyzer's <c>experimental/serverStatus</c> in the status bar, as its VS Code extension shows it: the server's name, a spinner while it
/// is not quiescent (loading the workspace, indexing, building proc macros), a warning or error mark when its health says so, and the message
/// as the tooltip. With several servers (one per root) it shows the one that reported last; the item goes when no server is left. A click
/// offers Reload Workspace, Rebuild Proc Macros and Restart Server.
/// </summary>
internal sealed class ServerStatusDisplay(ServerExtensionHost host, RustAnalyzerExtension extension) : IDisposable
{
    public const string ItemId = "rust-analyzer.status";

    private readonly object _gate = new();
    private IDisposable? _item;
    private ServerExtensionSession? _shown;
    private bool _disposed;

    public ServerStatus? Current { get; private set; }

    public static string Describe(ServerStatus? status) => status switch
    {
        null => "rust-analyzer",
        { Health: "error" } => "rust-analyzer: error",
        { Health: "warning" } => "rust-analyzer: warning",
        { Quiescent: false } => "rust-analyzer: working",
        _ => "rust-analyzer: ready",
    };

    public static string TextOf(ServerStatus status) => status switch
    {
        { Health: "error" } => "✖ rust-analyzer",
        { Health: "warning" } => "⚠ rust-analyzer",
        { Quiescent: false } => "⟳ rust-analyzer",
        _ => "✓ rust-analyzer",
    };

    public void Show(ServerExtensionSession session, ServerStatus status)
    {
        if (host.Services.StatusBar is not { } bar) return;
        lock (_gate)
        {
            if (_disposed) return;
            _shown = session;
            Current = status;
            var tooltip = status.Message is { Length: > 0 } m ? $"{Describe(status)}\n\n{m}" : Describe(status);
            // Set replaces the item of the same id; the previous token then removes nothing, so it is simply dropped
            _item = bar.Set(new StatusBarItemModel(ItemId, TextOf(status), tooltip, RustAnalyzerExtension.StatusMenuCommand, StatusBarAlignment.Left, Priority: -10,
                ColorKey: status.Health switch { "error" => "statusBarItem.errorForeground", "warning" => "statusBarItem.warningForeground", _ => null }));
        }
    }

    /// <summary>A server stopped: its status goes, and the item with it when it was the one shown.</summary>
    public void Remove(ServerExtensionSession session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_shown, session)) return;
            _shown = null;
            Current = null;
            _item?.Dispose();
            _item = null;
        }
        // another server of the same kind still running shows its last status
        foreach (var other in host.Sessions(extension))
            if (other.State is RustAnalyzerExtension.SessionState { Status: { } s }) { Show(other, s); break; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _item?.Dispose();
            _item = null;
            _shown = null;
        }
    }
}

public sealed partial class RustAnalyzerExtension
{
    private void OnServerStatus(ServerExtensionSession session, JsonElement p)
    {
        if (ServerStatus.From(p) is not { } status) return;
        if (session.State is SessionState state) state.Status = status;
        if (status.Message is { Length: > 0 } message && status.Health != "ok") session.Log($"status {status.Health}: {message}");
        _status?.Show(session, status);
    }
}
