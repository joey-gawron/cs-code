using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>A debug adapter shipped with the app.</summary>
public sealed record BuiltinDebugAdapter(string Type, string Label, IReadOnlyList<string> Languages, IDebugAdapterFactory Factory, IDebugConfigurationProvider? Provider = null)
{
    /// <summary>What this adapter does differently from what the client expects (GDB's native DAP); applied to every session of the type.</summary>
    public DebugAdapterQuirks? Quirks { get; init; }
    /// <summary>Another name for a debug type listed too (<c>node</c>, <c>rust-gdb</c>, <c>netcoredbg</c>): listed after the debuggers themselves.</summary>
    public bool IsAlias { get; init; }
}

/// <summary>
/// The default .NET story: Samsung <b>netcoredbg</b> (MIT; not vsdbg, whose licence forbids non-Microsoft products) launched as
/// <c>netcoredbg --interpreter=vscode</c>. Binary lookup order: <c>cscode.debug.netcoredbgPath</c> → <c>{ToolsDir}/netcoredbg/netcoredbg</c> → the copy
/// installed with the app (<see cref="BundledAdapters"/>) → PATH;
/// when none exists the user is OFFERED a download (never silent).
/// Also provides the initial <c>coreclr</c> launch configuration + the tasks.json build task it refers to.
/// </summary>
public sealed class NetCoreDbgAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
{
    public const string SettingPath = "cscode.debug.netcoredbgPath";
    public static readonly IReadOnlyList<string> Types = ["coreclr", "netcoredbg"];

    private readonly DapPlatform _p;
    private readonly NetCoreDbgInstaller _installer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public NetCoreDbgAdapterFactory(DapPlatform platform, NetCoreDbgInstaller? installer = null)
    {
        _p = platform;
        _installer = installer ?? new NetCoreDbgInstaller(logger: platform.Logger("CsCode.Dap.NetCoreDbg"));
        _logger = platform.Logger("CsCode.Dap.NetCoreDbg");
    }

    /// <summary>PATH lookup seam (tests must not mutate the process-wide PATH while other tests spawn children).</summary>
    internal Func<string?> PathVariable { get; init; } = () => Environment.GetEnvironmentVariable("PATH");

