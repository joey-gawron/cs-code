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
/// Rust (and C/C++) debugging through <b>CodeLLDB</b> (MIT, github.com/vadimcn/codelldb; VS Code's usual Rust debugger), type <c>lldb</c> — the
/// type CodeLLDB's own launch.json files use. Its standalone adapter <c>extension/adapter/codelldb</c> speaks DAP on stdio when started without
/// <c>--port</c>/<c>--connect</c> (one session per process; it exits when the session ends) and drives the LLDB it bundles (<c>liblldb</c>,
/// <c>lldb-server</c>). It is a <see cref="DebugAdapterStdioProcess"/>: below normal priority (<c>nice -n 10</c>, which <c>lldb-server</c> and the
/// debuggee inherit), tracked, with the environment marker the debuggee inherits; <c>PYTHONHOME</c>/<c>PYTHONPATH</c> are removed from its
/// environment (it embeds its own Python 3.12, as VS Code's extension does).
/// <para>Lookup: setting <c>cscode.debug.codelldbPath</c> → environment variable <c>CSCODE_CODELLDB</c> → the installed copy
/// <c>{ToolsDir}/codelldb/&lt;version&gt;</c> → the copy installed with the app (<see cref="BundledAdapters"/>) → <c>codelldb</c> on PATH; a path may
/// name the adapter itself, CodeLLDB's <c>extension/</c> directory or the install directory. When none exists the user is OFFERED the pinned
/// download (<see cref="CodeLldbInstaller"/>; never silent).</para>
/// <para>Started with <c>--settings {"sourceLanguages": […]}</c> (the configuration's <c>sourceLanguages</c>, else Rust): CodeLLDB then loads the
/// Rust toolchain's LLDB formatters (<c>rustc --print sysroot</c>, run in the folder) and offers its "Rust: on panic" filter. The provider fills in
/// what VS Code's CodeLLDB extension would: the <c>cargo</c> key (CodeLLDB's extension resolves it, not the adapter), <c>relativePathBase</c>,
/// <c>sourceLanguages</c>, the Debug Console mode, the standard library's sources (<c>sourceMap</c> onto rust-src) and the program's output in
/// the Debug Console (<c>terminal: "console"</c>) unless an integrated terminal was asked for. What CodeLLDB does differently is compensated by
/// <see cref="CodeLldbAdapterQuirks"/>.</para>
/// <para>Zig (<see cref="ZigDebugging"/>): a <c>build.zig</c> project gets Zig templates (the <c>zig build</c> executables, the current file's test
/// binary, attach) and its configurations load Zig's LLDB pretty printers (<c>initCommands</c>, before the user's); <c>sourceLanguages</c>
/// defaults to zig there, so the Rust formatters are not looked for.</para>
/// </summary>
public sealed class CodeLldbAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
{
    public const string SettingPath = "cscode.debug.codelldbPath";
    public const string EnvironmentVariable = "CSCODE_CODELLDB";
    /// <summary>VS Code's CodeLLDB setting for the Debug Console mode, honoured here; CsCode's default is "evaluate" (the accepted Rust default).</summary>
    public const string ConsoleModeSetting = "lldb.consoleMode";
    public const string DefaultConsoleMode = "evaluate";
    public const string PrimaryType = "lldb";
    public static readonly IReadOnlyList<string> Types = [PrimaryType];
    public static readonly IReadOnlyList<string> Languages = ["rust", "c", "cpp", "zig"];
    /// <summary>The launch property VS Code's CodeLLDB extension passes the adapter settings in.</summary>
    internal const string AdapterSettingsKey = "_adapterSettings";
    /// <summary>Removed from the adapter's environment (CodeLLDB embeds its own Python; VS Code's extension removes the same).</summary>
    public static readonly IReadOnlyList<string> RemovedEnvironment = ["PYTHONHOME", "PYTHONPATH", "CODELLDB_STARTUP"];

    private readonly DapPlatform _p;
    private readonly CodeLldbInstaller _installer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly RustToolchainLocator _rust;
    private readonly ZigToolchainLocator _zig;

