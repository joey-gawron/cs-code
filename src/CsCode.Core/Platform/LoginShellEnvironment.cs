using System.Diagnostics;
using System.Text;

namespace CsCode.Core.Platform;

/// <summary>
/// lsp-1 / dbg-4: a Mac app started from Finder, the Dock, Spotlight or <c>open</c> inherits launchd's environment
/// (<c>PATH=/usr/bin:/bin:/usr/sbin:/sbin</c>), not the one the user's shell builds from <c>~/.zprofile</c>, <c>~/.zshrc</c>, Homebrew's
/// <c>shellenv</c>, nvm, … so every language server, debug adapter, task and terminal CsCode starts would miss <c>/opt/homebrew/bin</c> and the
/// rest. As VS Code does, CsCode then asks the user's login shell for its environment, ONCE at start-up: <c>$SHELL -l -i -c</c> printing
/// <c>env -0</c> between two random markers (whatever the rc files print around it is ignored), bounded by <see cref="DefaultTimeout"/>, and
/// merges it into its own environment (<see cref="Merge"/>), which every child inherits. Nothing is removed and CsCode's own <c>CSCODE_*</c>
/// variables are kept. The resolution runs in the background (the window does not wait for it); whatever starts a child process first awaits
/// <see cref="WaitAsync"/>, which is complete at once when nothing is being resolved.
/// <para>Only on macOS, and only when CsCode was not started from a terminal (<c>TERM</c> unset: a terminal's environment is already the shell's),
/// unless <c>CSCODE_SKIP_SHELL_ENV</c> is set. The shell sees <c>CSCODE_RESOLVING_ENVIRONMENT=1</c>, so an rc file can skip slow work.</para>
/// </summary>
public static class LoginShellEnvironment
{
    /// <summary>Set (to anything) to never resolve the login shell's environment.</summary>
    public const string SkipVariable = "CSCODE_SKIP_SHELL_ENV";

    /// <summary>Set to <c>1</c> in the environment of the shell that is asked, so its rc files can tell.</summary>
    public const string ResolvingVariable = "CSCODE_RESOLVING_ENVIRONMENT";

    /// <summary>VS Code's default (<c>application.shellEnvironmentResolutionTimeout</c>): an rc file with nvm and a few plugins takes seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What a run of the shell produced: its exit code (null: killed at the timeout), its standard output and the tail of its standard error.</summary>
    public sealed record ShellRun(int? ExitCode, string Output, string Error);

    /// <summary>Runs <paramref name="shell"/> with <paramref name="args"/> and the extra variables in <paramref name="environment"/>, killing it at <paramref name="timeout"/>.</summary>
    public delegate Task<ShellRun> ShellRunner(string shell, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, TimeSpan timeout);

    /// <summary>The outcome of a resolution: the shell's environment (null when none was read) and a one-line description for the log.</summary>
    public sealed record Resolution(IReadOnlyDictionary<string, string>? Environment, string Description);

    private static Task<Resolution?> s_pending = Task.FromResult<Resolution?>(null);

    /// <summary>
    /// Completes once the environment CsCode was started with has been completed by the login shell's (or that failed, or nothing was to be
    /// done); never faults. Callers that start child processes await it first, so they inherit the merged environment.
    /// </summary>
    public static Task WaitAsync(CancellationToken ct = default) => Volatile.Read(ref s_pending).WaitAsync(ct);

    /// <summary>The resolution started by <see cref="Start"/> (null result: none was started).</summary>
    public static Task<Resolution?> Pending => Volatile.Read(ref s_pending);

    /// <summary>
    /// Starts the resolution when <see cref="ShouldResolve"/> says so, merging the result into this process's environment when it arrives. Call
    /// once, early (before any child process can start). Returns whether a resolution was started.
    /// </summary>
    public static bool Start()
    {
        if (!ShouldResolve(Environment.GetEnvironmentVariable, OperatingSystem.IsMacOS())) return false;
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/zsh"; // the macOS default login shell
        Volatile.Write(ref s_pending, Task.Run<Resolution?>(async () =>
        {
            var resolution = await ResolveAsync(shell, DefaultRunner, DefaultTimeout).ConfigureAwait(false);
            if (resolution.Environment is null) return resolution;
            var changed = Merge(resolution.Environment, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);
            return resolution with { Description = resolution.Description + $"; {changed.Count} variable(s) changed" + (changed.Count == 0 ? "" : ": " + string.Join(", ", changed)) };
        }));
        return true;
    }

    /// <summary>On macOS (<paramref name="isMacOS"/>), when <see cref="SkipVariable"/> is unset and CsCode was not started from a terminal (no <c>TERM</c>).</summary>
    public static bool ShouldResolve(Func<string, string?> environment, bool isMacOS) =>
        isMacOS && string.IsNullOrEmpty(environment(SkipVariable)) && string.IsNullOrEmpty(environment("TERM"));

