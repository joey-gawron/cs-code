using System.ComponentModel;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Ui.Collections;

namespace CsCode.Panels.Navigation;

/// <summary>What the Hierarchy window shows: VS Code's call hierarchy (incoming / outgoing calls) or type hierarchy (supertypes / subtypes).</summary>
public enum HierarchyDirection { IncomingCalls, OutgoingCalls, Supertypes, Subtypes }

public static class HierarchyDirections
{
    public static bool IsCalls(this HierarchyDirection d) => d is HierarchyDirection.IncomingCalls or HierarchyDirection.OutgoingCalls;

    /// <summary>The other direction of the same hierarchy (incoming ⇄ outgoing, supertypes ⇄ subtypes).</summary>
    public static HierarchyDirection Flipped(this HierarchyDirection d) => d switch
    {
        HierarchyDirection.IncomingCalls => HierarchyDirection.OutgoingCalls,
        HierarchyDirection.OutgoingCalls => HierarchyDirection.IncomingCalls,
        HierarchyDirection.Supertypes => HierarchyDirection.Subtypes,
        _ => HierarchyDirection.Supertypes,
    };

    /// <summary>The header in Visual Studio's wording ("Calls To 'Run'") and VS Code's for types.</summary>
    public static string Header(this HierarchyDirection d, string name) => d switch
    {
        HierarchyDirection.IncomingCalls => $"Calls To '{name}'",
        HierarchyDirection.OutgoingCalls => $"Calls From '{name}'",
        HierarchyDirection.Supertypes => $"Supertypes of '{name}'",
        _ => $"Subtypes of '{name}'",
    };
}

/// <summary>
/// One node of the tree, and the row that shows it (the list shows the expanded part of the tree, flattened). Children are asked for the
/// first time the node is expanded and kept; <see cref="Sites"/> are the call sites a call node stands for (where selecting it goes).
/// </summary>
public sealed class HierarchyNode : INotifyPropertyChanged
{
    internal HierarchyNode(HierarchyItem item, HierarchyNode? parent, IReadOnlyList<Location> sites)
    {
        Item = item;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Sites = sites;
    }

    public HierarchyItem Item { get; }
    public HierarchyNode? Parent { get; }
    public int Depth { get; }
    /// <summary>The call sites (incoming: in the caller, outgoing: in the parent); empty for roots and type nodes.</summary>
    public IReadOnlyList<Location> Sites { get; }
    /// <summary>Null until the node was expanded once.</summary>
    public IReadOnlyList<HierarchyNode>? Children { get; internal set; }
    public bool IsExpanded { get; internal set; }
    public bool IsLoading { get; internal set; }
    /// <summary>The provider failed or timed out for this node's children (the row says so; Refresh asks again).</summary>
    public string? Error { get; internal set; }

    public string Label => Item.Name;
    public double Indent => Depth * 14;
    /// <summary>Unknown until loaded (a chevron is shown), then whether there were any.</summary>
    public bool HasChildren => Children is null || Children.Count > 0;
    public string Chevron => IsLoading ? "loading" : !HasChildren ? "" : IsExpanded ? "chevron-down" : "chevron-right";

    /// <summary>The symbol's detail (signature, container) and where it is: <c>Program.cs:12</c>, plus how many calls when several.</summary>
    public string Detail
    {
        get
        {
            var file = Path.GetFileName(Item.Uri.IsFile ? Item.Uri.LocalPath : Item.Uri.AbsolutePath);
            var where = $"{file}:{Item.SelectionRange.Start.Line + 1}";
            var detail = string.IsNullOrWhiteSpace(Item.Detail) ? where : Item.Detail + " · " + where;
            if (Sites.Count > 1) detail += $" ({Sites.Count} calls)";
            if (Error is { } e) detail += " — " + e;
            return detail;
        }
    }

    public string Tooltip => (Item.Detail is { Length: > 0 } d ? Item.Name + Environment.NewLine + d : Item.Name) + Environment.NewLine
        + (Item.Uri.IsFile ? Item.Uri.LocalPath : Item.Uri.ToString()) + ":" + (Item.SelectionRange.Start.Line + 1);

    /// <summary>The symbol kind's Codicon.</summary>
    public string Icon => IconFor(Item.Kind);

