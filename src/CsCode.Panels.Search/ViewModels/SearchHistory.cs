using CsCode.Core.Platform;

namespace CsCode.Panels.Search.ViewModels;

/// <summary>
/// Most-recent-last list of search terms, capped, persisted through <see cref="IStateStorage"/>. Up/Down in the query box walks it:
/// <see cref="Previous"/> goes back in time (remembering what was typed so <see cref="Next"/> can return to it).
/// </summary>
public sealed class SearchHistory
{
    public const string StateKey = "search.history";
    public const int Capacity = 50;

    private readonly IStateStorage? _state;
    private readonly List<string> _items = new();
    private int _cursor;           // == _items.Count means "the live, not-yet-stored text"
    private string _draft = "";

    public SearchHistory(IStateStorage? state)
    {
        _state = state;
        if (state?.Get<List<string>>(StateKey) is { } saved)
            foreach (var s in saved.Where(s => !string.IsNullOrEmpty(s)).TakeLast(Capacity)) _items.Add(s);
        _cursor = _items.Count;
    }

    public IReadOnlyList<string> Items => _items;

    /// <summary>Records a term (moving an existing equal entry to the end) and persists the list.</summary>
    public void Add(string term)
    {
        if (string.IsNullOrEmpty(term)) return;
        _items.Remove(term);
        _items.Add(term);
        if (_items.Count > Capacity) _items.RemoveRange(0, _items.Count - Capacity);
        _cursor = _items.Count;
        _draft = "";
        _state?.Set(StateKey, _items.ToList());
    }

    /// <summary>The older entry, or null at the oldest. <paramref name="current"/> is kept so Down can come back to it.</summary>
    public string? Previous(string current)
    {
        if (_items.Count == 0) return null;
        if (_cursor == _items.Count) _draft = current;
        if (_cursor == 0) return null;
        return _items[--_cursor];
    }

    /// <summary>The newer entry; after the newest it returns the text that was being typed. Null when already there.</summary>
    public string? Next()
    {
        if (_cursor >= _items.Count) return null;
        _cursor++;
        return _cursor == _items.Count ? _draft : _items[_cursor];
    }

    public void ResetCursor()
    {
        _cursor = _items.Count;
        _draft = "";
    }
}
