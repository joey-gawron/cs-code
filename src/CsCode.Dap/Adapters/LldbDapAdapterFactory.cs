using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using CsCode.Dap.Transport;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Rust, C and C++ debugging through <b>lldb-dap</b>, LLVM's own Debug Adapter Protocol server for LLDB (Apache-2.0 with LLVM exceptions;
/// called <c>lldb-vscode</c> before LLVM 18), type <c>lldb-dap</c> — the type of LLVM's VS Code extension. Nothing to download on a Mac with
/// Xcode or its Command Line Tools (Apple ships it: <c>xcrun -f lldb-dap</c>), and LLVM's packages carry it on Linux. It speaks DAP on stdio
/// when started without <c>--connection</c>; one session per process. A <see cref="DebugAdapterStdioProcess"/>: below normal priority, tracked,
/// with the environment marker the debuggee inherits. Alongside CodeLLDB (type <c>lldb</c>, which CsCode.app ships), never instead of it.
/// <para>Lookup (<see cref="LocateAsync"/>): setting <c>cscode.debug.lldbDapPath</c> → on macOS <c>xcrun -f lldb-dap</c> (only when
/// <c>xcode-select -p</c> names a developer directory, so a Mac without the tools never gets their install dialog) → <c>lldb-dap</c>, then
/// <c>lldb-vscode</c> on PATH → a versioned <c>lldb-dap-NN</c> on PATH (Debian and Ubuntu name it so) → Homebrew's LLVM
/// (<c>/opt/homebrew/opt/llvm/bin</c>, <c>/usr/local/opt/llvm/bin</c>) on macOS, <c>/usr/lib/llvm-NN/bin</c> (newest first) on Linux.</para>
/// <para>The provider fills in what LLVM's extension would and the user did not write: CodeLLDB's <c>cargo</c> key (<see cref="CargoArtifacts"/>,
/// shared with GDB and CodeLLDB), <c>cwd</c>, <c>env</c> as the <c>"NAME=value"</c> list every lldb-dap version reads, <c>console:
/// integratedTerminal</c> as <c>runInTerminal</c> when there is a terminal, the Rust toolchain's LLDB formatters (<c>initCommands</c>, as
/// <c>rust-lldb</c> loads them; not into Apple's lldb-dap, where they hang the session: see <see cref="IsAppleLldbDap"/>) and the standard library's sources (<c>sourceMap</c> onto rust-src), and a textual <c>pid</c> as a number.</para>
/// </summary>
public sealed class LldbDapAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
{
    public const string SettingPath = "cscode.debug.lldbDapPath";
    public const string PrimaryType = "lldb-dap";
    public static readonly IReadOnlyList<string> Types = [PrimaryType];
    public static readonly IReadOnlyList<string> Languages = ["rust", "c", "cpp"];
    /// <summary>Its names, newest first (LLVM 18 renamed lldb-vscode).</summary>
    public static readonly IReadOnlyList<string> ExecutableNames = OperatingSystem.IsWindows() ? ["lldb-dap.exe", "lldb-vscode.exe"] : ["lldb-dap", "lldb-vscode"];

    private readonly DapPlatform _p;
    private readonly ILogger _logger;
    private readonly RustToolchainLocator _rust;
    private readonly SemaphoreSlim _xcrunGate = new(1, 1);
    private string? _xcrunFound;
    private bool _xcrunAsked;

