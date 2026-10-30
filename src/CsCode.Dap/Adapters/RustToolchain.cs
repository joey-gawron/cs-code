using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>A Rust toolchain as the debuggers need it: where its debugger helpers and the standard library's sources are.</summary>
/// <param name="Sysroot"><c>rustc --print sysroot</c>.</param>
/// <param name="Commit">rustc's commit hash (<c>rustc -vV</c>): the standard library's DWARF paths are <c>/rustc/&lt;commit&gt;/library/…</c>.</param>
internal sealed record RustToolchain(string Sysroot, string? Commit)
{
    /// <summary>GDB's pretty-printers for String, Vec, HashMap, Rc… (rust-gdb loads them).</summary>
    public string PrettyPrinters => Path.Combine(Sysroot, "lib", "rustlib", "etc", "gdb_load_rust_pretty_printers.py");
    /// <summary>The rust-src component: the standard library's sources, when installed.</summary>
    public string SourceRoot => Path.Combine(Sysroot, "lib", "rustlib", "src", "rust");
    /// <summary>Where the standard library's frames say their sources are (<c>/rustc/&lt;commit&gt;</c>), when the commit is known.</summary>
    public string? DebugInfoPrefix => Commit is { Length: > 0 } commit ? "/rustc/" + commit : null;
}

/// <summary>
/// Finds the Rust toolchain of a folder through <c>rustc</c> on PATH, run IN the folder so rustup honours a <c>rust-toolchain</c> file there;
/// cached per folder; null without rustc. Shared by the GDB and CodeLLDB factories.
/// </summary>
internal sealed class RustToolchainLocator(Func<string, string?> environment, ILogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RustToolchain?> _byFolder = new(StringComparer.Ordinal);

    public async Task<RustToolchain?> FindAsync(string? folder, CancellationToken ct)
    {
        var key = folder ?? "";
        await _gate.WaitAsync(ct);
        try
        {
            if (_byFolder.TryGetValue(key, out var known)) return known;
            var rustc = FindOnPath(environment, OperatingSystem.IsWindows() ? "rustc.exe" : "rustc");
            RustToolchain? found = null;
            if (rustc is not null)
            {
                var sysroot = (await RunAsync(rustc, ["--print", "sysroot"], folder, ct))?.Trim();
                if (sysroot is { Length: > 0 } && Directory.Exists(sysroot))
                {
                    var version = await RunAsync(rustc, ["-vV"], folder, ct) ?? "";
                    var commit = version.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("commit-hash:", StringComparison.Ordinal))?["commit-hash:".Length..].Trim();
                    found = new RustToolchain(sysroot, commit is { Length: > 0 } and not "unknown" ? commit : null);
                }
            }
            _byFolder[key] = found;
            return found;
        }
        finally { _gate.Release(); }
    }

    public static string? FindOnPath(Func<string, string?> environment, string name)
    {
        foreach (var dir in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    /// <summary>Runs a short tool (rustc) and returns its stdout, or null when it fails or takes more than 15 s.</summary>
    private async Task<string?> RunAsync(string exe, IEnumerable<string> args, string? cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            return p.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "{Tool} failed", exe);
            return null;
        }
    }
}
