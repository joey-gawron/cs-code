using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Collections;
using CsCode.Ui.Text;
using CsCode.Ui.Threading;

namespace CsCode.App.QuickInput;

/// <summary>One row in the quick-input list (palette entry or a QuickPickItem).</summary>
public sealed class QuickPickEntry : ObservableObject
{
    private bool _isChecked;

    public QuickPickEntry(string label) => Label = label;

    public string Label { get; init; }
    public string? Description { get; init; }
    public string? Detail { get; init; }
    public string? KeybindingLabel { get; init; }
    public string? IconKey { get; init; }
    public bool IsSeparator { get; init; }
    /// <summary>Indices into <see cref="Label"/> that matched the filter (fuzzy highlight spans).</summary>
    public int[] LabelMatches { get; init; } = [];
    public int[] DescriptionMatches { get; init; } = [];
    public object? Tag { get; init; }
    /// <summary>Palette entries carry their own action; run by the palette after the overlay closed.</summary>
    public Func<Task>? Accept { get; init; }
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasKeybinding => !string.IsNullOrEmpty(KeybindingLabel);
}

/// <summary>Produces list entries for a query string. Heavy sources run off the UI thread (QueryAsync); small ones answer instantly.</summary>
public interface IQuickPickSource
{
    string? Placeholder { get; }
    /// <summary>Non-null = an instant answer computed within the UI budget (small lists, empty-query defaults); null = use <see cref="QueryAsync"/>.</summary>
    IReadOnlyList<QuickPickEntry>? TryQuerySync(string text);
    Task<IReadOnlyList<QuickPickEntry>> QueryAsync(string text, CancellationToken ct);
}

/// <summary>Filters a fixed list of <see cref="QuickPickItem"/>s with the fuzzy matcher (the ShowQuickPick implementation).</summary>
public sealed class StaticQuickPickSource : IQuickPickSource
{
    public const int MaxVisible = 5000;
    private const int SyncThreshold = 400;

    private readonly IReadOnlyList<QuickPickItem> _items;
    private readonly QuickPickOptions _options;

    public StaticQuickPickSource(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options)
    {
        _items = items;
        _options = options ?? new QuickPickOptions();
    }

    public string? Placeholder => _options.Placeholder;

    public IReadOnlyList<QuickPickEntry>? TryQuerySync(string text) => _items.Count <= SyncThreshold ? Filter(text, CancellationToken.None) : null;

    public Task<IReadOnlyList<QuickPickEntry>> QueryAsync(string text, CancellationToken ct) => Task.Run(() => Filter(text, ct), ct);

    private IReadOnlyList<QuickPickEntry> Filter(string text, CancellationToken ct)
    {
        if (text.Length == 0)
            return _items.Take(MaxVisible).Select(i => ToEntry(i, [], [])).ToArray();

        var scored = new List<(QuickPickItem Item, int Score)>();
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.IsSeparator) continue;
            var s = FuzzyMatcher.Score(text, item.Label);
            if (s < 0 && _options.MatchOnDescription && item.Description is not null) s = FuzzyMatcher.Score(text, item.Description) - 5;
            if (s < 0 && _options.MatchOnDetail && item.Detail is not null) s = FuzzyMatcher.Score(text, item.Detail) - 10;
            if (s >= 0) scored.Add((item, s));
        }
        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(MaxVisible).Select(x => ToEntry(x.Item, FuzzyMatcher.Positions(text, x.Item.Label), [])).ToArray();
    }

    private static QuickPickEntry ToEntry(QuickPickItem i, int[] labelMatches, int[] descMatches) => new(i.Label)
    {
        Description = i.Description, Detail = i.Detail, IsSeparator = i.IsSeparator, Tag = i, IsChecked = i.Picked,
        LabelMatches = labelMatches, DescriptionMatches = descMatches,
    };
}

