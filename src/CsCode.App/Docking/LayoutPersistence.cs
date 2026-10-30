using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;
using Dock.Model.Core;

namespace CsCode.App.Docking;

public sealed record ToolLayout(string Id, string Dock, bool Visible, int Order);
public sealed record DocumentLayout(string Uri, bool Pinned, bool Preview, int Group);

/// <summary>A floating window: where it is, and the tool windows (ids) and documents (URIs) it holds, in tab order.</summary>
public sealed record FloatingLayout(double X, double Y, double Width, double Height, IReadOnlyList<string> Tools, IReadOnlyList<string> Documents);

/// <summary>A tool window that was hidden while it floated: it floats there again when it is shown.</summary>
public sealed record FloatingToolLayout(string Id, double X, double Y, double Width, double Height);

/// <summary>
/// Everything about the workbench arrangement that survives a restart. <see cref="Floating"/> and <see cref="FloatingHidden"/> were added in wave 3
/// (still version 1: a layout without them reads as "nothing floats", and an older CsCode ignores them).
/// </summary>
public sealed record LayoutSnapshot(
    int Version,
    IReadOnlyList<ToolLayout> Tools,
    double LeftProportion,
    double RightProportion,
    double BottomProportion,
    IReadOnlyList<DocumentLayout> Documents,
    string? ActiveDocument,
    IReadOnlyList<FloatingLayout>? Floating = null,
    IReadOnlyList<FloatingToolLayout>? FloatingHidden = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>JSON (de)serialisation of <see cref="LayoutSnapshot"/>. Tolerant: garbage, empty input or a newer version yields null (use defaults).</summary>
public static class LayoutSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(LayoutSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static LayoutSnapshot? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<LayoutSnapshot>(json, Options);
            if (s is null || s.Version is < 1 or > LayoutSnapshot.CurrentVersion) return null;
            if (s.Tools is null || s.Documents is null) return null;
            return s;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

/// <summary>
/// Captures and applies the layout against the Dock model, and persists it per workspace with a global default
/// (<see cref="IStateStorage"/>, workspace scope first, then application scope). Saving is debounced by the caller; applying never throws on stale ids
/// (tools or files that no longer exist are skipped). Floating windows are part of it: their place and what they hold, and where hidden floating
/// tools float when shown. A floating window is only created while the main window can show one (<see cref="CanPresentWindows"/>: a background
/// session restores into a hidden window); until then the floating part waits (<see cref="ApplyPendingFloating"/>) and is saved as it was.
/// </summary>
public sealed class LayoutPersistence
{
    public const string StateKey = "shell.layout";
    private readonly ShellDockFactory _factory;
    private readonly ToolWindowManager _tools;
    private readonly DocumentWell _well;
    private readonly IStateStorage? _state;
    private readonly IUiDispatcher _ui;
    private readonly string[] _defaultHidden;

    public LayoutPersistence(ShellDockFactory factory, ToolWindowManager tools, DocumentWell well, IUiDispatcher ui, IStateStorage? state = null, IEnumerable<string>? defaultHidden = null)
    {
        _factory = factory;
        _tools = tools;
        _well = well;
        _ui = ui;
        _state = state;
        _defaultHidden = defaultHidden?.ToArray() ?? [];
    }

    private string DockName(IDockable? d) => ReferenceEquals(d, _factory.LeftTools) ? "left" : ReferenceEquals(d, _factory.BottomTools) ? "bottom" : "right";

    /// <summary>Whether a floating window can be shown now (the main window is visible). Unset: always.</summary>
    public Func<bool>? CanPresentWindows { get; set; }

    // The floating part of a layout applied while no window could be shown (see CanPresentWindows).
    private IReadOnlyList<FloatingLayout>? _pendingFloating;

    public LayoutSnapshot Capture()
    {
        var tools = new List<ToolLayout>();
        foreach (var (dockName, dock) in new[] { ("left", _factory.LeftTools), ("right", _factory.RightTools), ("bottom", _factory.BottomTools) })
        {
            var order = 0;
            foreach (var t in dock.VisibleDockables!.OfType<ToolTab>()) tools.Add(new ToolLayout(t.Spec.Id, dockName, true, order++));
        }
        // A floating tool is visible; its well is the one it returns to.
        var floatingOrder = 500;
        foreach (var t in _tools.Tools.Where(t => _factory.IsFloating(t) && _tools.IsVisible(t.Spec.Id)))
            tools.Add(new ToolLayout(t.Spec.Id, DockName(_tools.HomeWell(t)), true, floatingOrder++));
        foreach (var t in _tools.Tools.Where(t => !tools.Any(x => x.Id == t.Spec.Id)))
            tools.Add(new ToolLayout(t.Spec.Id, DockName(t.OriginalOwner ?? t.Owner), false, 1000));

        var groups = _factory.DocumentGroups().ToList();
        var docs = new List<DocumentLayout>();
        foreach (var tab in _well.EditorTabs.Where(t => !t.Doc.IsUntitled))
            docs.Add(new DocumentLayout(tab.Uri.ToString(), tab.IsPinned, tab.IsPreview, Math.Max(0, groups.IndexOf(_factory.GroupOf(tab)!))));

        var floating = new List<FloatingLayout>();
        foreach (var window in _factory.FloatingWindows)
        {
            if (window.Layout is not { } layout) continue;
            var inWindow = Dockables(layout).ToList();
            var toolIds = inWindow.OfType<ToolTab>().Select(t => t.Spec.Id).ToArray();
            var uris = inWindow.OfType<EditorTab>().Where(t => !t.IsDisposed && !t.Doc.IsUntitled).Select(t => t.Uri.ToString()).ToArray();
            if (toolIds.Length == 0 && uris.Length == 0) continue;
            var b = ShellDockFactory.BoundsOf(window);
            floating.Add(new FloatingLayout(b.X, b.Y, b.Width, b.Height, toolIds, uris));
        }
        if (_pendingFloating is { } pending) floating.AddRange(pending); // not shown yet (hidden window): kept as they were
        var hiddenFloating = _factory.FloatingMemory.Select(kv => new FloatingToolLayout(kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Width, kv.Value.Height)).ToArray();
        return new LayoutSnapshot(LayoutSnapshot.CurrentVersion, tools, Proportion(_factory.LeftTools.Proportion, 0.22), Proportion(_factory.RightTools.Proportion, 0.24),
            Proportion(_factory.BottomTools.Proportion, 0.28), docs, _well.ActiveEditor?.Uri.ToString(),
            floating.Count > 0 ? floating : null, hiddenFloating.Length > 0 ? hiddenFloating : null);
    }

    private static IEnumerable<IDockable> Dockables(IDockable node)
    {
        if (node is ShellDocument or ToolTab) yield return node;
        if (node is IDock { VisibleDockables: { } kids })
            foreach (var k in kids.ToArray())
                foreach (var d in Dockables(k)) yield return d;
    }

    /// <summary>Docks every floating tool and document back (Reset Window Layout, and before a layout is applied).</summary>
    public void DockAllFloating()
    {
        foreach (var window in _factory.FloatingWindows)
        {
            if (window.Layout is not { } layout) continue;
            foreach (var d in Dockables(layout).ToArray())
            {
                if (d is ToolTab t) _factory.DockBack(t, _tools.HomeWell(t));
                else _factory.DockBack(d);
            }
        }
        _factory.CloseEmptyWindows();
    }

    private static double Proportion(double v, double fallback) => double.IsNaN(v) || v <= 0 || v >= 1 ? fallback : v;

    /// <summary>Applies tool placement, visibility and splitter proportions (synchronous), then reopens documents (asynchronous).</summary>
    public async Task ApplyAsync(LayoutSnapshot snapshot, bool restoreDocuments = true)
    {
        _ui.AssertUiThread();
        DockAllFloating();
        _pendingFloating = null;
        _factory.LeftTools.Proportion = Proportion(snapshot.LeftProportion, 0.22);
        _factory.RightTools.Proportion = Proportion(snapshot.RightProportion, 0.24);
        _factory.BottomTools.Proportion = Proportion(snapshot.BottomProportion, 0.28);

        foreach (var layout in snapshot.Tools.OrderBy(t => t.Order))
        {
            if (_tools.Get(layout.Id) is not { } tool) continue; // a tool that no longer exists
            var target = layout.Dock switch { "left" when _factory.IncludeLeftTools => _factory.LeftTools, "bottom" => _factory.BottomTools, _ => _factory.RightTools };
            _tools.Unpin(tool); // a snapshot places every tool in a well (Reset Window Layout included)
            var wasVisible = _tools.IsVisible(layout.Id);
            if (!wasVisible) _tools.Restore(tool);
            if (tool.Owner is IDock from && !ReferenceEquals(from, target)) _factory.MoveDockable(from, target, tool, null);
            if (!layout.Visible) _tools.Hide(layout.Id);
        }
        // A tool the snapshot does not know (its module was added after the layout was saved) gets its default visibility, not "visible".
        var known = snapshot.Tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in _tools.Tools.Where(t => !known.Contains(t.Spec.Id) && _defaultHidden.Contains(t.Spec.Id)).ToArray())
            _tools.Hide(tool.Spec.Id);

        if (!restoreDocuments) return;
        foreach (var d in snapshot.Documents.OrderBy(d => d.Group))
        {
            if (!Uri.TryCreate(d.Uri, UriKind.Absolute, out var uri)) continue;
            try
            {
                var tab = await _well.OpenAsync(uri, d.Preview, preserveFocus: true).ConfigureAwait(true);
                if (d.Pinned) _well.Pin(tab);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // the file was deleted or is unreadable now: restore the rest
            }
        }
        if (snapshot.ActiveDocument is { } active && Uri.TryCreate(active, UriKind.Absolute, out var au) && _well.Find(au) is { } at) _well.Activate(at);
        ApplyFloating(snapshot);
    }

    private void ApplyFloating(LayoutSnapshot snapshot)
    {
        foreach (var hidden in snapshot.FloatingHidden ?? [])
            if (_tools.Get(hidden.Id) is { } tool && !_tools.IsVisible(hidden.Id) && !_factory.IsFloating(tool))
                _factory.RememberFloating(hidden.Id, new FloatingBounds(hidden.X, hidden.Y, hidden.Width, hidden.Height));
        if (snapshot.Floating is not { Count: > 0 } floating) return;
        if (CanPresentWindows?.Invoke() == false)
        {
            _pendingFloating = floating; // a hidden window: float them when it is shown
            return;
        }
        Float(floating);
    }

    /// <summary>Floats what a layout applied while the main window was hidden wanted floating (call when the window is shown).</summary>
    public void ApplyPendingFloating()
    {
        if (_pendingFloating is not { } pending) return;
        _pendingFloating = null;
        Float(pending);
    }

    private void Float(IReadOnlyList<FloatingLayout> windows)
    {
        foreach (var f in windows)
        {
            var bounds = new FloatingBounds(f.X, f.Y, f.Width, f.Height);
            // Dock gives a floating window one kind of dock: tools and documents that shared one go into a window each, at the same place.
            var tools = f.Tools.Select(id => _tools.Get(id)).OfType<ToolTab>().Where(t => _tools.IsVisible(t.Spec.Id) && !_factory.IsFloating(t)).Cast<IDockable>();
            var docs = f.Documents.Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? _well.Find(uri) : null).OfType<EditorTab>()
                .Where(t => !_factory.IsFloating(t)).Cast<IDockable>();
            FloatTogether(tools.ToList(), bounds);
            FloatTogether(docs.ToList(), bounds);
        }
    }

    private void FloatTogether(IReadOnlyList<IDockable> dockables, FloatingBounds bounds)
    {
        if (dockables.Count == 0) return;
        if (dockables[0] is ToolTab first) _tools.Unpin(first);
        if (_factory.Float(dockables[0], bounds) is null || dockables[0].Owner is not IDock dock) return;
        foreach (var d in dockables.Skip(1))
        {
            if (d is ToolTab t) _tools.Unpin(t);
            if (d.Owner is IDock from) _factory.MoveDockable(from, dock, d, null);
        }
        dock.ActiveDockable = dockables[0];
    }

    /// <summary>Loads workspace layout, else the global default, else null (caller applies defaults).</summary>
    public LayoutSnapshot? Load()
    {
        if (_state is null) return null;
        return LayoutSerializer.Deserialize(_state.Get<string>(StateKey, StorageScope.Workspace))
            ?? LayoutSerializer.Deserialize(_state.Get<string>(StateKey, StorageScope.Application));
    }

    /// <summary>Saves to the workspace scope and updates the global default (so a new workspace starts like the last one).</summary>
    public void Save()
    {
        if (_state is null) return;
        var json = LayoutSerializer.Serialize(Capture());
        _state.Set(StateKey, json, StorageScope.Workspace);
        _state.Set(StateKey, json, StorageScope.Application);
    }

    /// <summary>"Reset window layout": forgets saved layouts and restores the default tool placement and visibility.</summary>
    public async Task ResetAsync()
    {
        _state?.Remove(StateKey, StorageScope.Workspace);
        _state?.Remove(StateKey, StorageScope.Application);
        foreach (var id in _factory.FloatingMemory.Keys.ToArray()) _factory.TakeFloatingMemory(id); // nothing floats after a reset
        var tools = _tools.Tools.Select(t => new ToolLayout(t.Spec.Id, t.Spec.DefaultLocation switch
        {
            CsCode.Ui.ToolWindowLocation.Left => "left", CsCode.Ui.ToolWindowLocation.Bottom => "bottom", _ => "right",
        }, !_defaultHidden.Contains(t.Spec.Id) && t.Spec.AutoShowWhen is null, 0)).ToList();
        await ApplyAsync(new LayoutSnapshot(LayoutSnapshot.CurrentVersion, tools, 0.22, 0.24, 0.28, [], null), restoreDocuments: false).ConfigureAwait(true);
    }
}
