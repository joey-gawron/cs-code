using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace CsCode.App.Tests.Install;

/// <summary>The outcome of a script or program run in the sandbox.</summary>
public sealed record SandboxRun(int ExitCode, string Output, string Error)
{
    public string All => Output + Error;
}

/// <summary>
/// A throwaway home for tools/install.sh and tools/uninstall.sh: HOME, XDG_DATA_HOME, XDG_CONFIG_HOME, XDG_CACHE_HOME, XDG_STATE_HOME and TMPDIR
/// all point into a fresh directory under /tmp (short, for Unix socket paths), and every run starts from <c>env -i</c> with PATH=/usr/bin:/bin:
/// no DBUS_SESSION_BUS_ADDRESS, no DISPLAY, no CSCODE_* variable, no user rc file (<c>bash --noprofile --norc</c>). The scripts always get
/// <c>--no-refresh</c>, so no desktop cache of the real session (kbuildsycoca6, update-desktop-database, gtk-update-icon-cache) is touched.
/// Dispose kills (by PID) every process whose environment has this HOME, then deletes the directory.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class InstallSandbox : IDisposable
{
    public InstallSandbox(string homeName = "home")
    {
        if (!OperatingSystem.IsLinux()) Xunit.Assert.Skip("the installer is for Linux (tools/install.ps1 and tools/macos are untested here)");
        Root = Path.Combine("/tmp", "csi-" + Guid.NewGuid().ToString("N")[..8]);
        Home = Path.Combine(Root, homeName);
        foreach (var d in new[] { Home, Tmp, Run }) Directory.CreateDirectory(d);
        File.SetUnixFileMode(Run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Root { get; }
    public string Home { get; }
    public string Tmp => Path.Combine(Root, "tmp");
    /// <summary>A private XDG_RUNTIME_DIR (0700) for runs of the real app: never the user's runtime directory.</summary>
    public string Run => Path.Combine(Root, "run");
    public string Data => Path.Combine(Home, ".local", "share");
    public string AppRoot => Path.Combine(Data, "cscode");
    public string Current => Path.Combine(AppRoot, "current");
    public string Manifest => Path.Combine(AppRoot, "install-manifest.tsv");
    public string Launcher => Path.Combine(Home, ".local", "bin", "cscode");
    public string DesktopEntry => Path.Combine(Data, "applications", "cscode.desktop");
    public string Icon(string size) => size == "scalable"
        ? Path.Combine(Data, "icons", "hicolor", "scalable", "apps", "cscode.svg")
        : Path.Combine(Data, "icons", "hicolor", $"{size}x{size}", "apps", "cscode.png");

    /// <summary>The installed version <c>current</c> points at (the real directory, as the app's AppContext.BaseDirectory sees it).</summary>
    public string CurrentVersionDir => Path.Combine(AppRoot, new FileInfo(Current).LinkTarget ?? throw new InvalidOperationException("no current link"));

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The environment of every run: only what the sandbox defines.</summary>
    public Dictionary<string, string?> Environment() => new()
    {
        ["HOME"] = Home,
        ["XDG_DATA_HOME"] = Data,
        ["XDG_CONFIG_HOME"] = Path.Combine(Home, ".config"),
        ["XDG_CACHE_HOME"] = Path.Combine(Home, ".cache"),
        ["XDG_STATE_HOME"] = Path.Combine(Home, ".local", "state"),
        ["TMPDIR"] = Tmp,
        ["PATH"] = "/usr/bin:/bin",
        ["LANG"] = "C.UTF-8",
    };

    public Task<SandboxRun> InstallAsync(params string[] args) => ScriptAsync("install.sh", ["--no-refresh", .. args]);

    public Task<SandboxRun> UninstallAsync(params string[] args) => ScriptAsync("uninstall.sh", ["--no-refresh", .. args]);

    /// <summary>A script of the source tree's tools/ (or any script path) through bash without rc files.</summary>
    public Task<SandboxRun> ScriptAsync(string script, IEnumerable<string> args, IDictionary<string, string?>? env = null)
    {
        var path = Path.IsPathRooted(script) ? script : Path.Combine(RepoRoot(), "tools", script);
        return RunAsync("/bin/bash", ["--noprofile", "--norc", path, .. args], env);
    }

    /// <summary>
    /// Runs <paramref name="program"/> under <c>env -i</c> with <see cref="Environment"/>, changed by <paramref name="env"/> (a null value removes
    /// a variable).
    /// </summary>
    public async Task<SandboxRun> RunAsync(string program, IEnumerable<string> args, IDictionary<string, string?>? env = null, int timeoutMs = 300_000)
    {
        var vars = Environment();
        if (env is not null) foreach (var (k, v) in env) vars[k] = v;
        var psi = new ProcessStartInfo("/usr/bin/env") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("-i");
        foreach (var (k, v) in vars) if (v is not null) psi.ArgumentList.Add($"{k}={v}");
        psi.ArgumentList.Add(program);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true); // the run this sandbox started
            throw new TimeoutException($"{program} {string.Join(' ', args)} did not finish within {timeoutMs / 1000} s");
        }
        return new SandboxRun(p.ExitCode, await output, await error);
    }

    /// <summary>
    /// A minimal stand-in for tools/publish-app.sh's output: <c>cscode</c> (a shell script that prints <paramref name="appVersion"/> for
    /// <c>--version</c> and otherwise writes its arguments, DOTNET_ROOT and PATH to <c>$HOME/fake-app.out</c>), <c>cscode.dll</c>, a nested file
    /// and <c>sleeper</c>, a copy of sleep(1): a process started from it runs FROM the installed version, as the real app would.
    /// </summary>
    public string FakePublish(string name = "publish", string appVersion = "9.8.7", string? extra = null)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "cscode"), $$"""
            #!/bin/sh
            if [ "$1" = --version ]; then echo {{appVersion}}; exit 0; fi
            out="$HOME/fake-app.out"
            : > "$out"
            for a in "$@"; do printf 'arg:%s\n' "$a" >> "$out"; done
            printf 'DOTNET_ROOT=%s\n' "${DOTNET_ROOT-<unset>}" >> "$out"
            printf 'PATH=%s\n' "${PATH-<unset>}" >> "$out"
            """.Replace("\r", ""));
        File.SetUnixFileMode(Path.Combine(dir, "cscode"), (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "cscode.dll"), "not really an assembly " + (extra ?? ""));
        File.WriteAllText(Path.Combine(dir, "nested", "data.txt"), "nested\n");
        var sleep = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        File.Copy(sleep, Path.Combine(dir, "sleeper"), overwrite: true);
        File.SetUnixFileMode(Path.Combine(dir, "sleeper"), (UnixFileMode)0b111_101_101);
        return dir;
    }

    /// <summary>
    /// Every entry under <see cref="Home"/>: <c>d</c> for a directory, <c>l:&lt;target&gt;</c> for a symlink, <c>f:&lt;mode&gt;:&lt;sha256&gt;</c>
    /// for a file, keyed by the path relative to HOME.
    /// </summary>
    public SortedDictionary<string, string> Snapshot(params string[] ignore)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Walk(string dir)
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var rel = Path.GetRelativePath(Home, entry.FullName);
                if (ignore.Contains(rel)) continue;
                if (entry.LinkTarget is { } target) result[rel] = "l:" + target;
                else if (entry is DirectoryInfo d) { result[rel] = "d"; Walk(d.FullName); }
                else result[rel] = $"f:{Convert.ToString((int)File.GetUnixFileMode(entry.FullName), 8)}:{Hash(entry.FullName)}";
            }
        }
        // a running session holds its lock file (<data>/sessions/.locks/*.lock) exclusively: listed, not read
        static string Hash(string file)
        {
            try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))); }
            catch (IOException) { return "in-use"; }
        }
        Walk(Home);
        return result;
    }

    public static string Describe(IDictionary<string, string> snapshot) => string.Join("\n", snapshot.Select(kv => $"{kv.Key} {kv.Value}"));

    /// <summary>The manifest's lines of one kind (version, file, dir, link, current, previous): the fields after the kind.</summary>
    public List<string[]> ManifestEntries(string kind) =>
        File.ReadAllLines(Manifest).Select(l => l.Split('\t')).Where(f => f[0] == kind).Select(f => f[1..]).ToList();

    /// <summary>Processes whose environment has this HOME (only processes this sandbox caused can).</summary>
    public List<int> Processes()
    {
        var needle = Encoding.UTF8.GetBytes("\0HOME=" + Home + "\0");
        var pids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == System.Environment.ProcessId) continue;
            try
            {
                var environ = File.ReadAllBytes(Path.Combine(dir, "environ"));
                var padded = new byte[environ.Length + 1]; // a leading NUL: the first variable matches like the others
                environ.CopyTo(padded, 1);
                if (padded.AsSpan().IndexOf(needle) >= 0 && Infrastructure.ProcessProbe.IsRunning(pid)) pids.Add(pid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return pids;
    }

    public void Dispose()
    {
        foreach (var pid in Processes())
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