    public LldbDapAdapterFactory(DapPlatform platform)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.LldbDap");
        _rust = new RustToolchainLocator(name => Environment(name), _logger);
    }

    /// <summary>Environment seam (tests must not mutate process-wide variables while other tests spawn children).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Whether Apple's tools are asked (<c>xcrun</c>); macOS only by default.</summary>
    internal bool AskXcrun { get; init; } = OperatingSystem.IsMacOS();

    /// <summary>Asks Apple's tools for lldb-dap (a seam for tests); default <see cref="XcrunFindAsync"/>.</summary>
    internal Func<CancellationToken, Task<string?>> Xcrun { get; init; } = XcrunFindAsync;

    /// <summary>Directories looked in after PATH: Homebrew's LLVM on macOS; on Linux the parent of the versioned <c>llvm-NN</c> directories.</summary>
    internal IReadOnlyList<string> WellKnownDirectories { get; init; } = OperatingSystem.IsMacOS()
        ? ["/opt/homebrew/opt/llvm/bin", "/usr/local/opt/llvm/bin"]
        : [];

    /// <summary>Where Debian/Ubuntu install LLVM versions (<c>llvm-NN/bin</c> below it); Linux only by default.</summary>
    internal string? VersionedLlvmRoot { get; init; } = OperatingSystem.IsLinux() ? "/usr/lib" : null;

    /// <summary>The built-in adapter: <c>lldb-dap</c>, without quirks (LLVM's adapter is the protocol's reference implementation for LLDB).</summary>
    public static IEnumerable<BuiltinDebugAdapter> Builtins(LldbDapAdapterFactory factory) =>
    [
        new(PrimaryType, "Rust / C / C++ (lldb-dap)", Languages, factory, factory),
    ];

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        var adapter = await LocateAsync(ct) ?? throw new DebugSessionStartException(NotFoundMessage(OperatingSystem.IsMacOS()));
        var folder = configuration.WorkspaceFolder ?? _p.PrimaryFolder;
        var cwd = folder is { IsFile: true } f && Directory.Exists(f.LocalPath) ? DapPlatform.NormalizeDir(f.LocalPath) : null;
        return new DebugAdapterStdioProcess(adapter, [], null, cwd) { DisplayName = "lldb-dap" };
    }

    /// <summary>Why a session cannot start without lldb-dap, and where to get it on this OS.</summary>
    internal static string NotFoundMessage(bool macOS) => macOS
        ? "lldb-dap was not found. It comes with Xcode and its Command Line Tools (xcode-select --install) and with Homebrew's LLVM (brew install llvm); or set 'cscode.debug.lldbDapPath'. CodeLLDB (type 'lldb'), which CsCode.app ships, debugs Rust, C and C++ too."
        : "lldb-dap was not found. Install LLDB from your distribution (it provides lldb-dap, or lldb-vscode before LLVM 18; Debian and Ubuntu name it lldb-dap-NN) or from LLVM, put it on PATH, or set 'cscode.debug.lldbDapPath'. GDB (type 'gdb') and CodeLLDB (type 'lldb') debug Rust, C and C++ too.";

    /// <summary>The lldb-dap executable (see the class remarks for the order); null when none is found. Runs <c>xcrun</c> at most once.</summary>
    public async Task<string?> LocateAsync(CancellationToken ct)
    {
        if (_p.Configuration?.Get<string>(SettingPath) is { Length: > 0 } configured && Resolve(configured) is { } fromSetting) return fromSetting;
        if (AskXcrun && await XcrunOnceAsync(ct) is { } fromXcode) return fromXcode;
        foreach (var name in ExecutableNames)
            if (RustToolchainLocator.FindOnPath(Environment, name) is { } onPath) return onPath;
        if (VersionedOnPath() is { } versioned) return versioned;
        foreach (var dir in WellKnownDirectories)
            foreach (var name in ExecutableNames)
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return VersionedLlvm();
    }

    /// <summary>Accepts the executable itself or a directory holding it (an LLVM <c>bin</c> directory).</summary>
    internal static string? Resolve(string path)
    {
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
            foreach (var name in ExecutableNames)
                if (File.Exists(Path.Combine(path, name))) return Path.GetFullPath(Path.Combine(path, name));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    private async Task<string?> XcrunOnceAsync(CancellationToken ct)
    {
        await _xcrunGate.WaitAsync(ct);
        try
        {
            if (!_xcrunAsked || (_xcrunFound is not null && !File.Exists(_xcrunFound)))
            {
                _xcrunFound = await Xcrun(ct);
                _xcrunAsked = true; // a Mac without the tools is asked once per run, not at every start
            }
            return _xcrunFound is { } found && File.Exists(found) ? found : null;
        }
        finally { _xcrunGate.Release(); }
    }

    /// <summary>
    /// <c>xcrun --find lldb-dap</c>, only after <c>xcode-select -p</c> named a developer directory: on a Mac without Xcode or its Command Line
    /// Tools, /usr/bin/xcrun would open the "install the command line developer tools" dialog. Both are bounded (5 s each); null on any failure.
    /// </summary>
    internal static async Task<string?> XcrunFindAsync(CancellationToken ct)
    {
        if (await RunAsync("/usr/bin/xcode-select", ["-p"], ct) is not { Length: > 0 } developerDir || !Directory.Exists(developerDir)) return null;
        return await RunAsync("/usr/bin/xcrun", ["--find", "lldb-dap"], ct) is { Length: > 0 } found && File.Exists(found) ? found : null;
    }

    private static async Task<string?> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The newest <c>lldb-dap-NN</c> (or <c>lldb-vscode-NN</c>) on PATH.</summary>
    private string? VersionedOnPath()
    {
        var best = (Version: -1, Path: (string?)null);
        foreach (var dir in (Environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "lldb-*-*") : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (var file in files)
                if (VersionOf(Path.GetFileName(file)) is { } v && v > best.Version) best = (v, file);
        }
        return best.Path;
    }

    /// <summary>NN of <c>lldb-dap-NN</c> or <c>lldb-vscode-NN</c>; null for any other name.</summary>
    internal static int? VersionOf(string fileName)
    {
        foreach (var name in new[] { "lldb-dap-", "lldb-vscode-" })
            if (fileName.StartsWith(name, StringComparison.Ordinal)
                && int.TryParse(fileName.AsSpan(name.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var version)) return version;
        return null;
    }

    /// <summary>Apple's lldb-dap (Xcode or its Command Line Tools), which runs Apple's LLDB.</summary>
    internal static bool IsAppleLldbDap(string? adapter) =>
        adapter is not null && (adapter.StartsWith("/Library/Developer/", StringComparison.Ordinal) || adapter.Contains(".app/Contents/Developer/", StringComparison.Ordinal));

    /// <summary>lldb-dap (or lldb-vscode) in the newest <c>{VersionedLlvmRoot}/llvm-NN/bin</c>.</summary>
    private string? VersionedLlvm()
    {
        if (VersionedLlvmRoot is not { } root || !Directory.Exists(root)) return null;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root, "llvm-*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var ordered = dirs
            .Select(d => (Dir: d, Version: int.TryParse(Path.GetFileName(d).AsSpan("llvm-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1))
            .Where(d => d.Version >= 0).OrderByDescending(d => d.Version);
        foreach (var (dir, _) in ordered)
            foreach (var name in ExecutableNames)
                if (File.Exists(Path.Combine(dir, "bin", name))) return Path.Combine(dir, "bin", name);
        return null;
    }

    // ------------------------------------------------------------------ configuration provider

    /// <summary>Fills in what LLVM's extension would and the user did not write; never overrides the user's values. See the class remarks.</summary>
    public async ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return configuration;
        var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["type"] = JsonSerializer.SerializeToElement(PrimaryType) };
        var folderPath = folder is { IsFile: true } f ? DapPlatform.NormalizeDir(f.LocalPath) : null;
        var rust = props.ContainsKey("cargo") || SourceLanguages(props).Contains("rust") || (folderPath is not null && File.Exists(Path.Combine(folderPath, "Cargo.toml")));

        if (!configuration.IsAttach)
        {
            // CodeLLDB's "cargo" key ({"args": […], "filter": …} or just the argument list): cargo builds, its executable becomes "program".
            if (props.TryGetValue("cargo", out var cargo) && !props.ContainsKey("program"))
            {
                var spec = cargo.ValueKind == JsonValueKind.Array ? JsonSerializer.SerializeToElement(new { args = cargo }) : cargo;
                if (spec.ValueKind != JsonValueKind.Object)
                {
                    await ReportAsync($"The \"cargo\" setting of '{configuration.Name}' must be an object or a list of cargo arguments.");
                    return null;
                }
                var program = await CargoArtifacts.ResolveProgramAsync(_p, folder, spec, ct);
                if (program is null) return null; // cargo failed or the user cancelled the choice: already reported
                props["program"] = JsonSerializer.SerializeToElement(program);
            }
            props.Remove("cargo"); // lldb-dap does not know it
            if (!props.ContainsKey("cwd") && folderPath is not null) props["cwd"] = JsonSerializer.SerializeToElement(folderPath);
            // Every lldb-dap reads "env" as a list of "NAME=value" (LLVM 20 also takes an object); VS Code's "NAME": null (remove) has no
            // equivalent and is dropped.
            if (props.TryGetValue("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                var list = new JsonArray();
                foreach (var p in env.EnumerateObject())
                    if (p.Value.ValueKind != JsonValueKind.Null)
                        list.Add($"{p.Name}={(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())}");
                props["env"] = JsonSerializer.SerializeToElement(list);
            }
            // VS Code's "console": the integrated terminal through runInTerminal when there is one; otherwise the output stays in the Debug
            // Console (a newer lldb-dap reads "console" itself and would ask for a terminal the client cannot give).
            if (props.TryGetValue("console", out var console) && console.ValueKind == JsonValueKind.String)
            {
                var inTerminal = console.GetString() is "integratedTerminal" or "externalTerminal" && _p.Terminals?.Invoke() is not null;
                props.Remove("console");
                if (inTerminal && !props.ContainsKey("runInTerminal")) props["runInTerminal"] = JsonSerializer.SerializeToElement(true);
            }
        }
        else
        {
            // `${command:pickProcess}` substitutes TEXT; lldb-dap's attach wants "pid" as a number (VS Code's cppdbg calls it "processId").
            if (!props.ContainsKey("pid") && props.Remove("processId", out var processId)) props["pid"] = processId;
            if (props.TryGetValue("pid", out var pid) && pid.ValueKind == JsonValueKind.String)
            {
                if (!int.TryParse(pid.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    await ReportAsync($"'{pid.GetString()}' is not a process id ('{configuration.Name}').");
                    return null;
                }
                props["pid"] = JsonSerializer.SerializeToElement(number);
            }
        }

        // A source map written as an object (CodeLLDB's form) becomes the list of [from, to] pairs every lldb-dap reads.
        var map = props.TryGetValue("sourceMap", out var sm) ? SourceMapPairs(sm) : new JsonArray();
        if (rust && await _rust.FindAsync(folderPath, ct) is { } toolchain)
        {
            // What rust-lldb does: the toolchain's formatters (String, Vec, HashMap, enums… by value), before the user's own init commands.
            // Not into Apple's LLDB (lang-2, measured with rust 1.96.1 and lldb-2103): its provider for a Result<i32, String> holding Ok(12) reads
            // the String at 0xc, fails, and LLDB then never answers: the Locals of any frame with such a value hang the whole session. Rust
            // values show as their raw structs there instead (a launch.json's own initCommands can still load them).
            var etc = Path.Combine(toolchain.Sysroot, "lib", "rustlib", "etc");
            var init = new JsonArray();
            var apple = IsAppleLldbDap(await LocateAsync(ct));
            if (!apple && File.Exists(Path.Combine(etc, "lldb_lookup.py"))) init.Add($"command script import \"{Path.Combine(etc, "lldb_lookup.py")}\"");
            if (!apple && File.Exists(Path.Combine(etc, "lldb_commands"))) init.Add($"command source -s 0 \"{Path.Combine(etc, "lldb_commands")}\"");
            if (init.Count > 0)
            {
                if (props.TryGetValue("initCommands", out var mine) && mine.ValueKind == JsonValueKind.Array)
                    foreach (var c in mine.EnumerateArray()) init.Add(JsonNode.Parse(c.GetRawText()));
                props["initCommands"] = JsonSerializer.SerializeToElement(init);
            }
            // The standard library's frames name /rustc/<commit>/…: mapped onto the rust-src component when it is installed.
            if (toolchain.DebugInfoPrefix is { } prefix && Directory.Exists(toolchain.SourceRoot) && !map.Any(pair => pair?[0]?.GetValue<string>() == prefix))
                map.Add(new JsonArray(prefix, toolchain.SourceRoot));
        }
        if (map.Count > 0) props["sourceMap"] = JsonSerializer.SerializeToElement(map);
        props.Remove("sourceLanguages"); // CodeLLDB's; lldb-dap does not know it
        return configuration with { Type = PrimaryType, Properties = props };
    }

    private static IReadOnlyList<string> SourceLanguages(IReadOnlyDictionary<string, JsonElement> props) =>
        props.TryGetValue("sourceLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array
            ? [.. langs.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)]
            : [];

    /// <summary>A <c>sourceMap</c> as lldb-dap's list of <c>[from, to]</c> pairs, whether it was written as that list or as an object.</summary>
    internal static JsonArray SourceMapPairs(JsonElement sourceMap)
    {
        var pairs = new JsonArray();
        if (sourceMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in sourceMap.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) pairs.Add(new JsonArray(p.Name, p.Value.GetString()));
        }
        else if (sourceMap.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in sourceMap.EnumerateArray())
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 && pair[0].ValueKind == JsonValueKind.String && pair[1].ValueKind == JsonValueKind.String)
                    pairs.Add(new JsonArray(pair[0].GetString(), pair[1].GetString()));
        }
        return pairs;
    }

    private Task ReportAsync(string message)
    {
        _logger.LogWarning("{Message}", message);
        return _p.Window is { } w ? w.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None).AsTask() : Task.CompletedTask;
    }

    /// <summary>
    /// What "Add Configuration…" offers (and a new launch.json for <c>lldb-dap</c> holds): for a cargo package the binary built by the
    /// <c>cargo: build</c> pre-launch task, the unit tests through <c>cargo test --no-run</c> (the <c>cargo</c> key) and attach; otherwise a
    /// C/C++ program launch and attach.
    /// </summary>
    public async ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct)
    {
        var package = await CargoPackage.ReadAsync(_p, folder, ct);
        var configs = new List<JsonObject>();
        if (package is not null)
        {
            var bin = package.Binaries.FirstOrDefault() ?? package.Name;
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Debug {bin} (lldb-dap, cargo build)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = $"${{workspaceFolder}}/target/debug/{bin}",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["preLaunchTask"] = "cargo: build",
            });
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Debug unit tests of {package.Name} (lldb-dap, cargo test)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["cargo"] = new JsonObject
                {
                    ["args"] = new JsonArray("test", "--no-run"),
                    ["filter"] = package.HasLibrary ? new JsonObject { ["kind"] = "lib" } : new JsonObject { ["name"] = bin, ["kind"] = "bin" },
                },
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
            });
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Attach to {bin} (lldb-dap)",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["program"] = $"${{workspaceFolder}}/target/debug/{bin}",
                ["pid"] = "${command:pickProcess}",
            });
        }
        else
        {
            configs.Add(new JsonObject
            {
                ["name"] = "lldb-dap: Launch program",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = "${workspaceFolder}/a.out",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
            });
            configs.Add(new JsonObject
            {
                ["name"] = "lldb-dap: Attach to process",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["pid"] = "${command:pickProcess}",
            });
        }
        return [.. configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder))];
    }

    /// <summary>The <c>cargo: build</c> task the binary template's preLaunchTask refers to (only for a cargo package), as the GDB adapter writes it.</summary>
    public async ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct)
    {
        if (await CargoPackage.ReadAsync(_p, folder, ct) is null) return [];
        return [("tasks.json", CargoPackage.BuildTasksJson)];
    }
}