    /// <summary>Where selecting the row goes: the first call site, else the symbol's name.</summary>
    public Location Target => Sites.Count > 0 ? Sites[0] : new Location(Item.Uri, Item.SelectionRange);

    public static string IconFor(SymbolKind kind) => kind switch
    {
        SymbolKind.Class => "symbol-class",
        SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor => "symbol-method",
        SymbolKind.Interface => "symbol-interface",
        SymbolKind.Struct => "symbol-structure",
        SymbolKind.Enum => "symbol-enum",
        SymbolKind.EnumMember => "symbol-enum-member",
        SymbolKind.Property => "symbol-property",
        SymbolKind.Field => "symbol-field",
        SymbolKind.Event => "symbol-event",
        SymbolKind.Constant => "symbol-constant",
        SymbolKind.Variable => "symbol-variable",
        SymbolKind.Module or SymbolKind.Namespace or SymbolKind.Package => "symbol-namespace",
        SymbolKind.Operator => "symbol-operator",
        SymbolKind.TypeParameter => "symbol-parameter",
        SymbolKind.File => "symbol-file",
        _ => "symbol-misc",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public override string ToString() => new string(' ', Depth * 2) + Item.Name + (IsExpanded ? " [-]" : HasChildren ? " [+]" : "");
}

/// <summary>
/// The Hierarchy window's model: one query (the symbol at a document position, the provider that answered, the direction), its tree and
/// the visible rows. Expanding a node asks the SAME provider for that node's children (the protocol requires the items to go back to the
/// server that made them) off the UI thread with a deadline; a newer query, a direction change or a refresh drops answers to older ones.
/// Lives in <see cref="HierarchyHost"/> (a singleton) so a command can fill it while the window is hidden; it holds no document, only the
/// query's URI and language (a refresh looks the document up again).
/// </summary>
public sealed class HierarchyModel : INotifyPropertyChanged
{
    /// <summary>Children kept per node (a server answering with thousands of callers must not grow the window without bound).</summary>
    public const int MaxChildren = 1000;
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    private readonly List<HierarchyNode> _roots = [];
    private ILanguageProvider? _provider;
    private int _generation;
    private HierarchyDirection _direction = HierarchyDirection.IncomingCalls;
    private string _header = "";
    private string _emptyText = HierarchyHost.EmptyHint;
    private HierarchyNode? _selected;

