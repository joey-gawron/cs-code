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
public sealed record BuiltinDebugAdapter(string Type, string Label, IReadOnlyList<string> Languages, IDebugAdapterFactory Factory, IDebugConfigurationProvider? Provider = null)
{
    /// <summary>What this adapter does differently from what the client expects (GDB's native DAP); applied to every session of the type.</summary>
    public DebugAdapterQuirks? Quirks { get; init; }
    /// <summary>Another name for a debug type listed too (<c>node</c>, <c>rust-gdb</c>, <c>netcoredbg</c>): listed after the debuggers themselves.</summary>
    public bool IsAlias { get; init; }
}

/// <summary>
/// The default .NET story: Samsung <b>netcoredbg</b> (MIT; not vsdbg, whose licence forbids non-Microsoft products) launched as
/// <c>netcoredbg --interpreter=vscode</c>. Binary lookup order: <c>cscode.debug.netcoredbgPath</c> → <c>{ToolsDir}/netcoredbg/netcoredbg</c> → PATH;
/// when none exists the user is OFFERED a download (never silent).
/// Also provides the initial <c>coreclr</c> launch configuration + the tasks.json build task it refers to.
/// </summary>
public sealed partial class NetCoreDbgAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
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
        var project = await ChooseStartupProjectAsync(folder, ask: true, ct);
        lock (_chosenGate) _chosen = (folder, project); // the tasks.json that follows builds the same project (ProvideInitialFilesAsync)
        var projectDir = project is { Directory.Length: > 0 } ? "${workspaceFolder}/" + project.Directory : "${workspaceFolder}";
        var name = project?.AssemblyName ?? (folder is { IsFile: true } f ? Path.GetFileName(f.LocalPath.TrimEnd('/')) : "app");
        var program = $"{projectDir}/bin/Debug/{project?.TargetFramework ?? DefaultTargetFramework}/{name}.dll";
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

    // ------------------------------------------------------------------ the project a new launch.json is for

    internal const string DefaultTargetFramework = "net10.0";
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", "artifacts", "target", "out" };
    private const int MaxCandidates = 64, MaxDirectoriesScanned = 256;

    private readonly object _chosenGate = new();
    private (Uri? Folder, DotnetProject? Project)? _chosen;

    /// <summary>A project the template can launch. <paramref name="RelativePath"/> uses '/' and is relative to the workspace folder.</summary>
    internal sealed record DotnetProject(string RelativePath, string AssemblyName, string TargetFramework, bool IsExecutable)
    {
        public string Directory => RelativePath.LastIndexOf('/') is var i and > 0 ? RelativePath[..i] : "";
    }

    /// <summary>
    /// The project F5's launch.json starts: the folder's own project file (.csproj/.fsproj/.vbproj), else the projects its solution lists
    /// (.sln/.slnx), else the project files up to two levels down (the <c>dotnet new sln</c> layout: <c>src/Web/Web.csproj</c>). The executable
    /// ones (OutputType Exe/WinExe, the Web and Worker SDKs) are preferred; with several the user picks the startup project (as VS Code's C#
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

    internal async Task<IReadOnlyList<DotnetProject>> FindProjectsAsync(Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || _p.Workspace is not { } ws) return [];
        var root = folder.ToString().TrimEnd('/');
        Uri At(string relative) => new(root + "/" + relative);
        async Task<IReadOnlyList<DirectoryEntry>> ListAsync(string relative)
        {
            try { return await ws.FileSystem.ReadDirectoryAsync(relative.Length == 0 ? folder : At(relative), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
        static bool IsProject(string name) => ProjectExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        static bool IsScanned(DirectoryEntry e) => e.Type == FileType.Directory && !e.Name.StartsWith('.') && !SkippedDirectories.Contains(e.Name);

        var top = await ListAsync("");
        var paths = top.Where(e => e.Type == FileType.File && IsProject(e.Name)).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            // the projects the folder's solution lists (relative to the solution, which is in the folder)
            var solutions = top.Where(e => e.Type == FileType.File && (e.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal);
            foreach (var solution in solutions)
            {
                string text;
                try { text = System.Text.Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(At(solution), ct)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var listed in SolutionProjects(solution, text))
                    if (IsProject(listed) && !paths.Contains(listed, StringComparer.Ordinal) && await ws.FileSystem.StatAsync(At(listed), ct) is { Type: FileType.File })
                        paths.Add(listed);
            }
        }
        if (paths.Count == 0)
        {
            // no solution either: the project files one and two levels down (bounded)
            var scanned = 0;
            var level = top.Where(IsScanned).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            for (var depth = 1; depth <= 2 && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    if (++scanned > MaxDirectoriesScanned || paths.Count >= MaxCandidates) break;
                    foreach (var e in (await ListAsync(dir)).OrderBy(e => e.Name, StringComparer.Ordinal))
                    {
                        if (e.Type == FileType.File && IsProject(e.Name)) paths.Add(dir + "/" + e.Name);
                        else if (IsScanned(e)) next.Add(dir + "/" + e.Name);
                    }
                }
                level = next;
            }
        }

        var projects = new List<DotnetProject>();
        foreach (var path in paths.Take(MaxCandidates))
        {
            string? xml = null;
            try { xml = System.Text.Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(At(path), ct)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* named after its file, default framework */ }
            projects.Add(ReadProject(path, xml));
        }
        return projects;
    }

    /// <summary>
    /// The project paths a solution lists, with '/': a .sln's <c>Project("{…}") = "Name", "src\Web\Web.csproj", "{…}"</c> lines, a .slnx's
    /// <c>&lt;Project Path="…"/&gt;</c> elements. Paths that leave the folder are left out.
    /// </summary>
    internal static IReadOnlyList<string> SolutionProjects(string solutionName, string text)
    {
        var listed = new List<string>();
        if (solutionName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try { listed.AddRange(XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project").Select(e => (string?)e.Attribute("Path")).OfType<string>()); }
            catch (System.Xml.XmlException) { /* not a solution we can read */ }
        }
        else
        {
            foreach (System.Text.RegularExpressions.Match m in SlnProjectLine().Matches(text)) listed.Add(m.Groups[1].Value);
        }
        var result = new List<string>();
        foreach (var raw in listed)
        {
            var path = raw.Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            if (path.Length == 0 || path.StartsWith("../", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal) || Path.IsPathRooted(path)) continue;
            result.Add(path);
        }
        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex("""^\s*Project\("\{[^}]*\}"\)\s*=\s*"[^"]*"\s*,\s*"([^"]+)"\s*,""", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex SlnProjectLine();

    /// <summary>Assembly name, first target framework and whether it builds a program, from the project file's own properties.</summary>
    internal static DotnetProject ReadProject(string relativePath, string? xml)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var tfm = DefaultTargetFramework;
        var executable = false;
        if (xml is not null)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                string? Property(string property) =>
                    doc.Descendants().Where(e => e.Name.LocalName == property).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0 && !v.Contains("$(", StringComparison.Ordinal));
                tfm = Property("TargetFramework")
                    ?? Property("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                    ?? tfm;
                if (Property("AssemblyName") is { } assembly) name = assembly;
                var sdk = (string?)doc.Root?.Attribute("Sdk") ?? "";
                var outputType = Property("OutputType");
                executable = outputType is not null
                    ? string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase)
                    : sdk.StartsWith("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || sdk.StartsWith("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
                if (string.Equals(Property("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)) executable = false;
            }
            catch (System.Xml.XmlException) { /* keep the defaults */ }
        }
        return new DotnetProject(relativePath, name, tfm, executable);
    }

    /// <summary>
    /// The built-in .NET debugger under both of its debug types: <c>coreclr</c> (what VS Code writes) and the alias <c>netcoredbg</c> (docs/DAP.md);
    /// one factory and one provider serve both, as js-debug's <c>pwa-node</c>/<c>node</c>.
    /// </summary>
    public static IEnumerable<BuiltinDebugAdapter> Builtins(NetCoreDbgAdapterFactory factory) =>
    [
        new BuiltinDebugAdapter("coreclr", ".NET (netcoredbg)", Languages, factory, factory),
        new BuiltinDebugAdapter("netcoredbg", ".NET (legacy 'netcoredbg' type)", Languages, factory, factory) { IsAlias = true },
    ];

    public static readonly IReadOnlyList<string> Languages = ["csharp", "fsharp", "vb"];
}
