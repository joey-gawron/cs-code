using Avalonia.Threading;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// The REAL application (<see cref="AppServices.Build"/> with every real module, no fakes) in a headless <see cref="MainWindow"/>, for end-to-end
/// tests of how the modules work together. <c>CSCODE_HOME</c> points at a temp directory for the host's lifetime, so the user's real settings,
/// keybindings and state are never read or written. Every built-in language server is disabled through the user settings file (opening a file
/// must not start csharp-ls or any other real server), and the Welcome page is off so tests start from an empty document well.
/// <c>CSCODE_HOME</c> is process-wide: this relies on the assembly's disabled test parallelisation.
/// </summary>
public sealed class RealAppHost : IAsyncDisposable
{
    private static readonly string[] BuiltinServers =
        ["typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua"];

    private readonly string? _previousHome;
    private bool _disposed;

    private RealAppHost()
    {
        _previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", Home.Path);
        var servers = string.Join(", ", BuiltinServers.Select(s => $"\"{s}\": {{ \"enabled\": false }}"));
        Home.WriteFile("config/settings.json", $$"""
            {
              "cscode.languageServers": { {{servers}} },
              "workbench.startupEditor": "none"
            }
            """);
    }

    /// <summary>Stands in for <c>CSCODE_HOME</c> (config, data, cache, logs).</summary>
    public TempDirectory Home { get; } = new("cscode-realapp-home-");
    /// <summary>A scratch folder for files the test opens (not opened as a workspace unless the test does so).</summary>
    public TempDirectory Files { get; } = new("cscode-realapp-files-");
    public ServiceProvider Services { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public ShellController Shell => Services.GetRequiredService<ShellController>();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Creates the home and files directories; <paramref name="seed"/> may write files before the app is composed (command-line targets).</summary>
    public static RealAppHost Create(Action<RealAppHost>? seed = null)
    {
        var host = new RealAppHost();
        seed?.Invoke(host);
        return host;
    }

    /// <summary>Composes the real app, shows the window and waits until the deferred startup (layout, command-line targets) has run.</summary>
    public async Task<RealAppHost> StartAsync(IReadOnlyList<string>? args = null, Action<IServiceCollection>? configure = null)
    {
        Services = AppServices.Build(args ?? [], configure);
        Window = new MainWindow(Services) { Width = 1280, Height = 800 };
        Window.Show();
        await DevHost.WaitFor(() => Window.IsVisible);
        // StartDeferredAsync runs at background priority after the first frame; the layout restore is its first visible effect.
        await DevScene.SettleAsync(40);
        return this;
    }

    /// <summary>Pumps the UI loop until <paramref name="condition"/> holds.</summary>
    public static Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10_000) => DevHost.WaitFor(condition, timeoutMs);

    /// <summary>Runs a command to completion, then lets the UI settle.</summary>
    public async Task RunAsync(string commandId, params object?[] args)
    {
        await Get<ICommandService>().ExecuteAsync(commandId, args);
        await DevScene.SettleAsync(10);
    }

    /// <summary>Starts a palette command (it completes only when the quick pick closes, so it must not be awaited first) and waits for the palette.</summary>
    public async Task<Task> OpenPaletteAsync(string commandId)
    {
        var running = Get<ICommandService>().ExecuteAsync(commandId).AsTask();
        if (!await WaitFor(() => Get<QuickInputViewModel>().IsVisible)) throw new TimeoutException($"{commandId} did not open the palette");
        return running;
    }

    /// <summary>Cancels the open palette and completes the command that opened it.</summary>
    public async Task ClosePaletteAsync(Task running)
    {
        Get<QuickInputViewModel>().Cancel();
        await WaitFor(() => running.IsCompleted);
        await running;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Window?.CloseWithoutPrompt();
            Dispatcher.UIThread.RunJobs();
            // Synchronously, on the UI thread: the provider's DisposeAsync resumes on the pool after its first async disposable and would then
            // dispose UI-thread singletons (document well, editor views) off the UI thread. Every singleton has a non-blocking Dispose.
            Services?.Dispose();
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", _previousHome);
            Files.Dispose();
            Home.Dispose();
        }
    }
}