    public BulkObservableCollection<HierarchyNode> Rows { get; } = new();
    public IReadOnlyList<HierarchyNode> Roots => _roots;
    public HierarchyDirection Direction => _direction;
    public bool IsCalls => _direction.IsCalls();
    public bool HasQuery => _provider is not null;
    /// <summary>The provider that answered the query (children must come from it).</summary>
    internal ILanguageProvider? Provider => _provider;
    /// <summary>The query's document and position (refresh prepares again there while the document is open).</summary>
    public (Uri Uri, string LanguageId, TextPosition Position)? Query { get; private set; }
    public string Header { get => _header; private set => Set(ref _header, value, nameof(Header)); }
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value, nameof(EmptyText)); }
    public bool IsEmpty => Rows.Count == 0;
    public HierarchyNode? Selected { get => _selected; set => Set(ref _selected, value, nameof(Selected)); }
    /// <summary>The toolbar's direction toggle: what pressing it shows.</summary>
    public string ToggleTip => _direction switch
    {
        HierarchyDirection.IncomingCalls => "Show Outgoing Calls",
        HierarchyDirection.OutgoingCalls => "Show Incoming Calls",
        HierarchyDirection.Supertypes => "Show Subtypes",
        _ => "Show Supertypes",
    };

    /// <summary>Raised when the rows were rebuilt (the view keeps the selected row in sight).</summary>
    internal event Action? RowsRebuilt;

    /// <summary>
    /// Shows a new query: <paramref name="items"/> (prepare's answer from <paramref name="provider"/>) as roots, the first expanded. UI thread.
    /// </summary>
    public Task SetAsync(ILanguageProvider provider, IReadOnlyList<HierarchyItem> items, HierarchyDirection direction, (Uri, string, TextPosition)? query) =>
        SetAsync(provider, items, direction, query, null);

    /// <summary>As above; <paramref name="reexpand"/> (from <see cref="ExpandedPaths"/>) expands those nodes again instead of the first root alone.</summary>
    internal Task SetAsync(ILanguageProvider provider, IReadOnlyList<HierarchyItem> items, HierarchyDirection direction, (Uri, string, TextPosition)? query, HashSet<string>? reexpand)
    {
        if ((provider is ICallHierarchyProvider) != direction.IsCalls() || (provider is ITypeHierarchyProvider) == direction.IsCalls())
            throw new ArgumentException("The provider does not serve that direction.", nameof(provider));
        _generation++;
        _provider = provider;
        _direction = direction;
        Query = query;
        _roots.Clear();
        foreach (var item in items.Take(MaxChildren)) _roots.Add(new HierarchyNode(item, null, []));
        Header = _roots.Count == 0 ? "" : direction.Header(_roots[0].Item.Name);
        EmptyText = "No results";
        Selected = null;
        OnPropertyChanged(nameof(Direction));
        OnPropertyChanged(nameof(IsCalls));
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(ToggleTip));
        Rebuild();
        if (_roots.Count == 0) return Task.CompletedTask;
        Selected = _roots[0];
        if (reexpand is null) return ExpandAsync(_roots[0]);
        return Task.WhenAll(_roots.Where(r => reexpand.Contains(PathOf(r))).Select(r => ExpandAsync(r, reexpand)));
    }

    /// <summary>The same roots in the other direction (incoming ⇄ outgoing, supertypes ⇄ subtypes); every child is asked for again.</summary>
    public Task SetDirectionAsync(HierarchyDirection direction)
    {
        if (_provider is null || direction.IsCalls() != _direction.IsCalls() || direction == _direction) return Task.CompletedTask;
        var items = _roots.Select(r => r.Item).ToList();
        return SetAsync(_provider, items, direction, Query);
    }

    public Task ToggleDirectionAsync() => SetDirectionAsync(_direction.Flipped());

    /// <summary>Asks for the children of every expanded node again (Refresh, when the query cannot be prepared anew).</summary>
    public Task ReloadAsync()
    {
        if (_provider is null) return Task.CompletedTask;
        var expanded = ExpandedPaths();
        _generation++;
        foreach (var r in _roots) { r.Children = null; r.IsExpanded = false; r.IsLoading = false; r.Error = null; }
        Rebuild();
        return Task.WhenAll(_roots.Where(r => expanded.Contains(PathOf(r))).Select(r => ExpandAsync(r, expanded)));
    }

    /// <summary>The expanded nodes, by their path of (place, name) from the root: what a refresh expands again.</summary>
    internal HashSet<string> ExpandedPaths()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        void Collect(HierarchyNode n)
        {
            if (n.IsExpanded) expanded.Add(PathOf(n));
            if (n.Children is { } c) foreach (var child in c) Collect(child);
        }
        foreach (var r in _roots) Collect(r);
        return expanded;
    }

    private static string PathOf(HierarchyNode n) => n.Parent is null ? Key(n) : PathOf(n.Parent) + "/" + Key(n);
    private static string Key(HierarchyNode n) => $"{n.Item.Uri}#{n.Item.SelectionRange.Start.Line}:{n.Item.SelectionRange.Start.Character}:{n.Item.Name}";

    public void Clear()
    {
        _generation++;
        _provider = null;
        Query = null;
        _roots.Clear();
        Header = "";
        EmptyText = HierarchyHost.EmptyHint;
        Selected = null;
        OnPropertyChanged(nameof(HasQuery));
        Rebuild();
    }

    // ------------------------------------------------------------------------------------------------ expand / collapse

    public Task ExpandAsync(HierarchyNode node) => ExpandAsync(node, null);

    private async Task ExpandAsync(HierarchyNode node, HashSet<string>? reexpand)
    {
        if (node.IsLoading) return;
        if (node.Children is null)
        {
            var generation = _generation;
            node.IsLoading = true;
            node.Error = null;
            node.Refresh();
            var (children, error) = await LoadChildrenAsync(node).ConfigureAwait(true);
            if (generation != _generation) return; // a newer query, direction or refresh: this answer is for a tree that is gone
            node.IsLoading = false;
            node.Children = children;
            node.Error = error;
        }
        node.IsExpanded = node.Children.Count > 0;
        node.Refresh();
        Rebuild();
        if (reexpand is null) return;
        var again = node.Children.Where(c => reexpand.Contains(PathOf(c))).Select(c => ExpandAsync(c, reexpand)).ToList();
        await Task.WhenAll(again).ConfigureAwait(true);
    }

    public void Collapse(HierarchyNode node)
    {
        if (!node.IsExpanded) return;
        node.IsExpanded = false;
        node.Refresh();
        Rebuild();
    }

    public Task ToggleAsync(HierarchyNode node)
    {
        if (node.IsExpanded) { Collapse(node); return Task.CompletedTask; }
        return node.HasChildren ? ExpandAsync(node) : Task.CompletedTask;
    }

    public void CollapseAll()
    {
        foreach (var r in _roots) CollapseTree(r);
        Rebuild();
        static void CollapseTree(HierarchyNode n)
        {
            n.IsExpanded = false;
            n.Refresh();
            if (n.Children is { } c) foreach (var child in c) CollapseTree(child);
        }
    }

    /// <summary>Asks the query's provider for <paramref name="node"/>'s children, off the UI thread, bounded by <see cref="Timeout"/>.</summary>
    private async Task<(IReadOnlyList<HierarchyNode> Children, string? Error)> LoadChildrenAsync(HierarchyNode node)
    {
        var provider = _provider;
        var direction = _direction;
        var item = node.Item;
        try
        {
            var result = await Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(Timeout);
                return await Children(provider, direction, item, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);
            return (result.Take(MaxChildren).Select(c => new HierarchyNode(c.Item, node, c.Sites)).ToList(), null);
        }
        catch (OperationCanceledException)
        {
            return ([], "timed out");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ([], "failed: " + ex.Message);
        }
    }

    private static async Task<IReadOnlyList<(HierarchyItem Item, IReadOnlyList<Location> Sites)>> Children(ILanguageProvider? provider, HierarchyDirection direction, HierarchyItem item, CancellationToken ct)
    {
        switch (direction)
        {
            case HierarchyDirection.IncomingCalls when provider is ICallHierarchyProvider calls:
                var incoming = await calls.IncomingCallsAsync(item, ct).ConfigureAwait(false) ?? [];
                // the call sites are in the caller
                return incoming.Select(c => (c.From, (IReadOnlyList<Location>)c.FromRanges.Select(r => new Location(c.From.Uri, r)).ToList())).ToList();
            case HierarchyDirection.OutgoingCalls when provider is ICallHierarchyProvider calls:
                var outgoing = await calls.OutgoingCallsAsync(item, ct).ConfigureAwait(false) ?? [];
                // the call sites are in the item being expanded (the caller), not in the callee
                return outgoing.Select(c => (c.To, (IReadOnlyList<Location>)c.FromRanges.Select(r => new Location(item.Uri, r)).ToList())).ToList();
            case HierarchyDirection.Supertypes when provider is ITypeHierarchyProvider types:
                return (await types.SupertypesAsync(item, ct).ConfigureAwait(false) ?? []).Select(t => (t, (IReadOnlyList<Location>)[])).ToList();
            case HierarchyDirection.Subtypes when provider is ITypeHierarchyProvider types:
                return (await types.SubtypesAsync(item, ct).ConfigureAwait(false) ?? []).Select(t => (t, (IReadOnlyList<Location>)[])).ToList();
            default:
                return [];
        }
    }

    /// <summary>The visible rows: every root, and the children of expanded nodes, depth first.</summary>
    private void Rebuild()
    {
        var rows = new List<HierarchyNode>();
        void Add(HierarchyNode n)
        {
            rows.Add(n);
            if (n.IsExpanded && n.Children is { } c) foreach (var child in c) Add(child);
        }
        foreach (var r in _roots) Add(r);
        var selected = _selected;
        Rows.ReplaceAll(rows);
        if (selected is not null && !rows.Contains(selected)) selected = AncestorIn(selected, rows);
        _selected = null; // the list cleared its selection on the reset: set it again so the binding re-selects the row
        Selected = selected;
        OnPropertyChanged(nameof(IsEmpty));
        RowsRebuilt?.Invoke();
    }

    private static HierarchyNode? AncestorIn(HierarchyNode node, List<HierarchyNode> rows)
    {
        for (var p = node.Parent; p is not null; p = p.Parent) if (rows.Contains(p)) return p;
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
