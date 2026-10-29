using System.Diagnostics;
using System.Globalization;
using System.Text;
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
/// Rust (and C/C++) debugging through GDB's NATIVE DAP interpreter — <c>gdb -i=dap</c>, GDB 14 or newer, nothing to download: a stdio adapter
/// started below normal priority (<c>nice -n 10</c>; the debuggee GDB launches inherits it), tracked, with an environment marker
/// (<see cref="DebugAdapterStdioProcess"/>). Types <c>gdb</c> and its alias <c>rust-gdb</c>; breakpoint languages rust, c, cpp.
/// <para>For Rust, what <c>rust-gdb</c> does is done on GDB's command line (<c>-iex</c>, before anything loads): the Rust pretty-printers
/// (<c>$(rustc --print sysroot)/lib/rustlib/etc/gdb_load_rust_pretty_printers.py</c>, when present: String, Vec, HashMap, Rc… shown by value),
/// and <c>set substitute-path /rustc/&lt;commit&gt; &lt;sysroot&gt;/lib/rustlib/src/rust</c> when the rust-src component is installed (frames
/// of the standard library then have their sources). CsCode's own <c>cscode_gdb.py</c> adds one-line summaries of Rust structs and enums
/// (<c>Point {x: 3, y: 4}</c>, <c>Some(5)</c>), which GDB's DAP shows as empty values.</para>
/// <para>Lookup: setting <c>cscode.debug.gdbPath</c> → <c>gdb</c> on PATH; extra arguments from <c>cscode.debug.gdbArgs</c> (e.g. <c>-nx</c>
/// to skip the user's gdbinit files). The provider moves <c>env</c> to <c>set environment</c> commands (GDB's own <c>env</c> REPLACES the whole
/// environment, VS Code users expect it to add to it), turns a textual <c>pid</c>/<c>processId</c> into the number GDB wants, and resolves
/// CodeLLDB's <c>"cargo": {"args": […]}</c> key: cargo builds, and the executable it reports becomes <c>program</c> (how a test binary,
/// <c>target/debug/deps/&lt;crate&gt;-&lt;hash&gt;</c>, is found). What GDB does differently is compensated by <see cref="GdbAdapterQuirks"/>.</para>
/// </summary>
public sealed class GdbAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
{
    public const string SettingPath = "cscode.debug.gdbPath";
    public const string ArgsSettingPath = "cscode.debug.gdbArgs";
    public const string PrimaryType = "gdb";
    public const string RustAlias = "rust-gdb";
    public static readonly IReadOnlyList<string> Types = [PrimaryType, RustAlias];
    public static readonly IReadOnlyList<string> Languages = ["rust", "c", "cpp"];
    /// <summary>The launch property the provider moves <c>env</c> to (GDB ignores unknown launch properties).</summary>
    internal const string EnvironmentKey = "__cscodeEnvironment";
    /// <summary>CsCode's GDB helper (Rust value summaries), copied next to the assembly.</summary>
    public const string HelperScript = "cscode_gdb.py";

    private readonly DapPlatform _p;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _rustGate = new(1, 1);
    private readonly Dictionary<string, RustToolchain?> _rustByFolder = new(StringComparer.Ordinal);