/// <summary>
/// The single quick-input overlay model behind the command palette, <c>ShowQuickPick</c> and <c>ShowInputBox</c>. One session at a
/// time: opening a new one resolves the previous with null (VS Code behaviour). Queries for big sources run on a worker and are
/// applied only if still current; the first paint after opening never waits for them (cheap sources answer synchronously).
/// UI-thread object.
/// </summary>
public sealed class QuickInputViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly Emitter<QuickPickEntry?> _activeChanged = new();
    private Session? _session;
    private int _generation;
    private bool _isVisible, _isInput, _canPickMany, _isBusy;
    private string _title = "", _placeholder = "", _text = "", _prompt = "";
    private string? _validation;
    private int _selectedIndex = -1;

    public QuickInputViewModel(IUiDispatcher ui) => _ui = ui;

    public BulkObservableCollection<QuickPickEntry> Items { get; } = new();

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public bool IsInput { get => _isInput; private set => SetProperty(ref _isInput, value); }
    public bool CanPickMany { get => _canPickMany; private set => SetProperty(ref _canPickMany, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Placeholder { get => _placeholder; private set => SetProperty(ref _placeholder, value); }
    public string Prompt { get => _prompt; private set => SetProperty(ref _prompt, value); }
    public string? Validation { get => _validation; private set => SetProperty(ref _validation, value); }
    public bool IsPassword { get; private set; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!SetProperty(ref _selectedIndex, value)) return;
            _activeChanged.Fire(ActiveEntry);
        }
    }

    public QuickPickEntry? ActiveEntry => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;

    /// <summary>Fires when the highlighted entry changes (live theme preview, document-symbol reveal).</summary>
    public IEventSource<QuickPickEntry?> ActiveEntryChanged => _activeChanged;

    /// <summary>Filter text (pick) or the typed value (input). Setting it re-queries the source.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            if (_session is null) return;
            if (_session.Input is { } input) Validation = input.Validate?.Invoke(value);
            else RequestQuery();
        }
    }

    /// <summary>Number of queries that were superseded before their result was applied (observability for tests).</summary>
    public int DroppedStaleResults { get; private set; }

    public Task<QuickPickEntry?> PickAsync(IQuickPickSource source, string? title = null, string? placeholder = null, string initialText = "", CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<QuickPickEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Begin(new Session { Source = source, Single = tcs }, title, placeholder, initialText, many: false, ct);
        return tcs.Task;
    }

    public Task<IReadOnlyList<QuickPickEntry>?> PickManyAsync(IQuickPickSource source, string? title = null, string? placeholder = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<QuickPickEntry>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Begin(new Session { Source = source, Many = tcs }, title, placeholder, "", many: true, ct);
        return tcs.Task;
    }

    public Task<string?> InputAsync(InputBoxOptions? options, CancellationToken ct = default)
    {
        options ??= new InputBoxOptions();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Begin(new Session { Input = options, Text = tcs }, options.Title, options.Placeholder, options.Value ?? "", many: false, ct);
        return tcs.Task;
    }

    private void Begin(Session session, string? title, string? placeholder, string initialText, bool many, CancellationToken ct)
    {
        _ui.AssertUiThread();
        CancelCurrent();
        _session = session;
        session.Ct = ct;
        if (ct.CanBeCanceled) session.CtReg = ct.Register(() => _ui.Post(() => { if (ReferenceEquals(_session, session)) Cancel(); }));
        IsInput = session.Input is not null;
        IsPassword = session.Input?.Password ?? false;
        OnPropertyChanged(nameof(IsPassword));
        CanPickMany = many;
        Title = title ?? "";
        Prompt = session.Input?.Prompt ?? "";
        Placeholder = placeholder ?? session.Source?.Placeholder ?? "";
        Validation = null;
        Items.Clear();
        SetProperty(ref _text, initialText, nameof(Text));
        IsVisible = true;
        if (session.Input is { } input) Validation = input.Validate?.Invoke(initialText);
        else RequestQuery();
    }

    private void RequestQuery()
    {
        var session = _session;
        if (session?.Source is null) return;
        var gen = ++_generation;
        session.QueryCts?.Cancel();
        session.QueryCts?.Dispose();
        session.QueryCts = null;

        var text = _text;
        var instant = session.Source.TryQuerySync(text);
        if (instant is not null)
        {
            IsBusy = false;
            ApplyResults(instant);
            return;
        }

        IsBusy = true;
        var cts = session.QueryCts = new CancellationTokenSource();
        var token = cts.Token;
        var source = session.Source;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await source.QueryAsync(text, token).ConfigureAwait(false);
                // Latest-wins: only the newest query may touch the list.
                _ui.PostCoalesced("quickinput.results", () =>
                {
                    if (gen != _generation || !ReferenceEquals(_session, session)) { DroppedStaleResults++; return; }
                    IsBusy = false;
                    ApplyResults(result);
                });
            }
            catch (OperationCanceledException)
            {
                // superseded or closed
            }
            catch (Exception ex)
            {
                _ui.Post(() => { if (gen == _generation) { IsBusy = false; ApplyResults([new QuickPickEntry("Error: " + ex.Message) { IsSeparator = false }]); } });
            }
        }, token);
    }

    private void ApplyResults(IReadOnlyList<QuickPickEntry> results)
    {
        var previous = ActiveEntry?.Label;
        Items.ReplaceAll(results);
        var index = -1;
        // Keep the highlighted label when it survives the refilter (stable under typing), else first selectable row.
        if (previous is not null) index = IndexOfLabel(previous);
        if (index < 0) index = NextSelectable(-1, +1);
        _selectedIndex = -2; // force notification
        SelectedIndex = index;
    }

    private int IndexOfLabel(string label)
    {
        for (var i = 0; i < Items.Count; i++) if (!Items[i].IsSeparator && Items[i].Label == label) return i;
        return -1;
    }

    private int NextSelectable(int from, int step)
    {
        for (var i = from + step; i >= 0 && i < Items.Count; i += step)
            if (!Items[i].IsSeparator) return i;
        return -1;
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        var next = NextSelectable(_selectedIndex, Math.Sign(delta));
        if (next < 0) next = NextSelectable(delta > 0 ? -1 : Items.Count, delta > 0 ? 1 : -1); // wrap
        if (next >= 0) SelectedIndex = next;
    }

    public void ToggleChecked()
    {
        if (CanPickMany && ActiveEntry is { } e) e.IsChecked = !e.IsChecked;
    }

    /// <summary>Enter: resolves the session with the highlighted entry (or checked entries / input text) and hides the overlay.</summary>
    public void Accept()
    {
        var session = _session;
        if (session is null) return;
        if (session.Input is not null)
        {
            if (Validation is { Length: > 0 }) return; // invalid input cannot be accepted
            var value = _text; // Finish clears the box before resolving
            Finish(session, () => session.Text!.TrySetResult(value));
            return;
        }
        if (session.Many is not null)
        {
            var picked = Items.Where(i => i.IsChecked).ToArray();
            Finish(session, () => session.Many.TrySetResult(picked));
            return;
        }
        var entry = ActiveEntry;
        if (entry is null) return;
        Finish(session, () => session.Single!.TrySetResult(entry));
    }

    /// <summary>Esc / click-away: resolves with null.</summary>
    public void Cancel()
    {
        var session = _session;
        if (session is null) return;
        Finish(session, () => ResolveNull(session));
    }

    private void CancelCurrent()
    {
        if (_session is { } s) Finish(s, () => ResolveNull(s), hide: false);
    }

    private static void ResolveNull(Session s)
    {
        s.Single?.TrySetResult(null);
        s.Many?.TrySetResult(null);
        s.Text?.TrySetResult(null);
    }

    private void Finish(Session session, Action resolve, bool hide = true)
    {
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        _generation++;
        session.QueryCts?.Cancel();
        session.QueryCts?.Dispose();
        session.CtReg.Dispose();
        if (hide)
        {
            IsVisible = false;
            IsBusy = false;
            Items.Clear();
            SetProperty(ref _text, "", nameof(Text));
        }
        resolve();
    }

    public void Dispose()
    {
        Cancel();
        _activeChanged.Clear();
    }

    private sealed class Session
    {
        public IQuickPickSource? Source;
        public InputBoxOptions? Input;
        public TaskCompletionSource<QuickPickEntry?>? Single;
        public TaskCompletionSource<IReadOnlyList<QuickPickEntry>?>? Many;
        public TaskCompletionSource<string?>? Text;
        public CancellationTokenSource? QueryCts;
        public CancellationToken Ct;
        public CancellationTokenRegistration CtReg;
    }
}
