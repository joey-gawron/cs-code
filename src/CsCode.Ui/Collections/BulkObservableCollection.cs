using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace CsCode.Ui.Collections;

/// <summary>
/// ObservableCollection with bulk operations that raise ONE change notification (Reset) instead of one per item,
/// so 50k-row lists update in a single layout pass. All mutation must happen on the UI thread.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection() { }
    public BulkObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>Replaces the entire content with one Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        if (items is ICollection<T> c && Items is List<T> list) list.Capacity = Math.Max(list.Capacity, c.Count);
        foreach (var i in items) Items.Add(i);
        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var added = items as IList ?? items.ToList();
        if (added.Count == 0) return;
        var start = Items.Count;
        foreach (var i in added) Items.Add((T)i!);
        // Large inserts: Reset is cheaper for virtualising panels than N Add notifications.
        if (added.Count > 32) RaiseReset();
        else OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        CheckReentrancy();
        var list = (List<T>)Items;
        list.RemoveRange(index, count);
        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        ((List<T>)Items).InsertRange(index, items);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
