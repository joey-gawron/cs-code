using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Lsp.Runtime;

namespace CsCode.Lsp.Extensions.Zls;

/// <summary>
/// What CsCode adds for zls (docs/ZIG.md): its settings, and the check that zls and the zig it runs are the same Zig version.
/// <para><b>Settings</b> (zls asks for <c>zls.*</c> through <c>workspace/configuration</c>, answered from CsCode's settings): build-on-save is
/// ON by default (<c>zls.enable_build_on_save</c>: zls runs <c>zig build --watch</c> in a project with a build.zig and reports its compile
/// errors, the <c>check</c> step when build.zig declares one, else the install step; zls's own default turns it on only with a check step);
/// <c>zls.zig_exe_path</c> picks the zig.</para>
/// <para><b>Version check</b>: zls must be built for the Zig it runs (zls 0.17.x for Zig 0.17.x; a mismatch reads the standard library wrong).
/// When zls is running, the zig it uses (<c>zls.zig_exe_path</c>, else the first <c>zig</c> on the PATH zls gets: its own directory, PATH, the
/// search directories) is asked <c>zig version</c> (bounded, off the caller's thread) and compared with the version zls reported in
/// <c>initialize</c>; on a mismatch ONE warning names both versions and both executables. zls's own mismatch message is held back while the
/// check runs and shown only when the check could not tell (so the user never gets two messages for one problem). Once per pair of versions
/// and executables per manager.</para>
/// </summary>
public sealed partial class ZlsExtension : LanguageServerExtension
{
    public const string ServerId = "zls";
    public const string BuildOnSaveSetting = "zls.enable_build_on_save";
    public const string ZigExePathSetting = "zls.zig_exe_path";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, bool> _verdicts = new(StringComparer.Ordinal); // versions and executables → mismatch (reported once)
    private int _disposed;

    public override string Name => ServerId;

    public override bool AppliesTo(LanguageServerDefinition definition) => definition.Id == ServerId;

    /// <summary>Runs <c>exe args</c> and returns its trimmed standard output (null on failure or after the time-out). A seam for tests.</summary>
    internal Func<string, string, CancellationToken, Task<string?>> Run { get; init; } = RunAsync;

    /// <summary>Environment seam (tests must not mutate process-wide variables).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public static IEnumerable<ConfigurationProperty> Properties()
    {
        yield return new(BuildOnSaveSetting, JsonSerializer.SerializeToElement(true),
            "zls: build the project when a file is saved and show the compiler's errors (zls runs 'zig build --watch': the 'check' step when build.zig declares one, else the install step). zls's own default is on only with a check step; CsCode's is on.");
        yield return new(ZigExePathSetting, null,
            "zls: the zig executable zls uses (not a directory). Unset: the first zig on the PATH zls gets (zls's own directory first). CsCode's zig/zls version check, debuggers and Test Explorer use it too.");
    }

    public override void Register(ServerExtensionHost host)
    {
        if (host.Services.Configuration is { } config) host.Disposables.Add(config.RegisterProperties(Properties()));
        host.Disposables.Add(Disposable.Create(() => Interlocked.Exchange(ref _disposed, 1)));
    }

    public override void Attach(ServerExtensionSession session) => session.State = new Check();

    public override void OnInitialized(ServerExtensionSession session) => StartCheck(session);

    /// <summary>zls's version complaint is held back until the check has run (and shown only when the check found nothing to say).</summary>
    public override bool HandleShowMessage(ServerExtensionSession session, MessageSeverity severity, string text)
    {
        if (!IsVersionMessage(text) || session.State is not Check check || Volatile.Read(ref _disposed) != 0) return false;
        lock (check) check.HeldBack = $"{session.Definition.DisplayName}: {text}";
        StartCheck(session);
        return true;
    }

    // ------------------------------------------------------------------ zls's own message

    /// <summary>zls's message about a zig it does not match (<c>ZLS '0.16.0' should be used with a Zig '0.16' release but found Zig '0.17.0'.</c>).</summary>
    internal static bool IsVersionMessage(string text) => ZlsVersionMessage().IsMatch(text);

    // ------------------------------------------------------------------ the check

    /// <summary>One zls's check: the one running and zls's complaint waiting for its verdict.</summary>
    private sealed class Check
    {
        public Task? Running;
        public string? HeldBack;
    }

    private void StartCheck(ServerExtensionSession session)
    {
        if (session.State is not Check check || Volatile.Read(ref _disposed) != 0) return;
        lock (check)
        {
            if (check.Running is { IsCompleted: false }) return;
            check.Running = Task.Run(() => CheckAndReportAsync(session, check));
        }
    }

