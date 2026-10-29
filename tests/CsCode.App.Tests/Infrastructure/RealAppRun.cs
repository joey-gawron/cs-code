using System.Diagnostics;
using Avalonia;
using CsCode.App.Startup;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Languages;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// The environment of a REAL app run in a test: <c>CSCODE_HOME</c> points at a temp directory for the duration (the real Platform services read
/// settings and state from it; never the user's), a workspace folder, and a crash-log directory. Restores the environment on dispose.
/// </summary>
public sealed class RealAppHome : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("CSCODE_HOME");

    public RealAppHome() => Environment.SetEnvironmentVariable("CSCODE_HOME", Home.Path);

    public TempDirectory Home { get; } = new("cscode-life-home-");
    public TempDirectory Workspace { get; } = new("cscode-life-ws-");
    public TempDirectory CrashLogs { get; } = new("cscode-life-logs-");

    /// <summary>The user settings file (<c>{ConfigDir}/settings.json</c> under <c>CSCODE_HOME</c>).</summary>
    public void WriteUserSettings(string json) => Home.WriteFile("config/settings.json", json);

    /// <summary>Service hook every real run in a test uses: crash reports go to a temp directory.</summary>
    public void Configure(IServiceCollection services)
    {
        services.RemoveAll<CrashHandler>();
        services.AddSingleton(new CrashHandler(CrashLogs.Path));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CSCODE_HOME", _previous);
        Home.Dispose();
        Workspace.Dispose();
        CrashLogs.Dispose();
    }
}

/// <summary>
/// Drives the app's real start and exit wiring (<see cref="App.Start"/> / <see cref="AppSession"/>) with the real modules on the headless platform.
/// Restores the <see cref="App"/> statics it changes. Disposing a run that the test did not finish closes its window and waits for the exit.
/// </summary>
public sealed class RealAppRun : IAsyncDisposable
{
    private readonly Action<IServiceCollection>? _previousConfigure = App.ConfigureServices;
    private readonly CrashHandler _previousCrash = App.Crash;
    private readonly TimeSpan _previousDeadline = App.ShutdownDeadline;

    public RealAppRun(RealAppHome env, Action<IServiceCollection>? configure = null, TimeSpan? shutdownDeadline = null)
    {
        App.ConfigureServices = s => { env.Configure(s); configure?.Invoke(s); };
        App.Crash = new CrashHandler(env.CrashLogs.Path);
        if (shutdownDeadline is { } d) App.ShutdownDeadline = d;
        Session = ((App)Application.Current!).Start(Lifetime);
    }

    public TestDesktopLifetime Lifetime { get; } = new();
    public AppSession Session { get; }
    public IServiceProvider Services => Session.Services ?? throw new InvalidOperationException("the app has no services (not started, or already shut down)");

    /// <summary>Waits until the session opened its main window (fails with the crash window's text when startup failed instead).</summary>
    public async Task<MainWindow> MainWindowAsync(int timeoutMs = 60_000)
    {
        await DevHost.WaitFor(() => Session.Window is not null, timeoutMs);
        return Session.Window switch
        {
            MainWindow main => main,
            CrashWindow crash => throw new InvalidOperationException("the app showed its crash window: " + string.Join(" | ",
                crash.GetLogicalDescendantTexts())),
            _ => throw new TimeoutException("the main window did not open"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Session.Ended.IsCompleted && Session.Window is { } w)
            {
                if (w is MainWindow main) main.CloseWithoutPrompt(); else w.Close();
                await Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            App.ConfigureServices = _previousConfigure;
            App.Crash = _previousCrash;
            App.ShutdownDeadline = _previousDeadline;
        }
    }
}

/// <summary>A minimal stdio language server (python3) for exit tests: answers <c>initialize</c>; "polite" also honours shutdown/exit, "stubborn" ignores
/// shutdown, exit, stdin EOF and SIGTERM (only a kill ends it).</summary>
public static class FakeLanguageServer
{
    public const string Id = "fakels";
    public const string Extension = ".fakels";

    private const string Script = """
        import json, signal, sys, time
        mode = sys.argv[1]
        if mode == 'stubborn':
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            signal.signal(signal.SIGHUP, signal.SIG_IGN)
        inp, out = sys.stdin.buffer, sys.stdout.buffer
        def read():
            length = None
            while True:
                line = inp.readline()
                if not line:
                    return None
                line = line.strip()
                if not line:
                    if length is not None:
                        break
                    continue
                key, _, value = line.decode('ascii').partition(':')
                if key.strip().lower() == 'content-length':
                    length = int(value)
            return json.loads(inp.read(length))
        def send(obj):
            body = json.dumps(obj).encode()
            out.write(b'Content-Length: ' + str(len(body)).encode() + b'\r\n\r\n' + body)
            out.flush()
        while True:
            msg = read()
            if msg is None:
                if mode == 'polite':
                    sys.exit(0)
                while True:
                    time.sleep(3600)
            method = msg.get('method')
            if method == 'initialize':
                send({'jsonrpc': '2.0', 'id': msg['id'], 'result': {'capabilities': {'textDocumentSync': 1}}})
            elif method == 'shutdown' and mode == 'polite':
                send({'jsonrpc': '2.0', 'id': msg['id'], 'result': None})
            elif method == 'exit' and mode == 'polite':
                sys.exit(0)
        """;

    public static string? Python => FindOnPath("python3");

    /// <summary>Writes the server script and returns the user settings that define it for <c>*.fakels</c> files.</summary>
    public static string Settings(RealAppHome env, string python, string mode, int shutdownTimeoutMs)
    {
        var script = env.Home.WriteFile("fake-language-server.py", Script);
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cscode.languageServers"] = new Dictionary<string, object>
            {
                [Id] = new { command = python, args = new[] { script, mode }, selector = new[] { new { pattern = "**/*" + Extension } } },
            },
            ["cscode.lsp.shutdownTimeoutMs"] = shutdownTimeoutMs,
        });
    }

    /// <summary>Waits until the fake server runs and returns its pid.</summary>
    public static async Task<int> WaitForPidAsync(IServiceProvider services, int timeoutMs = 30_000)
    {
        var manager = services.GetRequiredService<ILanguageServerManager>();
        int? Pid() => manager.Servers.FirstOrDefault(s => s.Id == Id && s.State == LanguageServerState.Running)?.ProcessId;
        await DevHost.WaitFor(() => Pid() is not null, timeoutMs);
        return Pid() ?? throw new TimeoutException("the fake language server did not start: " + string.Join(", ", manager.Servers.Select(s => $"{s.Id}={s.State}")));
    }

    public static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
}

/// <summary>Whether a pid still denotes a live process (a zombie counts as gone).</summary>
public static class ProcessProbe
{
    public static bool IsRunning(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var state = stat[(stat.LastIndexOf(')') + 2)..].TrimStart()[0];
                return state is not ('Z' or 'X');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    public static async Task<bool> WaitUntilGoneAsync(int pid, int timeoutMs = 5000) => await DevHost.WaitFor(() => !IsRunning(pid), timeoutMs);

    /// <summary>A failing test must not leave its fake server behind: kills <paramref name="pid"/> (a process the test caused to start) if it still runs.</summary>
    public static IDisposable KillOnDispose(int pid) => Core.Lifecycle.Disposable.Create(() =>
    {
        if (!IsRunning(pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    });
}

internal static class LogicalTextExtensions
{
    public static IEnumerable<string> GetLogicalDescendantTexts(this Avalonia.Controls.Control root) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? "");
}