    public GdbAdapterFactory(DapPlatform platform)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.Gdb");
    }

    /// <summary>Environment seam (tests must not mutate process-wide variables while other tests spawn children).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Where <see cref="HelperScript"/> is looked for (the directory of this assembly; tests may point elsewhere).</summary>
    internal string HelperDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "Adapters", "Gdb");

    public static string ExecutableName => OperatingSystem.IsWindows() ? "gdb.exe" : "gdb";

    /// <summary>The built-in adapters: <c>gdb</c> and its Rust alias, sharing one factory, one provider and the GDB quirks.</summary>
    public static IEnumerable<BuiltinDebugAdapter> Builtins(GdbAdapterFactory factory) =>
    [
        new(PrimaryType, "Rust / C / C++ (GDB)", Languages, factory, factory) { Quirks = GdbAdapterQuirks.Instance },
        new(RustAlias, "Rust (GDB, legacy 'rust-gdb' type)", Languages, factory, factory) { Quirks = GdbAdapterQuirks.Instance },
    ];

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        var gdb = Locate() ?? throw new DebugSessionStartException(
            "GDB was not found. Install it (GDB 14 or newer speaks the Debug Adapter Protocol: 'gdb -i=dap'), put 'gdb' on PATH, or set 'cscode.debug.gdbPath'.");
        var folder = configuration.WorkspaceFolder ?? _p.PrimaryFolder;
        var cwd = folder is { IsFile: true } f && Directory.Exists(f.LocalPath) ? f.LocalPath : null;

        var args = new List<string>(UserArguments()) { "-q" };
        void Early(string command) { args.Add("-iex"); args.Add(command); }
        if (await RustToolchainAsync(cwd, ct) is { } rust)
        {
            if (File.Exists(rust.PrettyPrinters)) Early("source " + rust.PrettyPrinters);
            if (rust.Commit is { Length: > 0 } commit && Directory.Exists(rust.SourceRoot)) Early($"set substitute-path /rustc/{commit} {rust.SourceRoot}");
        }
        if (Path.Combine(HelperDirectory, HelperScript) is var helper && File.Exists(helper)) Early("source " + helper);
        foreach (var command in EnvironmentCommands(configuration)) Early(command);
        args.Add("-i=dap");
        return new DebugAdapterStdioProcess(gdb, args, null, cwd) { DisplayName = "gdb" };
    }

    /// <summary>The GDB executable without side effects; null when none is found.</summary>
    public string? Locate()
    {
        if (_p.Configuration?.Get<string>(SettingPath) is { Length: > 0 } configured && File.Exists(configured)) return configured;
        return FindOnPath(ExecutableName);
    }

    private string? FindOnPath(string name)
    {
        foreach (var dir in (Environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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

    private IEnumerable<string> UserArguments()
    {
        if (_p.Configuration?.GetRaw(ArgsSettingPath) is not { ValueKind: JsonValueKind.Array } array) return [];
        return [.. array.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).Where(a => a.Length > 0)];
    }

    /// <summary>
    /// <c>set environment</c> / <c>unset environment</c> commands for the launch's <c>env</c> (moved aside by <see cref="ResolveAsync"/>): the
    /// debuggee gets GDB's environment — CsCode's, plus the adapter marker — with these changes, as VS Code's <c>env</c> means it.
    /// </summary>
    internal static IEnumerable<string> EnvironmentCommands(DebugConfiguration configuration)
    {
        if (!configuration.Properties.TryGetValue(EnvironmentKey, out var env) || env.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in env.EnumerateObject())
        {
            if (!IsVariableName(p.Name)) continue;
            if (p.Value.ValueKind == JsonValueKind.Null) { yield return "unset environment " + p.Name; continue; }
            var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            if (value.Contains('\n') || value.Contains('\r')) continue; // one GDB command per line: such a value cannot be passed (reported by the provider)
            yield return $"set environment {p.Name}={value}";
        }
    }

    private static bool IsVariableName(string name) => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(name[0]);

    // ------------------------------------------------------------------ Rust toolchain (pretty-printers, std sources)

    /// <param name="Sysroot"><c>rustc --print sysroot</c>.</param>
    /// <param name="Commit">rustc's commit hash (<c>rustc -vV</c>): the standard library's DWARF paths are <c>/rustc/&lt;commit&gt;/library/…</c>.</param>
    internal sealed record RustToolchain(string Sysroot, string? Commit)
    {
        public string PrettyPrinters => Path.Combine(Sysroot, "lib", "rustlib", "etc", "gdb_load_rust_pretty_printers.py");
        public string SourceRoot => Path.Combine(Sysroot, "lib", "rustlib", "src", "rust");
    }

    /// <summary>The Rust toolchain of <paramref name="folder"/> (rustup honours a rust-toolchain file there), cached per folder; null without rustc.</summary>
    internal async Task<RustToolchain?> RustToolchainAsync(string? folder, CancellationToken ct)
    {
        var key = folder ?? "";
        await _rustGate.WaitAsync(ct);
        try
        {
            if (_rustByFolder.TryGetValue(key, out var known)) return known;
            var rustc = FindOnPath(OperatingSystem.IsWindows() ? "rustc.exe" : "rustc");
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
            _rustByFolder[key] = found;
            return found;
        }
        finally { _rustGate.Release(); }
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
            _logger.LogDebug(ex, "{Tool} failed", exe);
            return null;
        }
    }

    // ------------------------------------------------------------------ configuration provider

    /// <summary>Fills in what GDB needs and the user did not write; never overrides the user's values. See the class remarks.</summary>
    public async ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return configuration;
        var props = new Dictionary<string, JsonElement>(configuration.Properties);
        var type = PrimaryType;
        props["type"] = JsonSerializer.SerializeToElement(type);

        if (!configuration.IsAttach)
        {
            if (props.TryGetValue("cargo", out var cargo) && cargo.ValueKind == JsonValueKind.Object && !props.ContainsKey("program"))
            {
                var program = await CargoArtifacts.ResolveProgramAsync(_p, folder, cargo, ct);
                if (program is null) return null; // cargo failed or the user cancelled the choice: already reported
                props["program"] = JsonSerializer.SerializeToElement(program);
            }
            if (!props.ContainsKey("cwd") && folder is { IsFile: true } f) props["cwd"] = JsonSerializer.SerializeToElement(DapPlatform.NormalizeDir(f.LocalPath));
            if (props.Remove("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                if (env.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String && (p.Value.GetString() ?? "").IndexOfAny(['\n', '\r']) >= 0) is { Name.Length: > 0 } bad)
                {
                    await ReportAsync($"The environment variable '{bad.Name}' of '{configuration.Name}' contains a line break, which GDB cannot pass to the program.");
                    return null;
                }
                props[EnvironmentKey] = env;
            }
        }
        else
        {
            // `${command:pickProcess}` substitutes TEXT; GDB's attach wants "pid" as a number (VS Code's cppdbg calls it "processId").
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
        return configuration with { Type = type, Properties = props };
    }

    private Task ReportAsync(string message)
    {
        _logger.LogWarning("{Message}", message);
        return _p.Window is { } w ? w.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None).AsTask() : Task.CompletedTask;
    }

    /// <summary>
    /// Templates for a cargo package (<c>Cargo.toml</c>): the binary built by the <c>cargo: build</c> pre-launch task, the unit tests through
    /// <c>cargo test --no-run</c> (the <c>cargo</c> key), and attach to a running process; without a Cargo.toml, a plain program launch.
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
                ["name"] = $"Rust: Debug {bin} (cargo build)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = $"${{workspaceFolder}}/target/debug/{bin}",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["preLaunchTask"] = "cargo: build",
            });
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Debug unit tests of {package.Name} (cargo test)",
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
                ["name"] = $"Rust: Attach to {bin}",
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
                ["name"] = "GDB: Launch program",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = "${workspaceFolder}/a.out",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
            });
            configs.Add(new JsonObject
            {
                ["name"] = "GDB: Attach to process",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["pid"] = "${command:pickProcess}",
            });
        }
        return [.. configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder))];
    }

    /// <summary>
    /// The <c>cargo: build</c> task the binary template's preLaunchTask refers to (only for a cargo package): a PROCESS task, so the folder's
    /// path is never re-read by a shell, run in the package folder.
    /// </summary>
    public async ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct)
    {
        if (await CargoPackage.ReadAsync(_p, folder, ct) is null) return [];
        const string tasks = """
            {
                "version": "2.0.0",
                "tasks": [
                    {
                        "label": "cargo: build",
                        "type": "process",
                        "command": "cargo",
                        "args": ["build"],
                        "options": { "cwd": "${workspaceFolder}" },
                        "problemMatcher": "$rustc",
                        "group": { "kind": "build", "isDefault": true }
                    }
                ]
            }

            """;
        return [("tasks.json", tasks)];
    }
}

