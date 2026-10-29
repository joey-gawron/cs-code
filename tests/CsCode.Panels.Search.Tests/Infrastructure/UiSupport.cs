using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Theming;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using CsCode.TestDoubles;

namespace CsCode.Panels.Search.Tests;

/// <summary>Colour theme service over the built-in VS2022 themes (no Editor module needed).</summary>
public sealed class StaticThemeService : IColorThemeService
{
    private readonly Emitter<ColorTheme> _changed = new();
    public ColorTheme Current { get; private set; } = VsThemes.Dark;
    public IReadOnlyList<ColorTheme> Available => VsThemes.All;
    public IEventSource<ColorTheme> Changed => _changed;
    public IDisposable Register(ColorTheme theme) => Disposable.Empty;
    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        Current = VsThemes.All.First(t => t.Id == themeId);
        _changed.Fire(Current);
        return ValueTask.CompletedTask;
    }

    public Rgba GetColor(string key) => Current.TryGetColor(key, out var c) ? c : Current.TryGetColor("foreground", out var f) ? new Rgba(f.R, f.G, f.B, 0) : new Rgba(128, 128, 128);
}

public static class UiSupport
{
    private static ThemeApplier? _applier;
    private static Application? _applierApp;
    private static readonly StaticThemeService Themes = new();

    /// <summary>Publishes the VS2022 Dark brushes as application resources (once per test run) and returns the applier.</summary>
    public static ThemeApplier EnsureTheme(ColorTheme? theme = null)
    {
        // The headless session may hand out a new Application instance between tests; the brushes live in ITS resources.
        if (_applier is not null && !ReferenceEquals(_applierApp, Application.Current))
        {
            _applier.Dispose();
            _applier = null;
        }
        _applierApp = Application.Current;
        _applier ??= new ThemeApplier(Application.Current!, Themes, AvaloniaUiDispatcher.Instance);
        if (theme is not null && _applier.AppliedThemeId != theme.Id) _applier.ApplyNow(theme);
        else if (theme is null && _applier.AppliedThemeId != VsThemes.Dark.Id) _applier.ApplyNow(VsThemes.Dark);
        return _applier;
    }

    public static string ScreenshotDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CsCode.slnx"))) dir = Path.GetDirectoryName(dir);
        var target = Path.Combine(dir ?? Path.GetTempPath(), ".scratch", "screenshots");
        Directory.CreateDirectory(target);
        return target;
    }

    /// <summary>Hosts <paramref name="view"/> in a window of the given size, lays it out, and returns the window (kept open by the caller).</summary>
    public static Window Host(Control view, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = view, Background = Avalonia.Media.Brushes.Black };
        window.Show();
        Pump();
        return window;
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static string Screenshot(Window window, string name)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(ScreenshotDir(), name + ".png");
#pragma warning disable CS0618 // Bitmap.Save(string, int?) is flagged obsolete in favour of an options overload; the PNG default is all a screenshot needs
        frame?.Save(path);
#pragma warning restore CS0618
        return path;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (condition()) return;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    public static IEnumerable<T> Descendants<T>(this Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();
}

/// <summary>A workspace with realistic-looking source files, a view-model over it, and the real engine (UI dispatcher = Avalonia's).</summary>
public sealed class UiFixture : IDisposable
{
    public SearchHarness Harness { get; } = new();
    public RecordingNavigationService Navigation { get; }
    public RecordingWindowService Windows { get; } = new();
    public InMemoryStateStorage State { get; } = new();
    public TestCommandService Commands { get; } = new();
    public FakeKeybindings Keybindings { get; } = new();
    public ReplaceService Replace { get; }
    public SearchServices Services { get; }
    public RecordingConfiguration Configuration { get; }

    public UiFixture()
    {
        Navigation = new RecordingNavigationService(Harness.Documents);
        Replace = new ReplaceService(Harness.Workspace, Harness.Documents);
        Configuration = new RecordingConfiguration(Harness.Config, Harness.Dir.Combine("user", "settings.json"));
        Services = new SearchServices
        {
            Workspace = Harness.Workspace, Ui = AvaloniaUiDispatcher.Instance, Engine = Harness.Engine, Replace = Replace, Configuration = Configuration,
            Documents = Harness.Documents, Navigation = Navigation, Windows = Windows, State = State, Commands = Commands, Keybindings = Keybindings,
            FileSystem = Harness.Workspace.FileSystem,
        };
    }

    public void WriteSampleSources()
    {
        Harness.Write("src/Program.cs", """
            using System;
            using MyApp.Services;

            namespace MyApp;

            public static class Program
            {
                public static void Main(string[] args)
                {
                    var service = new UserService();
                    var user = service.FindUser(args[0]);
                    Console.WriteLine($"Hello {user.Name}");
                }
            }
            """);
        Harness.Write("src/Services/UserService.cs", """
            namespace MyApp.Services;

            public class UserService
            {
                private readonly IUserRepository _repository;

                public UserService(IUserRepository repository) => _repository = repository;

                public User FindUser(string name) => _repository.Find(name);

                public IReadOnlyList<User> AllUsers() => _repository.All();
            }
            """);
        Harness.Write("src/Services/UserServiceTests.cs", "using Xunit;\n\npublic class UserServiceTests\n{\n    [Fact]\n    public void Finds_a_user()\n    {\n        var sut = new UserService(new FakeRepository());\n        Assert.NotNull(sut.FindUser(\"ada\"));\n    }\n}\n");
        Harness.Write("docs/architecture.md", "# Architecture\n\nThe UserService talks to the repository. See also: UserService docs.\n");
        Harness.Write("README.md", "# MyApp\n\nRun `dotnet run` to start. Uses UserService for lookups.\n");
    }

    public void Dispose()
    {
        Replace.Dispose();
        Harness.Dispose();
    }
}
