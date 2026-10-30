using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// Keeps the server's workspace folders equal to the client's folders that live on the server (the ssh: folders of that host, or every local
/// folder for the process language host): its language servers get the same roots and its workspace settings are read there.
/// </summary>
public sealed class WorkspaceFoldersMirror : IRemoteParticipant, IDisposable
{
    private readonly IWorkspaceService? _workspace;
    private readonly UriMapper _mapper;
    private readonly ILogger _log;
    private readonly IDisposable _sub;
    private volatile RemoteSession? _session;

    public WorkspaceFoldersMirror(IWorkspaceService? workspace, UriMapper mapper, ILogger? log = null)
    {
        _workspace = workspace;
        _mapper = mapper;
        _log = log ?? NullLogger.Instance;
        _sub = workspace?.FoldersChanged.Subscribe(e => { if (_session is { IsOpen: true } s) _ = PushAsync(s, CancellationToken.None); }) ?? Disposable.Empty;
    }

    public void Attach(RpcPeer peer) { }

    public async Task OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        _session = session;
        await PushAsync(session, ct).ConfigureAwait(false);
    }

    public void OnDisconnected() => _session = null;

    private async Task PushAsync(RemoteSession session, CancellationToken ct)
    {
        var folders = (_workspace?.Folders ?? []).Where(_mapper.Handles).Select(_mapper.ToServerPath).OfType<string>().ToArray();
        try { await session.Peer.InvokeAsync<SetFoldersParams, Empty>(RemoteMethods.WorkspaceSetFolders, new SetFoldersParams(folders), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            _log.LogWarning("Could not set the server's workspace folders: {Error}", ex.Message);
        }
    }

    public void Dispose() => _sub.Dispose();
}

/// <summary>
/// User settings flow client → server: the client's settings.json text is sent on connect and again (debounced) whenever the settings change;
/// the server writes it as its own user settings file. (Workspace settings are read on the server, next to the files.) For a remote window the
/// server takes machine-scoped settings (the programs it runs) from its host's own machine settings file instead, as VS Code does.
/// </summary>
public sealed class UserSettingsMirror : IRemoteParticipant, IDisposable
{
    private readonly IConfigurationService? _config;
    private readonly IFileSystem? _fs;
    private readonly ILogger _log;
    private readonly IDisposable _sub;
    private readonly Timer _debounce;
    private volatile RemoteSession? _session;
    private string? _lastSent;
    private readonly bool _remoteWindow;

    /// <param name="remoteWindow">true for a remote host (ssh, docker, wsl); false for the local process language host, whose machine is the client's.</param>
    public UserSettingsMirror(IConfigurationService? config, IFileSystem? fs, ILogger? log = null, bool remoteWindow = false)
    {
        _remoteWindow = remoteWindow;
        _config = config;
        _fs = fs;
        _log = log ?? NullLogger.Instance;
        _debounce = new Timer(state => { if (_session is { IsOpen: true } s) _ = PushAsync(s, CancellationToken.None); }, null, Timeout.Infinite, Timeout.Infinite);
        _sub = config?.Changed.Subscribe(e => { try { _debounce.Change(300, Timeout.Infinite); } catch (ObjectDisposedException) { } }) ?? Disposable.Empty;
    }

    public void Attach(RpcPeer peer) { }

    public async Task OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        _session = session;
        _lastSent = null;
        await PushAsync(session, ct).ConfigureAwait(false);
    }

    public void OnDisconnected() => _session = null;

    internal async Task PushAsync(RemoteSession session, CancellationToken ct)
    {
        if (_config is null) return;
        try
        {
            var text = await ReadUserSettingsAsync(ct).ConfigureAwait(false);
            if (text == _lastSent) return;
            await session.Peer.InvokeAsync<SetUserSettingsParams, Empty>(RemoteMethods.SettingsSetUser, new SetUserSettingsParams(text, _remoteWindow), ct).ConfigureAwait(false);
            _lastSent = text;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not send the user settings to the server: {Error}", ex.Message);
        }
    }

    private async Task<string> ReadUserSettingsAsync(CancellationToken ct)
    {
        var path = _config!.UserSettingsPath;
        if (string.IsNullOrEmpty(path) || _fs is null) return "{}";
        var uri = FileUris.FromPath(path, OperatingSystem.IsWindows());
        if (await _fs.StatAsync(uri, ct).ConfigureAwait(false) is null) return "{}";
        return Encoding.UTF8.GetString(await _fs.ReadFileAsync(uri, ct).ConfigureAwait(false));
    }

    public void Dispose()
    {
        _sub.Dispose();
        _debounce.Dispose();
    }
}