/// <summary>What the templates need from a folder's <c>Cargo.toml</c>: the package name, its binaries and whether it has a library.</summary>
internal sealed record CargoPackage(string Name, IReadOnlyList<string> Binaries, bool HasLibrary)
{
    /// <summary>
    /// A small reader for the keys the templates need (<c>[package] name</c>, <c>[[bin]] name</c>, <c>[lib]</c>, <c>src/main.rs</c>,
    /// <c>src/lib.rs</c>, <c>src/bin/*.rs</c>); null without a Cargo.toml with a package.
    /// </summary>
    public static async Task<CargoPackage?> ReadAsync(DapPlatform p, Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || p.Workspace is not { } ws) return null;
        var root = folder.ToString().TrimEnd('/');
        async Task<bool> ExistsAsync(string relative)
        {
            try { return await ws.FileSystem.StatAsync(new Uri(root + "/" + relative), ct) is { Type: FileType.File }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) { return false; }
        }
        string toml;
        try
        {
            var uri = new Uri(root + "/Cargo.toml");
            if (await ws.FileSystem.StatAsync(uri, ct) is not { Type: FileType.File }) return null;
            toml = Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) { return null; }

        string? name = null;
        var bins = new List<string>();
        var hasLib = false;
        var section = "";
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', ' ');
                if (section == "lib") hasLib = true;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || line[..eq].Trim() != "name") continue;
            var value = line[(eq + 1)..].Trim();
            var hash = value.IndexOf('#');
            if (hash > 0 && value.LastIndexOf('"') < hash) value = value[..hash].Trim();
            value = value.Trim('"', '\'');
            if (section == "package") name = value;
            else if (section == "bin") bins.Add(value);
        }
        if (name is null) return null;
        if (bins.Count == 0 && await ExistsAsync("src/main.rs")) bins.Add(name);
        if (!hasLib) hasLib = await ExistsAsync("src/lib.rs");
        return new CargoPackage(name, bins, hasLib);
    }
}
