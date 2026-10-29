using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Ui.Threading;

namespace CsCode.Ui.Theming;

/// <summary>
/// The Color Theme dropdown (toolbar and Settings page): every installed theme, grouped Dark / Light / High contrast, with a swatch per entry.
/// Arrowing through the open list previews live (<see cref="IColorThemePreview"/>: nothing is persisted); Enter or a click applies and persists
/// <c>workbench.colorTheme</c>; Escape or clicking away reverts. Arrow keys on the closed, focused box apply directly, like Visual Studio's combos.
/// The box follows every other way of changing the theme (palette command, settings.json, another window) through
/// <see cref="IColorThemeService.Changed"/>. UI-thread control; dispose it with its owner.
/// </summary>
public sealed class ThemePicker : ComboBox, IDisposable
{
    private static int _instances;

    private readonly IColorThemeService _themes;
    private readonly IConfigurationService? _config;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _subs = new();
    private readonly string _syncKey = "themepicker.sync." + Interlocked.Increment(ref _instances);
    private ColorTheme[] _builtFrom = [];
    private readonly ThemePreviewer _previewer;
    private bool _syncing, _commitOnClose, _disposed;
    private string? _selectionWhenOpened;

    protected override Type StyleKeyOverride => typeof(ComboBox);

    public ThemePicker(IColorThemeService themes, IUiDispatcher ui, IConfigurationService? config = null, TimeProvider? time = null)
    {
        _themes = themes;
        _ui = ui;
        _config = config;
        _previewer = new ThemePreviewer(themes, ui, time);
        MaxDropDownHeight = 460;
        ItemTemplate = new FuncDataTemplate<ThemeListItem>((item, _) => item switch
        {
            ThemeGroupItem g => GroupHeader(g),
            ThemeChoiceItem c => ChoiceRow(c),
            _ => new TextBlock(),
        });
        ToolTip.SetTip(this, "Color Theme");
        Avalonia.Automation.AutomationProperties.SetName(this, "Color Theme");

        SelectionChanged += OnSelectionChanged;
        DropDownOpened += OnDropDownOpened;
        DropDownClosed += OnDropDownClosed;
        // Tunnel: the commit gestures must be seen before the ComboBox acts on them (it selects and closes in its own handlers).
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnItemGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        Sync();
        // Changed may fire on any thread (a settings.json edit arrives on the IO lane): one coalesced UI post per burst.
        _subs.Add(themes.Changed.Subscribe(_ => _ui.PostCoalesced(_syncKey, Sync)));
    }

    /// <summary>The theme entries the list currently holds (tests and the Settings page read it).</summary>
    public IEnumerable<ThemeChoiceItem> Choices => (ItemsSource as IEnumerable<ThemeListItem> ?? []).OfType<ThemeChoiceItem>();

    /// <summary>Id of the theme the box shows as selected.</summary>
    public string? SelectedThemeId => (SelectedItem as ThemeChoiceItem)?.Id;

    // ------------------------------------------------------------------ list

    /// <summary>Rebuilds the entries when the set of themes changed (a user theme was added, the shell completed a theme): never per switch.</summary>
    private void RebuildIfChanged()
    {
        var available = _themes.Available;
        if (available.Count == _builtFrom.Length && !available.Where((t, i) => !ReferenceEquals(t, _builtFrom[i])).Any()) return;
        _builtFrom = available.ToArray();
        var items = ThemeSelection.BuildItems(_builtFrom);
        _syncing = true;
        try { ItemsSource = items; }
        finally { _syncing = false; }
    }

    /// <summary>Shows the current theme as the selection (not while the list is open: the user is choosing).</summary>
    private void Sync()
    {
        if (_disposed || IsDropDownOpen) return;
        RebuildIfChanged();
        var current = _themes.Current.Id;
        if (SelectedThemeId == current) return;
        _syncing = true;
        try { SelectedItem = Choices.FirstOrDefault(c => c.Id == current); }
        finally { _syncing = false; }
    }

