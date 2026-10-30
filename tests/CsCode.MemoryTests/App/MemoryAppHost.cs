using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Threading;
using CsCode.App;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.MemoryTests.App;

/// <summary>
/// The REAL application (<see cref="AppServices.Build"/> with every module, no fakes) in a headless <see cref="MainWindow"/> — the composition the
/// App tests' RealAppHost uses — for memory scenarios and budgets. Shared (as linked source) by the memory tests and tools/profile's profiling host.
/// <c>CSCODE_HOME</c> points at a temp directory for the host's lifetime (never the user's settings or state); every built-in language server is
/// disabled unless <see cref="MemoryAppHostOptions.EnabledServers"/> names it; the Welcome page is off; the terminal's default profile is a
/// sandboxed <c>bash --noprofile --norc</c> (HOME, XDG_CONFIG_HOME, XDG_DATA_HOME, ZDOTDIR in temp directories). Frames: the headless platform
/// renders only when ticked, so <see cref="SettleAsync"/> ticks the compositor like a 60 Hz render loop would (software Skia on the UI thread).
/// </summary>
public sealed class MemoryAppHost : IAsyncDisposable
{
    public static readonly string[] BuiltinServers =
        ["typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua"];

    private readonly string? _previousHome;
    private readonly string? _previousData;
    private bool _disposed;

    private MemoryAppHost(MemoryAppHostOptions options)
    {
        _previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        _previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", Home.Path);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Home.Combine("xdg-data")); // the Platform trash: never the user's
        foreach (var dir in new[] { "home", "config", "data" }) Directory.CreateDirectory(Sandbox.Combine(dir));
        var settings = new Dictionary<string, object?>
        {
            ["cscode.languageServers"] = BuiltinServers.Where(s => !options.EnabledServers.Contains(s)).ToDictionary(s => s, _ => (object)new { enabled = false }),
            ["workbench.startupEditor"] = "none",
            [CsCode.Terminal.Sessions.TerminalSettingKeys.ProfilesKey] = new Dictionary<string, object>
            {
                ["sandboxed"] = new
                {
                    path = FindOnPath("bash") ?? "/bin/bash",
                    args = new[] { "--noprofile", "--norc" },
                    env = new Dictionary<string, string?>
                    {
                        ["HOME"] = Sandbox.Combine("home"), ["XDG_CONFIG_HOME"] = Sandbox.Combine("config"), ["XDG_DATA_HOME"] = Sandbox.Combine("data"),
                        ["ZDOTDIR"] = Sandbox.Combine("home"), ["PS1"] = "$ ", ["BASH_ENV"] = null, ["ENV"] = null, ["PROMPT_COMMAND"] = null,
                        ["HISTFILE"] = Sandbox.Combine("home", ".bash_history"), ["LANG"] = "C.UTF-8", ["SSH_AUTH_SOCK"] = null,
                    },
                },
            },
            [CsCode.Terminal.Sessions.TerminalSettingKeys.DefaultProfileKey] = "sandboxed",
            ["terminal.integrated.cwd"] = Sandbox.Combine("home"),
            ["terminal.integrated.shellIntegration.enabled"] = false,
        };
        foreach (var (k, v) in options.Settings) settings[k] = v;
        Home.WriteFile("config/settings.json", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Stands in for <c>CSCODE_HOME</c> (config, data, cache, logs).</summary>
    public TempDirectory Home { get; } = new("cscode-mem-home-");
    /// <summary>Files the scenarios open (a workspace folder when passed as an argument).</summary>
    public TempDirectory Files { get; } = new("cscode-mem-files-");
    /// <summary>HOME and XDG directories of the sandboxed terminal shell.</summary>
    public TempDirectory Sandbox { get; } = new("cscode-mem-sandbox-");
    public ServiceProvider Services { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public ShellController Shell => Services.GetRequiredService<ShellController>();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Writes the settings, composes the real app, shows the window and waits for the deferred startup. UI thread.</summary>
    public static async Task<MemoryAppHost> StartAsync(MemoryAppHostOptions? options = null, Action<MemoryAppHost>? seed = null)
    {
        options ??= new MemoryAppHostOptions();
        var host = new MemoryAppHost(options);
        try
        {
            seed?.Invoke(host);
            host.Services = AppServices.Build(options.Args?.Invoke(host) ?? [], services => options.Configure?.Invoke(host, services));
            host.Window = new MainWindow(host.Services) { Width = 1280, Height = 800 };
            host.Window.Show();
            await WaitFor(() => host.Window.IsVisible);
            await SettleAsync(40);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Pumps the UI loop and ticks the compositor (a frame) every round.</summary>
    public static async Task SettleAsync(int rounds = 20, bool frames = true)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (frames) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }

    public static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    public static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Window?.CloseWithoutPrompt();
            Dispatcher.UIThread.RunJobs();
            // Synchronously, on the UI thread (as RealAppHost): every singleton has a non-blocking Dispose.
            Services?.Dispose();
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", _previousHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousData);
            Files.Dispose();
            Home.Dispose();
            Sandbox.Dispose();
        }
    }
}

/// <summary>What <see cref="MemoryAppHost.StartAsync"/> composes.</summary>
public sealed record MemoryAppHostOptions
{
    /// <summary>Extra user settings (merged over the host's defaults).</summary>
    public Dictionary<string, object?> Settings { get; init; } = new();
    /// <summary>Built-in language servers left enabled (all others are disabled).</summary>
    public IReadOnlyCollection<string> EnabledServers { get; init; } = [];
    /// <summary>Command-line arguments (e.g. the workspace folder), computed once the host's directories exist.</summary>
    public Func<MemoryAppHost, IReadOnlyList<string>>? Args { get; init; }
    /// <summary>Replaces registrations after the real modules (remote transport, options).</summary>
    public Action<MemoryAppHost, IServiceCollection>? Configure { get; init; }
}
