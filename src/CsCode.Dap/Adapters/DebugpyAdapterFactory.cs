using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Python;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Adapters;

/// <summary>
/// Python debugging through Microsoft's <b>debugpy</b> (MIT), types <c>debugpy</c> and its legacy alias <c>python</c> (docs/PYTHON.md §4).
/// <para><b>Adapter.</b> Launching, and attaching to a process id: <c>&lt;interpreter&gt; -m debugpy.adapter</c> (stdio) when the interpreter has
/// debugpy, otherwise the pinned bundled copy run as <c>&lt;interpreter&gt; {ToolsDir}/debugpy/&lt;version&gt;/debugpy/adapter</c>
/// (<see cref="DebugpyInstaller"/>; the user is asked before it is downloaded). The interpreter is the configuration's <c>python</c>, else the
/// folder's selected one (<see cref="IPythonEnvironmentService"/>). Attaching with <c>connect</c> (or the legacy <c>host</c> / <c>port</c>) talks to
/// the debugpy adapter already listening there (<c>python -m debugpy --listen 5678 app.py</c>), as VS Code does: no adapter process of ours.</para>
/// <para><b>Child processes</b> (<c>subProcess</c>, multiprocessing): debugpy announces each with DAP's <c>startDebugging</c> request, which CsCode
/// supports (it advertises <c>supportsStartDebuggingRequest</c>), or, to a client that does not, with its own <c>debugpyAttach</c> event; both start
/// a child session whose configuration is an attach with <c>connect</c> to the parent adapter's listener and <c>subProcessId</c>
/// (debugpy/adapter/clients.py, <c>notify_of_subprocess</c>).</para>
/// <para><b>Provider.</b> Fills in what VS Code's Python Debugger extension would: <c>python</c>, <c>cwd</c> (the folder), <c>console</c>
/// (<c>integratedTerminal</c> when there is a terminal), the folder's <c>.env</c> when no <c>envFile</c> is given (<c>python.envFile</c>), a
/// text <c>processId</c> (<c>${command:pickProcess}</c>) as a number, and <c>program</c> = the active Python file when nothing says what to run.</para>
/// </summary>
public sealed class DebugpyAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugCurrentFileProvider
{
    public const string PrimaryType = "debugpy";
    public const string LegacyType = "python";
    public const string SettingPath = "cscode.debug.debugpyPath";
    public const string EnvFileSetting = "python.envFile";
    public const string EnvironmentVariable = "CSCODE_DEBUGPY";
    public static readonly IReadOnlyList<string> Types = [PrimaryType, LegacyType];
    public static readonly IReadOnlyList<string> Languages = ["python"];

