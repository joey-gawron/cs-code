using System.Collections.Concurrent;
using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Server;

/// <summary>
/// The server-side stand-ins for the GUI services the language servers talk to: window prompts/progress/status, navigation (show document,
/// apply workspace edit) and output channels are forwarded to the client, which shows them through its real services. Registered over the
/// defaults so the Lsp module needs no knowledge of being remote.
/// </summary>
internal static class ClientServices
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<MirroringFeatureRegistry>();
        services.AddSingleton<ILanguageFeatureRegistry>(sp => sp.GetRequiredService<MirroringFeatureRegistry>());
        services.AddSingleton<ClientWindowService>();
        services.AddSingleton<IWindowService>(sp => sp.GetRequiredService<ClientWindowService>());
        services.AddSingleton<INavigationService, ClientNavigationService>();
        services.AddSingleton<IOutputService, ForwardingOutputService>();
    }
}

/// <summary>Forwards <see cref="IWindowService"/> to the client (<c>window/*</c>). Dialogs that would show client-side paths are not offered (null).</summary>
internal sealed class ClientWindowService : IWindowService, IDisposable
{
    private static readonly TimeSpan UserWait = TimeSpan.FromMinutes(30);
    private readonly RpcPeer _peer;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _progress = new(StringComparer.Ordinal);

    public ClientWindowService(RpcPeer peer)
    {
        _peer = peer;
        peer.OnNotification<ProgressCancelParams>(RemoteMethods.WindowProgressCancel, p =>
        {
            if (_progress.TryGetValue(p.Id, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        });
    }

    public async ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) =>
        (await _peer.InvokeAsync<ShowMessageParams, ShowMessageResult>(RemoteMethods.WindowShowMessage, new ShowMessageParams(severity, message, actions), ct, UserWait).ConfigureAwait(false))?.Action;

    public async ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
    {
        var picked = await PickAsync(items, options, false, ct).ConfigureAwait(false);
        return picked is [var first, ..] ? first : null;
    }

    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) =>
        PickAsync(items, options, true, ct);

    private async ValueTask<IReadOnlyList<QuickPickItem>?> PickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options, bool many, CancellationToken ct)
    {
        var wire = items.Select(i => new WireQuickPickItem(i.Label, i.Description, i.Detail, i.Picked, i.IsSeparator)).ToArray();
        var r = await _peer.InvokeAsync<QuickPickParams, QuickPickResult>(RemoteMethods.WindowQuickPick, new QuickPickParams(wire, options, many), ct, UserWait).ConfigureAwait(false);
        if (r?.Selected is null) return null;
        return r.Selected.Where(i => i >= 0 && i < items.Count).Select(i => items[i]).ToArray(); // items keep their Tag: it never left this process
    }

    public async ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default)
    {
        var o = options ?? new InputBoxOptions();
        // Validate is a delegate: it cannot cross the wire, so the answer is validated here and the box shown again with the message as prompt.
        var prompt = o.Prompt;
        while (true)
        {
            var r = await _peer.InvokeAsync<WireInputBoxOptions, InputBoxResult>(RemoteMethods.WindowInputBox,
                new WireInputBoxOptions(o.Title, prompt, o.Placeholder, o.Value, o.Password), ct, UserWait).ConfigureAwait(false);
            if (r?.Value is not { } value) return null;
            if (o.Validate?.Invoke(value) is not { } error) return value;
            prompt = error;
        }
    }

    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);

    public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        using var cts = new CancellationTokenSource();
        _progress[id] = cts;
        _peer.Notify(RemoteMethods.WindowProgress, new ProgressParams(id, "begin", title, cancellable));
        try { return await work(new Reporter(_peer, id), cts.Token).ConfigureAwait(false); }
        finally
        {
            _progress.TryRemove(id, out _);
            _peer.Notify(RemoteMethods.WindowProgress, new ProgressParams(id, "end"));
        }
    }

    private sealed class Reporter(RpcPeer peer, string id) : IProgressReporter
    {
        public void Report(string? message = null, double? percent = null) =>
            peer.Notify(RemoteMethods.WindowProgress, new ProgressParams(id, "report", Message: message, Percent: percent));
    }

    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _peer.Notify(RemoteMethods.WindowStatus, new StatusParams(id, text, timeout is { } t ? (int)t.TotalMilliseconds : 0));
        return Disposable.Create(() => _peer.Notify(RemoteMethods.WindowStatus, new StatusParams(id, null, 0)));
    }

    public async ValueTask<bool> OpenExternalAsync(Uri uri) =>
        (await _peer.InvokeAsync<OpenExternalParams, BoolResult>(RemoteMethods.WindowOpenExternal, new OpenExternalParams(uri)).ConfigureAwait(false))?.Value ?? false;

    public async ValueTask CopyToClipboardAsync(string text) =>
        await _peer.InvokeAsync<TextParams, Empty>(RemoteMethods.WindowClipboard, new TextParams(text)).ConfigureAwait(false);

    public void Dispose()
    {
        foreach (var cts in _progress.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }
}

