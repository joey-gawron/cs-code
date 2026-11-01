using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Protocol;
using CsCode.Remote.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// <see cref="IRemoteMachineSettings"/> for the connected host: its machine settings file is <c>&lt;server data dir&gt;/machine/settings.json</c>
/// (the data dir comes from the server's <c>hello</c>), reached through the remote file system like any other remote file. Writes go to the
/// server (<c>settings/updateMachine</c>), whose configuration service edits the file in place and applies it. The file's directory is watched
/// through the routing watcher while a host is connected, so an edit in the editor (or on the host) refreshes the Settings page's Remote tab.
/// </summary>
public sealed class RemoteMachineSettings : IRemoteMachineSettings, IDisposable
{
    private readonly RemoteConnectionManager _manager;
    private readonly Func<IFileWatcher?> _watcher;
    private readonly ILogger _log;
    private readonly Emitter<Uri?> _changed = new();
    private readonly IDisposable _sub;
    private readonly object _gate = new();
    private Uri? _uri;
    private string? _label;
    private IDisposable? _watch;
    private bool _disposed;

    public RemoteMachineSettings(RemoteConnectionManager manager, Func<IFileWatcher?> watcher, ILogger? log = null)
    {
        _manager = manager;
        _watcher = watcher;
        _log = log ?? NullLogger.Instance;
        _sub = manager.StatusChanged.Subscribe(OnStatus);
        OnStatus(manager.Status);
    }

    /// <summary>The machine settings file below a server data dir (the server's <c>ServerHost.MachineSettingsPath</c>, on a POSIX host).</summary>
    public static string PathIn(string dataDir) => dataDir.TrimEnd('/') + "/machine/settings.json";

    public Uri? SettingsUri { get { lock (_gate) return _uri; } }
    public string? HostLabel { get { lock (_gate) return _label; } }
    public IEventSource<Uri?> Changed => _changed;

    private void OnStatus(RemoteConnectionStatus status)
    {
        Uri? uri;
        string? label;
        switch (status.State)
        {
            case RemoteConnectionState.Connected:
                uri = _manager.Session is { } session ? _manager.ToUri(PathIn(session.Server.DataDir)) : null;
                label = uri is null ? null : RemoteShellContributions.Label(status);
                break;
            case RemoteConnectionState.Reconnecting:
                return; // the same host comes back: keep the file (and its watch, which the remote watcher re-arms)
            default:
                uri = null;
                label = null;
                break;
        }
        IDisposable? oldWatch = null;
        lock (_gate)
        {
            if (_disposed || (Equals(_uri, uri) && _label == label)) return;
            var moved = !Equals(_uri, uri);
            _uri = uri;
            _label = label;
            if (moved)
            {
                oldWatch = _watch;
                _watch = null;
            }
        }
        oldWatch?.Dispose();
        if (uri is not null) _ = Task.Run(() => ArmWatch(uri)); // the watch call reaches the server: never on the status thread
        _changed.Fire(uri);
    }

    private void ArmWatch(Uri file)
    {
        try
        {
            if (_watcher() is not { } watcher) return;
            var dir = new Uri(file, ".");
            var watch = watcher.Watch(dir, recursive: false, excludeGlobs: null, changes =>
            {
                if (changes.Any(c => c.Uri.AbsolutePath == file.AbsolutePath || c.Uri.AbsolutePath.TrimEnd('/') == dir.AbsolutePath.TrimEnd('/'))) _changed.Fire(file);
            });
            lock (_gate)
            {
                if (!_disposed && Equals(_uri, file) && _watch is null) { _watch = watch; return; }
            }
            watch.Dispose(); // disconnected (or another host) meanwhile
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            _log.LogInformation("The remote machine settings file is not watched ({Error}); the Settings page refreshes on its own writes only", ex.Message);
        }
    }

    public async ValueTask UpdateAsync(string key, object? value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (SettingsUri is null) throw new RemoteDisconnectedException("No remote host is connected: there are no remote settings to change");
        await new PoolHop(); // serialising the value and reaching the session never run on the caller (the Settings page's UI thread)
        var json = value switch
        {
            null => (JsonElement?)null,
            JsonElement e => e.ValueKind == JsonValueKind.Null ? null : e,
            _ => JsonSerializer.SerializeToElement(value, CoreJson.Options),
        };
        var session = await _manager.WaitForSessionAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        try
        {
            await session.Peer.InvokeAsync<UpdateMachineSettingParams, Empty>(RemoteMethods.SettingsUpdateMachine, new UpdateMachineSettingParams(key, json), ct)
                .ConfigureAwait(false);
        }
        catch (RemoteException ex) when (ex.Code is RpcErrorCodes.InternalError or RpcErrorCodes.InvalidParams)
        {
            throw new InvalidOperationException(ex.Message, ex); // refused by the server (an application-scoped key, a malformed file)
        }
        _changed.Fire(SettingsUri);
    }

    public void Dispose()
    {
        IDisposable? watch;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watch = _watch;
            _watch = null;
            _uri = null;
        }
        _sub.Dispose();
        watch?.Dispose();
    }
}
