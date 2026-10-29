using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// The whole module wired through its real DI registration (<c>AddCsCodeEditor</c>) over the shared fakes, with real windows: for headless UI
/// tests that type into a real <see cref="CodeEditorControl"/>, run commands and inspect rendering.
/// </summary>
public sealed class ViewHost : IDisposable
{
    private readonly List<Window> _windows = new();

    public required ServiceProvider Provider { get; init; }
    public required EditorViewFactory Factory { get; init; }
    public required TestConfigurationService Config { get; init; }
    public required RecordingWindowService Window { get; init; }
    public required TestCommandService Commands { get; init; }
    public required RecordingNavigationService Navigation { get; init; }
    public required DocumentManager Documents { get; init; }
    public required TempDirectory Dir { get; init; }

    public static ViewHost Create(Action<IServiceCollection>? configure = null)
    {
        var config = new TestConfigurationService();
        var window = new RecordingWindowService();
        var documents = new DocumentManager();
        var commands = new TestCommandService();
        var navigation = new RecordingNavigationService(documents);
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(config);
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<ICommandService>(commands);
        services.AddSingleton<IContextKeyService>(new TestContextKeyService());
        services.AddSingleton<INavigationService>(navigation);
        configure?.Invoke(services);
        services.AddCsCodeEditor();
        var sp = services.BuildServiceProvider();
        return new ViewHost
        {
            Provider = sp,
            Factory = sp.GetRequiredService<EditorViewFactory>(),
            Config = config,
            Window = window,
            Commands = commands,
            Navigation = navigation,
            Documents = documents,
            Dir = new TempDirectory("cscode-view-"),
        };
    }

    /// <summary>Opens a real file with <paramref name="text"/> and shows a view over it in a window; returns once it has been laid out.</summary>
    public async Task<(EditorDocument Doc, CodeEditorControl View, Window Window)> OpenAsync(string text, string fileName = "test.cs", double width = 900, double height = 600)
    {
        var path = Dir.WriteFile(fileName, text);
        var doc = (EditorDocument)await Factory.OpenDocumentAsync(new Uri(path));
        var view = (CodeEditorControl)Factory.CreateView(doc);
        var window = new Window { Width = width, Height = height, Content = view };
        _windows.Add(window);
        window.Show();
        Pump();
        view.Editor.TextArea.Focus();
        Pump();
        return (doc, view, window);
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Waits (pumping the dispatcher) until the condition holds.</summary>
    public static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            Pump();
            await Task.Delay(15);
        }
    }

    public void Close(Window window)
    {
        window.Close();
        _windows.Remove(window);
    }

    public void Dispose()
    {
        foreach (var w in _windows.ToArray()) w.Close();
        Provider.Dispose();
        Dir.Dispose();
    }
}
