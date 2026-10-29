using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Panels.Search.ViewModels;
using CsCode.Ui.Collections;
using CsCode.Ui.Text;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.Settings;

/// <summary>One line of the shortcuts table.</summary>
/// <param name="Command">Command id (or "-id" for a binding that removes a default one).</param>
/// <param name="Title">The command's palette title when known, else the id.</param>
public sealed record ShortcutRow(string Command, string Title, string Keys, string When, string Source, bool IsRemoval);

/// <summary>Formats key chords for display ("Ctrl+K, Ctrl+C").</summary>
public static class KeyLabel
{
    public static string Format(IEnumerable<KeyStroke> chord) => string.Join(", ", chord.Select(Format));

    public static string Format(KeyStroke s)
    {
        var parts = new List<string>(4);
        if (s.Ctrl) parts.Add("Ctrl");
        if (s.Shift) parts.Add("Shift");
        if (s.Alt) parts.Add("Alt");
        if (s.Meta) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Win");
        parts.Add(KeyName(s.Key));
        return string.Join('+', parts);
    }

    private static string KeyName(string key) => key switch
    {
        { Length: 1 } => key.ToUpperInvariant(),
        ['f', .. var n] when int.TryParse(n, out _) => "F" + n,
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "numpad_add" => "Num +",
        _ when key.StartsWith("numpad", StringComparison.Ordinal) => "Num " + key[6..],
        _ => char.ToUpperInvariant(key[0]) + key[1..],
    };
}

/// <summary>
/// The Keyboard Shortcuts view: a filterable table of <see cref="IKeybindingService.All"/> (command, key, when, source). Rebuilt (coalesced)
/// when bindings or the command table change. Editing bindings is done in keybindings.json (the button opens it).
/// </summary>
public sealed class KeyboardShortcutsViewModel : ObservableObject, IDisposable
{
    private readonly SearchServices _s;
    private readonly DisposableStore _subs = new();
    private readonly string _refreshKey = "keybindings.refresh." + Guid.NewGuid().ToString("N");
    private List<ShortcutRow> _all = new();
    private string _filter = "";
    private bool _disposed;

    public KeyboardShortcutsViewModel(SearchServices services)
    {
        _s = services;
        OpenJsonCommand = new RelayCommand(() => _ = OpenKeybindingsJsonAsync());
        if (services.Keybindings is { } keys)
            _subs.Add(keys.Changed.Subscribe(args => services.Ui.PostCoalesced(_refreshKey, Reload, UiPriority.Background)));
        if (services.Commands is { } cmds)
            _subs.Add(cmds.Registered.Subscribe(id => services.Ui.PostCoalesced(_refreshKey, Reload, UiPriority.Background)));
        Reload();
    }

    public BulkObservableCollection<ShortcutRow> Rows { get; } = new();
    public ICommand OpenJsonCommand { get; }
    public int TotalCount => _all.Count;
    public int ShownCount => Rows.Count;

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) ApplyFilter(); }
    }

    public void Reload()
    {
        if (_disposed) return;
        var titles = (_s.Commands?.All ?? []).ToDictionary(m => m.Id, m => m.Title ?? m.Id, StringComparer.Ordinal);
        var rows = new List<ShortcutRow>();
        foreach (var b in _s.Keybindings?.All ?? [])
        {
            var id = b.IsNegative ? "-" + b.Command.TrimStart('-') : b.Command;
            var plain = b.Command.TrimStart('-');
            rows.Add(new ShortcutRow(id, titles.GetValueOrDefault(plain, plain), KeyLabel.Format(b.Chord), b.When ?? "", b.Source == KeyBindingSource.User ? "User" : "Default", b.IsNegative));
        }
        _all = rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Keys, StringComparer.Ordinal).ToList();
        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
    }

    private void ApplyFilter()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { Rows.ReplaceAll(_all); OnPropertyChanged(nameof(ShownCount)); return; }
        var scored = new List<(ShortcutRow Row, int Score)>();
        foreach (var r in _all)
        {
            var hay = r.Title + " " + r.Command + " " + r.Keys + " " + r.When + " " + r.Source;
            var total = 0;
            var ok = true;
            foreach (var w in words)
            {
                // Keys like "ctrl+k" are matched against the label ("Ctrl+K") case-insensitively and as a plain substring first.
                var s = hay.Contains(w, StringComparison.OrdinalIgnoreCase) ? 100 : FuzzyMatcher.Score(w, hay);
                if (s < 0) { ok = false; break; }
                total += s;
            }
            if (ok) scored.Add((r, total));
        }
        Rows.ReplaceAll(scored.OrderByDescending(x => x.Score).Select(x => x.Row).ToList());
        OnPropertyChanged(nameof(ShownCount));
    }

    public async Task OpenKeybindingsJsonAsync()
    {
        if (_s.Commands is { } c && c.Has("workbench.action.openGlobalKeybindingsFile"))
            await c.ExecuteAsync("workbench.action.openGlobalKeybindingsFile");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        Rows.Clear();
        _all.Clear();
    }
}
