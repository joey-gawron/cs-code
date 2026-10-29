using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>A debug adapter shipped with the app.</summary>
public sealed record BuiltinDebugAdapter(string Type, string Label, IReadOnlyList<string> Languages, IDebugAdapterFactory Factory, IDebugConfigurationProvider? Provider = null);

/// <summary>
/// The default .NET story: Samsung <b>netcoredbg</b> (MIT; not vsdbg, whose licence forbids non-Microsoft products) launched as
/// <c>netcoredbg --interpreter=vscode</c>. Binary lookup order: <c>cscode.debug.netcoredbgPath</c> → <c>{ToolsDir}/netcoredbg/netcoredbg</c> → PATH;
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
            throw new DebugSessionStartException(
                "netcoredbg was not found. Install it with tools/fetch-netcoredbg.sh, put it on PATH, or set 'cscode.debug.netcoredbgPath' (or accept the download offer).");
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnsafeArchiveException or PlatformNotSupportedException or TaskCanceledException)
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
        var (project, tfm) = await FindProjectAsync(folder, ct);
        var program = $"${{workspaceFolder}}/bin/Debug/{tfm}/{project}.dll";
        var launch = new JsonObject
        {
            ["name"] = ".NET: Launch",
            ["type"] = "coreclr",
            ["request"] = "launch",
            ["preLaunchTask"] = "build",
            ["program"] = program,
            ["args"] = new JsonArray(),
            ["cwd"] = "${workspaceFolder}",
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
        var projectFile = await FindProjectFileAsync(folder, ct);
        var projectArg = projectFile is null ? "" : "${workspaceFolder}/" + projectFile;
        var args = projectArg.Length > 0
            ? $"\"build\", \"{projectArg}\", \"/property:GenerateFullPaths=true\", \"/consoleloggerparameters:NoSummary\""
            : "\"build\", \"/property:GenerateFullPaths=true\", \"/consoleloggerparameters:NoSummary\"";
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
                    }
                ]
            }

            """;
        return [("tasks.json", tasks)];
    }

    private async Task<string?> FindProjectFileAsync(Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || _p.Workspace is not { } ws) return null;
        try
        {
            var entries = await ws.FileSystem.ReadDirectoryAsync(folder, ct);
            return entries.Where(e => e.Type == FileType.File && e.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<(string Project, string Tfm)> FindProjectAsync(Uri? folder, CancellationToken ct)
    {
        var file = await FindProjectFileAsync(folder, ct);
        var project = file is null ? (folder is { IsFile: true } f ? Path.GetFileName(f.LocalPath.TrimEnd('/')) : "app") : Path.GetFileNameWithoutExtension(file);
        var tfm = "net10.0";
        if (file is not null && folder is { IsFile: true } root && _p.Workspace is { } ws)
        {
            try
            {
                var xml = System.Text.Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(new Uri(root.ToString().TrimEnd('/') + "/" + file), ct));
                var doc = XDocument.Parse(xml);
                var value = doc.Descendants("TargetFramework").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0)
                    ?? doc.Descendants("TargetFrameworks").Select(e => e.Value.Split(';')[0].Trim()).FirstOrDefault(v => v.Length > 0);
                if (value is not null) tfm = value;
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { /* keep the default */ }
        }
        return (project, tfm);
    }
}
