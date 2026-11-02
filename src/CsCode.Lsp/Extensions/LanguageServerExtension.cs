using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;

namespace CsCode.Lsp.Extensions;

/// <summary>
/// What CsCode adds to the plain LSP client for ONE kind of server (rust-analyzer; clangd and Roslyn follow the same pattern): its experimental
/// client capabilities, its commands, its custom notifications and its additions to standard answers (hover actions, lenses). VS Code gets these
/// from each server's own extension; CsCode has no extensions, so each is a class here, listed in <see cref="ServerExtensions.CreateBuiltins"/>.
/// <para>Lifetimes. One instance per <see cref="Manager.LanguageServerManager"/>: <see cref="Register"/> runs once when the manager is built and
/// registers what lives as long as the app (commands, status-bar items) in the host's store, so a command exists before any server runs and
/// finds the session to talk to when it is invoked (<see cref="ServerExtensionHost.ActiveSession"/>). One <see cref="ServerExtensionSession"/>
/// per running server (per workspace root): <see cref="Attach"/> runs before the connection starts listening (the only time notification
/// handlers can be added), <see cref="OnInitialized"/> after the <c>initialize</c> handshake, and the session's
/// <see cref="ServerExtensionSession.Disposables"/> die with that server.</para>
/// <para>Threading. Notification handlers, <see cref="OnInitialized"/> and the answer hooks run on the Language lane, never on the UI thread;
/// command handlers run on the caller's thread (usually the UI thread) and must only enqueue (<see cref="ServerExtensionSession.RequestAsync(string, object?, CancellationToken, TimeSpan?)"/>
/// hops to the lane). An extension keeps no per-session state outside its sessions (a session's <see cref="ServerExtensionSession.State"/>
/// slot holds it), so a server that stops leaves nothing behind.</para>
/// </summary>
public abstract class LanguageServerExtension
{
    /// <summary>Shown in logs: <c>"rust-analyzer"</c>.</summary>
    public abstract string Name { get; }

    /// <summary>Whether sessions of <paramref name="definition"/> get this extension (usually by <see cref="LanguageServerDefinition.Id"/>).</summary>
    public abstract bool AppliesTo(LanguageServerDefinition definition);

    /// <summary>Adds members to the <c>initialize</c> request's <c>capabilities.experimental</c> (merged with any other extension's: last write wins).</summary>
    public virtual void AddExperimentalCapabilities(JsonObject experimental) { }

    /// <summary>Once per manager: register commands and other app-lifetime items in <see cref="ServerExtensionHost.Disposables"/>.</summary>
    public virtual void Register(ServerExtensionHost host) { }

    /// <summary>A server is starting: add its notification handlers (<see cref="ServerExtensionSession.OnNotification"/>). On the starting thread.</summary>
    public virtual void Attach(ServerExtensionSession session) { }

    /// <summary>The server answered <c>initialize</c> and is running. On the Language lane.</summary>
    public virtual void OnInitialized(ServerExtensionSession session) { }

    /// <summary>The server stopped (crash, restart, shutdown): its session's disposables are released right after. On any thread.</summary>
    public virtual void OnStopped(ServerExtensionSession session) { }

    /// <summary>
    /// Whether <see cref="AdjustHover"/> needs the raw <c>textDocument/hover</c> answer (members the protocol does not have, such as
    /// rust-analyzer's <c>actions</c>). False keeps hover on the plain typed path.
    /// </summary>
    public virtual bool WantsRawHover => false;

    /// <summary>The hover CsCode shows for <paramref name="raw"/> (already mapped: <paramref name="hover"/>). On the Language lane.</summary>
    public virtual Hover? AdjustHover(ServerExtensionSession session, JsonElement raw, Hover? hover, Uri document) => hover;

    /// <summary>Whether <see cref="RewriteAnswer"/> sees the raw answers of <paramref name="method"/> (requests sent through the typed path).</summary>
    public virtual bool RewritesAnswerOf(string method) => false;

    /// <summary>
    /// The answer CsCode maps instead of <paramref name="raw"/> (rust-analyzer's snippet text edits made plain), for the methods
    /// <see cref="RewritesAnswerOf"/> names. On the Language lane, before the answer is deserialised.
    /// </summary>
    public virtual JsonElement RewriteAnswer(ServerExtensionSession session, string method, JsonElement raw) => raw;

    /// <summary>The lenses CsCode shows for a document (drop, keep or rewrite the server's). On the Language lane.</summary>
    public virtual IReadOnlyList<CodeLens> AdjustCodeLenses(ServerExtensionSession session, IReadOnlyList<CodeLens> lenses, Uri document) => lenses;
}

/// <summary>The built-in extensions. A new one is one line here.</summary>
public static class ServerExtensions
{
    /// <summary>Fresh instances for one manager (an extension may hold per-manager state).</summary>
    public static IReadOnlyList<LanguageServerExtension> CreateBuiltins() => [];
}
