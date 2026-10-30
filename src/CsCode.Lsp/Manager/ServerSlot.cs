using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;
using Microsoft.Extensions.Logging;

namespace CsCode.Lsp.Manager;

/// <summary>
/// The long-lived identity "server X for root Y": supervises successive <see cref="LanguageClient"/> sessions. Owns the restart policy
/// (exponential backoff, at most N crashes per window, then Faulted with ONE message offering "Restart"), the memory-cap policy (at most
/// <see cref="MaxMemoryRestarts"/> restarts, each announced, then the cap is no longer enforced and ONE message says so: T39), the set of documents
/// that belong to this server (so a restarted session re-opens them), and the published <see cref="LanguageServerInfo"/>.
/// </summary>
internal sealed class ServerSlot(LanguageServerManager manager, ClientContext ctx, LanguageServerDefinition definition, string key, Uri? rootUri, IReadOnlyList<Uri> folders) : IClientObserver, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<Uri, ITextDocument> _documents = new();
    private readonly List<DateTimeOffset> _crashes = new();
    private LanguageClient? _client;
    private CancellationTokenSource? _restartCts;
    private volatile LanguageServerState _state = LanguageServerState.Stopped;
    private string? _lastError;
    private string? _serverName, _serverVersion;
    private int? _pid;
    private bool _userStopped, _autoStartTried, _faultMessageShown, _notFoundShown, _disposed;
    private int _memoryRestarts;      // automatic restarts for the memory cap since the user last (re)started the server or changed its cap
    private bool _memoryCapSuspended; // the cap fired again after MaxMemoryRestarts restarts: no longer enforced until then

    /// <summary>
    /// T39: how many times the memory cap restarts a server before it stops enforcing the cap. The cap is compared with the RSS of the whole
    /// process tree, which counts more than the V8 heap VS Code limits; a project whose normal size is above it would otherwise be killed and
    /// restarted forever (each restart re-indexes the project). A manual restart, or a change of <c>restartAboveBytes</c>, re-arms it.
    /// </summary>
    internal const int MaxMemoryRestarts = 3;

    public LanguageServerDefinition Definition { get; private set; } = definition;
    public string Key { get; } = key;
    public Uri? RootUri { get; } = rootUri;
    public IReadOnlyList<Uri> Folders { get; private set; } = folders;
    public LanguageServerState State => _state;
    public LanguageClient? Client => Volatile.Read(ref _client);
    public int DocumentCount { get { lock (_gate) return _documents.Count; } }
    public int CrashCountInWindow { get { lock (_gate) return _crashes.Count; } }
    public int MemoryRestartCount { get { lock (_gate) return _memoryRestarts; } }
    public bool MemoryCapSuspended { get { lock (_gate) return _memoryCapSuspended; } }

    /// <summary>The cap a new session is tracked with: the definition's, or none once the slot stopped enforcing it.</summary>
    private long? EffectiveMemoryCap { get { lock (_gate) return _memoryCapSuspended ? null : Definition.RestartAboveBytes; } }

    public LanguageServerInfo Info
    {
        get
        {
            lock (_gate) return new LanguageServerInfo(Definition.Id, Definition.DisplayName, _state, RootUri, _pid, _serverName, _serverVersion, _lastError);
        }
    }

    private LspSettings Settings => ctx.Settings();

    private void SetState(LanguageServerState state, string? error = null, bool keepError = false)
    {
        lock (_gate)
        {
            _state = state;
            if (!keepError) _lastError = error;
            var c = _client;
            _pid = c?.ProcessId;
            _serverName = c?.ServerName ?? _serverName;
            _serverVersion = c?.ServerVersion ?? _serverVersion;
        }
        manager.OnSlotChanged();
    }

    // ---- documents ---------------------------------------------------------------------------------------------

    public void Attach(ITextDocument doc)
    {
        LanguageClient? client;
        bool autoStart;
        lock (_gate)
        {
            if (_disposed) return;
            _documents[doc.Uri] = doc;
            client = _client;
            autoStart = !_userStopped && !_autoStartTried && Definition.AutoStart && _state == LanguageServerState.Stopped;
            if (autoStart) _autoStartTried = true;
        }
        client?.AttachDocument(doc);
        if (autoStart) _ = StartAsync(user: false).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Detach(Uri uri)
    {
        LanguageClient? client;
        lock (_gate)
        {
            _documents.Remove(uri);
            client = _client;
        }
        client?.DetachDocument(uri);
    }

    public void OnChanged(TextDocumentChangedEventArgs args) => Volatile.Read(ref _client)?.OnChanged(args);
    public void OnSaved(ITextDocument doc) => Volatile.Read(ref _client)?.OnSaved(doc);

    // ---- lifecycle ---------------------------------------------------------------------------------------------

    // Public lifecycle calls hop to the Language lane first: process creation / teardown is blocking work that must never run on the thread
    // that called us (the shell may well be on the UI thread). The caller pays one enqueue.
    public Task StartAsync(bool user = true, CancellationToken ct = default) => OnLaneAsync(() => StartInnerAsync(user, ct));
    public Task StopAsync(CancellationToken ct = default) => OnLaneAsync(() => StopInnerAsync(ct));
    public Task RestartAsync(CancellationToken ct = default) => OnLaneAsync(() => RestartInnerAsync(ct));

    private Task OnLaneAsync(Func<Task> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try
            {
                work().ContinueWith(static (t, state) =>
                {
                    var source = (TaskCompletionSource)state!;
                    if (t.IsFaulted) source.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) source.TrySetCanceled();
                    else source.TrySetResult();
                }, tcs, TaskScheduler.Default);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        if (!ctx.Queue.Post(LanePriority.Interactive, Run, exempt: true)) ThreadPool.UnsafeQueueUserWorkItem(static r => r(), (Action)Run, preferLocal: false); // queue already disposed
        return tcs.Task;
    }

    private async Task StartInnerAsync(bool user, CancellationToken ct)
    {
        if (user)
        {
            lock (_gate) { _userStopped = false; _autoStartTried = true; _crashes.Clear(); _faultMessageShown = false; _notFoundShown = false; ResetMemoryPolicy(); }
            CancelRestartTimer(); // an explicit start supersedes a pending automatic restart (but an automatic restart must not cancel itself)
        }
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || _userStopped) return;
            if (_client is { IsOperational: true } || _state == LanguageServerState.Running) return;
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        SetState(LanguageServerState.Starting, keepError: true);
        var client = new LanguageClient(ctx, Definition, Key, RootUri, Folders, this);
        client.SetMemoryCap(EffectiveMemoryCap);
        lock (_gate)
        {
            _client = client;
            foreach (var d in _documents.Values) client.AttachDocument(d);
        }
        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_client, client)) return; // the session already ended (OnExited handled it)
                _faultMessageShown = false;
                _notFoundShown = false;
            }
            SetState(LanguageServerState.Running);
        }
        catch (ServerNotFoundException ex)
        {
            await DiscardClientAsync(client).ConfigureAwait(false);
            var hint = BuiltinLanguageServers.InstallHint(Definition.Id);
            SetState(LanguageServerState.Stopped, $"'{ex.Command}' was not found. {hint}");
            ShowNotFoundOnce(ex.Command, hint);
        }
        catch (OperationCanceledException)
        {
            await DiscardClientAsync(client).ConfigureAwait(false);
            SetState(LanguageServerState.Stopped);
        }
        catch (Exception ex)
        {
            await DiscardClientAsync(client).ConfigureAwait(false);
            HandleCrash(ex.Message);
        }
    }

    private async Task DiscardClientAsync(LanguageClient client)
    {
        lock (_gate) { if (ReferenceEquals(_client, client)) _client = null; }
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task StopInnerAsync(CancellationToken ct)
    {
        lock (_gate) _userStopped = true;
        CancelRestartTimer();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        LanguageClient? client;
        lock (_gate) { client = _client; _client = null; }
        if (client is null)
        {
            if (_state != LanguageServerState.Stopped) SetState(LanguageServerState.Stopped);
            return;
        }
        SetState(LanguageServerState.Stopping, keepError: true);
        try
        {
            await client.StopAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally { SetState(LanguageServerState.Stopped, keepError: true); }
    }

    private async Task RestartInnerAsync(CancellationToken ct)
    {
        CancelRestartTimer();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            lock (_gate) { _userStopped = false; _autoStartTried = true; _crashes.Clear(); _faultMessageShown = false; _notFoundShown = false; ResetMemoryPolicy(); }
            if (_disposed) return;
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>New effective definition (settings changed): restart a running server only if what it was launched with differs.</summary>
    public void UpdateDefinition(LanguageServerDefinition updated, IReadOnlyList<Uri> folders)
    {
        bool restart;
        LanguageClient? recap = null;
        lock (_gate)
        {
            restart = !DefinitionOverrides.SameLaunch(Definition, updated) && _state is LanguageServerState.Running or LanguageServerState.Starting or LanguageServerState.Unresponsive;
            if (Definition.RestartAboveBytes != updated.RestartAboveBytes)
            {
                ResetMemoryPolicy(); // a new cap starts with a clean count and is enforced again
                recap = _client;
            }
            Definition = updated;
            Folders = folders;
        }
        if (restart) _ = RestartAsync().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        else recap?.SetMemoryCap(updated.RestartAboveBytes); // the running session is tracked with the new cap at once
    }

    private void ResetMemoryPolicy()
    {
        _memoryRestarts = 0;
        _memoryCapSuspended = false;
    }

    // ---- crash handling ----------------------------------------------------------------------------------------

    public void OnExited(LanguageClient client, ClientExit exit)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_client, client)) return; // an old session
            _client = null;
        }
        _ = Task.Run(async () =>
        {
            try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
            HandleCrash(exit.Reason ?? "the server process exited");
        });
    }

    private void HandleCrash(string reason)
    {
        int count;
        var s = Settings;
        lock (_gate)
        {
            if (_userStopped || _disposed) { _state = LanguageServerState.Stopped; return; }
            var now = DateTimeOffset.UtcNow;
            _crashes.RemoveAll(t => now - t > s.RestartWindow);
            _crashes.Add(now);
            count = _crashes.Count;
        }
        if (count > s.MaxRestarts)
        {
            SetState(LanguageServerState.Faulted, reason);
            ShowFaultOnce(reason);
            return;
        }
        var delay = s.RestartBackoff[Math.Min(count - 1, s.RestartBackoff.Length - 1)];
        SetState(LanguageServerState.Starting, reason); // "restarting": the last error stays visible
        ScheduleRestart(delay);
    }

    private void ScheduleRestart(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await StartAsync(user: false, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ctx.Logger.LogErrorSafe(ex); }
        });
    }

    private void CancelRestartTimer()
    {
        lock (_gate)
        {
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = null;
        }
    }

    // ---- user-facing messages (one each, never a storm) ----------------------------------------------------------

    private void ShowFaultOnce(string reason)
    {
        lock (_gate) { if (_faultMessageShown) return; _faultMessageShown = true; }
        if (ctx.Services.Window is not { } window) return;
        _ = Task.Run(async () =>
        {
            var chosen = await window.ShowMessageAsync(MessageSeverity.Error,
                $"{Definition.DisplayName} keeps crashing and was stopped ({reason}).", [new MessageAction("Restart")]).ConfigureAwait(false);
            if (chosen == "Restart") await RestartAsync().ConfigureAwait(false);
        });
    }

    private void ShowNotFoundOnce(string command, string hint)
    {
        lock (_gate) { if (_notFoundShown) return; _notFoundShown = true; }
        _ = ctx.Services.Window?.ShowMessageAsync(MessageSeverity.Info, $"{Definition.DisplayName}: language server '{command}' was not found. {hint}").AsTask();
    }

    // ---- IClientObserver ----------------------------------------------------------------------------------------

    public void OnHealthChanged(LanguageClient client, ServerHealth health)
    {
        if (!ReferenceEquals(Volatile.Read(ref _client), client)) return;
        if (health == ServerHealth.Unresponsive)
        {
            SetState(LanguageServerState.Unresponsive, "Unresponsive: consecutive requests timed out");
            ctx.Services.Window?.SetStatusBarMessage($"{Definition.DisplayName} is not responding", TimeSpan.FromSeconds(8));
        }
        else SetState(LanguageServerState.Running);
    }

    /// <summary>
    /// T39: the monitor saw the session's process tree above its cap. The first <see cref="MaxMemoryRestarts"/> times the server is restarted (a
    /// status-bar message and a log line say so); the crash history is kept, so this never resets the crash policy. The next time the cap is no
    /// longer enforced: the server keeps running and one warning says why, offering a restart (which re-arms the cap).
    /// </summary>
    public void OnMemoryCapExceeded(LanguageClient client, long cap)
    {
        if (!ReferenceEquals(Volatile.Read(ref _client), client)) return;
        _ = OnLaneAsync(() => RestartForMemoryAsync(client, cap)).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task RestartForMemoryAsync(LanguageClient client, long cap)
    {
        int count;
        lock (_gate)
        {
            if (_disposed || _userStopped || _memoryCapSuspended || !ReferenceEquals(_client, client)) return;
            if (_memoryRestarts >= MaxMemoryRestarts) { _memoryCapSuspended = true; count = 0; }
            else count = ++_memoryRestarts;
        }
        var capText = FormatBytes(cap);
        if (count == 0)
        {
            client.SetMemoryCap(null);
            client.Log.Add($"[client] the process tree is above its {capText} memory cap again after {MaxMemoryRestarts} restarts: the cap is no longer enforced until the server is restarted");
            ShowMemoryCapSuspended(capText);
            return;
        }
        client.Log.Add($"[client] the process tree is above its {capText} memory cap: restarting the server ({count} of {MaxMemoryRestarts})");
        ctx.Services.Window?.SetStatusBarMessage($"{Definition.DisplayName} used more than its {capText} memory cap and was restarted", TimeSpan.FromSeconds(10));
        CancelRestartTimer();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _userStopped || !ReferenceEquals(_client, client)) return; // stopped, restarted or crashed meanwhile
            await StopCoreAsync().ConfigureAwait(false);
            if (_disposed || _userStopped) return;
            await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private void ShowMemoryCapSuspended(string capText)
    {
        if (ctx.Services.Window is not { } window) return;
        var message = $"{Definition.DisplayName} keeps going over its {capText} memory cap (restarted {MaxMemoryRestarts} times). It keeps running and the cap "
            + $"is no longer enforced until you restart it. To change the cap, set \"{LspSettingKeys.LanguageServers}\": {{ \"{Definition.Id}\": {{ \"restartAboveBytes\": … }} }} (null turns it off).";
        _ = Task.Run(async () =>
        {
            var chosen = await window.ShowMessageAsync(MessageSeverity.Warning, message, [new MessageAction("Restart")]).ConfigureAwait(false);
            if (chosen == "Restart") await RestartAsync().ConfigureAwait(false);
        });
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 && bytes % (1L << 30) == 0 ? $"{bytes >> 30} GiB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MiB"
        : $"{bytes} bytes";

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; _userStopped = true; }
        CancelRestartTimer();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        lock (_gate) _documents.Clear();
    }
}

internal static class LoggerExtensions
{
    public static void LogErrorSafe(this Microsoft.Extensions.Logging.ILogger logger, Exception ex)
    {
        try { logger.LogError(ex, "Language server supervision failed"); }
        catch (Exception) { /* logging must never throw into a supervisor */ }
    }
}
