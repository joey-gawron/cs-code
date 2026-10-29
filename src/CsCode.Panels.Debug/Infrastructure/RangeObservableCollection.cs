using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// ObservableCollection with range operations that raise ONE precise notification (Add/Remove with an index and the item list) instead
/// of one per item or a Reset. Precise notifications matter for the debug trees: a Reset makes the list control drop its selection,
/// focus and scroll position every time a node is expanded, a stop arrives or a page of children is appended.
/// All mutation happens on the UI thread.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection() { }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        var list = (List<T>)Items;
        list.InsertRange(index, items);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList(), index));
    }

    public void AddRange(IReadOnlyList<T> items) => InsertRange(Count, items);

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        CheckReentrancy();
        var list = (List<T>)Items;
        var removed = list.GetRange(index, count);
        list.RemoveRange(index, count);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    /// <summary>Replaces everything with one Reset (use only when the whole content really changed).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var list = (List<T>)Items;
        list.Clear();
        list.AddRange(items);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Removes everything with one Reset and releases the backing array when it grew large.</summary>
    public void Reset()
    {
        if (Count == 0) return;
        CheckReentrancy();
        var list = (List<T>)Items;
        list.Clear();
        if (list.Capacity > 4096) list.Capacity = 256;
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseCountChanged()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
