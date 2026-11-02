using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Languages;

/// <summary>
/// Adds values to what a language server is told about its settings (<c>workspace/configuration</c> answers), on top of the definition's
/// <c>settings</c> and the user's settings: the selected Python interpreter is <c>python.pythonPath</c> for Pyright (language stage 1). Resolve
/// all of them with <c>IEnumerable&lt;ILanguageServerSettingsContributor&gt;</c>. Additive.
/// </summary>
public interface ILanguageServerSettingsContributor
{
    /// <summary>
    /// Members to merge into <paramref name="section"/> (an object; they replace members of the same name) for server <paramref name="serverId"/>,
    /// asked for <paramref name="scope"/> (the request's <c>scopeUri</c>: a folder or a file; null when the server gave none), or null. Called on the
    /// language lane: cheap and non-blocking.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement>? Contribute(string serverId, string section, Uri? scope);

    /// <summary>Fires (with a server id, or null for every server) when a contribution changed: the client pushes <c>workspace/didChangeConfiguration</c>.</summary>
    IEventSource<string?> Changed { get; }
}
