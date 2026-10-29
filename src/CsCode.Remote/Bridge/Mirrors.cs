using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// Server diagnostics → the local <see cref="IDiagnosticService"/> under one owner per connection (squiggles, Error List). The server already coalesces
/// (latest state per document, 50 ms batches); this only maps and stores. Everything the connection published is cleared when it goes away.
/// </summary>
public sealed class DiagnosticsMirror(IDiagnosticService diagnostics, string owner) : IRemoteParticipant
{
    public string Owner { get; } = owner;

    public void Attach(RpcPeer peer) =>
        peer.OnNotification<DiagPublishParams>(RemoteMethods.DiagPublish, p =>
        {
            foreach (var e in p.Entries) diagnostics.Set(e.Uri, Owner, e.Diagnostics, e.Version);
        });

    public Task OnConnectedAsync(RemoteSession session, CancellationToken ct) => Task.CompletedTask;

    public void OnDisconnected() => diagnostics.Clear(Owner);
}

/// <summary>
/// Server-originated UI requests → the GUI's real services: <c>window/*</c> (messages with actions, quick picks, input boxes, progress, status
/// messages, open external, clipboard) through <see cref="IWindowService"/>, <c>nav/*</c> (show document, apply workspace edit) through
/// <see cref="INavigationService"/>, and server output channels into <see cref="IOutputService"/> (prefixed). Missing services answer "dismissed".
/// </summary>
public sealed class UiForwarding : IRemoteParticipant
{
    public const int MaxConcurrentProgress = 32;
    private readonly Func<IWindowService?> _window;
    private readonly Func<INavigationService?> _navigation;
    private readonly Func<IOutputService?> _output;
    private readonly string _outputPrefix;
    private readonly ConcurrentDictionary<string, ProgressState> _progress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _status = new(StringComparer.Ordinal);
    private volatile RpcPeer? _peer;

    public UiForwarding(Func<IWindowService?> window, Func<INavigationService?> navigation, Func<IOutputService?> output, string outputPrefix)
    {
        _window = window;
        _navigation = navigation;
        _output = output;
        _outputPrefix = outputPrefix;
    }

