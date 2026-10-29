using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Platform;
using CsCode.Ui.Collections;

namespace CsCode.App.Notifications;

/// <summary>One notification (toast and notification-centre row). UI-thread object; all mutation goes through <see cref="NotificationCenter"/>.</summary>
public sealed class NotificationItem : ObservableObject
{
    private double? _progress;
    private string? _progressMessage;
    private bool _isToast = true;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal NotificationItem(MessageSeverity severity, string message, IReadOnlyList<MessageAction> actions, bool isProgress, NotificationCenter owner)
    {
        Severity = severity;
        Message = message;
        Actions = actions;
        IsProgress = isProgress;
        Owner = owner;
        InvokeCommand = new RelayCommand<MessageAction>(a => Owner.Complete(this, a?.Title));
        DismissCommand = new RelayCommand(() => Owner.Complete(this, null));
        CancelCommand = new RelayCommand(RequestCancel);
    }

    internal NotificationCenter Owner { get; }
    public MessageSeverity Severity { get; }
    public string Message { get; }
    public IReadOnlyList<MessageAction> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public bool IsProgress { get; }
    public bool IsCompleted => _result.Task.IsCompleted;
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;

    /// <summary>Progress in percent (0-100) or null for indeterminate.</summary>
    public double? Progress { get => _progress; internal set => SetProperty(ref _progress, value); }
    public string? ProgressMessage { get => _progressMessage; internal set => SetProperty(ref _progressMessage, value); }
    /// <summary>True while it is shown as a popup toast (false once it only lives in the notification centre list).</summary>
    public bool IsToast { get => _isToast; internal set => SetProperty(ref _isToast, value); }

    public bool CanCancel { get; internal init; }
    public CancellationTokenSource? Cancellation { get; internal init; }

    public IRelayCommand<MessageAction> InvokeCommand { get; }
    public IRelayCommand DismissCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>Completes with the chosen action title, or null when dismissed.</summary>
    public Task<string?> Result => _result.Task;
    internal bool TrySetResult(string? value) => _result.TrySetResult(value);

    private void RequestCancel()
    {
        Cancellation?.Cancel();
        Owner.Complete(this, null);
    }
}

/// <summary>
/// Toasts (max <see cref="MaxToasts"/> visible) plus a bounded notification-centre history. UI-thread only; the window service
/// marshals into it. Auto-dismiss moves a toast into the centre rather than losing it, like VS Code.
/// </summary>
public sealed class NotificationCenter : ObservableObject
{
    public const int MaxToasts = 3;
    public const int MaxHistory = 100;

    private readonly Action<NotificationItem>? _onShown;
    private int _unread;
    private bool _isCenterOpen;

    public NotificationCenter(Action<NotificationItem>? onShown = null) => _onShown = onShown;

    public BulkObservableCollection<NotificationItem> Toasts { get; } = new();
    public BulkObservableCollection<NotificationItem> History { get; } = new();

    public int Unread { get => _unread; private set => SetProperty(ref _unread, value); }
    public bool IsCenterOpen
    {
        get => _isCenterOpen;
        set
        {
            if (!SetProperty(ref _isCenterOpen, value)) return;
            if (value) Unread = 0;
        }
    }

    public NotificationItem Show(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null)
    {
        var item = new NotificationItem(severity, message, actions ?? [], isProgress: false, this);
        Add(item);
        return item;
    }

    public NotificationItem ShowProgress(string title, bool cancellable, CancellationTokenSource? cts)
    {
        var item = new NotificationItem(MessageSeverity.Info, title, [], isProgress: true, this) { CanCancel = cancellable, Cancellation = cts };
        Add(item);
        return item;
    }

    private void Add(NotificationItem item)
    {
        History.Insert(0, item);
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        Toasts.Insert(0, item);
        while (Toasts.Count > MaxToasts)
        {
            var oldest = Toasts[^1];
            Toasts.RemoveAt(Toasts.Count - 1);
            oldest.IsToast = false; // still listed in the centre
        }
        if (!_isCenterOpen) Unread++;
        _onShown?.Invoke(item);
    }

    /// <summary>Resolves the notification (action title or null) and removes it from toasts and history.</summary>
    internal void Complete(NotificationItem item, string? result)
    {
        item.TrySetResult(result);
        Toasts.Remove(item);
        History.Remove(item);
        OnPropertyChanged(nameof(History));
    }

    /// <summary>Demotes a toast to the centre list only (auto-dismiss); an action-less one also resolves with null.</summary>
    public void DemoteToast(NotificationItem item)
    {
        if (!Toasts.Remove(item)) return;
        item.IsToast = false;
        if (!item.HasActions && !item.IsProgress) { item.TrySetResult(null); }
    }

    public void ClearAll()
    {
        foreach (var n in History.ToArray()) n.TrySetResult(null);
        Toasts.Clear();
        History.Clear();
        Unread = 0;
    }
}
