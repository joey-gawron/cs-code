using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.App.Background;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;

namespace CsCode.App.Tests.Background;

/// <summary>What one run of the <c>cscode</c> launcher did.</summary>
public sealed record LauncherRun(int ExitCode, string Output, string Error, TimeSpan Elapsed);

/// <summary>
/// REAL CsCode processes for background-mode tests: the built <c>cscode</c> executable runs with <c>--headless-test</c> (the Avalonia headless platform:
/// no display, test-only IPC verbs) in a private <c>CSCODE_HOME</c> under /tmp, so its IPC directory, locks and sessions are this harness's alone and
/// the user's running CsCode is never reached. Every built-in language server is disabled and the Welcome page is off. Dispose quits every
/// process of that home and then kills whatever is left by PID (found through its environment: only processes with this <c>CSCODE_HOME</c> match).
/// </summary>
public sealed class SessionProcessHarness : IDisposable
{
    private readonly bool _trace;

    private static readonly string[] BuiltinServers =
        ["typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua"];

    /// <param name="trace">Write a per-phase timeline of every process to <see cref="TracePath"/> (adds file writes to the measured paths).</param>
    public SessionProcessHarness(string? extraSettings = null, bool trace = true)
    {
        _trace = trace;
        // Short: Unix socket paths are limited to ~108 bytes.
        Home = Path.Combine("/tmp", "csb-" + Guid.NewGuid().ToString("N")[..8]);
        Workspace = Path.Combine(Home, "ws");
        Directory.CreateDirectory(Path.Combine(Home, "config"));
        Directory.CreateDirectory(Workspace);
        var servers = string.Join(", ", BuiltinServers.Select(s => $"\"{s}\": {{ \"enabled\": false }}"));
        File.WriteAllText(Path.Combine(Home, "config", "settings.json"), $$"""
            {
              "cscode.languageServers": { {{servers}} },
              "workbench.startupEditor": "none"{{(extraSettings is null ? "" : ",\n  " + extraSettings)}}
            }
            """);
        Runtime = RuntimeDirectory.Resolve(name => name == "CSCODE_HOME" ? Home : Environment.GetEnvironmentVariable(name));
        Store = new SessionStore(Path.Combine(Home, "data"));
        Executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "cscode.exe" : "cscode");
    }

    public string Home { get; }
    public string Workspace { get; }
    public RuntimeDirectory Runtime { get; }
    public SessionStore Store { get; }
    public string Executable { get; }
    public string TracePath => Path.Combine(Home, "trace.log");

    public string WriteFile(string relative, string content)
    {
        var path = Path.Combine(Workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Runs <c>cscode --headless-test args...</c> to completion (bounded) and measures it from process start to exit.</summary>
    public async Task<LauncherRun> RunAsync(params string[] args) => await RunAsync(TimeSpan.FromSeconds(90), args);

    public async Task<LauncherRun> RunAsync(TimeSpan timeout, params string[] args)
    {
        using var p = Start(args);
        var clock = Stopwatch.StartNew();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // the launcher itself (a PID this harness started)
            throw new TimeoutException($"cscode {string.Join(' ', args)} did not exit within {timeout.TotalSeconds:0} s");
        }
        clock.Stop();
        return new LauncherRun(p.ExitCode, await output, await error, clock.Elapsed);
    }

    /// <summary>Starts the launcher without waiting (for <c>--wait</c>). The caller disposes the process.</summary>
    public Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Workspace,
        };
        psi.ArgumentList.Add("--headless-test");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CSCODE_HOME"] = Home;
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(Home, "xdg-data"); // the Platform trash: never the user's real Trash
        if (_trace) psi.Environment["CSCODE_SESSION_TRACE"] = TracePath;
        else psi.Environment.Remove("CSCODE_SESSION_TRACE");
        psi.Environment.Remove("DISPLAY");
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.Environment.Remove("SSH_AUTH_SOCK");
        var p = Process.Start(psi)!;
        p.StandardInput.Close();
        return p;
    }

    public IReadOnlyList<ResidentProcess> Discover() => ResidentDiscovery.Discover(Runtime, TimeSpan.FromSeconds(2));

    public IpcMessage Request(int pid, IpcMessage request, TimeSpan? timeout = null) =>
        IpcClient.Request(Runtime.EndpointOf(pid), request, TimeSpan.FromSeconds(2), timeout ?? TimeSpan.FromSeconds(30));

    public void Must(int pid, IpcMessage request)
    {
        var reply = Request(pid, request);
        if (reply.Ok != true) throw new InvalidOperationException($"{request.Type} failed: {reply.Error}");
    }

    public InspectedState Inspect(int pid, bool withText = false, bool collect = false)
    {
        var reply = Request(pid, new IpcMessage { Type = IpcVerbs.Inspect, Text = withText ? "text" : null, Force = collect });
        if (reply.Ok != true || reply.Data is not { } data) throw new InvalidOperationException("inspect failed: " + reply.Error);
        return data.Deserialize<InspectedState>()!;
    }

    public async Task<ResidentProcess> WaitForAsync(Func<ResidentProcess, bool> match, int timeoutMs = 90_000, string what = "process")
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (Discover().FirstOrDefault(match) is { } found) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException($"no {what} appeared within {timeoutMs} ms; trace:\n{Trace()}");
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(25);
        }
        return true;
    }

    public string Trace()
    {
        try { return File.Exists(TracePath) ? File.ReadAllText(TracePath) : "(no trace)"; }
        catch (IOException) { return "(trace unreadable)"; }
    }

    /// <summary>The PIDs of every process running with this harness's <c>CSCODE_HOME</c> (Linux: from /proc/&lt;pid&gt;/environ).</summary>
    public IReadOnlyList<int> ProcessesOfThisHome()
    {
        var pids = new List<int>();
        if (!OperatingSystem.IsLinux()) return pids;
        var needle = Encoding.UTF8.GetBytes("CSCODE_HOME=" + Home + "\0");
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                var env = File.ReadAllBytes(Path.Combine(dir, "environ"));
                if (env.AsSpan().IndexOf(needle) >= 0 && ProcessProbeIsLive(pid)) pids.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pids;
    }

    private static bool ProcessProbeIsLive(int pid) => Infrastructure.ProcessProbe.IsRunning(pid);

    /// <summary>CPU time (user + system) of a process in seconds (Linux).</summary>
    public static double CpuSeconds(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        // fields[0] is the state (field 3 of the man page): utime is field 14, stime field 15.
        var ticks = long.Parse(fields[11], System.Globalization.CultureInfo.InvariantCulture) + long.Parse(fields[12], System.Globalization.CultureInfo.InvariantCulture);
        return ticks / 100.0; // USER_HZ
    }

    /// <summary>CPU seconds and name of each thread of a process (Linux), to tell what a process that should be idle is doing.</summary>
    public static IReadOnlyDictionary<int, (string Name, double Seconds)> ThreadCpu(int pid)
    {
        var result = new Dictionary<int, (string, double)>();
        foreach (var dir in Directory.EnumerateDirectories($"/proc/{pid}/task"))
        {
            try
            {
                var tid = int.Parse(Path.GetFileName(dir), System.Globalization.CultureInfo.InvariantCulture);
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var ticks = long.Parse(fields[11], System.Globalization.CultureInfo.InvariantCulture) + long.Parse(fields[12], System.Globalization.CultureInfo.InvariantCulture);
                result[tid] = (File.ReadAllText(Path.Combine(dir, "comm")).Trim(), ticks / 100.0);
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public void KillProcessesOfThisHome()
    {
        foreach (var pid in ProcessesOfThisHome())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Dispose()
    {
        try
        {
            using var quit = Start("--quit", "--all");
            quit.WaitForExit(30_000);
            if (!quit.HasExited) quit.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        KillProcessesOfThisHome();
        WaitUntilAsync(() => ProcessesOfThisHome().Count == 0, 5000).GetAwaiter().GetResult();
        try { Directory.Delete(Home, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
