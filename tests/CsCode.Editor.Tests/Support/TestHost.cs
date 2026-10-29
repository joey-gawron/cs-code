using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Threading;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// One wired-up editor environment for tests: the shared fakes from CsCode.TestDoubles registered in DI, an
/// <see cref="EditorServices"/> over them (falling back to the module's local file system / watcher), a temp directory.
/// </summary>
public sealed class TestHost : IDisposable
{
    public required ServiceProvider Provider { get; init; }
    public required EditorServices Services { get; init; }
    public required TestConfigurationService Config { get; init; }
    public required RecordingWindowService Window { get; init; }
    public required DocumentManager Documents { get; init; }
    public required TempDirectory Dir { get; init; }
    public required IUiThread Ui { get; init; }

    public static TestHost Create(IUiThread? ui = null, Action<IServiceCollection>? configure = null)
    {
        ui ??= AvaloniaUiThread.Instance;
        var config = new TestConfigurationService();
        var window = new RecordingWindowService();
        var documents = new DocumentManager();
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(config);
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<ICommandService>(new TestCommandService());
        services.AddSingleton<IContextKeyService>(new TestContextKeyService());
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return new TestHost
        {
            Provider = provider,
            Services = new EditorServices(provider, ui),
            Config = config,
            Window = window,
            Documents = documents,
            Dir = new TempDirectory("cscode-editor-"),
            Ui = ui,
        };
    }

    public Uri FileUri(string name) => new(Dir.Combine(name));

    public void Dispose()
    {
        Services.Dispose();
        Provider.Dispose();
        Dir.Dispose();
    }

    /// <summary>Polls (yielding to the dispatcher) until <paramref name="condition"/> holds; fails the test after <paramref name="timeout"/>.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(25);
        }
    }
}
