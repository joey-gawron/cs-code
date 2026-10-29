using CommunityToolkit.Mvvm.ComponentModel;

namespace CsCode.App.Notifications;

/// <summary>Modal prompts (dirty-close Save / Don't Save / Cancel, destructive confirmations). Faked in tests via <see cref="IModalDialogs"/>.</summary>
public interface IModalDialogs
{
    /// <summary>Shows a modal dialog; returns the pressed button label, or null if dismissed (Esc / click-away).</summary>
    Task<string?> ShowAsync(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0);
}

/// <summary>The in-window modal dialog overlay model (UI thread). One dialog at a time; a new one cancels the previous.</summary>
public sealed class DialogHostViewModel : ObservableObject, IModalDialogs
{
    private TaskCompletionSource<string?>? _tcs;
    private bool _isVisible;
    private string _title = "", _message = "";
    private IReadOnlyList<string> _buttons = [];
    private int _defaultIndex;

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public IReadOnlyList<string> Buttons { get => _buttons; private set => SetProperty(ref _buttons, value); }
    public int DefaultIndex { get => _defaultIndex; private set => SetProperty(ref _defaultIndex, value); }

    public Task<string?> ShowAsync(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0)
    {
        _tcs?.TrySetResult(null);
        var tcs = _tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Title = title;
        Message = message;
        Buttons = buttons;
        DefaultIndex = defaultIndex;
        IsVisible = true;
        return tcs.Task;
    }

    public void Choose(string? button)
    {
        var tcs = _tcs;
        _tcs = null;
        IsVisible = false;
        tcs?.TrySetResult(button);
    }
}