    private readonly DapPlatform _p;
    private readonly DebugpyInstaller _installer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public DebugpyAdapterFactory(DapPlatform platform, DebugpyInstaller? installer = null)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.Debugpy");
        _installer = installer ?? new DebugpyInstaller(logger: _logger);
    }

    public static IEnumerable<BuiltinDebugAdapter> Builtins(DebugpyAdapterFactory factory) =>
    [
        new(PrimaryType, "Python (debugpy)", Languages, factory, factory) { Quirks = DebugpyAdapterQuirks.Instance },
        new(LegacyType, "Python (legacy 'python' type, debugpy)", Languages, factory, factory) { Quirks = DebugpyAdapterQuirks.Instance, IsAlias = true },
    ];

    /// <summary>Environment seam (tests must not mutate process-wide variables).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        if (configuration.IsAttach && !configuration.Properties.ContainsKey("processId") && Endpoint(configuration) is { } endpoint)
            return new DebugAdapterServer(endpoint.Host, endpoint.Port);
        if (configuration.IsAttach && configuration.Properties.ContainsKey("listen"))
            throw new DebugSessionStartException("Attaching with \"listen\" (debugpy connecting to CsCode) is not supported: start the program with --listen and attach with \"connect\".");
        var python = PythonOf(configuration) ?? (await ActiveAsync(configuration.WorkspaceFolder, ct))?.Executable
            ?? throw new DebugSessionStartException(NoInterpreterMessage);
        var cwd = configuration.WorkspaceFolder is { IsFile: true } f && Directory.Exists(f.LocalPath) ? f.LocalPath : null;
        if (_p.Python is { } environments && await environments.HasModuleAsync(python, "debugpy", ct))
            return new DebugAdapterExecutable(python, ["-m", "debugpy.adapter"], Cwd: cwd);
        var adapter = Locate() ?? await OfferInstallAsync(ct) ?? throw new DebugSessionStartException(
            $"The Python debugger (debugpy) is not installed for {python}. Install it into that environment (python -m pip install debugpy), accept the download offer, or set '{SettingPath}'.");
        return new DebugAdapterExecutable(python, [adapter], Cwd: cwd);
    }

    private const string NoInterpreterMessage = "No Python interpreter was found to debug with. Pick one with 'Python: Select Interpreter', or set \"python\" in the launch configuration.";

    /// <summary>Where an attach connects: <c>connect.host</c>/<c>connect.port</c>, else the legacy top-level <c>host</c>/<c>port</c>.</summary>
    internal static (string Host, int Port)? Endpoint(DebugConfiguration configuration)
    {
        JsonElement? connect = configuration.Properties.TryGetValue("connect", out var c) && c.ValueKind == JsonValueKind.Object ? c : null;
        string? host = connect is { } cc && cc.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : configuration.GetString("host");
        int? port = Port(connect is { } cp && cp.TryGetProperty("port", out var p) ? p : null) ?? Port(configuration.Properties.TryGetValue("port", out var tp) ? tp : null);
        return port is { } value ? (string.IsNullOrEmpty(host) || host == "localhost" ? "127.0.0.1" : host, value) : null;

        static int? Port(JsonElement? e) => e switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), out var i) => i,
            _ => null,
        };
    }

    /// <summary>The interpreter a configuration names (<c>python</c>, a string or the first of an array; or the legacy <c>pythonPath</c>).</summary>
    internal static string? PythonOf(DebugConfiguration configuration)
    {
        foreach (var key in new[] { "python", "pythonPath" })
            if (configuration.Properties.TryGetValue(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].GetString() is { Length: > 0 } first) return first;
            }
        return null;
    }

    private async ValueTask<PythonEnvironment?> ActiveAsync(Uri? folder, CancellationToken ct) =>
        _p.Python is { } environments ? await environments.GetActiveAsync(folder ?? _p.PrimaryFolder, ct) : null;

    /// <summary>The bundled adapter directory without side effects (setting, then <c>CSCODE_DEBUGPY</c>, then the installed copy); null when there is none.</summary>
    public string? Locate()
    {
        foreach (var candidate in new[] { _p.Configuration?.Get<string>(SettingPath), Environment(EnvironmentVariable) })
            if (candidate is { Length: > 0 } && ResolveAdapter(candidate) is { } found) return found;
        return _p.Paths is { } paths && File.Exists(Path.Combine(DebugpyInstaller.AdapterPath(paths.ToolsDir), "__main__.py")) ? DebugpyInstaller.AdapterPath(paths.ToolsDir) : null;
    }

    /// <summary>Accepts the adapter directory, the debugpy package directory or the directory holding it.</summary>
    public static string? ResolveAdapter(string path)
    {
        foreach (var candidate in new[] { path, Path.Combine(path, "adapter"), Path.Combine(path, "debugpy", "adapter") })
            if (File.Exists(Path.Combine(candidate, "__main__.py")) && File.Exists(Path.Combine(candidate, "..", "__init__.py"))) return Path.GetFullPath(candidate);
        return null;
    }

    private async ValueTask<string?> OfferInstallAsync(CancellationToken ct)
    {
        if (_p.Window is not { } window || _p.Paths is not { } paths) return null;
        await _installGate.WaitAsync(ct);
        try
        {
            if (Locate() is { } already) return already;
            var choice = await window.ShowMessageAsync(MessageSeverity.Info,
                $"Debugging Python needs Microsoft's debugpy {DebugpyInstaller.Version} (MIT licence, about 5.4 MB), which the selected interpreter does not have. Download it from PyPI (files.pythonhosted.org)?",
                [new MessageAction("Download"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
            if (choice != "Download") return null;
            return await window.WithProgressAsync("Downloading debugpy", cancellable: true, (progress, token) => _installer.InstallAsync(paths.ToolsDir, progress, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "debugpy download failed");
            await window.ShowMessageAsync(MessageSeverity.Error, $"Could not install debugpy: {ex.Message}", null, CancellationToken.None);
            return null;
        }
        finally { _installGate.Release(); }
    }

    // ------------------------------------------------------------------ configuration provider

    public async ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return configuration;
        var props = new Dictionary<string, JsonElement>(configuration.Properties) { ["type"] = JsonSerializer.SerializeToElement(PrimaryType) };
        if (props.ContainsKey("subProcessId")) return configuration with { Type = PrimaryType, Properties = props }; // a child: debugpy wrote it whole
        var dir = folder is { IsFile: true } f ? DapPlatform.NormalizeDir(f.LocalPath) : null;
        if (configuration.IsAttach)
        {
            // `${command:pickProcess}` substitutes TEXT; debugpy wants the pid as a number
            if (props.TryGetValue("processId", out var pid) && pid.ValueKind == JsonValueKind.String && int.TryParse(pid.GetString(), out var number))
                props["processId"] = JsonSerializer.SerializeToElement(number);
            if (!props.ContainsKey("processId") && !props.ContainsKey("connect") && !props.ContainsKey("listen") && !props.ContainsKey("port"))
                props["connect"] = JsonSerializer.SerializeToElement(new { host = "127.0.0.1", port = 5678 }); // VS Code's template default
            if (props.ContainsKey("processId") && PythonOf(configuration) is null && await ActiveAsync(folder, ct) is { } env)
                props["python"] = JsonSerializer.SerializeToElement(env.Executable);
            return configuration with { Type = PrimaryType, Properties = props };
        }

        if (!props.ContainsKey("program") && !props.ContainsKey("module") && !props.ContainsKey("code"))
        {
            if (_p.Navigation?.ActiveDocument is { LanguageId: "python", Uri.IsFile: true } doc) props["program"] = JsonSerializer.SerializeToElement(doc.Uri.LocalPath);
            else
            {
                await ReportAsync($"'{configuration.Name}' says neither \"program\", \"module\" nor \"code\", and the active file is not a Python file.");
                return null;
            }
        }
        if (PythonOf(configuration) is null)
        {
            if (await ActiveAsync(folder, ct) is not { } env)
            {
                await ReportAsync(NoInterpreterMessage);
                return null;
            }
            props["python"] = JsonSerializer.SerializeToElement(env.Executable);
        }
        if (!props.ContainsKey("cwd") && dir is not null) props["cwd"] = JsonSerializer.SerializeToElement(dir);
        var console = configuration.GetString("console");
        var terminal = _p.Terminals?.Invoke() is not null;
        if (console is null || (console is "integratedTerminal" or "externalTerminal" && !terminal))
            props["console"] = JsonSerializer.SerializeToElement(console is null && terminal ? "integratedTerminal" : "internalConsole");
        if (!props.ContainsKey("envFile") && await DefaultEnvFileAsync(dir, ct) is { } fromFile)
            props["env"] = EnvFile.Merge(props.TryGetValue("env", out var env0) ? env0 : null, fromFile);
        return configuration with { Type = PrimaryType, Properties = props };
    }

    /// <summary>The variables of <c>python.envFile</c> (default <c>${workspaceFolder}/.env</c>) when that file exists, else null.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> DefaultEnvFileAsync(string? folder, CancellationToken ct)
    {
        var setting = _p.Configuration?.Get<string>(EnvFileSetting) is { Length: > 0 } s ? s : "${workspaceFolder}/.env";
        var path = setting.Replace("${workspaceFolder}", folder ?? "", StringComparison.Ordinal);
        if (!Path.IsPathRooted(path) || path.Contains("${", StringComparison.Ordinal)) return null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > EnvFile.MaxBytes) return null;
            return EnvFile.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Setting} {Path} could not be read; ignored", EnvFileSetting, path);
            return null;
        }
    }

    private async Task ReportAsync(string message)
    {
        _logger.LogWarning("{Message}", message);
        if (_p.Window is { } window) await window.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None);
    }

    /// <summary>"Python Debugger: Current File" for <paramref name="file"/>: what F5 runs on a Python file when the folder has no launch configuration.</summary>
    public DebugConfiguration? ForCurrentFile(Uri file, string languageId, Uri? folder)
    {
        if (languageId != "python" || !file.IsFile) return null;
        var node = CurrentFile("${file}");
        node["program"] = file.LocalPath;
        return DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(node), folder);
    }

    private static JsonObject CurrentFile(string program) => new()
    {
        ["name"] = "Python Debugger: Current File",
        ["type"] = PrimaryType,
        ["request"] = "launch",
        ["program"] = program,
        ["console"] = "integratedTerminal",
    };

    public async ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct)
    {
        var module = folder is { IsFile: true } f ? await Task.Run(() => FindMainModule(f.LocalPath), ct) : null;
        JsonObject[] configs =
        [
            CurrentFile("${file}"),
            new()
            {
                ["name"] = "Python Debugger: Module",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["module"] = module ?? "main",
                ["console"] = "integratedTerminal",
            },
            new()
            {
                ["name"] = "Python Debugger: Attach",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["connect"] = new JsonObject { ["host"] = "localhost", ["port"] = 5678 },
            },
            new()
            {
                ["name"] = "Python Debugger: Attach using Process Id",
                ["type"] = PrimaryType,
                ["request"] = "attach",
                ["processId"] = "${command:pickProcess}",
            },
        ];
        return configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder)).ToArray();
    }

    /// <summary>A package of the folder (or its <c>src/</c>) with a <c>__main__.py</c>: the module "Python Debugger: Module" runs.</summary>
    internal static string? FindMainModule(string folder)
    {
        foreach (var root in new[] { folder, Path.Combine(folder, "src") })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Take(200))
                    if (File.Exists(Path.Combine(dir, "__main__.py"))) return Path.GetFileName(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>
/// debugpy's differences from the client's defaults: its <c>debugpyAttach</c> event (a subprocess to attach to, sent to clients without
/// <c>startDebugging</c>) starts a child session.
/// </summary>
public sealed class DebugpyAdapterQuirks : DebugAdapterQuirks
{
    public static DebugpyAdapterQuirks Instance { get; } = new();

    public const string AttachEvent = "debugpyAttach";

    public override (string Request, JsonObject Configuration)? ChildSessionFromEvent(string name, JsonElement body)
    {
        if (name != AttachEvent || body.ValueKind != JsonValueKind.Object) return null;
        var configuration = JsonNode.Parse(body.GetRawText()) as JsonObject ?? new JsonObject();
        configuration["request"] = "attach";
        return ("attach", configuration);
    }
}
