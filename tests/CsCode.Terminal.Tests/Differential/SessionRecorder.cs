using System.Diagnostics;
using System.Text;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Pty.Unix;
using CsCode.Terminal.Tests.Support;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// Records a real program's output on a real PTY (the Unix host) at a fixed size, answering its terminal queries (DA, DSR, OSC colours)
/// through an emulator the way a terminal would, so programs that probe the terminal (nvim) behave as they do on screen. The recording is the
/// exact byte stream a terminal received; the differential suite replays it into every emulator.
/// </summary>
public sealed class SessionRecorder : IPtyListener, IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryStream _bytes = new();
    private readonly UnixPtyHost _host = new();
    private readonly VtEmulator _answerer;
    private long _lastOutput = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SessionRecorder(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        _answerer = new VtEmulator(columns, rows) { Respond = b => _host.Write(b) };
    }

    public int Columns { get; }
    public int Rows { get; }
    public int ProcessId => _host.ProcessId;

    public void Start(PtyStartInfo info) => _host.Start(info with { Columns = Columns, Rows = Rows }, this);

    public void OnOutput(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _bytes.Write(data);
            _answerer.Feed(data);
            _lastOutput = Stopwatch.GetTimestamp();
        }
    }

    public void OnExited(int exitCode) => _exit.TrySetResult(exitCode);

    public Task<int> Exit => _exit.Task;

    public string ScreenText { get { lock (_gate) return _answerer.ScreenText(); } }

    public byte[] Snapshot() { lock (_gate) return _bytes.ToArray(); }

    public void Type(string text) => _host.Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Waits until <paramref name="text"/> is on the (answering emulator's) screen.</summary>
    public async Task<bool> WaitScreenAsync(string text, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ScreenText.Contains(text, StringComparison.Ordinal)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>Waits until no output arrived for <paramref name="quietMs"/> (bounded by <paramref name="timeoutMs"/>).</summary>
    public async Task SettleAsync(int quietMs = 300, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            long last;
            lock (_gate) last = _lastOutput;
            if ((Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency >= quietMs) return;
            await Task.Delay(20);
        }
    }

    public void Kill() => _host.Kill(TimeSpan.FromMilliseconds(500));

    public void Dispose()
    {
        _host.Dispose();
        _bytes.Dispose();
    }
}

/// <summary>Real sessions recorded at test time (the recordings are the corpus: same bytes into every emulator).</summary>
public static class RealSessions
{
    public const int Columns = 80;
    public const int Rows = 24;

    public static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx")) && Path.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    /// <summary>Records one named session; null when its program is missing (the caller skips with the reason).</summary>
    public static async Task<(byte[]? Bytes, string? Missing)> RecordAsync(string name, ShellSandbox sandbox)
    {
        switch (name)
        {
            case "bash-ls-color":
            {
                var dir = sandbox.Dir.Combine("listing");
                Directory.CreateDirectory(Path.Combine(dir, "a-directory"));
                Directory.CreateDirectory(Path.Combine(dir, "src"));
                File.WriteAllText(Path.Combine(dir, "README.md"), "x");
                File.WriteAllText(Path.Combine(dir, "archive.tar.gz"), "x");
                File.WriteAllText(Path.Combine(dir, "image.png"), "x");
                var script = Path.Combine(dir, "run.sh");
                File.WriteAllText(script, "#!/bin/sh\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.CreateSymbolicLink(Path.Combine(dir, "link-to-readme"), Path.Combine(dir, "README.md"));
                using var r = new SessionRecorder(Columns, Rows);
                var env = sandbox.Environment(new Dictionary<string, string> { ["PS1"] = "\\[\\e[1;32m\\]user@cscode\\[\\e[0m\\]:\\[\\e[1;34m\\]\\w\\[\\e[0m\\]\\$ " });
                r.Start(new PtyStartInfo(ShellSandbox.Bash, ["--noprofile", "--norc", "-i"], dir, env, Columns, Rows));
                await r.WaitScreenAsync("$ ");
                r.Type("ls --color=always -F\r");
                await r.WaitScreenAsync("run.sh");
                r.Type("ls --color=always -1 /usr/bin | head -12; echo 'done' \"$((40+2))\"\r");
                await r.WaitScreenAsync("done 42");
                await r.SettleAsync();
                var bytes = r.Snapshot();
                r.Kill();
                return (bytes, null);
            }
            case "git-log-less":
            {
                var git = ShellSandbox.Which("git");
                var less = ShellSandbox.Which("less");
                var repo = RepoRoot();
                if (git is null || less is null || repo is null) return (null, "git, less or the repository is missing");
                using var r = new SessionRecorder(Columns, Rows);
                var env = sandbox.Environment(new Dictionary<string, string> { ["GIT_PAGER"] = "less", ["LESS"] = "R", ["GIT_CONFIG_NOSYSTEM"] = "1", ["LESSHISTFILE"] = "-" });
                r.Start(new PtyStartInfo(git, ["-C", repo, "log", "-n", "60", "--color=always", "--decorate", "--stat"], repo, env, Columns, Rows));
                await r.WaitScreenAsync("commit");
                await r.SettleAsync();
                r.Type("jjj");
                await r.SettleAsync();
                r.Type(" ");
                await r.SettleAsync();
                var bytes = r.Snapshot();
                r.Type("q");
                await r.Exit.WaitAsync(TimeSpan.FromSeconds(10));
                return (bytes, null);
            }
            case "nvim-edit":
            {
                var nvim = ShellSandbox.Which("nvim") ?? (File.Exists(Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/", ".nix-profile/bin/nvim"))
                    ? Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".nix-profile/bin/nvim") : null);
                if (nvim is null) return (null, "nvim is not installed");
                var file = sandbox.Dir.Combine("edit.txt");
                File.WriteAllText(file, string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}: the quick brown fox jumps over the lazy dog\n")));
                using var r = new SessionRecorder(Columns, Rows);
                r.Start(new PtyStartInfo(nvim, ["-u", "NONE", "-i", "NONE", "-n", file], sandbox.Dir.Path, sandbox.Environment(), Columns, Rows));
                await r.WaitScreenAsync("line 1:");
                await r.SettleAsync(500);
                r.Type("3Gdd");
                await r.SettleAsync();
                r.Type("Ohello from cscode 中文\e");
                await r.SettleAsync();
                r.Type(":set number\r");
                await r.SettleAsync();
                r.Type("20G");
                await r.SettleAsync();
                var bytes = r.Snapshot();
                r.Type(":qa!\r");
                await r.Exit.WaitAsync(TimeSpan.FromSeconds(10));
                return (bytes, null);
            }
            case "top-batch":
            case "top-interactive":
            {
                var top = ShellSandbox.Which("top");
                if (top is null) return (null, "top is not installed");
                using var r = new SessionRecorder(Columns, Rows);
                string[] args = name == "top-batch" ? ["-b", "-n", "1", "-w", "80"] : ["-n", "1", "-d", "0.5"];
                r.Start(new PtyStartInfo(top, args, sandbox.Dir.Path, sandbox.Environment(), Columns, Rows));
                await r.Exit.WaitAsync(TimeSpan.FromSeconds(15));
                return (r.Snapshot(), null);
            }
            case "python-repl":
            {
                var python = ShellSandbox.Which("python3");
                if (python is null) return (null, "python3 is not installed");
                using var r = new SessionRecorder(Columns, Rows);
                r.Start(new PtyStartInfo(python, ["-I", "-q"], sandbox.Dir.Path, sandbox.Environment(), Columns, Rows));
                await r.WaitScreenAsync(">>> ");
                r.Type("1 + 1\r");
                await r.WaitScreenAsync("2");
                r.Type("print('héllo wörld ✓')\r");
                await r.WaitScreenAsync("wörld");
                r.Type("[i * i for i in range(40)]\r");
                await r.WaitScreenAsync("1521");
                await r.SettleAsync();
                var bytes = r.Snapshot();
                r.Type("exit()\r");
                await r.Exit.WaitAsync(TimeSpan.FromSeconds(10));
                return (bytes, null);
            }
            case "progress-bar":
            {
                using var r = new SessionRecorder(Columns, Rows);
                r.Start(new PtyStartInfo(ShellSandbox.Sh,
                    ["-c", "i=0; while [ $i -le 40 ]; do bar=$(printf '%*s' $i '' | tr ' ' '#'); printf '\\r[%-40s] %3d%%' \"$bar\" $((i*100/40)); i=$((i+1)); done; printf '\\ndone\\n'"],
                    sandbox.Dir.Path, sandbox.Environment(), Columns, Rows));
                await r.Exit.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(100);
                return (r.Snapshot(), null);
            }
            default: throw new ArgumentException(name);
        }
    }
}