    /// <summary>
    /// The arguments that make <paramref name="shell"/> a login, interactive shell running <paramref name="command"/>: <c>-l -i -c</c> (sh, bash,
    /// zsh, fish, ksh, nu); <c>-i -c</c> for csh/tcsh (which take <c>-l</c> only alone); <c>-Login -Command</c> for PowerShell.
    /// </summary>
    public static IReadOnlyList<string> ShellArguments(string shell, string command)
    {
        var name = Path.GetFileNameWithoutExtension(shell);
        return name switch
        {
            "csh" or "tcsh" => ["-i", "-c", command],
            "pwsh" or "powershell" => ["-Login", "-Command", command],
            _ => ["-l", "-i", "-c", command],
        };
    }

    /// <summary>The command the shell runs: the marker, the environment NUL-separated, the marker again (external programs only: any shell's syntax).</summary>
    public static string Command(string marker) => $"/usr/bin/printf '%s' '{marker}'; /usr/bin/env -0; /usr/bin/printf '%s' '{marker}'";

    /// <summary>Runs the shell through <paramref name="run"/> and reads its environment. Never throws; a failure is described in the result.</summary>
    public static async Task<Resolution> ResolveAsync(string shell, ShellRunner run, TimeSpan timeout)
    {
        var marker = "cscode-env-" + Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        ShellRun result;
        try
        {
            result = await run(shell, ShellArguments(shell, Command(marker)), new Dictionary<string, string> { [ResolvingVariable] = "1" }, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new Resolution(null, $"login shell environment: {shell} could not be started: {ex.Message}");
        }
        var elapsed = $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms";
        if (result.ExitCode is null)
            return new Resolution(null, $"login shell environment: {shell} did not finish within {timeout.TotalSeconds:F0} s; using the environment CsCode was started with");
        if (Parse(result.Output, marker) is not { } env)
        {
            var error = result.Error.Trim();
            return new Resolution(null, $"login shell environment: {shell} exited with {result.ExitCode} without printing its environment ({elapsed})"
                + (error.Length == 0 ? "" : ": " + (error.Length > 300 ? error[^300..] : error)));
        }
        return new Resolution(env, $"login shell environment: read {env.Count} variable(s) from {shell} in {elapsed}");
    }

    /// <summary>The variables between the two markers of <paramref name="output"/> (<c>NAME=value</c>, NUL-separated); null when the markers are missing.</summary>
    public static IReadOnlyDictionary<string, string>? Parse(string output, string marker)
    {
        var start = output.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = output.IndexOf(marker, start, StringComparison.Ordinal);
        if (end < 0) return null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output[start..end].Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue; // no name: not a variable
            env[entry[..eq]] = entry[(eq + 1)..];
        }
        return env;
    }

    /// <summary>
    /// Variables of the shell that describe the shell itself (its directory, nesting level, last command) or CsCode (its own <c>CSCODE_*</c> switches,
    /// <see cref="ResolvingVariable"/>), never copied.
    /// </summary>
    public static bool IsExcluded(string name) =>
        name is "PWD" or "OLDPWD" or "SHLVL" or "_" || name.StartsWith("CSCODE_", StringComparison.Ordinal);

    /// <summary>
    /// Copies each variable of <paramref name="shellEnvironment"/> that is not <see cref="IsExcluded"/> and differs from the current value into the
    /// environment (<paramref name="set"/>). Nothing is removed. Returns the names that changed, sorted.
    /// </summary>
    public static IReadOnlyList<string> Merge(IReadOnlyDictionary<string, string> shellEnvironment, Func<string, string?> get, Action<string, string> set)
    {
        var changed = new List<string>();
        foreach (var (name, value) in shellEnvironment)
        {
            if (IsExcluded(name) || string.Equals(get(name), value, StringComparison.Ordinal)) continue;
            set(name, value);
            changed.Add(name);
        }
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    /// <summary>
    /// The real runner: no terminal (standard input is closed at once, so a prompt in an rc file reads end of file), standard error drained, the
    /// whole process tree killed at the timeout.
    /// </summary>
    public static ShellRunner DefaultRunner { get; } = RunShellAsync;

    private static async Task<ShellRun> RunShellAsync(string shell, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home && Directory.Exists(home) ? home : "/",
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("the shell did not start");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* gone meanwhile */ }
            return new ShellRun(null, "", "");
        }
        // A background job the rc file started may keep the pipes open after the shell exited: the output is not waited for beyond a moment.
        var drained = Task.WhenAll(output, error);
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false) != drained)
            return new ShellRun(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : "", "");
        return new ShellRun(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
