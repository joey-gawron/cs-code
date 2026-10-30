using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.Output;

/// <summary>The Output panel: channel dropdown, clear, and a virtualised monospace line list that follows the tail while the user is at the bottom.</summary>
public sealed class OutputView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    private readonly OutputViewModel _vm;
    private readonly ListBox _list;
    private readonly IWindowService? _window;
    private readonly MenuServices? _menus;
    private bool _scrollQueued, _wordWrap;

    public OutputView(OutputViewModel vm, IWindowService? window = null, MenuServices? menus = null)
    {
        _vm = vm;
        _window = window;
        _menus = menus;
        DataContext = vm;
        var combo = new ComboBox { MinWidth = 200, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(OutputViewModel.Channels)));
        combo.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(OutputViewModel.SelectedChannel)) { Mode = BindingMode.TwoWay });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4) };
        bar.Children.Add(ViewHelpers.Label("Show output from:", "descriptionForeground"));
        bar.Children.Add(combo);
        bar.Children.Add(ViewHelpers.IconButton("clear-all", "Clear Output", vm.Clear));

        // Several lines can be selected (Shift/Ctrl+click) and copied, as text is in Visual Studio's Output window.
        _list = new ListBox { ItemsSource = vm.Lines, ItemTemplate = LineTemplate(wrap: false), SelectionMode = SelectionMode.Multiple };
        vm.Lines.CollectionChanged += OnLinesChanged;
        _list.KeyDown += OnListKeyDown;
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // a batch of lines, the oldest trimmed, a line completed, Clear All or Word Wrap replaces the rows

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(bar);
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        Content = root;
    }

    private static FuncDataTemplate<string> LineTemplate(bool wrap) => new((s, _) => s is null ? new Panel() : Line(s, wrap), supportsRecycling: true);

    private static Control Line(string? text, bool wrap)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(8, 0), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        t.FontFamily = ThemeBrushes.CodeFont;
        t.FontFeatures = ThemeBrushes.CodeFontFeatures;
        return t;
    }

    // ---- Copy, Clear All, Word Wrap (Visual Studio's Output window menu)

    /// <summary>Word wrap of the lines (the menu's check item); off by default, as in Visual Studio.</summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value) return;
            _wordWrap = value;
            _list.ItemTemplate = LineTemplate(value);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        }
    }

    /// <summary>
    /// View &gt; Tool Windows &gt; Output: the keyboard on a line (the selected one, else one on screen), not on the channel picker, where Down
    /// switches the channel. With no line (an empty channel, or after Clear Output) there is none, and the shell's fallback passes the picker by
    /// for the window itself.
    /// </summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public bool CanCopy => _list.SelectedItems?.Count > 0 && _window is not null;

    /// <summary>The selected lines, in the order they appear in the window.</summary>
    internal string SelectedText()
    {
        if (_list.SelectedItems is not { Count: > 0 } selected) return "";
        var picked = selected.Cast<object>().ToHashSet();
        return string.Join("\n", _vm.Lines.Where(l => picked.Contains(l)));
    }

    public async Task CopyAsync()
    {
        if (_window is not null && SelectedText() is { Length: > 0 } text) await _window.CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    internal IReadOnlyList<MenuEntry> Menu() => MenuSections.Join(
        ClipboardKeys.Entries(this),
        [
            new MenuEntry("Clear _All", () => { _vm.Clear(); return Task.CompletedTask; }) { Icon = "clear-all", IsEnabled = _vm.Lines.Count > 0 },
            new MenuEntry("_Word Wrap", () => { WordWrap = !WordWrap; return Task.CompletedTask; }) { Icon = "word-wrap", IsChecked = WordWrap },
        ],
        _menus is null ? null : MenuContributions.Resolve(_menus, Core.Contributions.MenuIds.OutputContext, _vm.SelectedChannel is { } c ? [c] : []));

    private void OnListKeyDown(object? sender, KeyEventArgs e) => ClipboardKeys.TryHandle(e, this);

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ContextMenus.Open(Menu(), _list, accessibleName: "Output menu");
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.AutoScroll || _scrollQueued || _vm.Lines.Count == 0) return;
        _scrollQueued = true; // one scroll per UI turn, however many appends arrived
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm.Lines.Count > 0) _list.ScrollIntoView(_vm.Lines[^1]);
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _vm.Lines.CollectionChanged -= OnLinesChanged;
        _list.KeyDown -= OnListKeyDown;
        _list.ContextRequested -= OnContextRequested;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}
