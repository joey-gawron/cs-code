using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;

namespace CsCode.Panels.Git.Engine;

/// <summary>What one git invocation produced. <see cref="Output"/> is raw bytes (paths in <c>-z</c> output are UTF-8, file contents may be anything).</summary>
public sealed record GitResult(int ExitCode, byte[] Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>A git command that exited with an error; <see cref="Message"/> is git's own explanation (the last lines of its standard error).</summary>
public sealed class GitException(string command, int exitCode, string error)
    : Exception(string.IsNullOrWhiteSpace(error) ? $"git {command} failed (exit code {exitCode})" : error.Trim())
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string Error { get; } = error;
}

/// <summary>
/// The git executable and the lane its processes run on. Git runs as a child process (the CLI is the only interface: no libgit2); each call is
/// one process with <c>GIT_TERMINAL_PROMPT=0</c> (never wait for a password on a terminal that is not there), <c>GIT_OPTIONAL_LOCKS=0</c> (status
/// never takes the index lock away from the user's own git), and no pager or editor (porcelain output is locale-free, so git's messages stay in the user's language).
/// Processes are started and read on the lane's dedicated threads ("cscode-git-N"), never on the UI thread or the shared pool; cancelling kills
/// the process. A missing git is reported once, with how to fix it (<see cref="Unavailable"/>).
/// </summary>
public sealed class GitRunner : IDisposable
{
    /// <summary>Where git is looked for after the PATH: the usual install directories, which a GUI app's PATH may lack.</summary>
    public static IReadOnlyList<string> DefaultSearchDirs { get; } = OperatingSystem.IsWindows()
        ? [@"C:\Program Files\Git\cmd", @"C:\Program Files\Git\bin"]
        : OperatingSystem.IsMacOS()
            ? ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "~/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "/usr/bin"]
            : ["/usr/bin", "/usr/local/bin", "~/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "/run/current-system/sw/bin"];

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread[] _threads;
    private readonly IMemoryMonitor? _memory;
    private readonly Func<string?> _configuredPath;
    private readonly object _gate = new();
    private Task<string?>? _resolved;
    private string? _resolvedFor;
    private long _resolvedAt;
    private int _disposed;

    /// <param name="configuredPath">The <c>git.path</c> setting (read on each resolution); null or empty = search.</param>
    /// <param name="threads">Lane width: status refreshes, blame and a long pull can run side by side.</param>
    public GitRunner(Func<string?> configuredPath, IMemoryMonitor? memory = null, int threads = 3)
    {
        _configuredPath = configuredPath;
        _memory = memory;
        _threads = new Thread[Math.Max(1, threads)];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Loop) { IsBackground = true, Name = $"cscode-git-{i + 1}", Priority = ThreadPriority.BelowNormal };
            _threads[i].Start();
        }
    }

    /// <summary>The overall limit of one network operation when <see cref="NetworkTimeout"/> is not set (stalls end sooner: <see cref="GitNetwork"/>).</summary>
    public static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Answers the prompts of network operations (ssh passphrases and host keys, HTTPS credentials) in the GUI; null: nothing can prompt.</summary>
    public IAskpassService? Askpass { get; set; }

    /// <summary>Whether network operations use <see cref="Askpass"/> (setting <c>git.useIntegratedAskPass</c>).</summary>
    public Func<bool> UseAskpass { get; set; } = () => true;

    /// <summary>The overall limit of one network operation (setting <c>git.networkTimeout</c>); <see cref="Timeout.InfiniteTimeSpan"/> = none.</summary>
    public Func<TimeSpan> NetworkTimeout { get; set; } = () => DefaultNetworkTimeout;

    /// <summary>True on one of the lane's threads (tests).</summary>
    public bool IsLaneThread => Array.IndexOf(_threads, Thread.CurrentThread) >= 0;

    /// <summary>Why git cannot be used, when it cannot (null while it can, or before the first call).</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The executable in use, once resolved.</summary>
    public string? ExecutablePath { get; private set; }

    /// <summary>Resolves git (the configured path, the PATH after the login shell's environment arrived, the install directories) and checks it runs.</summary>
    public Task<string?> ResolveAsync()
    {
        var configured = _configuredPath();
        lock (_gate)
        {
            // A failed lookup is retried after a while (git installed meanwhile); a found git is kept until git.path changes.
            var stale = _resolved is { IsCompleted: true } && ExecutablePath is null && Environment.TickCount64 - _resolvedAt > 30_000;
            if (_resolved is not null && _resolvedFor == configured && !stale) return _resolved;
            _resolvedFor = configured;
            _resolvedAt = Environment.TickCount64;
            return _resolved = ResolveOuterAsync(configured);
        }
    }

    private async Task<string?> ResolveOuterAsync(string? configured)
    {
        // The login shell's PATH (macOS app started from the Dock) is merged into this process's environment asynchronously; wait for it, bounded.
        try { await LoginShellEnvironment.WaitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return await RunOnLane(() => ResolveCore(configured)).ConfigureAwait(false);
    }

    private string? ResolveCore(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(Expand(configured.Trim()));
        else
        {
            if (FindOnPath("git") is { } onPath) candidates.Add(onPath);
            foreach (var dir in DefaultSearchDirs)
            {
                var candidate = Path.Combine(Expand(dir), OperatingSystem.IsWindows() ? "git.exe" : "git");
                if (!candidates.Contains(candidate)) candidates.Add(candidate);
            }
        }
        foreach (var candidate in candidates)
        {
            if (!IsExecutable(candidate)) continue;
            try
            {
                var r = RunSync(candidate, null, ["--version"], null, null, TimeSpan.FromSeconds(10), CancellationToken.None, null);
                if (r.Ok && r.Text.StartsWith("git version", StringComparison.Ordinal))
                {
                    ExecutablePath = candidate;
                    Unavailable = null;
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        Unavailable = !string.IsNullOrWhiteSpace(configured)
            ? $"Git was not found at \"{configured}\" (setting git.path)."
            : OperatingSystem.IsMacOS()
                ? "Git was not found. Install the Xcode Command Line Tools (xcode-select --install) or Homebrew's git, or set git.path."
                : "Git was not found on the PATH. Install git, or set git.path.";
        return null;
    }

    /// <summary>
    /// Runs <c>git -C <paramref name="workingDirectory"/> <paramref name="args"/></c> on the lane. Throws <see cref="GitException"/> for a non-zero
    /// exit when <paramref name="throwOnError"/>, <see cref="InvalidOperationException"/> when git is unavailable, and
    /// <see cref="OperationCanceledException"/> when cancelled (the process is killed).
    /// </summary>
    public async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, byte[]? stdin = null, bool throwOnError = true,
        TimeSpan? timeout = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? environment = null)
    {
        var exe = await ResolveAsync().ConfigureAwait(false) ?? throw new InvalidOperationException(Unavailable ?? "Git is not available.");
        var result = await RunOnLane(() => RunSync(exe, workingDirectory, args, stdin, environment, timeout ?? TimeSpan.FromSeconds(60), ct, null)).ConfigureAwait(false);
        return Checked(args, result, throwOnError);
    }

    /// <summary>
    /// Runs a network operation (fetch, pull, push) as <see cref="RunAsync"/> does, but so that it can neither prompt on a terminal nor hang for
    /// ever (<see cref="GitNetwork"/>): ssh in batch mode or with the GUI askpass helper, connect and stall timeouts, git in a session of its own,
    /// and an overall limit (<see cref="NetworkTimeout"/>). Cancelling kills the process tree as usual.
    /// </summary>
    public async Task<GitResult> RunNetworkAsync(string workingDirectory, IReadOnlyList<string> args, bool throwOnError = true, CancellationToken ct = default)
    {
        var exe = await ResolveAsync().ConfigureAwait(false) ?? throw new InvalidOperationException(Unavailable ?? "Git is not available.");
        var config = await RunAsync(workingDirectory, ["config", "--get-regexp", GitNetwork.ConfigPattern], throwOnError: false, timeout: TimeSpan.FromSeconds(15), ct: ct).ConfigureAwait(false);
        using var askpass = UseAskpass() ? StartAskpass() : null;
        var env = GitNetwork.Environment(Environment.GetEnvironmentVariable, GitNetwork.ParseConfig(config.Ok ? config.Text : ""),
            askpass is null ? null : (askpass.Program, askpass.Environment));
        var timeout = NetworkTimeout();
        var result = await RunOnLane(() => RunSync(exe, workingDirectory, args, null, env, timeout, ct, GitNetwork.DetachLauncher)).ConfigureAwait(false);
        return Checked(args, result, throwOnError);
    }

    private IAskpassSession? StartAskpass()
    {
        try { return Askpass?.Start(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static GitResult Checked(IReadOnlyList<string> args, GitResult result, bool throwOnError)
    {
        if (throwOnError && !result.Ok)
        {
            // some refusals go to standard output ("nothing to commit, working tree clean")
            var error = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : string.Join('\n', result.Text.Split('\n').TakeLast(20));
            throw new GitException(args.Count > 0 ? args[0] : "", result.ExitCode, error);
        }
        return result;
    }

    private Task<T> RunOnLane<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { tcs.TrySetResult(work()); }
                catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { tcs.TrySetException(new ObjectDisposedException(nameof(GitRunner))); }
        return tcs.Task;
    }

    private void Loop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        }
        catch (ObjectDisposedException) { }
    }

    /// <param name="launcher">A program (and its arguments) that git runs under, given git's path and arguments after its own (setsid).</param>
    private GitResult RunSync(string exe, string? cwd, IReadOnlyList<string> args, byte[]? stdin, IReadOnlyDictionary<string, string>? env, TimeSpan timeout, CancellationToken ct,
        IReadOnlyList<string>? launcher)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(launcher is { Count: > 0 } ? launcher[0] : exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (launcher is { Count: > 0 })
        {
            for (var i = 1; i < launcher.Count; i++) psi.ArgumentList.Add(launcher[i]);
            psi.ArgumentList.Add(exe);
        }
        if (cwd is not null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(cwd); psi.WorkingDirectory = cwd; }
        // No pager, no editor, no credential prompt on a terminal: a GUI has none. Credential helpers (osxkeychain, libsecret, GCM) still work.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("color.ui=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["GIT_EDITOR"] = OperatingSystem.IsWindows() ? "rem" : "true";
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        psi.Environment.Remove("GIT_INDEX_FILE");
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) { if (stderr.Length < 64 * 1024) stderr.AppendLine(e.Data); } };
        process.Start();
        using var tracked = TrackSafe(process);
        process.BeginErrorReadLine();
        var killed = 0;
        void Kill() { if (Interlocked.Exchange(ref killed, 1) == 0) try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { } }
        using var cancel = ct.Register(Kill);
        using var timer = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan ? new Timer(_ => Kill(), null, timeout, Timeout.InfiniteTimeSpan) : null;
        try
        {
            if (stdin is not null) process.StandardInput.BaseStream.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException) { /* the process exited without reading its input: its exit code says why */ }
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();
        string err;
        lock (stderr) err = stderr.ToString();
        if (Volatile.Read(ref killed) == 1) return new GitResult(-1, output.ToArray(), $"git {(args.Count > 0 ? args[0] : "")} timed out after {Describe(timeout)}");
        return new GitResult(process.ExitCode, output.ToArray(), err);
    }

    private static string Describe(TimeSpan t) => t.TotalSeconds < 120 ? $"{t.TotalSeconds:0} s" : $"{t.TotalMinutes:0} min";

    private IDisposable? TrackSafe(Process p)
    {
        if (_memory is null) return null;
        try { return _memory.TrackChildProcess("git", p.Id); }
        catch (InvalidOperationException) { return null; }
    }

    private static string Expand(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string? FindOnPath(string command)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? command + ".exe" : command);
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    internal static bool IsExecutable(string path)
    {
        try
        {
#pragma warning disable RS0030 // runs on the git lane (a dedicated thread), not the UI thread
            if (!File.Exists(path)) return false;
#pragma warning restore RS0030
            return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
    }
}
