using System.Text;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

/// <summary>
/// <c>workspace/setFolders</c> keeps the server's workspace (what its language servers see as roots, where its workspace settings come from) equal
/// to the client's remote folders; <c>settings/setUser</c> mirrors the client's user settings.json into the server's own user settings file, which
/// the server's configuration service then serves (workspace settings are read here, next to the files).
/// </summary>
internal sealed class WorkspaceHandlers(WorkspaceService workspace, ConfigurationService configuration, IFileSystem fs)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Register(RpcPeer peer)
    {
        peer.OnRequest<SetFoldersParams, Empty>(RemoteMethods.WorkspaceSetFolders, async (p, ct) =>
        {
            await SetFoldersAsync(p.Folders, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnRequest<SetUserSettingsParams, Empty>(RemoteMethods.SettingsSetUser, async (p, ct) =>
        {
            await SetUserSettingsAsync(p.Text, ct).ConfigureAwait(false);
            return Empty.Value;
        });
    }

    public async Task SetFoldersAsync(IReadOnlyList<string> folders, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wanted = folders.Select(f => FileUri.FromDirectoryPath(FileSystemHandlers.Uri(f).LocalPath)).Distinct().ToArray();
            if (workspace.Folders.SequenceEqual(wanted)) return;
            if (wanted.Length == 0) { await workspace.CloseFolderAsync(ct).ConfigureAwait(false); return; }
            if (workspace.PrimaryFolder != wanted[0]) await workspace.OpenFolderAsync(wanted[0], ct).ConfigureAwait(false);
            foreach (var extra in workspace.Folders.Skip(1).Except(wanted).ToArray()) await workspace.RemoveFolderAsync(extra, ct).ConfigureAwait(false);
            foreach (var add in wanted.Skip(1)) await workspace.AddFolderAsync(add, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetUserSettingsAsync(string text, CancellationToken ct)
    {
        if (text.Length > 4 * 1024 * 1024) throw new ArgumentException("settings.json larger than 4 MB");
        var path = configuration.UserSettingsPath;
        var uri = FileUri.FromPath(path);
        var current = await fs.StatAsync(uri, ct).ConfigureAwait(false) is not null
            ? Encoding.UTF8.GetString(await fs.ReadFileAsync(uri, ct).ConfigureAwait(false))
            : null;
        if (current == text) return;
        await fs.WriteFileAsync(uri, Encoding.UTF8.GetBytes(text), createParents: true, ct).ConfigureAwait(false);
        await configuration.ReloadAsync(ct).ConfigureAwait(false); // apply now rather than at the watcher's next debounce
    }
}