    private static Control GroupHeader(ThemeGroupItem g) => new TextBlock
    {
        Text = g.Title, FontWeight = FontWeight.SemiBold, FontSize = 11, Margin = new Thickness(0, 4, 0, 1),
        Foreground = ThemeBrushes.Get("descriptionForeground"), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Control ChoiceRow(ThemeChoiceItem c)
    {
        var bars = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1, Margin = new Thickness(3, 2), VerticalAlignment = VerticalAlignment.Center };
        var widths = new[] { 10.0, 7.0, 12.0 };
        for (var i = 0; i < c.Accents.Count; i++) bars.Children.Add(new Border { Height = 2, Width = widths[i % widths.Length], Background = c.Accents[i], HorizontalAlignment = HorizontalAlignment.Left });
        var swatch = new Border
        {
            Width = 18, Height = 14, Background = c.Background, BorderBrush = c.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, Child = bars,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Children = { swatch, new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } },
        };
    }

    // Group headers are part of the list but neither focusable nor selectable (arrowing skips them).
    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        var header = item is ThemeGroupItem;
        container.IsEnabled = !header;
        container.Focusable = !header;
        container.MinHeight = header ? 18 : 24;
        if (container is ContentControl cc) cc.Padding = header ? new Thickness(8, 2) : new Thickness(4, 1);
    }

    // ------------------------------------------------------------------ preview / commit / revert

    private void OnDropDownOpened(object? sender, EventArgs e)
    {
        RebuildIfChanged();
        _selectionWhenOpened = _themes.Current.Id;
        _previewer.Cancel();
        _commitOnClose = false;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (!IsDropDownOpen) return;
        if (e.Key is Key.Enter or Key.Space) _commitOnClose = true;
        else if (e.Key == Key.Escape) _commitOnClose = false;
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        if (IsDropDownOpen && e.Source is Visual v && v.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is { IsEnabled: true }) _commitOnClose = true;
    }

    private void OnItemGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!IsDropDownOpen || e.Source is not Visual v || v.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is not { } container) return;
        if (ItemFromContainer(container) is ThemeChoiceItem c) PreviewTheme(c.Id);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _disposed) return;
        if (SelectedItem is not ThemeChoiceItem choice)
        {
            _ui.PostCoalesced(_syncKey, Sync); // a header can never be the selection
            return;
        }
        if (IsDropDownOpen) PreviewTheme(choice.Id); // applied when the list closes with Enter or a click
        else _ = CommitAsync(choice.Id);
    }

    private void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var commit = _commitOnClose && SelectedItem is ThemeChoiceItem;
        _commitOnClose = false;
        var previewed = _previewer.Shown is not null;
        _previewer.Cancel(); // a preview still waiting for its interval must not land after the commit or the revert
        if (commit) _ = CommitAsync(((ThemeChoiceItem)SelectedItem!).Id);
        else
        {
            if (_selectionWhenOpened is { } original && (previewed || _themes.Current.Id != original)) ThemeSelection.EndPreview(_themes, original);
            _ui.PostCoalesced(_syncKey, Sync);
        }
    }

    private void PreviewTheme(string id) => _previewer.Request(id);

    private async Task CommitAsync(string id)
    {
        try { await ThemeSelection.CommitAsync(_themes, _config, id).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The theme is applied; only persisting failed (read-only or broken settings.json). The platform reports settings file errors itself.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        // Disposed with its list open (its tab closed meanwhile): the preview it started must not outlive it.
        if (IsDropDownOpen && _previewer.Shown is not null && _selectionWhenOpened is { } original) ThemeSelection.EndPreview(_themes, original);
        _previewer.Dispose();
        SelectionChanged -= OnSelectionChanged;
        DropDownOpened -= OnDropDownOpened;
        DropDownClosed -= OnDropDownClosed;
    }
}
