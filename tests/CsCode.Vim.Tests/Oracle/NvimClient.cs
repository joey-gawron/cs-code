using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace CsCode.Vim.Tests.Oracle;

public sealed class NvimException(string message) : Exception(message);

/// <summary>
/// Drives ONE real <c>nvim --headless --embed</c> process over msgpack-RPC on its stdio. Typed input goes through
/// <c>nvim_input</c>, i.e. the same path a UI uses, so undo sync / typeahead / mapping behaviour is genuinely interactive.
/// The process is private to this instance: only its own PID is ever killed, and all XDG dirs point into a temp dir so the
/// user's config, shada and swap files are never touched.
/// </summary>
public sealed class NvimClient : IDisposable
{
    private readonly Process _process;
    private readonly Stream _in;
    private readonly Stream _out;
    private readonly string _stateDir;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<List<object?>>> _pending = new();
    private readonly Thread _reader;
    private readonly StringBuilder _stderr = new();
    private long _nextId;
    private volatile bool _closed;

    public static string? FindNvim()
    {
        var env = Environment.GetEnvironmentVariable("CSCODE_NVIM");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (home is not null)
        {
            var p = Path.Combine(home, ".nix-profile", "bin", "nvim");
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, "nvim");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public NvimClient(string nvimPath)
    {
        _stateDir = Path.Combine(Path.GetTempPath(), "cscode-nvim-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_stateDir);
        var psi = new ProcessStartInfo(nvimPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -u NONE: no config/plugins. -i NONE: no shada. -n: no swap. Never touches the user's real state.
        foreach (var a in new[] { "--headless", "--embed", "-u", "NONE", "-i", "NONE", "-n", "--noplugin" }) psi.ArgumentList.Add(a);
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(_stateDir, "config");
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(_stateDir, "data");
        psi.Environment["XDG_STATE_HOME"] = Path.Combine(_stateDir, "state");
        psi.Environment["XDG_CACHE_HOME"] = Path.Combine(_stateDir, "cache");
        psi.Environment["NVIM_LOG_FILE"] = Path.Combine(_stateDir, "nvim.log");
        psi.Environment["HOME"] = _stateDir;
        _process = Process.Start(psi) ?? throw new NvimException("could not start nvim");
        _in = _process.StandardInput.BaseStream;
        _out = _process.StandardOutput.BaseStream;
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) { if (_stderr.Length < 4000) _stderr.AppendLine(e.Data); } };
        _process.BeginErrorReadLine();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "nvim-rpc-reader" };
        _reader.Start();
    }

    public int ProcessId => _process.Id;

    private void ReadLoop()
    {
        try
        {
            while (true)
            {
                var msg = MsgPack.Read(_out) as List<object?>;
                if (msg is null || msg.Count < 3) continue;
                if (msg[0] is long type && type == 1 && msg[1] is long id && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
                // notifications (2) and server->client requests (0) are irrelevant for a UI-less embed
            }
        }
        catch (Exception)
        {
            // Any read failure (EOF when nvim exits, torn stream) ends the session; callers see NvimException.
            _closed = true;
            foreach (var kv in _pending) kv.Value.TrySetException(new NvimException("nvim exited: " + Stderr()));
        }
    }

    public string Stderr() { lock (_stderr) return _stderr.ToString(); }

    public object? Call(string method, params object?[] args) => Call(TimeSpan.FromSeconds(10), method, args);

    public object? Call(TimeSpan timeout, string method, params object?[] args)
    {
        if (_closed) throw new NvimException("nvim is not running");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<List<object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using (var ms = new MemoryStream())
        {
            MsgPack.Write(ms, new List<object?> { 0L, id, method, args.ToList() });
            _in.Write(ms.GetBuffer(), 0, (int)ms.Length);
            _in.Flush();
        }
        if (!tcs.Task.Wait(timeout))
        {
            _pending.TryRemove(id, out _);
            throw new NvimException($"timeout waiting for nvim_{method} (nvim blocked?)");
        }
        var res = tcs.Task.Result;
        if (res[2] is not null) throw new NvimException($"{method}: {Describe(res[2])}");
        return res[3];
    }

    private static string Describe(object? o) => o is List<object?> l && l.Count > 1 ? l[1]?.ToString() ?? "?" : o?.ToString() ?? "?";

    public object? Lua(string code, params object?[] args) => Call("nvim_exec_lua", code, args.ToList());
    public void Command(string ex) => Call("nvim_command", ex);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { Call(TimeSpan.FromMilliseconds(500), "nvim_command", "qa!"); } catch (Exception) { /* fall through to kill */ }
                if (!_process.WaitForExit(500)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
        try { Directory.Delete(_stateDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
