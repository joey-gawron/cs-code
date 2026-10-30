using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>The few things that need a real window: native dialogs, clipboard, launching URIs. Faked in tests.</summary>
public interface IShellHost
{
    ValueTask<IReadOnlyList<Uri>?> PickOpenAsync(OpenDialogOptions options, CancellationToken ct);
    ValueTask<Uri?> PickSaveAsync(SaveDialogOptions options, CancellationToken ct);
    ValueTask CopyTextAsync(string text);
    ValueTask<bool> LaunchAsync(Uri uri);
}

/// <summary>
/// <see cref="IWindowService"/> for the shell: non-modal toasts with action buttons plus a notification centre, command-palette-style
/// quick pick / input overlays (not OS dialogs), native file dialogs via the host, progress in the status bar and as a notification.
/// Every method may be called from any thread; the synchronous cost on the caller is one dispatcher post.
/// </summary>
public sealed class WindowService : IWindowService
{
    private readonly IUiDispatcher _ui;
    private readonly NotificationCenter _notifications;
    private readonly QuickInputViewModel _quick;
    private readonly IStatusBarService _status;
    private readonly IShellHost _host;
    private readonly TimeProvider _time;
    private readonly IClipboardService? _clipboard;
    private int _progressSeq;

    /// <param name="clipboard">When registered, copies go through it (clipboard ring); otherwise straight to the host's clipboard.</param>
    public WindowService(IUiDispatcher ui, NotificationCenter notifications, QuickInputViewModel quick, IStatusBarService status, IShellHost host, TimeProvider? time = null,
        IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
        _ui = ui;
        _notifications = notifications;
        _quick = quick;
        _status = status;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default)
    {
        var item = await _ui.InvokeAsync(() =>
        {
            var n = _notifications.Show(severity, message, actions);
            if (!n.HasActions) ScheduleDemote(n, severity);
            return n;
        }).ConfigureAwait(false);
        await using var reg = ct.Register(() => _ui.Post(() => _notifications.Complete(item, null))).ConfigureAwait(false);
        return await item.Result.ConfigureAwait(false);
    }

    private void ScheduleDemote(NotificationItem n, MessageSeverity severity)
    {
        var delay = severity switch { MessageSeverity.Error => TimeSpan.FromSeconds(20), MessageSeverity.Warning => TimeSpan.FromSeconds(12), _ => TimeSpan.FromSeconds(8) };
        var timer = _time.CreateTimer(_ => _ui.Post(() => _notifications.DemoteToast(n)), null, delay, Timeout.InfiniteTimeSpan);
        _ = n.Result.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
    }

    public async ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
    {
        var pending = await _ui.InvokeAsync(() => _quick.PickAsync(new StaticQuickPickSource(items, options), options?.Title, options?.Placeholder, "", ct)).ConfigureAwait(false);
        var entry = await pending.ConfigureAwait(false);
        return entry?.Tag as QuickPickItem;
    }

    public async ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default)
    {
        var pending = await _ui.InvokeAsync(() => _quick.PickManyAsync(new StaticQuickPickSource(items, options), options?.Title, options?.Placeholder, ct)).ConfigureAwait(false);
        var entries = await pending.ConfigureAwait(false);
        return entries?.Select(e => e.Tag).OfType<QuickPickItem>().ToArray();
    }

    public async ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default)
    {
        var pending = await _ui.InvokeAsync(() => _quick.InputAsync(options, ct)).ConfigureAwait(false);
        return await pending.ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => _host.PickOpenAsync(options, ct);

    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => _host.PickSaveAsync(options, ct);

    public async Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var cts = new CancellationTokenSource();
        var id = Interlocked.Increment(ref _progressSeq);
        var item = await _ui.InvokeAsync(() => _notifications.ShowProgress(title, cancellable, cts)).ConfigureAwait(false);
        using var statusItem = _status.Set(new StatusBarItemModel("progress." + id, "$(sync) " + title, Tooltip: title, Alignment: StatusBarAlignment.Left, Priority: 1000 - id % 100));
        var reporter = new ProgressReporter(_ui, item, id);
        try
        {
            return await work(reporter, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _ui.Post(() => _notifications.Complete(item, null));
        }
    }

    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null)
    {
        var reg = _status.Set(new StatusBarItemModel("status.message", text, Alignment: StatusBarAlignment.Left, Priority: int.MaxValue));
        if (timeout is { } t)
        {
            var timer = _time.CreateTimer(_ => reg.Dispose(), null, t, Timeout.InfiniteTimeSpan);
            return Disposable.Create(() => { timer.Dispose(); reg.Dispose(); });
        }
        return reg;
    }

    public ValueTask<bool> OpenExternalAsync(Uri uri) => _host.LaunchAsync(uri);

    public ValueTask CopyToClipboardAsync(string text) => _clipboard is { } clipboard ? clipboard.WriteAsync(ClipboardText.Plain(text)) : _host.CopyTextAsync(text);

    private sealed class ProgressReporter(IUiDispatcher ui, NotificationItem item, int id) : IProgressReporter
    {
        private string? _message;
        private double? _percent;

        public void Report(string? message = null, double? percent = null)
        {
            if (message is not null) Volatile.Write(ref _message, message);
            if (percent is not null) _percent = percent;
            // Latest-wins: a worker reporting 10k times a second costs at most one UI update per turn.
            ui.PostCoalesced("progress:" + id, () =>
            {
                item.ProgressMessage = Volatile.Read(ref _message);
                item.Progress = _percent;
            });
        }
    }
}
