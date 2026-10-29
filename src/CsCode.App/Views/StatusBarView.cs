using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;

namespace CsCode.App.Views;

public enum StatusBarState { Normal, Debugging, NoFolder }

/// <summary>
/// The VS status bar: blue normally, orange while debugging, purple when no folder is open. Items come from <see cref="IStatusBarService"/>
/// (left/right, by priority); text may contain <c>$(icon)</c> runs; items with a command id are clickable. The bar rebuilds only when the
/// service reports a change (already coalesced to one per UI turn), and everything is disposed with the view.
/// </summary>
public sealed class StatusBarView : Border, IDisposable
{
    private readonly IStatusBarService _service;
    private readonly ICommandService _commands;
    private readonly IContextKeyService _keys;
    private readonly DisposableStore _subs = new();
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal };
    private StatusBarState _state = StatusBarState.Normal;

    public StatusBarView(IStatusBarService service, ICommandService commands, IContextKeyService keys, IUiDispatcher ui)
    {
        _service = service;
        _commands = commands;
        _keys = keys;
        Height = 22;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _right.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(_left);
        Grid.SetColumn(_right, 1);
        grid.Children.Add(_right);
        Child = grid;
        ApplyState(ComputeState());
        Rebuild(service.Items);
        _subs.Add(service.Changed.Subscribe(items => Rebuild(items)));
        _subs.Add(keys.Changed.Subscribe(changed =>
        {
            if (changed.Contains("inDebugMode") || changed.Contains("workspaceFolderCount")) ui.Post(() => ApplyState(ComputeState()));
        }));
    }

    public StatusBarState State => _state;

    private StatusBarState ComputeState() =>
        _keys.Get("inDebugMode") is true ? StatusBarState.Debugging
        : _keys.Get("workspaceFolderCount") is int n && n == 0 ? StatusBarState.NoFolder
        : StatusBarState.Normal;

    private void ApplyState(StatusBarState state)
    {
        _state = state;
        var (bg, fg) = state switch
        {
            StatusBarState.Debugging => ("statusBar.debuggingBackground", "statusBar.debuggingForeground"),
            StatusBarState.NoFolder => ("statusBar.noFolderBackground", "statusBar.noFolderForeground"),
            _ => ("statusBar.background", "statusBar.foreground"),
        };
        Background = ThemeBrushes.Get(bg);
        TextElement.SetForeground(this, ThemeBrushes.Get(fg));
        Rebuild(_service.Items, force: true); // the foreground changed: every item's content must be recreated once
    }

    // Incremental: elements are keyed by item id and only touched when their model changed, so a status update (caret move, diagnostics
    // count) costs one small content rebuild instead of destroying and recreating the whole bar.
    private sealed class Entry(Control element, StatusBarItemModel model)
    {
        public Control Element { get; } = element;
        public StatusBarItemModel Model { get; set; } = model;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private IBrush ForegroundBrush => ThemeBrushes.Get(_state switch { StatusBarState.Debugging => "statusBar.debuggingForeground", StatusBarState.NoFolder => "statusBar.noFolderForeground", _ => "statusBar.foreground" });

    private void Rebuild(IReadOnlyList<StatusBarItemModel> items, bool force = false)
    {
        if (force) _entries.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var left = new List<Control>();
        var right = new List<Control>();
        foreach (var item in items)
        {
            if (!item.IsVisible) continue;
            seen.Add(item.Id);
            if (_entries.TryGetValue(item.Id, out var entry))
            {
                if (!entry.Model.Equals(item))
                {
                    var rebuilt = Create(item);
                    entry = _entries[item.Id] = new Entry(rebuilt, item);
                }
            }
            else _entries[item.Id] = entry = new Entry(Create(item), item);
            (item.Alignment == StatusBarAlignment.Left ? left : right).Add(entry.Element);
        }
        foreach (var gone in _entries.Keys.Where(k => !seen.Contains(k)).ToArray()) _entries.Remove(gone);

        // Right-aligned items are listed highest priority first = rightmost, so render them in reverse.
        right.Reverse();
        SyncChildren(_left, left);
        SyncChildren(_right, right);
    }

    private static void SyncChildren(Panel panel, List<Control> desired)
    {
        var same = panel.Children.Count == desired.Count;
        for (var i = 0; same && i < desired.Count; i++) same = ReferenceEquals(panel.Children[i], desired[i]);
        if (same) return;
        panel.Children.Clear();
        panel.Children.AddRange(desired);
    }

    private Control Create(StatusBarItemModel item)
    {
        var content = ViewHelpers.RichText(item.Text, item.ColorKey is { } ck ? ThemeBrushes.Get(ck) : ForegroundBrush);
        Control element;
        if (item.CommandId is { } cmd)
        {
            var b = new Button { Content = content, Padding = new Thickness(7, 0), MinHeight = 0, Height = 22, CornerRadius = new CornerRadius(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            b.Classes.Add("statusitem");
            b.Click += async (_, _) => { try { await _commands.ExecuteAsync(cmd).ConfigureAwait(true); } catch (CommandNotFoundException) { } };
            element = b;
        }
        else element = new Border { Child = content, Padding = new Thickness(7, 0), Height = 22 };
        if (item.Tooltip is { } tip) ToolTip.SetTip(element, tip);
        return element;
    }

    public void Dispose()
    {
        _subs.Dispose();
        _entries.Clear();
        _left.Children.Clear();
        _right.Children.Clear();
    }
}