    public static string ExecutableName => OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        var path = Locate() ?? await OfferInstallAsync(ct) ??
            throw new DebugSessionStartException(NetCoreDbgInstaller.CurrentPlatformAsset() is null
                ? "netcoredbg was not found. " + NetCoreDbgInstaller.NoBuildMessage
                : "netcoredbg was not found. Install it with tools/fetch-netcoredbg.sh, put it on PATH, or set 'cscode.debug.netcoredbgPath' (or accept the download offer).");
        return new DebugAdapterExecutable(path, ["--interpreter=vscode"]);
    }

    /// <summary>Locates the binary without side effects; null when it is not installed.</summary>
    public string? Locate()
    {
        if (_p.Configuration?.Get<string>(SettingPath) is { Length: > 0 } configured && File.Exists(configured)) return configured;
        if (_p.Paths is { } paths)
        {
            var tool = Path.Combine(paths.ToolsDir, "netcoredbg", ExecutableName);
            if (File.Exists(tool)) return tool;
        }
        if (BundledAdapters.NetCoreDbg(_p.Paths) is { } bundled) return bundled;
        foreach (var dir in (PathVariable() ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    private async ValueTask<string?> OfferInstallAsync(CancellationToken ct)
    {
        if (_p.Window is not { } window || _p.Paths is not { } paths) return null;
        if (NetCoreDbgInstaller.CurrentPlatformAsset() is null) return null;
        await _installGate.WaitAsync(ct);
        try
        {
            if (Locate() is { } already) return already; // another start finished the install while we waited
            var choice = await window.ShowMessageAsync(MessageSeverity.Info,
                "Debugging .NET needs netcoredbg (Samsung, MIT licence), which is not installed. Download it from github.com/Samsung/netcoredbg?",
                [new MessageAction("Download"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
            if (choice != "Download") return null;
            return await window.WithProgressAsync("Downloading netcoredbg", cancellable: true,
                (progress, token) => _installer.InstallAsync(paths.ToolsDir, progress, token));
        }
        // InvalidOperationException: the release has no asset for this machine, or the archive has no netcoredbg in it.
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnsafeArchiveException or PlatformNotSupportedException
                                   or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "netcoredbg download failed");
            await window.ShowMessageAsync(MessageSeverity.Error, $"Could not install netcoredbg: {ex.Message}", null, CancellationToken.None);
            return null;
        }
        finally { _installGate.Release(); }
    }

    // ------------------------------------------------------------------ configuration provider

    /// <summary>Fills in sensible defaults a hand-written coreclr config may omit; never overrides the user's values.</summary>
    public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return ValueTask.FromResult<DebugConfiguration?>(configuration);
        // netcoredbg (the vscode interpreter) needs `cwd` for a launch request; default to the program's folder, else the workspace.
        if (!configuration.IsAttach && !configuration.Properties.ContainsKey("cwd"))
        {
            var cwd = configuration.GetString("program") is { Length: > 0 } program && Path.GetDirectoryName(program) is { Length: > 0 } dir ? dir
                : folder is { IsFile: true } f ? f.LocalPath.TrimEnd('/') : null;
            if (cwd is not null)
            {
                var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["cwd"] = JsonSerializer.SerializeToElement(cwd) };
                configuration = configuration with { Properties = props };
            }
        }
        // `${command:pickProcess}` substitutes TEXT; netcoredbg's attach wants the pid as a JSON number.
        if (configuration.IsAttach && configuration.Properties.TryGetValue("processId", out var pid) && pid.ValueKind == JsonValueKind.String
            && int.TryParse(pid.GetString(), out var number))
        {
            var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["processId"] = JsonSerializer.SerializeToElement(number) };
            configuration = configuration with { Properties = props };
        }
        return ValueTask.FromResult<DebugConfiguration?>(configuration);
    }

    public async ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct)
    {
        var project = await ChooseStartupProjectAsync(folder, ask: true, ct);
        lock (_chosenGate) _chosen = (folder, project); // the tasks.json that follows builds the same project (ProvideInitialFilesAsync)
        var projectDir = project is { Directory.Length: > 0 } ? "${workspaceFolder}/" + project.Directory : "${workspaceFolder}";
        var name = project?.AssemblyName ?? (folder is { IsFile: true } f ? Path.GetFileName(f.LocalPath.TrimEnd('/')) : "app");
        var program = $"{projectDir}/bin/Debug/{project?.TargetFramework ?? DotnetProjects.DefaultTargetFramework}/{name}.dll";
        var launch = new JsonObject
        {
            ["name"] = ".NET: Launch",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["preLaunchTask"] = "build",
            ["program"] = program,
            ["args"] = new JsonArray(),
            ["cwd"] = projectDir, // VS Code's C# assets: the project's directory (a web app reads its appsettings.json there)
            ["stopAtEntry"] = false,
            ["console"] = "internalConsole",
        };
        var attach = new JsonObject
        {
            ["name"] = ".NET: Attach",
            ["type"] = "coreclr",
            ["request"] = "attach",
            ["processId"] = "${command:pickProcess}",
        };
        return
        [
            DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(launch), folder),
            DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(attach), folder),
        ];
    }

    public async ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct)
    {
        DotnetProject? project;
        bool known;
        lock (_chosenGate)
        {
            known = _chosen is { } c && c.Folder == folder;
            project = known ? _chosen!.Value.Project : null;
            _chosen = null;
        }
        if (!known) project = await ChooseStartupProjectAsync(folder, ask: false, ct);
        var projectArg = project is null ? "" : "${workspaceFolder}/" + project.RelativePath;
        var args = projectArg.Length > 0
            ? $"\"build\", {JsonSerializer.Serialize(projectArg, ReadableJson)}, \"/property:GenerateFullPaths=true\", \"/consoleloggerparameters:NoSummary\"" // a path may need escaping
            : "\"build\", \"/property:GenerateFullPaths=true\", \"/consoleloggerparameters:NoSummary\"";
        // "watch" runs the project under dotnet watch (hot reload) as a background task: $dotnet-watch's begin and end patterns tell when a
        // rebuild or reload is running and when the program is up again, so it can be a preLaunchTask or followed in Run Task
        var watchArgs = projectArg.Length > 0 ? $"\"watch\", \"run\", \"--project\", {JsonSerializer.Serialize(projectArg, ReadableJson)}" : "\"watch\", \"run\"";
        var tasks = $$"""
            {
                "version": "2.0.0",
                "tasks": [
                    {
                        "label": "build",
                        "type": "process",
                        "command": "dotnet",
                        "args": [{{args}}],
                        "problemMatcher": "$msCompile",
                        "group": { "kind": "build", "isDefault": true }
                    },
                    {
                        "label": "watch",
                        "type": "process",
                        "command": "dotnet",
                        "args": [{{watchArgs}}],
                        "isBackground": true,
                        "problemMatcher": "$dotnet-watch"
                    }
                ]
            }

            """;
        return [("tasks.json", tasks)];
    }

    // ------------------------------------------------------------------ the project a new launch.json is for

    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object _chosenGate = new();
    private (Uri? Folder, DotnetProject? Project)? _chosen;

    /// <summary>
    /// The project F5's launch.json starts, among the folder's projects (<see cref="DotnetProjects"/>). The executable ones (OutputType
    /// Exe/WinExe, the Web and Worker SDKs; not a test project) are preferred; with several the user picks the startup project (as VS Code's C#
    /// assets ask), else the first one. Null when the folder has no project at all.
    /// </summary>
    internal async Task<DotnetProject?> ChooseStartupProjectAsync(Uri? folder, bool ask, CancellationToken ct)
    {
        var candidates = await FindProjectsAsync(folder, ct);
        if (candidates.Count == 0) return null;
        var executables = candidates.Where(p => p.IsExecutable).ToList();
        if (executables.Count == 0) return candidates[0];
        if (executables.Count == 1 || !ask || _p.Window is not { } window) return executables[0];
        var picked = await window.ShowQuickPickAsync(
            [.. executables.Select(p => new QuickPickItem(Path.GetFileName(p.RelativePath), p.Directory.Length > 0 ? p.Directory : null, Tag: p))],
            new QuickPickOptions(Title: "Select the startup project", Placeholder: "The project the new launch.json starts"), ct);
        return picked?.Tag as DotnetProject ?? executables[0];
    }

    internal async Task<IReadOnlyList<DotnetProject>> FindProjectsAsync(Uri? folder, CancellationToken ct) =>
        folder is { IsFile: true } && _p.Workspace is { } ws ? (await DotnetProjects.FindAsync(ws.FileSystem, folder, ct)).Projects : [];

    /// <summary>
    /// The built-in .NET debugger under both of its debug types: <c>coreclr</c> (what VS Code writes) and the alias <c>netcoredbg</c> (docs/DAP.md);
    /// one factory and one provider serve both, as js-debug's <c>pwa-node</c>/<c>node</c>.
    /// </summary>
    public static IEnumerable<BuiltinDebugAdapter> Builtins(NetCoreDbgAdapterFactory factory) =>
    [
        new BuiltinDebugAdapter("coreclr", ".NET (netcoredbg)", Languages, factory, factory) { Quirks = NetCoreDbgAdapterQuirks.Instance },
        new BuiltinDebugAdapter("netcoredbg", ".NET (legacy 'netcoredbg' type)", Languages, factory, factory) { Quirks = NetCoreDbgAdapterQuirks.Instance, IsAlias = true },
    ];

    public static readonly IReadOnlyList<string> Languages = ["csharp", "fsharp", "vb"];
}