/// <summary>Forwards "show document" and "apply workspace edit" to the client's editor. There are no editors on the server.</summary>
internal sealed class ClientNavigationService(RpcPeer peer) : INavigationService
{
    private readonly Emitter<IEditorDocument?> _active = new();

    public async ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default)
    {
        await peer.InvokeAsync<NavOpenParams, Empty>(RemoteMethods.NavOpen, new NavOpenParams(uri, selection, preview, preserveFocus), ct).ConfigureAwait(false);
        return null;
    }

    public IEditorDocument? ActiveDocument => null;
    public TextPosition? ActiveCaret => null;
    public IEventSource<IEditorDocument?> ActiveDocumentChanged => _active;
    public IReadOnlyList<IEditorDocument> OpenDocuments => [];

    public async ValueTask<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit, string? label = null, CancellationToken ct = default) =>
        (await peer.InvokeAsync<NavApplyEditParams, BoolResult>(RemoteMethods.NavApplyEdit, new NavApplyEditParams(edit, label), ct, TimeSpan.FromMinutes(30)).ConfigureAwait(false))?.Value ?? false;
}

/// <summary>
/// Output channels (language server logs): kept in bounded rings here and forwarded to the client, coalesced every 250 ms and capped at 64 KB per
/// channel per flush (the rest is replaced by a marker), so a flooding server cannot flood the connection.
/// </summary>
internal sealed class ForwardingOutputService : IOutputService, IDisposable
{
    private const int MaxFlushChars = 64 * 1024;
    private readonly RpcPeer _peer;
    private readonly ConcurrentDictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly Emitter<string> _changed = new();
    private readonly Timer _timer;

    public ForwardingOutputService(RpcPeer peer)
    {
        _peer = peer;
        _timer = new Timer(_ => Flush(), null, 250, 250);
    }

    public IReadOnlyList<string> ChannelNames => _channels.Keys.ToArray();
    public IEventSource<string> ChannelsChanged => _changed;

    public IOutputChannel GetOrCreate(string name)
    {
        var created = false;
        var c = _channels.GetOrAdd(name, n => { created = true; return new Channel(n); });
        if (created) _changed.Fire(name);
        return c;
    }

    private void Flush()
    {
        foreach (var c in _channels.Values)
            if (c.TakePending() is { Length: > 0 } text) _peer.Notify(RemoteMethods.OutputAppend, new OutputAppendParams(c.Name, text));
    }

    public void Dispose() => _timer.Dispose();

    private sealed class Channel(string name) : IOutputChannel
    {
        private const int RingChars = 256 * 1024;
        private readonly object _gate = new();
        private readonly StringBuilder _ring = new();
        private readonly StringBuilder _pending = new();
        private bool _truncated;
        private readonly Emitter<string> _appended = new();

        public string Name { get; } = name;
        public IEventSource<string> Appended => _appended;

        public void Append(string text)
        {
            lock (_gate)
            {
                _ring.Append(text);
                if (_ring.Length > RingChars) _ring.Remove(0, _ring.Length - RingChars / 2);
                if (_pending.Length + text.Length <= MaxFlushChars) _pending.Append(text);
                else _truncated = true;
            }
            _appended.Fire(text);
        }

        public void AppendLine(string line) => Append(line + "\n");
        public void Clear() { lock (_gate) _ring.Clear(); }
        public string GetText() { lock (_gate) return _ring.ToString(); }

        public string? TakePending()
        {
            lock (_gate)
            {
                if (_pending.Length == 0 && !_truncated) return null;
                var text = _pending.ToString() + (_truncated ? "\n[… output truncated by the remote server …]\n" : "");
                _pending.Clear();
                _truncated = false;
                return text;
            }
        }

        public void Dispose() { }
    }
}
