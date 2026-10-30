using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.App;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Views;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Terminal.Tests.Infrastructure;

/// <summary>
/// The REAL application (<see cref="AppServices.Build"/>, every module, the headless <see cref="MainWindow"/>) with <c>CSCODE_HOME</c> in a temp
/// directory, built-in language servers off, and the terminal's default profile a sandboxed shell: <c>bash --noprofile --norc</c> with HOME,
/// XDG_CONFIG_HOME, XDG_DATA_HOME and ZDOTDIR in temp directories and PS1 "$ " — no test ever runs the user's rc files or touches their config.
/// </summary>
public sealed class TerminalAppHost : IAsyncDisposable
{
    private static readonly string[] BuiltinServers =
        ["typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua"];

    private readonly string? _previousHome;

    private TerminalAppHost(string shell, string[] shellArgs, Dictionary<string, object?>? extraSettings)
    {
        _previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", Home.Path);
        Directory.CreateDirectory(Sandbox.Combine("home"));
        Directory.CreateDirectory(Sandbox.Combine("config"));
        Directory.CreateDirectory(Sandbox.Combine("data"));
        var settings = new Dictionary<string, object?>
        {
            ["cscode.languageServers"] = BuiltinServers.ToDictionary(s => s, _ => (object)new { enabled = false }),
            ["workbench.startupEditor"] = "none",
            [CsCode.Terminal.Sessions.TerminalSettingKeys.ProfilesKey] = new Dictionary<string, object>
            {
                ["sandboxed"] = new
                {
                    path = shell,
                    args = shellArgs,
                    env = new Dictionary<string, string?>
                    {
                        ["HOME"] = Sandbox.Combine("home"),
                        ["XDG_CONFIG_HOME"] = Sandbox.Combine("config"),
                        ["XDG_DATA_HOME"] = Sandbox.Combine("data"),
                        ["ZDOTDIR"] = Sandbox.Combine("home"),
                        ["PS1"] = "$ ",
                        ["BASH_ENV"] = null,
                        ["ENV"] = null,
                        ["PROMPT_COMMAND"] = null,
                        ["HISTFILE"] = Sandbox.Combine("home", ".bash_history"),
                        ["LANG"] = "C.UTF-8",
                    },
                },
            },
            [CsCode.Terminal.Sessions.TerminalSettingKeys.DefaultProfileKey] = "sandboxed",
            ["terminal.integrated.cwd"] = Sandbox.Combine("home"),
        };
        if (extraSettings is not null) foreach (var (k, v) in extraSettings) settings[k] = v;
        Home.WriteFile("config/settings.json", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public TempDirectory Home { get; } = new("cscode-term-app-home-");
    public TempDirectory Sandbox { get; } = new("cscode-term-app-sandbox-");
    public ServiceProvider Services { get; private set; } = null!;
    public MainWindow Window { get; private set; } = null!;
    public ShellController Shell => Services.GetRequiredService<ShellController>();
    public TerminalService Terminals => Services.GetRequiredService<TerminalService>();
    public TerminalPanelRuntime Runtime => Services.GetRequiredService<TerminalPanelRuntime>();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static async Task<TerminalAppHost> StartAsync(string? shell = null, string[]? shellArgs = null, Dictionary<string, object?>? settings = null,
        Action<IServiceCollection>? configure = null)
    {
        var host = new TerminalAppHost(shell ?? Support.Which("bash") ?? "/bin/bash", shellArgs ?? ["--noprofile", "--norc"], settings);
        host.Services = AppServices.Build([], services =>
        {
            // never a real X server's PRIMARY selection, also when DISPLAY happens to be set
            services.AddSingleton(sp => new CsCode.App.Services.ClipboardService(sp.GetRequiredService<Ui.Threading.IUiDispatcher>(),
                CsCode.App.Services.AvaloniaPlatformClipboard.Of(() => sp.GetRequiredService<WindowHolder>().Window), () => new CsCode.App.Services.InMemoryPrimarySelection()));
            configure?.Invoke(services);
        });
        host.Window = new MainWindow(host.Services) { Width = 1280, Height = 800 };
        host.Window.Show();
        await WaitFor(() => host.Window.IsVisible);
        await SettleAsync(40);
        return host;
    }

    /// <summary>The label a menu shows for <paramref name="keys"/> on this OS ("Ctrl+Shift+B" on Linux, "⌃⇧B" on macOS).</summary>
    public static string KeyLabel(string keys) => CsCode.Platform.Keybindings.KeyBindingFormatter.Format(KeyStrokeParser.ParseChord(keys),
        OperatingSystem.IsMacOS() ? CsCode.Platform.HostOs.MacOs : OperatingSystem.IsWindows() ? CsCode.Platform.HostOs.Windows : CsCode.Platform.HostOs.Linux);

    /// <summary>Pumps the UI loop until <paramref name="condition"/> holds.</summary>
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

    public static async Task SettleAsync(int rounds = 20)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
        }
    }

    public async Task RunAsync(string commandId, params object?[] args)
    {
        await Get<ICommandService>().ExecuteAsync(commandId, args);
        await SettleAsync(10);
    }

    /// <summary>The Terminal view while the tool window shows it.</summary>
    public TerminalView? View => Runtime.View;

    /// <summary>Renders frames until the active terminal's drawn screen contains <paramref name="text"/>.</summary>
    public async Task<bool> WaitForScreenAsync(string text, int timeoutMs = 15_000) =>
        await WaitFor(() => View?.Control.Snapshot is { } s && s.Rows.Any(r => r.GetText().Contains(text, StringComparison.Ordinal)), timeoutMs);

    public string ScreenText() => View?.Control.Snapshot is { } s ? string.Join('\n', s.Rows.Select(r => r.GetText())) : "";

    /// <summary>Writes a headless render of the window to the git-ignored screenshots folder.</summary>
    public string Capture(string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        var dir = Path.Combine(Support.RepoRoot() ?? Path.GetTempPath(), ".scratch", "screenshots", "terminal");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Window?.CloseWithoutPrompt();
            Dispatcher.UIThread.RunJobs();
            Services?.Dispose();
            await Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", _previousHome);
            Home.Dispose();
            Sandbox.Dispose();
        }
    }
}

public static class Support
{
    public static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Append("/usr/bin").Append("/bin"))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        return null;
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link among its directories resolved (realpath): what the kernel reports as a process's
    /// directory and what a shell started there takes as $PWD. The temp directory is under one on macOS (/var is /private/var).
    /// </summary>
    public static string PhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var result = Path.GetPathRoot(full)!;
        foreach (var part in full[result.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            result = Path.Combine(result, part);
            if (new DirectoryInfo(result).ResolveLinkTarget(returnFinalTarget: true) is { } target) result = target.FullName;
        }
        return result;
    }
}
