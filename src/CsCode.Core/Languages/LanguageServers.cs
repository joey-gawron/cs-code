using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

public enum LanguageServerState { Stopped, Starting, Running, Stopping, Faulted }

/// <summary>How to run one native (C#-hosted) language client. Sources: built-in defaults, `cscode.languageServers` setting.</summary>
public sealed record LanguageServerDefinition(
    string Id,
    string DisplayName,
    DocumentSelector Selector,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env = null,
    string? WorkingDirectory = null,
    /// <summary>Files/dirs (globs) whose presence marks a workspace root for this server; empty = workspace folder.</summary>
    IReadOnlyList<string>? RootMarkers = null,
    JsonElement? InitializationOptions = null,
    /// <summary>Sent via workspace/didChangeConfiguration and answered for workspace/configuration (section → value).</summary>
    JsonElement? Settings = null,
    /// <summary>Settings key under which the user can override this definition: cscode.languageServers.&lt;Id&gt;.</summary>
    string? SettingsKey = null,
    bool AutoStart = true,
    /// <summary>Restart the server if its RSS exceeds this (bytes); null = never.</summary>
    long? RestartAboveBytes = null);

public sealed record LanguageServerInfo(
    string Id,
    string DisplayName,
    LanguageServerState State,
    Uri? RootUri,
    int? ProcessId,
    string? ServerName,
    string? ServerVersion,
    string? LastError);

/// <summary>Owns native LSP client lifecycles. Implemented by CsCode.Lsp.</summary>
public interface ILanguageServerManager
{
    IReadOnlyList<LanguageServerDefinition> Definitions { get; }
    IReadOnlyList<LanguageServerInfo> Servers { get; }

    /// <summary>Adds a definition; the server starts lazily on first matching document if <c>AutoStart</c>.</summary>
    IDisposable AddDefinition(LanguageServerDefinition definition);

    ValueTask StartAsync(string id, CancellationToken ct = default);
    ValueTask StopAsync(string id, CancellationToken ct = default);
    ValueTask RestartAsync(string id, CancellationToken ct = default);
    ValueTask StopAllAsync(CancellationToken ct = default);

    IEventSource<IReadOnlyList<LanguageServerInfo>> ServersChanged { get; }
}
