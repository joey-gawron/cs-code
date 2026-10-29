using Avalonia.Controls;
using Avalonia.Threading;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Views;

/// <summary>Keeps keyboard focus in a list whose rows the view-model replaces wholesale (see <see cref="IRowsReplacedNotifier"/>).</summary>
public sealed class ListFocusKeeper : IDisposable
{
    private readonly ListBox _list;
    private IRowsReplacedNotifier? _source;
    private bool _hadFocus;

    public ListFocusKeeper(ListBox list) => _list = list;

    public void Attach(object? model)
    {
        Detach();
        if (model is not IRowsReplacedNotifier notifier) return;
        _source = notifier;
        notifier.RowsReplacing += OnReplacing;
        notifier.RowsReplaced += OnReplaced;
    }

    private void OnReplacing() => _hadFocus = _list.IsKeyboardFocusWithin;

    private void OnReplaced()
    {
        if (!_hadFocus) return;
        _hadFocus = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (_list.IsKeyboardFocusWithin) return;
            _list.UpdateLayout();
            var target = _list.SelectedItem ?? (_list.ItemCount > 0 ? _list.Items[0] : null);
            if (target is not null && _list.ContainerFromItem(target) is Control container) container.Focus();
            else _list.Focus();
        }, DispatcherPriority.Input);
    }

    public void Detach()
    {
        if (_source is null) return;
        _source.RowsReplacing -= OnReplacing;
        _source.RowsReplaced -= OnReplaced;
        _source = null;
    }

    public void Dispose() => Detach();
}