    private sealed class ProgressState
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgressReporter? Reporter;
        public (string? Message, double? Percent)? Pending;
    }

    public void Attach(RpcPeer peer)
    {
        _peer = peer;
        peer.OnRequest<ShowMessageParams, ShowMessageResult>(RemoteMethods.WindowShowMessage, async (p, ct) =>
            new ShowMessageResult(_window() is { } w ? await w.ShowMessageAsync(p.Severity, p.Message, p.Actions, ct).ConfigureAwait(false) : null));
        peer.OnRequest<QuickPickParams, QuickPickResult>(RemoteMethods.WindowQuickPick, async (p, ct) =>
        {
            if (_window() is not { } w) return new QuickPickResult(null);
            var items = p.Items.Select((i, n) => new QuickPickItem(i.Label, i.Description, i.Detail, i.Picked, n, i.IsSeparator)).ToArray();
            if (p.Many)
            {
                var many = await w.ShowQuickPickManyAsync(items, p.Options, ct).ConfigureAwait(false);
                return new QuickPickResult(many?.Select(i => (int)i.Tag!).ToArray());
            }
            var one = await w.ShowQuickPickAsync(items, p.Options, ct).ConfigureAwait(false);
            return new QuickPickResult(one is null ? null : [(int)one.Tag!]);
        });
        peer.OnRequest<WireInputBoxOptions, InputBoxResult>(RemoteMethods.WindowInputBox, async (p, ct) =>
            new InputBoxResult(_window() is { } w ? await w.ShowInputBoxAsync(new InputBoxOptions(p.Title, p.Prompt, p.Placeholder, p.Value, p.Password), ct).ConfigureAwait(false) : null));
        peer.OnRequest<OpenExternalParams, BoolResult>(RemoteMethods.WindowOpenExternal, async (p, _) =>
            new BoolResult(_window() is { } w && await w.OpenExternalAsync(p.Uri).ConfigureAwait(false)));
        peer.OnRequest<TextParams, Empty>(RemoteMethods.WindowClipboard, async (p, _) =>
        {
            if (_window() is { } w) await w.CopyToClipboardAsync(p.Text).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnNotification<StatusParams>(RemoteMethods.WindowStatus, OnStatus);
        peer.OnNotification<ProgressParams>(RemoteMethods.WindowProgress, OnProgress);
        peer.OnRequest<NavOpenParams, Empty>(RemoteMethods.NavOpen, async (p, ct) =>
        {
            if (_navigation() is { } n) await n.OpenAsync(p.Uri, p.Selection, p.Preview, p.PreserveFocus, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnRequest<NavApplyEditParams, BoolResult>(RemoteMethods.NavApplyEdit, async (p, ct) =>
            new BoolResult(_navigation() is { } n && await n.ApplyWorkspaceEditAsync(p.Edit, p.Label, ct).ConfigureAwait(false)));
        peer.OnNotification<OutputAppendParams>(RemoteMethods.OutputAppend, p => _output()?.GetOrCreate(_outputPrefix + p.Channel).Append(p.Text));
    }

    private void OnStatus(StatusParams p)
    {
        if (_status.TryRemove(p.Id, out var old)) old.Dispose();
        if (p.Text is null || _window() is not { } w) return;
        if (_status.Count >= 64) return; // a server leaking status messages cannot grow this
        _status[p.Id] = w.SetStatusBarMessage(p.Text, p.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(p.TimeoutMs) : null);
    }

    private void OnProgress(ProgressParams p)
    {
        switch (p.Kind)
        {
            case "begin":
                if (_progress.Count >= MaxConcurrentProgress || _window() is not { } w) return;
                var state = new ProgressState();
                if (!_progress.TryAdd(p.Id, state)) return;
                _ = w.WithProgressAsync(p.Title ?? "", p.Cancellable, async (reporter, ct) =>
                {
                    state.Reporter = reporter;
                    if (state.Pending is { } pending) reporter.Report(pending.Message, pending.Percent);
                    using var reg = ct.Register(() => _peer?.Notify(RemoteMethods.WindowProgressCancel, new ProgressCancelParams(p.Id)));
                    await state.Done.Task.ConfigureAwait(false);
                    return true;
                });
                break;
            case "report":
                if (!_progress.TryGetValue(p.Id, out var s)) return;
                if (s.Reporter is { } r) r.Report(p.Message, p.Percent);
                else s.Pending = (p.Message, p.Percent);
                break;
            case "end":
                if (_progress.TryRemove(p.Id, out var e)) e.Done.TrySetResult();
                break;
        }
    }

    public Task OnConnectedAsync(RemoteSession session, CancellationToken ct) => Task.CompletedTask;

    public void OnDisconnected()
    {
        foreach (var id in _progress.Keys.ToArray()) if (_progress.TryRemove(id, out var s)) s.Done.TrySetResult();
        foreach (var id in _status.Keys.ToArray()) if (_status.TryRemove(id, out var d)) d.Dispose();
    }
}

/// <summary>
/// Commands the language servers contributed on the server (code actions and lenses carry them) are registered locally as forwarders to
/// <c>commands/execute</c>, unless a local command of that id exists. They go away with the connection.
/// </summary>
public sealed class CommandsBridge(Func<ICommandService?> commands, ILogger? log = null) : IRemoteParticipant
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<string, IDisposable> _registered = new(StringComparer.Ordinal);
    private volatile RpcPeer? _peer;

    public int Count => _registered.Count;

    public void Attach(RpcPeer peer)
    {
        _peer = peer;
        peer.OnNotification<CommandsChangedParams>(RemoteMethods.CommandsChanged, OnChanged);
    }

    private void OnChanged(CommandsChangedParams p)
    {
        if (commands() is not { } service) return;
        var wanted = p.Commands.ToHashSet(StringComparer.Ordinal);
        foreach (var id in _registered.Keys.Where(id => !wanted.Contains(id)).ToArray())
            if (_registered.TryRemove(id, out var t)) t.Dispose();
        foreach (var id in wanted)
        {
            if (_registered.ContainsKey(id) || service.Has(id)) continue;
            _registered[id] = service.Register(id, inv => ExecuteAsync(id, inv), new CommandMetadata(id, Category: "Remote"));
        }
    }

    private async ValueTask<object?> ExecuteAsync(string id, CommandInvocation invocation)
    {
        if (_peer is not { IsClosed: false } peer) throw new RemoteDisconnectedException($"'{id}' runs on the remote, which is not connected");
        var args = invocation.Args.Select(a => a is JsonElement je ? je : JsonSerializer.SerializeToElement(a, peer.Json)).ToArray();
        var r = await peer.InvokeAsync<ExecuteCommandParams, ExecuteCommandResult>(RemoteMethods.CommandsExecute, new ExecuteCommandParams(id, args), invocation.Cancellation)
            .ConfigureAwait(false);
        return r?.Value;
    }

    public Task OnConnectedAsync(RemoteSession session, CancellationToken ct) => Task.CompletedTask;

    public void OnDisconnected()
    {
        foreach (var id in _registered.Keys.ToArray())
            if (_registered.TryRemove(id, out var t))
            {
                try { t.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "Unregistering {Command} failed", id); }
            }
    }
}

/// <summary>
/// Forwards the shell's will-save and file-operation calls for documents/files of a connection to the server's language servers (bounded: a
/// slow server delays a save by at most <see cref="WillSaveTimeout"/>).
/// </summary>
public sealed class RemoteSaveParticipant(Func<RemoteSession?> session) : IWillSaveParticipant, IFileOperationParticipant
{
    public static readonly TimeSpan WillSaveTimeout = TimeSpan.FromMilliseconds(1500);

    private RpcPeer? PeerFor(Uri uri) => session() is { IsOpen: true } s && s.Mapper.Handles(uri) ? s.Peer : null;

    public async ValueTask<IReadOnlyList<Core.Text.TextEdit>?> OnWillSaveAsync(Core.Text.ITextDocument document, DocumentSaveReason reason, CancellationToken ct)
    {
        if (PeerFor(document.Uri) is not { } peer) return null;
        try
        {
            return await peer.InvokeAsync<WillSaveParams, IReadOnlyList<Core.Text.TextEdit>>(RemoteMethods.LangWillSave, new WillSaveParams(document.Uri, reason), ct, WillSaveTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return null; }
    }

    public ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) =>
        CallAsync(new FileOperationParams("willRename", Renames: Mine(files)), ct);
    public async ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) =>
        await CallAsync(new FileOperationParams("didRename", Renames: Mine(files)), ct).ConfigureAwait(false);
    public async ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) =>
        await CallAsync(new FileOperationParams("didCreate", Files: Mine(files)), ct).ConfigureAwait(false);
    public async ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) =>
        await CallAsync(new FileOperationParams("didDelete", Files: Mine(files)), ct).ConfigureAwait(false);

    private IReadOnlyList<(Uri, Uri)>? Mine(IReadOnlyList<(Uri OldUri, Uri NewUri)> files) =>
        session() is { IsOpen: true } s ? files.Where(f => s.Mapper.Handles(f.OldUri) && s.Mapper.Handles(f.NewUri)).Select(f => (f.OldUri, f.NewUri)).ToArray() : null;

    private IReadOnlyList<Uri>? Mine(IReadOnlyList<Uri> files) =>
        session() is { IsOpen: true } s ? files.Where(s.Mapper.Handles).ToArray() : null;

    private async ValueTask<WorkspaceEdit?> CallAsync(FileOperationParams p, CancellationToken ct)
    {
        if ((p.Renames?.Count ?? 0) == 0 && (p.Files?.Count ?? 0) == 0) return null;
        if (session() is not { IsOpen: true } s) return null;
        try { return await s.Peer.InvokeAsync<FileOperationParams, WorkspaceEdit>(RemoteMethods.LangFileOperation, p, ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return null; }
    }
}
