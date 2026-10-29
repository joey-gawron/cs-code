using System.Windows.Input;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// ICommand over a synchronous or asynchronous body. Exceptions never escape <see cref="Execute"/> (an <c>async void</c> that throws would
/// take the process down): they go to the owner's error handler, which shows them inline in the panel.
/// </summary>
public sealed class UiCommand : ICommand
{
    private readonly Func<object?, Task> _run;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;

    public UiCommand(Action run, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => { run(); return Task.CompletedTask; }, canExecute is null ? null : _ => canExecute(), onError) { }

    public UiCommand(Func<Task> run, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => run(), canExecute is null ? null : _ => canExecute(), onError) { }

    public UiCommand(Func<object?, Task> run, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _run = run;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try { await _run(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    /// <summary>Test/seam entry point: runs the command and lets the caller await it (errors still go to the handler).</summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        try { await _run(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
