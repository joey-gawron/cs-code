using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using CsCode.Core.Lifecycle;
using CsCode.Core.Theming;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>Colour theme service double: a registered theme plus a neutral grey for ids a theme does not define.</summary>
public sealed class TestColorThemeService : IColorThemeService
{
    private readonly List<ColorTheme> _themes = new();
    private readonly Emitter<ColorTheme> _changed = new();
    private ColorTheme? _current;

    public ColorTheme Current => _current ?? throw new InvalidOperationException("No theme registered");
    public IReadOnlyList<ColorTheme> Available => _themes;
    public IEventSource<ColorTheme> Changed => _changed;

    public IDisposable Register(ColorTheme theme)
    {
        _themes.Add(theme);
        _current ??= theme;
        return Disposable.Create(() => _themes.Remove(theme));
    }

    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        _current = _themes.First(t => t.Id == themeId);
        _changed.Fire(_current);
        return ValueTask.CompletedTask;
    }

    public Rgba GetColor(string key) => Current.TryGetColor(key, out var c) ? c : new Rgba(128, 128, 128);
}

/// <summary>Applies the VS2022 themes to the headless application exactly like the shell does (the <c>vsc:*</c> brushes the views bind).</summary>
public static class TestTheme
{
    private static Application? s_app;
    private static ThemeApplier? s_applier;
    private static TestColorThemeService? s_themes;

    public static void Apply(ColorTheme theme)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No Avalonia application");
        // The headless session may hand each test a fresh Application: the brushes must be published into the one the test uses.
        if (s_applier is null || s_themes is null || !ReferenceEquals(s_app, app))
        {
            s_applier?.Dispose();
            s_app = app;
            s_themes = new TestColorThemeService();
            VsThemes.RegisterAll(s_themes);
            s_applier = new ThemeApplier(app, s_themes, AvaloniaUiDispatcher.Instance);
        }
        s_themes.SetCurrentAsync(theme.Id).AsTask().GetAwaiter().GetResult();
        s_applier.ApplyNow(theme);
    }

    public static string ScreenshotDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx")))
            {
                var path = Path.Combine(dir.FullName, ".scratch", "screenshots");
                Directory.CreateDirectory(path);
                return path;
            }
        throw new InvalidOperationException("Repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Shows <paramref name="content"/> in a window of the given size, lets layout settle and writes a PNG.</summary>
    public static async Task<string> ShotAsync(Control content, string name, double width = 900, double height = 260)
    {
        var window = new Window { Width = width, Height = height, Content = content, WindowDecorations = WindowDecorations.None };
        window.Show();
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame captured");
        var path = Path.Combine(ScreenshotDirectory(), name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Content = null;
        window.Close();
        return path;
    }
}
