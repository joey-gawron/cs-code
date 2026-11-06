using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.App.Services;

/// <summary>
/// <c>Language: Restart Language Server...</c> (pick one of the servers CsCode knows; a hung or faulted one included) and
/// <c>Language: Restart All Language Servers</c>. They go through <see cref="ILanguageServerManager.RestartAsync"/>, so they serve the local
/// servers, the out-of-process language host and a remote workspace's servers alike.
/// </summary>
internal static class LanguageServerCommands
{
    public const string RestartServer = "languages.restartServer";
    public const string RestartAll = "languages.restartAllServers";

    public static IDisposable Register(ICommandService commands, ILanguageServerManager? servers, IWindowService? window)
    {
        var store = new DisposableStore();
        if (servers is null) return store;
        if (!commands.Has(RestartServer))
            store.Add(commands.Register(RestartServer, async _ => { await RestartOneAsync(servers, window).ConfigureAwait(false); return null; },
                new CommandMetadata(RestartServer, "Restart Language Server...", "Language")));
        if (!commands.Has(RestartAll))
            store.Add(commands.Register(RestartAll, async _ => { await RestartAllAsync(servers, window).ConfigureAwait(false); return null; },
                new CommandMetadata(RestartAll, "Restart All Language Servers", "Language")));
        return store;
    }

    /// <summary>The servers by id, each once (a multi-root workspace has one per root; restarting an id restarts them all).</summary>
    internal static IReadOnlyList<(string Id, string Name, string State)> Known(ILanguageServerManager servers) =>
        [.. servers.Servers.GroupBy(s => s.Id, StringComparer.Ordinal)
            .Select(g => (g.Key, g.First().DisplayName, string.Join(", ", g.Select(s => s.State.ToString().ToLowerInvariant()).Distinct())))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];

    private static async Task RestartOneAsync(ILanguageServerManager servers, IWindowService? window)
    {
        var known = Known(servers);
        if (known.Count == 0)
        {
            if (window is not null) await window.ShowMessageAsync(MessageSeverity.Info, "No language server has started yet (one starts when a file of its language opens).").ConfigureAwait(false);
            return;
        }
        string id;
        if (known.Count == 1 || window is null) id = known[0].Id;
        else
        {
            var items = known.Select(k => new QuickPickItem(k.Name, k.State, Tag: k.Id)).ToArray();
            var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions("Restart Language Server", "Select the language server to restart")).ConfigureAwait(false);
            if (picked?.Tag is not string chosen) return;
            id = chosen;
        }
        await RestartAsync(servers, window, [id]).ConfigureAwait(false);
    }

    private static Task RestartAllAsync(ILanguageServerManager servers, IWindowService? window) =>
        RestartAsync(servers, window, [.. Known(servers).Select(k => k.Id)]);

    private static async Task RestartAsync(ILanguageServerManager servers, IWindowService? window, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;
        var failures = new List<string>();
        foreach (var id in ids)
        {
            try { await servers.RestartAsync(id).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failures.Add($"{id}: {ex.Message}"); }
        }
        if (window is null) return;
        if (failures.Count > 0) await window.ShowMessageAsync(MessageSeverity.Error, "Could not restart: " + string.Join("; ", failures)).ConfigureAwait(false);
        else window.SetStatusBarMessage(ids.Count == 1 ? "Restarting " + ids[0] + "…" : $"Restarting {ids.Count} language servers…", TimeSpan.FromSeconds(3));
    }
}
