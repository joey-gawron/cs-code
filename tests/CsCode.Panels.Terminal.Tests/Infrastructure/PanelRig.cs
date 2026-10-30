using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Core.Text;
using CsCode.Panels.Terminal.Views;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Terminal.Tests.Infrastructure;

/// <summary>A PTY host without a process: records what the terminal writes and lets the test play the program's output.</summary>
public sealed class FakePtyHost : IPtyHost
{
    private readonly object _gate = new();
    private readonly MemoryStream _written = new();
    private IPtyListener? _listener;

    public List<(int Columns, int Rows)> Sizes { get; } = new();
    public List<bool> UrgentFlags { get; } = new();
    public int ProcessId => 4242;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public long PendingWriteBytes => 0;
    public long MaxPendingWriteBytes => 1 << 20;
    public bool Killed { get; private set; }

    public void Start(PtyStartInfo info, IPtyListener listener)
    {
        _listener = listener;
        lock (_gate) Sizes.Add((info.Columns, info.Rows));
    }

    public bool Write(ReadOnlySpan<byte> data, bool urgent = false)
    {
        lock (_gate)
        {
            _written.Write(data);
            UrgentFlags.Add(urgent);
        }
        return true;
    }

    public string Written { get { lock (_gate) return Encoding.UTF8.GetString(_written.ToArray()); } }

    public void ClearWritten() { lock (_gate) { _written.SetLength(0); UrgentFlags.Clear(); } }

    /// <summary>The program writes this.</summary>
    public void Output(string text) => _listener?.OnOutput(Encoding.UTF8.GetBytes(text));

    public void Exit(int code)
    {
        HasExited = true;
        ExitCode = code;
        _listener?.OnExited(code);
    }

    public void Resize(int columns, int rows) { lock (_gate) Sizes.Add((columns, rows)); }
    public void Kill(TimeSpan? grace = null) => Killed = true;
    public string? GetForegroundProcessName() => "fakesh";
    public void Dispose() => Killed = true;
}

/// <summary>
/// The Terminal view in a headless window over fakes (commands, context keys, configuration, clipboard, window, navigation) and fake PTYs,
/// for panel behaviour that does not need the whole application.
/// </summary>
public sealed class PanelRig : IAsyncDisposable
{
    public readonly List<FakePtyHost> Hosts = new();
    public readonly TestCommandService Commands = new();
    public readonly TestContextKeyService Keys = new();
    public readonly TestConfigurationService Config = new();
    public readonly InMemoryClipboardService Clipboard = new();
    public readonly RecordingWindowService WindowService = new();
    public readonly RecordingNavigationService Navigation = new(new DocumentManager());
    public readonly ContributionRegistry Contributions = new();
    public readonly ServiceProvider Services;
    public readonly Window Window;
    public readonly TerminalView View;

    public PanelRig(int width = 900, int height = 400)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommandService>(Commands);
        services.AddSingleton<IContextKeyService>(Keys);
        services.AddSingleton<IConfigurationService>(Config);
        services.AddSingleton<IClipboardService>(Clipboard);
        services.AddSingleton<IWindowService>(WindowService);
        services.AddSingleton<INavigationService>(Navigation);
        services.AddSingleton<IContributionRegistry>(Contributions);
        services.AddSingleton(sp => new TerminalService(Config, hostFactory: () => { var h = new FakePtyHost(); lock (Hosts) Hosts.Add(h); return h; }));
        services.AddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());
        services.AddSingleton<TerminalPanelRuntime>();
        Services = services.BuildServiceProvider();
        View = new TerminalView(Runtime);
        Window = new Window { Width = width, Height = height, Content = View };
        Window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    public TerminalPanelRuntime Runtime => Services.GetRequiredService<TerminalPanelRuntime>();
    public TerminalService Service => Services.GetRequiredService<TerminalService>();
    public TerminalControl Control => View.Control;
    /// <summary>The fake PTY of the active terminal (one host per terminal, created in id order).</summary>
    public FakePtyHost Host => Hosts[Session.Id - 1];
    public TerminalSession Session => Service.ActiveSession!;

    /// <summary>Waits for the view's first terminal (it creates one when shown) and a rendered frame.</summary>
    public async Task<PanelRig> ReadyAsync()
    {
        await Pump(() => Service.ActiveSession is not null && Hosts.Count > 0);
        await Pump(() => Control.Snapshot is not null);
        return this;
    }

    public static async Task<bool> Pump(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>Plays program output and waits until it is drawn.</summary>
    public async Task OutputAsync(string text, string? waitFor = null)
    {
        Host.Output(text);
        if (waitFor is not null && !await Pump(() => ScreenText().Contains(waitFor, StringComparison.Ordinal)))
            throw new TimeoutException($"'{waitFor}' never drawn:\n{ScreenText()}");
        await Pump(() => false, 60);
    }

    public string ScreenText() => Control.Snapshot is { } s ? string.Join('\n', s.Rows.Select(r => r.GetText())) : "";

    /// <summary>The centre of cell (column, row) in window coordinates.</summary>
    public Point Cell(int column, int row)
    {
        var f = Control.Font;
        var p = new Point(TerminalControl.PaddingLeft + (column + 0.5) * f.CellWidth, TerminalControl.PaddingTop + (row + 0.5) * f.CellHeight);
        return Control.TranslatePoint(p, Window) ?? p;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        View.Dispose();
        Dispatcher.UIThread.RunJobs();
        await Services.DisposeAsync();
    }
}
