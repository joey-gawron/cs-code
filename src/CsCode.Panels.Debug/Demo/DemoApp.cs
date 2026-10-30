using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Dap;
using CsCode.Dap.Adapters;
using CsCode.TestDoubles;
using CsCode.Ui;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug.Demo;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<DemoApp>().UsePlatformDetect().WithInterFont().WithCodeFonts().LogToTrace().StartWithClassicDesktopLifetime(args);
}

/// <summary>Colour theme service for the demo: the three built-in VS2022 themes, grey for anything a theme does not define.</summary>
internal sealed class DemoThemes : IColorThemeService
{
    private readonly List<ColorTheme> _themes = new();
    private readonly Emitter<ColorTheme> _changed = new();
    private ColorTheme? _current;
    public ColorTheme Current => _current ?? throw new InvalidOperationException();
    public IReadOnlyList<ColorTheme> Available => _themes;
    public IEventSource<ColorTheme> Changed => _changed;
    public IDisposable Register(ColorTheme theme) { _themes.Add(theme); _current ??= theme; return Disposable.Create(() => _themes.Remove(theme)); }
    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default) { _current = _themes.First(t => t.Id == themeId); _changed.Fire(_current); return ValueTask.CompletedTask; }
    public Rgba GetColor(string key) => Current.TryGetColor(key, out var c) ? c : new Rgba(128, 128, 128);
}

/// <summary>Minimal window service: clipboard through Avalonia, everything else answers "nothing".</summary>
internal sealed class DemoWindowService : IWindowService
{
    public string? Status { get; private set; }
    public event Action<string>? StatusChanged;
    public Func<string, ValueTask>? Clipboard { get; set; }
    public ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default) { Publish(message); return ValueTask.FromResult<string?>(null); }
    public ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<QuickPickItem?>(null);
    public ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<QuickPickItem>?>(null);
    public ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    public ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>?>(null);
    public ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default) => ValueTask.FromResult<Uri?>(null);
    public Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work) => work(new Quiet(), CancellationToken.None);
    public IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null) { Publish(text); return Disposable.Empty; }
    public ValueTask<bool> OpenExternalAsync(Uri uri) => ValueTask.FromResult(false);
    public ValueTask CopyToClipboardAsync(string text) => Clipboard?.Invoke(text) ?? ValueTask.CompletedTask;
    private void Publish(string text) { Status = text; StatusChanged?.Invoke(text); }
    private sealed class Quiet : IProgressReporter { public void Report(string? message = null, double? percent = null) { } }
}

internal sealed class DemoApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var themes = new DemoThemes();
            VsThemes.RegisterAll(themes);
            _ = new ThemeApplier(this, themes, AvaloniaUiDispatcher.Instance);
            var window = new DemoWindow(themes);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => window.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>The debug panels in a real window next to a tiny debug toolbar, driven by the real DAP module and netcoredbg.</summary>
