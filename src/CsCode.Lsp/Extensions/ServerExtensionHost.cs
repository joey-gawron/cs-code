using CsCode.Core.Lifecycle;
using CsCode.Lsp.Manager;

namespace CsCode.Lsp.Extensions;

/// <summary>
/// What an extension's app-lifetime registrations (<see cref="LanguageServerExtension.Register"/>) can reach: the Core services, a store that
/// lives as long as the manager, and the running sessions — the one serving the active editor first, as VS Code's rust-analyzer extension
/// picks "the client for the active document".
/// </summary>
public sealed class ServerExtensionHost
{
    private readonly LanguageServerManager _manager;

    internal ServerExtensionHost(LanguageServerManager manager, LspServices services)
    {
        _manager = manager;
        Services = services;
    }

    public LspServices Services { get; }

    /// <summary>The manager (restart a server, list them).</summary>
    public Core.Languages.ILanguageServerManager Manager => _manager;

    /// <summary>Released when the manager is disposed.</summary>
    public DisposableStore Disposables { get; } = new();

    /// <summary>The running sessions of <paramref name="extension"/> (one per server root).</summary>
    public IReadOnlyList<ServerExtensionSession> Sessions(LanguageServerExtension extension) =>
        [.. _manager.Slots.Select(s => s.Client?.Extension).Where(e => e is { IsRunning: true } && ReferenceEquals(e.Extension, extension)).Select(e => e!)];

    /// <summary>
    /// The running session of <paramref name="extension"/> that serves <paramref name="document"/>; else one whose root contains it; else null.
    /// </summary>
    public ServerExtensionSession? SessionFor(LanguageServerExtension extension, Uri document)
    {
        if (_manager.SessionServing(document, extension) is { IsRunning: true } routed) return routed;
        var path = document.IsFile ? document.LocalPath : null;
        return Sessions(extension)
            .Where(s => path is not null && s.RootUri is { IsFile: true } root && IsUnder(path, root.LocalPath))
            .OrderByDescending(s => s.RootUri!.LocalPath.Length)
            .FirstOrDefault();
    }

    /// <summary>The session for the active editor's document (<see cref="SessionFor"/>), else the only running one, else null.</summary>
    public ServerExtensionSession? ActiveSession(LanguageServerExtension extension)
    {
        if (Services.Navigation?.ActiveDocument?.Uri is { } active && SessionFor(extension, active) is { } s) return s;
        var all = Sessions(extension);
        return all.Count == 1 ? all[0] : null;
    }

    private static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd('/', '\\');
        return path.StartsWith(r, StringComparison.Ordinal) && (path.Length == r.Length || path[r.Length] is '/' or '\\');
    }
}