    private async Task CheckAndReportAsync(ServerExtensionSession session, Check check)
    {
        (string? Message, bool Mismatch) verdict;
        try { verdict = await CheckCoreAsync(session.ServerVersion, session.Definition, session.Services, session.Settings.SearchDirs, session.Log, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            session.Log($"the zig/zls version check failed: {ex.Message}");
            verdict = (null, false);
        }
        string? zlsOwn;
        lock (check) { zlsOwn = check.HeldBack; check.HeldBack = null; }
        if (session.Services.Window is not { } window || Volatile.Read(ref _disposed) != 0) return;
        if (verdict.Message is not null) await window.ShowMessageAsync(MessageSeverity.Warning, verdict.Message).ConfigureAwait(false);
        // zls's own complaint: dropped when CsCode's message said it (now or before), shown as zls said it when the check could not tell
        else if (zlsOwn is not null && !verdict.Mismatch) await window.ShowMessageAsync(MessageSeverity.Warning, zlsOwn).ConfigureAwait(false);
    }

    /// <summary>The message to show (null: nothing new) and whether zls and zig are known not to match (also when that was reported before).</summary>
    private async Task<(string? Message, bool Mismatch)> CheckCoreAsync(string? reportedVersion, LanguageServerDefinition definition, LspServices services,
        IReadOnlyList<string> searchDirs, Action<string> log, CancellationToken ct)
    {
        var zls = Zls(definition, services, searchDirs);
        var zlsVersion = reportedVersion is { Length: > 0 } ? reportedVersion : zls is null ? null : await Run(zls, "--version", ct).ConfigureAwait(false);
        var zig = Zig(zls, services, searchDirs);
        if (zlsVersion is null || zig is null) return (null, false);
        var zigVersion = await Run(zig, "version", ct).ConfigureAwait(false);
        if (zigVersion is null) return (null, false);
        var key = $"{zlsVersion}|{zigVersion}|{zls}|{zig}";
        var mismatch = !Compatible(zlsVersion, zigVersion);
        lock (_verdicts)
        {
            if (_verdicts.ContainsKey(key)) return (null, mismatch);
            if (_verdicts.Count >= 32) _verdicts.Clear();
            _verdicts[key] = mismatch;
        }
        if (!mismatch) return (null, false);
        var message = Mismatch(zlsVersion, zls, zigVersion, zig);
        log(message);
        return (message, true);
    }

    /// <summary>
    /// Whether zls <paramref name="zlsVersion"/> serves Zig <paramref name="zigVersion"/>: the same major and minor version (zls 0.17.x for
    /// Zig 0.17.x; a development build of either is held to the same rule). Unparseable versions are not judged.
    /// </summary>
    internal static bool Compatible(string zlsVersion, string zigVersion) =>
        MajorMinor(zlsVersion) is not { } zls || MajorMinor(zigVersion) is not { } zig || zls == zig;

    private static (int, int)? MajorMinor(string version)
    {
        var m = VersionPattern().Match(version);
        return m.Success ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
    }

    internal static string Mismatch(string zlsVersion, string? zls, string zigVersion, string zig)
    {
        var series = MajorMinor(zigVersion) is var (major, minor) ? $"{major}.{minor}" : zigVersion;
        var zlsSeries = MajorMinor(zlsVersion) is var (zmajor, zminor) ? $"{zmajor}.{zminor}" : zlsVersion;
        return $"zls and Zig do not match: zls {zlsVersion}{(zls is null ? "" : $" ({zls})")} is built for Zig {zlsSeries}, but the zig it runs is {zigVersion} ({zig}). " +
            $"Install the zls release for Zig {series} (github.com/zigtools/zls/releases; tools/fetch-zig.sh pins a matching pair), or set '{ZigExePathSetting}' to a Zig {zlsSeries}.";
    }

    /// <summary>The zls executable the session runs (its effective definition's command, resolved as the connection resolves it).</summary>
    private static string? Zls(LanguageServerDefinition definition, LspServices services, IReadOnlyList<string> searchDirs)
    {
        var env = definition.Env is { } e && e.TryGetValue("PATH", out var p) ? new Dictionary<string, string> { ["PATH"] = p } : null;
        return ToolResolver.Resolve(definition.Command, services.Paths?.ToolsDir, null, env, searchDirs);
    }

    /// <summary>The zig zls runs: <c>zls.zig_exe_path</c>, else the first zig on the PATH zls is started with (its own directory first).</summary>
    private string? Zig(string? zls, LspServices services, IReadOnlyList<string> searchDirs)
    {
        if (services.Configuration?.Get<string>(ZigExePathSetting) is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : null;
        var path = zls is null ? Environment("PATH") : ToolResolver.ExtendPath(Environment("PATH"), zls, searchDirs);
        return ToolResolver.FindOnPath(OperatingSystem.IsWindows() ? "zig.exe" : "zig", path);
    }

    private static async Task<string?> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        try
        {
            await LoginShellEnvironment.WaitAsync(ct).ConfigureAwait(false);
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            return p.ExitCode == 0 ? (await stdout.ConfigureAwait(false)).Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }

    [GeneratedRegex(@"^\s*v?(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\bZLS '[^']*' (should be used with a Zig|does not support Zig)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ZlsVersionMessage();
}