    public CodeLldbAdapterFactory(DapPlatform platform, CodeLldbInstaller? installer = null)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.CodeLldb");
        _installer = installer ?? new CodeLldbInstaller(logger: _logger);
        _rust = new RustToolchainLocator(name => Environment(name), _logger);
        _zig = new ZigToolchainLocator(name => Environment(name), key => _p.Configuration?.Get<string>(key), _logger);
    }

    /// <summary>Environment seam (tests must not mutate process-wide variables while other tests spawn children).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Where <see cref="ZigDebugging.HelperScript"/> is looked for (the directory of this assembly; tests may point elsewhere).</summary>
    internal string ZigHelperDirectory { get; init; } = ZigDebugging.DefaultHelperDirectory;

    /// <summary>The built-in adapter: <c>lldb</c> with the CodeLLDB quirks.</summary>
    public static IEnumerable<BuiltinDebugAdapter> Builtins(CodeLldbAdapterFactory factory) =>
    [
        new(PrimaryType, "Rust / C / C++ / Zig (CodeLLDB)", Languages, factory, factory) { Quirks = CodeLldbAdapterQuirks.Instance },
    ];

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        var adapter = Locate() ?? await OfferInstallAsync(ct) ?? throw new DebugSessionStartException(NotFoundMessage(OperatingSystem.IsMacOS()));
        var folder = configuration.WorkspaceFolder ?? _p.PrimaryFolder;
        // rustc (for the Rust formatters) runs in the adapter's directory: the folder's, so rustup honours a rust-toolchain file there.
        var cwd = folder is { IsFile: true } f && Directory.Exists(f.LocalPath) ? DapPlatform.NormalizeDir(f.LocalPath) : null;
        var settings = new JsonObject
        {
            ["sourceLanguages"] = new JsonArray([.. SourceLanguages(configuration).Select(l => (JsonNode)l)]),
            ["evaluateForHovers"] = true,
            ["commandCompletions"] = true,
        };
        return new DebugAdapterStdioProcess(adapter, ["--settings", settings.ToJsonString()], null, cwd)
        {
            DisplayName = "codelldb",
            RemoveEnv = RemovedEnvironment,
        };
    }

    /// <summary>Why a session cannot start without CodeLLDB, and what to do. On macOS it is the native debugger (GDB cannot debug arm64 macOS
    /// programs) and CsCode.app ships it, so the advice names the bundle instead of GDB.</summary>
    internal static string NotFoundMessage(bool macOS) => macOS
        ? "CodeLLDB was not found. On macOS it is the debugger for Rust, C and C++ (GDB cannot debug native arm64 macOS programs) and CsCode.app ships it in Contents/Resources/debug-adapters (tools/package-macos.sh); accept the download offer, install it with tools/fetch-codelldb.sh, or set 'cscode.debug.codelldbPath' or CSCODE_CODELLDB to its adapter (extension/adapter/codelldb)."
        : "CodeLLDB was not found. Install it with tools/fetch-codelldb.sh, set 'cscode.debug.codelldbPath' or CSCODE_CODELLDB to its adapter (extension/adapter/codelldb), or accept the download offer. GDB (type 'gdb') debugs Rust without a download.";

    /// <summary>The configuration's <c>sourceLanguages</c>, else Rust (what this adapter is built in for; a Zig configuration has them set by the provider).</summary>
    internal static IReadOnlyList<string> SourceLanguages(DebugConfiguration configuration) =>
        configuration.Properties.TryGetValue("sourceLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array
            && langs.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList() is { Count: > 0 } list
            ? list : ["rust"];

    /// <summary>The CodeLLDB adapter without side effects; null when it is not installed.</summary>
    public string? Locate()
    {
        if (_p.Configuration?.Get<string>(SettingPath) is { Length: > 0 } configured && ResolveAdapter(configured) is { } fromSetting) return fromSetting;
        if (Environment(EnvironmentVariable) is { Length: > 0 } env && ResolveAdapter(env) is { } fromEnv) return fromEnv;
        if (_p.Paths is { } paths && File.Exists(CodeLldbInstaller.AdapterPath(paths.ToolsDir))) return CodeLldbInstaller.AdapterPath(paths.ToolsDir);
        if (BundledAdapters.CodeLldb(_p.Paths) is { } bundled) return bundled;
        return RustToolchainLocator.FindOnPath(Environment, CodeLldbInstaller.AdapterExecutableName);
    }

    /// <summary>Accepts the adapter itself, CodeLLDB's <c>adapter/</c> or <c>extension/</c> directory, or an install directory containing <c>extension/</c>.</summary>
    public static string? ResolveAdapter(string path)
    {
        try
        {
            if (File.Exists(path)) return Path.GetFullPath(path);
            if (!Directory.Exists(path)) return null;
            var name = CodeLldbInstaller.AdapterExecutableName;
            foreach (var candidate in new[] { Path.Combine(path, name), Path.Combine(path, "adapter", name), Path.Combine(path, "extension", "adapter", name) })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    private async ValueTask<string?> OfferInstallAsync(CancellationToken ct)
    {
        if (_p.Window is not { } window || _p.Paths is not { } paths || _installer.Target is not { } package) return null;
        await _installGate.WaitAsync(ct);
        try
        {
            if (Locate() is { } already) return already; // another start finished the install while we waited
            var choice = await window.ShowMessageAsync(MessageSeverity.Info,
                $"Debugging with CodeLLDB needs CodeLLDB {CodeLldbInstaller.Version} (MIT licence, with its own LLDB: about {package.SizeMegabytes} MB to download, about 170 MB installed), which is not installed. Download it from github.com/vadimcn/codelldb?",
                [new MessageAction("Download"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
            if (choice != "Download") return null;
            return await window.WithProgressAsync("Downloading CodeLLDB", cancellable: true,
                (progress, token) => _installer.InstallAsync(paths.ToolsDir, progress, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnsafeArchiveException or PlatformNotSupportedException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "CodeLLDB download failed");
            await window.ShowMessageAsync(MessageSeverity.Error, $"Could not install CodeLLDB: {ex.Message}", null, CancellationToken.None);
            return null;
        }
        finally { _installGate.Release(); }
    }

    // ------------------------------------------------------------------ configuration provider

    /// <summary>Fills in what VS Code's CodeLLDB extension would and the user did not write; never overrides the user's values.</summary>
    public async ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return configuration;
        var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["type"] = JsonSerializer.SerializeToElement(PrimaryType) };
        var folderPath = folder is { IsFile: true } f ? DapPlatform.NormalizeDir(f.LocalPath) : null;

        if (!configuration.IsAttach)
        {
            // CodeLLDB's "cargo" key ({"args": […], "filter": …} or just the argument list) is resolved by its VS Code extension, never by the
            // adapter: cargo builds, and the executable it reports becomes "program" (CargoArtifacts, shared with the GDB adapter).
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
            if (!props.ContainsKey("cwd") && folderPath is not null) props["cwd"] = JsonSerializer.SerializeToElement(folderPath);
            // The program's output goes to the Debug Console (as GDB's does) unless an integrated terminal was asked for and there is one.
            // CodeLLDB's own default is the integrated terminal through runInTerminal and its terminal agent.
            if (!props.ContainsKey("terminal"))
            {
                var inTerminal = configuration.GetString("console") == "integratedTerminal" && _p.Terminals?.Invoke() is not null;
                if (!inTerminal) props["terminal"] = JsonSerializer.SerializeToElement("console");
            }
            // CodeLLDB's env values are strings only (VS Code's "VAR": null, which removes a variable elsewhere, would fail the launch).
            if (props.TryGetValue("env", out var env) && env.ValueKind == JsonValueKind.Object && env.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
            {
                var kept = new JsonObject();
                foreach (var p in env.EnumerateObject())
                    if (p.Value.ValueKind != JsonValueKind.Null) kept[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                props["env"] = JsonSerializer.SerializeToElement(kept);
            }
        }
        else
        {
            // `${command:pickProcess}` substitutes TEXT; CodeLLDB's attach wants "pid" as a number (VS Code's cppdbg calls it "processId").
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

        if (!props.ContainsKey("relativePathBase") && folderPath is not null) props["relativePathBase"] = JsonSerializer.SerializeToElement(folderPath);
        var zig = ZigDebugging.IsZig(props, folderPath) && !props.ContainsKey("cargo");
        if (!props.ContainsKey("sourceLanguages"))
            props["sourceLanguages"] = JsonSerializer.SerializeToElement(zig ? ["zig"] : SourceLanguages(configuration));
        // Zig's pretty printers (slices, optionals, error unions, tagged unions by value), before the user's own init commands
        if (zig) ZigDebugging.PrependInitCommands(props, ZigDebugging.PrettyPrinterCommands(await _zig.FindAsync(ct), ZigHelperDirectory));

        // The Debug Console evaluates expressions (the accepted Rust default; commands with a backtick, '/cmd ' or '-exec ').
        var adapterSettings = props.TryGetValue(AdapterSettingsKey, out var existing) && existing.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(existing.GetRawText())!.AsObject() : new JsonObject();
        if (!adapterSettings.ContainsKey("consoleMode"))
            adapterSettings["consoleMode"] = _p.Configuration?.Get<string>(ConsoleModeSetting) is { Length: > 0 } mode ? mode : DefaultConsoleMode;
        props[AdapterSettingsKey] = JsonSerializer.SerializeToElement(adapterSettings);

        // The standard library's frames name /rustc/<commit>/library/…: mapped onto the rust-src component when it is installed (what rust-gdb's
        // substitute-path does for GDB), so they have their sources.
        if (await _rust.FindAsync(folderPath, ct) is { DebugInfoPrefix: { } prefix } rust && Directory.Exists(rust.SourceRoot))
        {
            var map = props.TryGetValue("sourceMap", out var sm) && sm.ValueKind == JsonValueKind.Object ? JsonNode.Parse(sm.GetRawText())!.AsObject() : new JsonObject();
            if (!map.ContainsKey(prefix))
            {
                map[prefix] = rust.SourceRoot;
                props["sourceMap"] = JsonSerializer.SerializeToElement(map);
            }
        }
        return configuration with { Type = PrimaryType, Properties = props };
    }

    private Task ReportAsync(string message)
    {
        _logger.LogWarning("{Message}", message);
        return _p.Window is { } w ? w.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None).AsTask() : Task.CompletedTask;
    }

    /// <summary>
    /// Templates for a cargo package (<c>Cargo.toml</c>): the binary built by the <c>cargo: build</c> pre-launch task, the unit tests through
    /// <c>cargo test --no-run</c> (the <c>cargo</c> key) and attach to a running process; for a <c>build.zig</c> project the Zig ones
    /// (<see cref="ZigDebugging.Templates"/>); otherwise a plain program launch and attach.
    /// </summary>
    public async ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct)
    {
        var package = await CargoPackage.ReadAsync(_p, folder, ct);
        if (package is null && await ZigProject.ReadAsync(_p, folder, ct) is { } zig)
            return ZigDebugging.Templates(zig, PrimaryType, "CodeLLDB", folder!, c =>
            {
                c["sourceLanguages"] = new JsonArray("zig");
                c["terminal"] = "console";
            });
        var configs = new List<JsonObject>();
        if (package is not null)
        {
            var bin = package.Binaries.FirstOrDefault() ?? package.Name;
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Debug {bin} (CodeLLDB, cargo build)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = $"${{workspaceFolder}}/target/debug/{bin}",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["preLaunchTask"] = "cargo: build",
                ["sourceLanguages"] = new JsonArray("rust"),
                ["terminal"] = "console",
            });
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Debug unit tests of {package.Name} (CodeLLDB, cargo test)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["cargo"] = new JsonObject
                {
                    ["args"] = new JsonArray("test", "--no-run"),
                    ["filter"] = package.HasLibrary ? new JsonObject { ["kind"] = "lib" } : new JsonObject { ["name"] = bin, ["kind"] = "bin" },
                },
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["sourceLanguages"] = new JsonArray("rust"),
                ["terminal"] = "console",
            });
            configs.Add(new JsonObject
            {
                ["name"] = $"Rust: Attach to {bin} (CodeLLDB)",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["program"] = $"${{workspaceFolder}}/target/debug/{bin}",
                ["pid"] = "${command:pickProcess}",
                ["sourceLanguages"] = new JsonArray("rust"),
            });
        }
        else
        {
            if (await CFolder.ReadAsync(_p, folder, ct) is { } c) configs.AddRange(CFolder.Configurations(c, PrimaryType, "CodeLLDB", sourceLanguages: true));
            configs.Add(new JsonObject
            {
                ["name"] = "LLDB: Launch program",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = "${workspaceFolder}/a.out",
                ["args"] = new JsonArray(),
                ["cwd"] = "${workspaceFolder}",
                ["terminal"] = "console",
            });
            configs.Add(new JsonObject
            {
                ["name"] = "LLDB: Attach to process",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["pid"] = "${command:pickProcess}",
            });
        }
        return [.. configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder))];
    }

    /// <summary>The <c>cargo: build</c> task the binary template's preLaunchTask refers to (only for a cargo package), as the GDB adapter writes it;
    /// for a <c>build.zig</c> project the Zig templates' tasks; for a folder of C files the C single-file template's build task.</summary>
    public async ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct)
    {
        if (await CargoPackage.ReadAsync(_p, folder, ct) is not null) return [("tasks.json", CargoPackage.BuildTasksJson)];
        if (await ZigProject.ReadAsync(_p, folder, ct) is not null) return [("tasks.json", ZigDebugging.TasksJson(ZigDebugging.TaskCommand(_zig, name => Environment(name))))];
        return await CFolder.ReadAsync(_p, folder, ct) is not null ? [("tasks.json", CFolder.BuildTasksJson)] : []; // the C single-file template's build task
    }
}
