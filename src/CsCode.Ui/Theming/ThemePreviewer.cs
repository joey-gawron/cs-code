using CsCode.Core.Theming;
using CsCode.Ui.Threading;

namespace CsCode.Ui.Theming;

/// <summary>
/// The live preview of one theme picker while the user arrows through it. Holding an arrow key repeats about 30 times a second, and every theme
/// switch makes each control re-resolve its resources (several milliseconds of UI work), so previews are shown at most once per
/// <see cref="MinInterval"/>: the first at once, the latest of a burst when the interval has passed (latest wins). UI-thread object.
/// </summary>
public sealed class ThemePreviewer : IDisposable
{
    /// <summary>About 8 previews a second: the list still feels live, and theme switching stays a small share of the UI thread.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    private readonly IColorThemeService _themes;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private long _lastShown;
    private string? _shown, _pending;
    private ITimer? _timer;
    private int _generation;

    public ThemePreviewer(IColorThemeService themes, IUiDispatcher ui, TimeProvider? time = null)
    {
        _themes = themes;
        _ui = ui;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Id of the theme the last preview showed (null when none since the last <see cref="Cancel"/>).</summary>
    public string? Shown => _shown;

    /// <summary>Asks to show <paramref name="themeId"/>; shown now, or when the interval since the last preview has passed.</summary>
    public void Request(string themeId)
    {
        if (_pending is null && themeId == _shown) return;
        var elapsed = _lastShown == 0 ? MinInterval : _time.GetElapsedTime(_lastShown);
        if (elapsed >= MinInterval)
        {
            _pending = null;
            Show(themeId);
            return;
        }
        _pending = themeId;
        if (_timer is not null) return;
        var generation = _generation;
        _timer = _time.CreateTimer(_ => _ui.Post(() => Flush(generation)), null, MinInterval - elapsed, Timeout.InfiniteTimeSpan);
    }

    private void Flush(int generation)
    {
        if (generation != _generation) return; // cancelled meanwhile (the list closed)
        _timer?.Dispose();
        _timer = null;
        if (_pending is { } id)
        {
            _pending = null;
            Show(id);
        }
    }

    private void Show(string id)
    {
        _lastShown = _time.GetTimestamp();
        _shown = id;
        ThemeSelection.Preview(_themes, id);
    }

    /// <summary>Drops a pending preview (the picker commits or reverts next). Does not end the service's preview.</summary>
    public void Cancel()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _pending = null;
        _shown = null;
        _lastShown = 0;
    }

    public void Dispose() => Cancel();
}
