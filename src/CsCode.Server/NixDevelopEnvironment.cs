using CsCode.Core.Platform;
using CsCode.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// Work inside the workspace's <c>nix develop</c> shell: with <c>remote.nix.develop</c> = <c>auto</c> and a <c>flake.nix</c> in the primary folder, the
/// server asks <c>nix develop --command env -0</c> for the environment of the dev shell (its shellHook included) and merges it into its own, as
/// <see cref="LoginShellEnvironment"/> does for the login shell, so every terminal, language server, task and debug adapter it starts inherits the
/// toolchain the flake provides. Whatever starts a child process waits for the resolution (<see cref="LoginShellEnvironment.Also"/>). A folder that
/// changes, a changed <c>flake.nix</c> or <c>flake.lock</c>, or a changed setting resolves again (the previous values are put back first); an unchanged
/// workspace costs a file stat. A failure leaves the plain environment and says so.
/// </summary>
internal sealed class NixDevelopEnvironment(IServiceProvider sp, ILogger log)
{
    public static IReadOnlyList<ConfigurationProperty> Properties() => RemoteSettings.NixProperties();

    /// <summary>Variables of <c>nix develop</c>'s shell that describe that shell or a build, not the toolchain: never merged.</summary>
    public static bool IsExcluded(string name) =>
        LoginShellEnvironment.IsExcluded(name)
        || name is "HOME" or "USER" or "LOGNAME" or "SHELL" or "TERM" or "TMP" or "TMPDIR" or "TEMP" or "TEMPDIR" or "NIX_BUILD_TOP" or "NIX_LOG_FD" or "IN_NIX_SHELL_STAMP";

    /// <summary>The flake reference for <paramref name="folder"/> and the <c>remote.nix.flake</c> setting.</summary>
    public static string FlakeReference(string folder, string setting)
    {
        var path = Path.TrimEndingDirectorySeparator(folder);
        return string.IsNullOrWhiteSpace(setting) ? path : setting.StartsWith('#') ? path + setting : setting;
    }

    /// <summary>The <c>nix</c> arguments: the dev shell of <paramref name="flake"/> running a shell that prints its environment between the markers.</summary>
    public static IReadOnlyList<string> Arguments(string flake, string marker) =>
    [
        "--extra-experimental-features", "nix-command flakes", "develop", "--no-write-lock-file", flake,
        "--command", "sh", "-c", $"printf '%s' '{marker}'; env -0; printf '%s' '{marker}'",
    ];

    /// <summary>Records what a merge replaced so it can be put back.</summary>
    public sealed class Overlay
    {
        private readonly Dictionary<string, string?> _originals = new(StringComparer.Ordinal);

        /// <summary>Sets each non-excluded variable of <paramref name="environment"/> that differs; returns how many changed.</summary>
        public int Apply(IReadOnlyDictionary<string, string> environment, Func<string, string?> get, Action<string, string?> set)
        {
            var changed = 0;
            foreach (var (name, value) in environment)
            {
                if (IsExcluded(name) || string.Equals(get(name), value, StringComparison.Ordinal)) continue;
                if (!_originals.ContainsKey(name)) _originals[name] = get(name);
                set(name, value);
                changed++;
            }
            return changed;
        }

        /// <summary>Puts every changed variable back (removing the ones that did not exist).</summary>
        public void Revert(Action<string, string?> set)
        {
            foreach (var (name, original) in _originals) set(name, original);
            _originals.Clear();
        }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private string? _stamp;
    private Overlay _overlay = new();

    /// <summary>
    /// Starts (or skips, when nothing changed) the resolution for the workspace whose primary folder is <paramref name="folder"/> (a local path on this
    /// host; null: none). Returns at once: what starts child processes waits for it. Call before the folder is opened.
    /// </summary>
    public void Begin(string? folder)
    {
        var config = sp.GetService<IConfigurationService>();
        var enabled = string.Equals(config?.Get(RemoteSettings.NixDevelop, "off"), "auto", StringComparison.OrdinalIgnoreCase)
            && folder is not null && File.Exists(Path.Combine(folder, "flake.nix"));
        var flake = enabled ? FlakeReference(folder!, config?.Get(RemoteSettings.NixFlake, "") ?? "") : null;
        var stamp = flake is null ? null : flake + "|" + Stamp(folder!, "flake.nix") + "|" + Stamp(folder!, "flake.lock");
        lock (_lock)
        {
            if (stamp == _stamp) return;
            _stamp = stamp;
        }
        var timeout = TimeSpan.FromSeconds(Math.Clamp(config?.Get(RemoteSettings.NixTimeout, 600) ?? 600, 10, 3600));
        LoginShellEnvironment.Also(Task.Run(() => ResolveAsync(flake, folder, timeout)));
    }

    private static string Stamp(string folder, string file)
    {
        try { var i = new FileInfo(Path.Combine(folder, file)); return i.Exists ? i.Length + ":" + i.LastWriteTimeUtc.Ticks : "-"; }
        catch (IOException) { return "?"; }
    }

    private async Task ResolveAsync(string? flake, string? folder, TimeSpan timeout)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _overlay.Revert(Environment.SetEnvironmentVariable); // the plain environment first: a folder without a flake gets exactly that
            if (flake is null) return;
            var window = sp.GetService<IWindowService>();
            var marker = "cscode-nix-" + Guid.NewGuid().ToString("N");
            LoginShellEnvironment.ShellRun result;
            try
            {
                Task<LoginShellEnvironment.ShellRun> Run() => LoginShellEnvironment.DefaultRunner("nix", Arguments(flake, marker), new Dictionary<string, string>(), timeout);
                result = window is null ? await Run().ConfigureAwait(false)
                    : await window.WithProgressAsync($"Entering nix develop ({flake})", false, async (_, _) => await Run().ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                await FailAsync(window, flake, $"nix could not be started: {ex.Message}").ConfigureAwait(false);
                return;
            }
            if (result.ExitCode is null) { await FailAsync(window, flake, $"nix develop did not finish within {timeout.TotalSeconds:F0} s").ConfigureAwait(false); return; }
            if (LoginShellEnvironment.Parse(result.Output, marker) is not { } environment)
            {
                var error = result.Error.Trim();
                await FailAsync(window, flake, $"nix develop exited with {result.ExitCode}" + (error.Length == 0 ? "" : ": " + (error.Length > 400 ? error[^400..] : error))).ConfigureAwait(false);
                return;
            }
            var changed = _overlay.Apply(environment, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);
            log.LogInformation("nix develop environment for {Flake}: {Changed} variable(s) changed", flake, changed);
        }
        finally { _gate.Release(); }
    }

    private async Task FailAsync(IWindowService? window, string flake, string why)
    {
        lock (_lock) _stamp = null; // the next folder (re)open tries again
        log.LogWarning("nix develop environment for {Flake} not applied: {Why}", flake, why);
        if (window is not null)
            await window.ShowMessageAsync(MessageSeverity.Warning, $"Not in the nix develop environment ({flake}): {why}. Terminals and language servers use the plain environment.").ConfigureAwait(false);
    }
}