internal sealed class DemoWindow : Window
{
    private const string Program = """
        using System;
        using System.Collections.Generic;

        namespace Sample;

        public sealed class Item
        {
            public string Name { get; set; } = "widget";
            public int Count { get; set; } = 3;
            public string[] Tags { get; set; } = { "red", "round" };
        }

        public static class Program
        {
            public static int Add(int a, int b)
            {
                var sum = a + b; // @add
                return sum;
            }

            public static int Outer(int n)
            {
                var inner = Add(n, 1);
                return inner * 2;
            }

            public static int Main()
            {
                int x = 41;
                string s = "hello world";
                var item = new Item();
                var numbers = new List<int>();
                for (int i = 0; i < 5; i++)
                {
                    x += i;
                    numbers.Add(x * 3);
                    Console.WriteLine($"loop {i}: x = {x}");
                }
                var r = Outer(x);
                Console.WriteLine($"done {s} {r} {item.Name} {numbers.Count}");
                return 7;
            }
        }
        """;

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><UseAppHost>false</UseAppHost><Optimize>false</Optimize>
            <DebugType>portable</DebugType><ImplicitUsings>disable</ImplicitUsings><Nullable>enable</Nullable><InvariantGlobalization>true</InvariantGlobalization>
          </PropertyGroup>
        </Project>
        """;

    private readonly ServiceProvider _provider;
    private readonly IDebugService _debug;
    private readonly DemoWindowService _window = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cscode-panels-demo-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Control> _views = new();
    private readonly List<Button> _runButtons = new();
    private string? _dll, _source;

    public DemoWindow(IColorThemeService themes)
    {
        Title = "CsCode debug panels (demo, real netcoredbg)";
        Width = 1400;
        Height = 900;

        var documents = new DocumentManager();
        var services = new ServiceCollection();
        var settings = new TestConfigurationService();
        if (FindNetCoreDbg() is { } exe) settings.Set(NetCoreDbgAdapterFactory.SettingPath, exe);
        services.AddSingleton<IConfigurationService>(settings);
        services.AddSingleton<IWindowService>(_window);
        services.AddSingleton<ICommandService, TestCommandService>();
        services.AddSingleton<IContextKeyService, TestContextKeyService>();
        services.AddSingleton<IOutputService, InMemoryOutputService>();
        services.AddSingleton<INavigationService>(new RecordingNavigationService(documents));
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<IAppPaths>(new TestAppPaths());
        services.AddSingleton<IUiDispatcher>(AvaloniaUiDispatcher.Instance);
        CsCode.Dap.ServiceCollectionExtensions.AddCsCodeDap(services);
        CsCode.Panels.Debug.ServiceCollectionExtensions.AddCsCodeDebugPanels(services);
        _provider = services.BuildServiceProvider();
        _debug = _provider.GetRequiredService<IDebugService>();
        _debug.AddWatch("x + 1");
        _debug.AddWatch("a + b");
        _window.StatusChanged += text => AvaloniaUiDispatcher.Instance.Post(() => _status.Text = text);
        _window.Clipboard = async text => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } c) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(c, text); };

        var tools = _provider.GetServices<IToolWindow>().ToDictionary(t => t.Id);
        Control Panel(string id)
        {
            var view = tools[id].CreateView(_provider);
            _views.Add(view);
            var title = new TextBlock { Text = tools[id].Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("vsc:tab.activeForeground"));
            var header = new Border { Height = 24, Child = title };
            header.Bind(Border.BackgroundProperty, new DynamicResourceExtension("vsc:tab.activeBackground"));
            var rule = new Border { Height = 2 };
            rule.Bind(Border.BackgroundProperty, new DynamicResourceExtension("vsc:tab.activeBorderTop"));
            var grid = new Grid { RowDefinitions = new RowDefinitions("2,24,*") };
            Grid.SetRow(header, 1);
            Grid.SetRow(view, 2);
            grid.Children.Add(rule);
            grid.Children.Add(header);
            grid.Children.Add(view);
            var frame = new Border { BorderThickness = new Thickness(1), Child = grid };
            frame.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("vsc:panel.border"));
            return frame;
        }

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("*,*,1.2*") };
        void Place(Control c, int row, int col, int span = 1) { Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span); layout.Children.Add(c); }
        Place(Panel("debug.locals"), 0, 0);
        Place(Panel("debug.callstack"), 0, 1);
        Place(Panel("debug.watch"), 1, 0);
        Place(Panel("debug.breakpoints"), 1, 1);
        Place(Panel("debug.console"), 2, 0, 2);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
        var start = Tool("Start", async () => await StartAsync());
        _runButtons.AddRange([start,
            Tool("Continue", () => WithSession(s => s.ContinueAsync())),
            Tool("Step Over", () => WithSession(s => s.StepOverAsync())),
            Tool("Step Into", () => WithSession(s => s.StepIntoAsync())),
            Tool("Step Out", () => WithSession(s => s.StepOutAsync())),
            Tool("Stop", async () => await _debug.StopAllAsync())]);
        foreach (var b in _runButtons) bar.Children.Add(b);
        bar.Children.Add(_status);
        var barBorder = new Border { Child = bar };
        barBorder.Bind(Border.BackgroundProperty, new DynamicResourceExtension("vsc:toolbar.background"));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(layout, 1);
        root.Children.Add(barBorder);
        root.Children.Add(layout);
        root.Bind(Avalonia.Controls.Panel.BackgroundProperty, new DynamicResourceExtension("vsc:panel.background"));
        Content = root;

        _status.Text = "Preparing the sample program…";
        Opened += async (_, _) => await PrepareAsync();
    }

    private static Button Tool(string text, Func<Task> run)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3) };
        button.Click += async (_, _) =>
        {
            try { await run(); }
            catch (Exception) { /* the panels and the console show failures; a toolbar click must never crash the demo */ }
        };
        return button;
    }

    private async Task WithSession(Func<IDebugSession, ValueTask> action)
    {
        if (_debug.ActiveSession is { } session) await action(session);
    }

    private static string? FindNetCoreDbg()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".scratch", "tools", "netcoredbg", NetCoreDbgAdapterFactory.ExecutableName);
            if (File.Exists(candidate)) return candidate;
        }
        return Environment.GetEnvironmentVariable("CSCODE_NETCOREDBG") is { Length: > 0 } p && File.Exists(p) ? p : null;
    }

    /// <summary>Builds the sample with <c>dotnet build</c> (asynchronously) and puts a breakpoint in <c>Add</c>.</summary>
    private async Task PrepareAsync()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            await File.WriteAllTextAsync(Path.Combine(_dir, "Sample.csproj"), Project);
            _source = Path.Combine(_dir, "Program.cs");
            await File.WriteAllTextAsync(_source, Program);
            var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "build", "Sample.csproj", "-c", "Debug", "-o", "out", "--nologo", "-v", "q" }) psi.ArgumentList.Add(a);
            using var build = Process.Start(psi)!;
            var output = build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { _status.Text = "Building the sample failed: " + (await output).Trim(); return; }
            _dll = Path.Combine(_dir, "out", "Sample.dll");
            var line = Program.Split('\n').ToList().FindIndex(l => l.Contains("// @add", StringComparison.Ordinal));
            _debug.Breakpoints.Toggle(new Uri(_source), line);
            if (FindNetCoreDbg() is null) _status.Text = "netcoredbg not found: run tools/fetch-netcoredbg.sh, then restart this demo.";
            else if (Environment.GetCommandLineArgs().Contains("--start")) await StartAsync();
            else _status.Text = "Ready. A breakpoint is set in Add(); press Start (or run the demo with --start).";
        }
        catch (Exception ex) { _status.Text = "Preparing the sample failed: " + ex.Message; }
    }

    private async Task StartAsync()
    {
        if (_dll is null) return;
        var config = DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "coreclr", ["request"] = "launch", ["name"] = "Sample", ["program"] = _dll, ["cwd"] = _dir,
            ["args"] = Array.Empty<string>(), ["stopAtEntry"] = false, ["justMyCode"] = true, ["console"] = "internalConsole",
        }));
        _status.Text = "Starting…";
        var session = await _debug.StartAsync(config);
        _status.Text = session is null ? "The session did not start." : "Debugging.";
    }

    public void Shutdown()
    {
        foreach (var v in _views) (v as IDisposable)?.Dispose();
        try { _debug.StopAllAsync().AsTask().Wait(3000); }
        catch (Exception) { /* closing */ }
        _provider.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
