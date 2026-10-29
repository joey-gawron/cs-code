using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using CsCode.Panels.Search.Settings;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Search.Views;

/// <summary>The Settings document view. Disposed by the shell when its tab closes; disposing flushes edits still waiting for their debounce.</summary>
public sealed partial class SettingsView : UserControl, IDisposable
{
    public static readonly IValueConverter IndentConverter = new FuncValueConverter<int, Thickness>(level => new Thickness(14 + 16 * level, 0, 0, 0));
    public static readonly IValueConverter NotBooleanConverter = new FuncValueConverter<SettingKind, bool>(k => k != SettingKind.Boolean);

    private readonly Action<SettingsViewModel>? _release;
    private bool _disposed;

    public SettingsView() : this(null!, null) { }

    public SettingsView(SettingsViewModel viewModel, Action<SettingsViewModel>? release = null)
    {
        _release = release;
        InitializeComponent();
        if (viewModel is not null) DataContext = viewModel;
        if (viewModel?.Themes is { } themes)
        {
            ThemePicker = new ThemePicker(themes, viewModel.Ui, viewModel.Configuration) { MinWidth = 280, MinHeight = 26, Padding = new Thickness(4, 1) };
            ThemePickerHost.Content = ThemePicker;
            AppearanceSection.IsVisible = true;
        }
    }

    /// <summary>The Environment &gt; Appearance Color Theme dropdown (null without a colour theme service).</summary>
    public ThemePicker? ThemePicker { get; }

    /// <summary>The gear button of a setting: Reset / Copy ID / Edit in settings.json (a ContextMenu built on demand, so no flyout DataContext tricks).</summary>
    private void OnGearClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingItemViewModel item } button) return;
        var items = new List<Control>
        {
            new MenuItem { Header = "Reset Setting", IsEnabled = item.IsModified, Command = item.ResetCommand },
            new MenuItem { Header = "Copy Setting ID", Command = item.CopyIdCommand },
            new MenuItem { Header = "Edit in settings.json", Command = item.EditInJsonCommand },
        };
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemePicker?.Dispose();
        if (DataContext is SettingsViewModel vm)
        {
            if (_release is not null) _release(vm); else vm.Dispose();
        }
    }
}
