using System.Diagnostics;
using System.Text;
using CsCode.Terminal.Emulator;
using CsCode.TestDoubles;

namespace CsCode.Terminal.Tests.Differential;

/// <summary>
/// tmux as a reference terminal. Every run starts a PRIVATE server (<c>-S &lt;temp&gt;/sock</c>, <c>-f /dev/null</c>, <c>SHELL=/bin/sh</c>, HOME and
/// XDG directories in a temp directory) whose only pane puts its tty in raw mode, cats the input file and signals a wait-for channel; the
/// visible screen is read with <c>capture-pane -p -e -N</c> and the cursor with <c>display-message</c>. The server is then killed by its
/// socket and, if it is still there, by its PID. The user's tmux server and default socket are never touched.
/// </summary>
public sealed class TmuxOracle : IOracle
{
    private readonly TempDirectory _dir = new("cscode-tmux-oracle-");
    private int _runs;

    public static string? Executable =>
        Environment.GetEnvironmentVariable("CSCODE_TMUX") is { Length: > 0 } custom && File.Exists(custom) ? custom
        : File.Exists("/usr/bin/tmux") ? "/usr/bin/tmux" : null;

    public string Name => "tmux";

    // tmux keeps no underline style in its capture for every style, and reports the cursor past the margin while a wrap is pending.
    public Compared Capabilities => Compared.Text | Compared.Colors | Compared.Bold | Compared.Italic | Compared.Underline | Compared.Inverse
        | Compared.Strike | Compared.Dim | Compared.Blink | Compared.Invisible | Compared.Cursor | Compared.AlternateScreen;

    /// <summary>PIDs of servers started and their panes (tests assert they are all gone).</summary>
    public List<int> StartedPids { get; } = new();

    public ScreenState Run(byte[] input, int columns, int rows)
    {
        var tmux = Executable ?? throw new InvalidOperationException("tmux is not installed");
        var n = Interlocked.Increment(ref _runs);
        var dir = Path.Combine(_dir.Path, "run" + n);
        Directory.CreateDirectory(Path.Combine(dir, "home"));
        var sock = Path.Combine(dir, "sock");
        var file = Path.Combine(dir, "in.bin");
        File.WriteAllBytes(file, input);
        var paneCommand = $"stty raw -echo -opost; cat '{file}'; '{tmux}' -S '{sock}' wait-for -S done; exec sleep 600";
        int serverPid = 0, panePid = 0;
        try
        {
            Tmux(dir, sock, ["new-session", "-d", "-x", columns.ToString(), "-y", rows.ToString(), "-s", "diff", paneCommand]);
            var pids = Tmux(dir, sock, ["display-message", "-p", "-t", "diff", "#{pid},#{pane_pid}"]).Trim().Split(',');
            serverPid = int.Parse(pids[0]);
            panePid = int.Parse(pids[1]);
            lock (StartedPids) StartedPids.AddRange([serverPid, panePid]);
            Tmux(dir, sock, ["wait-for", "done"], timeoutMs: 20_000);
            var capture = Tmux(dir, sock, ["capture-pane", "-p", "-e", "-N", "-t", "diff"]);
            var cursor = Tmux(dir, sock, ["display-message", "-p", "-t", "diff", "#{cursor_x},#{cursor_y},#{alternate_on}"]).Trim().Split(',');
            var state = Parse(capture, columns, rows);
            state.CursorX = Math.Min(int.Parse(cursor[0]), columns - 1);
            state.CursorY = int.Parse(cursor[1]);
            state.AlternateScreen = cursor[2] == "1";
            return state;
        }
        finally
        {
            try { Tmux(dir, sock, ["kill-server"], timeoutMs: 5_000, check: false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            foreach (var pid in new[] { serverPid, panePid })
                if (pid > 0 && !WaitGone(pid, 2_000)) KillPid(pid);
        }
    }

    /// <summary>
    /// The capture is one SGR stream: attributes carry from line to line exactly as written, so it is parsed by an emulator of the same width
    /// (plain text and SGR only, the simplest input there is) with CR LF between the lines.
    /// </summary>
    private static ScreenState Parse(string capture, int columns, int rows)
    {
        var lines = capture.Split('\n');
        var parser = new VtEmulator(columns, rows, new EmulatorOptions { Reflow = false });
        // capture-pane marks line-drawing characters with SO ... SI (G1), the raw ASCII letter in between.
        var sb = new StringBuilder("\e)0");
        for (var r = 0; r < rows && r < lines.Length; r++)
        {
            if (r > 0) sb.Append("\r\n");
            sb.Append(lines[r]);
        }
        parser.Feed(sb.ToString());
        return ScreenState.From(parser);
    }

    private static string Tmux(string dir, string sock, IReadOnlyList<string> args, int timeoutMs = 10_000, bool check = true)
    {
        var psi = new ProcessStartInfo(Executable!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-S");
        psi.ArgumentList.Add(sock);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("/dev/null");
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var key in new[] { "SSH_AUTH_SOCK", "TMUX", "TMUX_PANE", "DISPLAY", "WAYLAND_DISPLAY", "BASH_ENV", "ENV" }) psi.Environment.Remove(key);
        psi.Environment["SHELL"] = "/bin/sh";
        psi.Environment["HOME"] = Path.Combine(dir, "home");
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(dir, "home");
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(dir, "home");
        psi.Environment["ZDOTDIR"] = Path.Combine(dir, "home");
        psi.Environment["TMUX_TMPDIR"] = dir;
        psi.Environment["LC_ALL"] = "C.UTF-8";
        psi.Environment["TERM"] = "xterm-256color";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"tmux {string.Join(' ', args)} timed out");
        }
        if (check && p.ExitCode != 0) throw new InvalidOperationException($"tmux {args[0]} failed ({p.ExitCode}): {stderr.Result}");
        return stdout.Result;
    }

    private static bool WaitGone(int pid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!Support.ProcessChecks.IsAlive(pid)) return true;
            Thread.Sleep(20);
        }
        return !Support.ProcessChecks.IsAlive(pid);
    }

    private static void KillPid(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => _dir.Dispose();
}
