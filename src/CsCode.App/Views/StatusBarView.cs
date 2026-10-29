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
        Rebuild(_service.Items);
    }

    private void Rebuild(IReadOnlyList<StatusBarItemModel> items)
    {
        _left.Children.Clear();
        _right.Children.Clear();
        var fg = ThemeBrushes.Get(_state switch { StatusBarState.Debugging => "statusBar.debuggingForeground", StatusBarState.NoFolder => "statusBar.noFolderForeground", _ => "statusBar.foreground" });
        foreach (var item in items)
        {
            if (!item.IsVisible) continue;
            var content = ViewHelpers.RichText(item.Text, item.ColorKey is { } ck ? ThemeBrushes.Get(ck) : fg);
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
            (item.Alignment == StatusBarAlignment.Left ? _left : _right).Children.Add(element);
        }
        // Right-aligned items are listed highest priority first = rightmost, so render them in reverse.
        var rev = _right.Children.Reverse().ToArray();
        _right.Children.Clear();
        _right.Children.AddRange(rev);
    }

    public void Dispose()
    {
        _subs.Dispose();
        _left.Children.Clear();
        _right.Children.Clear();
    }
}
