using System.Diagnostics;
using System.Text;
using CsCode.Terminal.Pty;
using CsCode.TestDoubles;

namespace CsCode.Terminal.Tests.Support;

/// <summary>
/// A sandbox for shells under test: HOME, XDG_CONFIG_HOME, XDG_DATA_HOME and ZDOTDIR point into a temp directory and bash runs with
/// <c>--noprofile --norc</c>, so no test ever reads the user's rc files or config. PATH is the test process's own.
/// </summary>
public sealed class ShellSandbox : IDisposable
{
    public TempDirectory Dir { get; } = new("cscode-term-sandbox-");

    public ShellSandbox()
    {
        Directory.CreateDirectory(Dir.Combine("home"));
        Directory.CreateDirectory(Dir.Combine("config"));
        Directory.CreateDirectory(Dir.Combine("data"));
    }

    public string Home => Dir.Combine("home");

    public Dictionary<string, string> Environment(IReadOnlyDictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = System.Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Home,
            ["XDG_CONFIG_HOME"] = Dir.Combine("config"),
            ["XDG_DATA_HOME"] = Dir.Combine("data"),
            ["ZDOTDIR"] = Home,
            ["TERM"] = "xterm-256color",
            ["LANG"] = "C.UTF-8",
            ["PS1"] = "$ ",
        };
        if (extra is not null) foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    public static string Bash => Which("bash") ?? "/bin/bash";
    public static string Sh => Which("sh") ?? "/bin/sh";

    public static string? Which(string name)
    {
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in new[] { "/usr/bin", "/bin" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public PtyStartInfo BashInfo(int columns = 80, int rows = 24, params string[] extraArgs) =>
        new(Bash, ["--noprofile", "--norc", .. extraArgs], Home, Environment(), columns, rows);

    public PtyStartInfo Command(string file, IReadOnlyList<string> args, int columns = 80, int rows = 24) =>
        new(file, args, Home, Environment(), columns, rows);

    public void Dispose() => Dir.Dispose();
}

/// <summary>Collects a PTY's output (decoded leniently as UTF-8) and its exit for assertions.</summary>
public sealed class RecordingPtyListener : IPtyListener
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytes;

    public string Text { get { lock (_gate) return _text.ToString(); } }
    public long Bytes => Interlocked.Read(ref _bytes);
    public Task<int> Exit => _exit.Task;
    public int OutputCalls;
    public string? OutputThreadName;

    public void OnOutput(ReadOnlySpan<byte> data)
    {
        Interlocked.Add(ref _bytes, data.Length);
        Interlocked.Increment(ref OutputCalls);
        OutputThreadName ??= Thread.CurrentThread.Name;
        var chars = new char[_decoder.GetCharCount(data, false)];
        _decoder.GetChars(data, chars, false);
        lock (_gate)
        {
            _text.Append(chars);
            if (_text.Length > 4_000_000) _text.Remove(0, _text.Length - 1_000_000);
        }
    }

    public void OnExited(int exitCode) => _exit.TrySetResult(exitCode);

    public async Task<bool> WaitForAsync(string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Text.Contains(text, StringComparison.Ordinal)) return true;
            await Task.Delay(10);
        }
        return Text.Contains(text, StringComparison.Ordinal);
    }

    public async Task<int> WaitExitAsync(int timeoutMs = 10_000) => await Exit.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
}

public static class ProcessChecks
{
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            return close > 0 && stat[close + 2] != 'Z' && stat[close + 2] != 'X';
        }
        catch (IOException) { return false; }
    }

    /// <summary>Any entry in /proc for the pid, zombies included.</summary>
    public static bool Exists(int pid) => Directory.Exists($"/proc/{pid}");

    public static async Task<bool> WaitGoneAsync(int pid, int timeoutMs = 5_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!Exists(pid)) return true;
            await Task.Delay(20);
        }
        return !Exists(pid);
    }

    public static int OpenDescriptors() => Directory.GetFileSystemEntries("/proc/self/fd").Length;

    /// <summary>"fd -> target" for every open descriptor (to name a leak).</summary>
    public static IReadOnlyList<string> DescriptorTargets() =>
        Directory.GetFileSystemEntries("/proc/self/fd").Select(f =>
        {
            try { return Path.GetFileName(f) + "->" + (new FileInfo(f).LinkTarget ?? "?"); }
            catch (IOException) { return Path.GetFileName(f) + "->gone"; }
        }).ToList();

    public static int ThreadCount() => Process.GetCurrentProcess().Threads.Count;
}
