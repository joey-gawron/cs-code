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
/// Node.js / TypeScript debugging through Microsoft's standalone <b>js-debug</b> (<c>dapDebugServer.js</c>, MIT): the adapter runs as a server on
/// the user's Node.js (<c>node dapDebugServer.js 0 127.0.0.1</c>, below-normal priority, a tracked child process owned by the session), prints its
/// loopback port, and the session connects over TCP. The launched program becomes a CHILD session (DAP <c>startDebugging</c>) that the debug
/// service connects to the same server; breakpoints hit there. Types <c>pwa-node</c> and its legacy alias <c>node</c>.
/// <para>Lookup of the adapter: setting <c>cscode.debug.jsDebugPath</c> → environment variable <c>CSCODE_JSDEBUG</c> → the installed copy
/// <c>{ToolsDir}/js-debug/&lt;version&gt;/js-debug</c>; either may name the js-debug directory or <c>dapDebugServer.js</c> itself. When none
/// exists the user is OFFERED the pinned download (never silent). Node.js: setting <c>cscode.debug.nodePath</c> → PATH.</para>
/// <para>Adapter quirk handled here: without <c>__workspaceFolder</c> (which VS Code's js-debug extension always sets) js-debug drops every
/// <c>${workspaceFolder}</c> entry of its default <c>resolveSourceMapLocations</c>, so NO source map is ever resolved and breakpoints in
/// <c>.ts</c> files never bind (checked against v1.140.0). The provider fills it in.</para>
/// </summary>
public sealed class JsDebugAdapterFactory : IDebugAdapterFactory, IDebugConfigurationProvider, IDebugInitialFilesProvider
{
    public const string SettingPath = "cscode.debug.jsDebugPath";
    public const string NodeSettingPath = "cscode.debug.nodePath";
    public const string EnvironmentVariable = "CSCODE_JSDEBUG";
    public const string PrimaryType = "pwa-node";
    public static readonly IReadOnlyList<string> Types = [PrimaryType, "node"];
    public static readonly IReadOnlyList<string> Languages = ["typescript", "javascript", "typescriptreact", "javascriptreact"];
    /// <summary>What dapDebugServer.js prints once it listens (v1.140.0: "Debug server listening at 127.0.0.1:41235").</summary>
    public const string ListeningPattern = @"^Debug server listening at (?<host>[^\s]+?):(?<port>\d+)\s*$";
    internal const string PendingTargetKey = "__pendingTargetId";

    private readonly DapPlatform _p;
    private readonly JsDebugInstaller _installer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public JsDebugAdapterFactory(DapPlatform platform, JsDebugInstaller? installer = null)
    {
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.JsDebug");
        _installer = installer ?? new JsDebugInstaller(logger: _logger);
    }

    /// <summary>Environment seam (tests must not mutate process-wide variables while other tests spawn children).</summary>
    internal Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public static string NodeExecutableName => OperatingSystem.IsWindows() ? "node.exe" : "node";

    // ------------------------------------------------------------------ adapter

    public async ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase)) return null;
        var node = LocateNode() ?? throw new DebugSessionStartException(
            "Debugging JavaScript/TypeScript needs Node.js, which was not found. Install it, put 'node' on PATH, or set 'cscode.debug.nodePath'.");
        var script = Locate() ?? await OfferInstallAsync(ct) ?? throw new DebugSessionStartException(
            "The JavaScript debugger (js-debug) was not found. Install it with tools/fetch-js-debug.sh, set 'cscode.debug.jsDebugPath' or CSCODE_JSDEBUG to the js-debug directory, or accept the download offer.");
        var folder = configuration.WorkspaceFolder ?? _p.PrimaryFolder;
        var cwd = folder is { IsFile: true } f && Directory.Exists(f.LocalPath) ? f.LocalPath : Path.GetTempPath();
        return new DebugAdapterServerProcess(node, [script, "0", "127.0.0.1"], ListeningPattern, null, cwd)
        {
            DisplayName = "js-debug",
            StartTimeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>The js-debug server script without side effects; null when it is not installed.</summary>
    public string? Locate()
    {
        if (_p.Configuration?.Get<string>(SettingPath) is { Length: > 0 } configured && ResolveServerScript(configured) is { } fromSetting) return fromSetting;
        if (Environment(EnvironmentVariable) is { Length: > 0 } env && ResolveServerScript(env) is { } fromEnv) return fromEnv;
        if (_p.Paths is { } paths && File.Exists(JsDebugInstaller.ServerScript(paths.ToolsDir))) return JsDebugInstaller.ServerScript(paths.ToolsDir);
        return null;
    }

    /// <summary>Accepts <c>dapDebugServer.js</c> itself, the js-debug directory (with <c>src/</c>), its parent (the install directory) or its <c>src/</c>.</summary>
    public static string? ResolveServerScript(string path)
    {
        try
        {
            if (File.Exists(path)) return path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null;
            if (!Directory.Exists(path)) return null;
            foreach (var candidate in new[] { Path.Combine(path, "src", "dapDebugServer.js"), Path.Combine(path, "js-debug", "src", "dapDebugServer.js"), Path.Combine(path, "dapDebugServer.js") })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    public string? LocateNode()
    {
        if (_p.Configuration?.Get<string>(NodeSettingPath) is { Length: > 0 } configured && File.Exists(configured)) return configured;
        foreach (var dir in (Environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, NodeExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    private async ValueTask<string?> OfferInstallAsync(CancellationToken ct)
    {
        if (_p.Window is not { } window || _p.Paths is not { } paths) return null;
        await _installGate.WaitAsync(ct);
        try
        {
            if (Locate() is { } already) return already; // another start finished the install while we waited
            var choice = await window.ShowMessageAsync(MessageSeverity.Info,
                $"Debugging JavaScript and TypeScript needs Microsoft's js-debug adapter {JsDebugInstaller.Version} (MIT licence, about 1.2 MB), which is not installed. Download it from github.com/microsoft/vscode-js-debug?",
                [new MessageAction("Download"), new MessageAction("Cancel", IsCloseAffordance: true)], ct);
            if (choice != "Download") return null;
            return await window.WithProgressAsync("Downloading js-debug", cancellable: true,
                (progress, token) => _installer.InstallAsync(paths.ToolsDir, progress, token));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnsafeArchiveException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "js-debug download failed");
            await window.ShowMessageAsync(MessageSeverity.Error, $"Could not install js-debug: {ex.Message}", null, CancellationToken.None);
            return null;
        }
        finally { _installGate.Release(); }
    }

    // ------------------------------------------------------------------ configuration provider

    /// <summary>Fills in what VS Code's js-debug extension would; never overrides the user's values. Child (<c>startDebugging</c>) configurations pass untouched.</summary>
    public ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct)
    {
        if (!Types.Contains(configuration.Type, StringComparer.OrdinalIgnoreCase) || configuration.Properties.ContainsKey(PendingTargetKey))
            return ValueTask.FromResult<DebugConfiguration?>(configuration);
        var props = new Dictionary<string, JsonElement>(configuration.Properties);
        var type = configuration.Type;
        if (!string.Equals(type, PrimaryType, StringComparison.Ordinal))
        {
            type = PrimaryType; // js-debug's legacy alias: the standalone server only knows the pwa-* types
            props["type"] = JsonSerializer.SerializeToElement(type);
        }
        if (!props.ContainsKey("__workspaceFolder"))
        {
            var root = folder is { IsFile: true } f ? DapPlatform.NormalizeDir(f.LocalPath)
                : configuration.GetString("cwd") is { Length: > 0 } cwd ? cwd
                : configuration.GetString("program") is { Length: > 0 } program ? Path.GetDirectoryName(program) : null;
            if (root is not null) props["__workspaceFolder"] = JsonSerializer.SerializeToElement(root);
        }
        // CsCode has no terminal for debuggees: a runInTerminal launch pipes the program's streams into the Debug Console, and js-debug ALSO
        // reports every console.* call itself, so each console.log line would show twice. A terminal launch runs in the console instead, with
        // js-debug capturing the program's streams (outputCapture "std"): every line once, process.stdout.write included, as a terminal shows it.
        if (!configuration.IsAttach && configuration.GetString("console") is "integratedTerminal" or "externalTerminal")
        {
            props["console"] = JsonSerializer.SerializeToElement("internalConsole");
            if (!props.ContainsKey("outputCapture")) props["outputCapture"] = JsonSerializer.SerializeToElement("std");
        }
        // `${command:pickProcess}` substitutes the pid as TEXT: js-debug's attach expects "processId": "<pid>" (a string), which is what we have.
        return ValueTask.FromResult<DebugConfiguration?>(configuration with { Type = type, Properties = props });
    }

    public async ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct)
    {
        var layout = await InspectFolderAsync(folder, ct);
        var configs = new List<JsonObject>();
        if (layout.TypeScriptEntry is { } entry)
        {
            var ts = new JsonObject
            {
                ["name"] = "TypeScript: Launch (tsc build)",
                ["type"] = PrimaryType,
                ["request"] = "launch",
                ["program"] = InFolder(entry),
                ["preLaunchTask"] = "tsc: build",
                ["outFiles"] = layout.OutDir is { Length: > 0 } outDir
                    ? new JsonArray(InFolder(outDir) + "/**/*.js")
                    : new JsonArray("${workspaceFolder}/**/*.js", "!**/node_modules/**"), // no outDir: tsc writes the .js next to the .ts
                ["sourceMaps"] = true,
                ["skipFiles"] = new JsonArray("<node_internals>/**"),
                ["cwd"] = "${workspaceFolder}",
                ["console"] = "internalConsole",
            };
            // js-debug only reads source maps under ${workspaceFolder} by default: an outDir outside the folder ("../build" in a monorepo
            // package, an absolute path) must be allowed explicitly, or no map is found and .ts breakpoints never bind.
            if (layout.OutDir is { Length: > 0 } outside && IsOutsideFolder(outside))
                ts["resolveSourceMapLocations"] = new JsonArray("${workspaceFolder}/**", InFolder(outside) + "/**", "!**/node_modules/**");
            configs.Add(ts);
        }
        configs.Add(new JsonObject
        {
            ["name"] = "Node.js: Launch Program",
            ["type"] = PrimaryType,
            ["request"] = "launch",
            ["program"] = InFolder(layout.JavaScriptEntry),
            ["skipFiles"] = new JsonArray("<node_internals>/**"),
            ["cwd"] = "${workspaceFolder}",
            ["console"] = "internalConsole",
        });
        configs.Add(new JsonObject
        {
            ["name"] = "Node.js: Attach (--inspect)",
            ["type"] = PrimaryType,
            ["request"] = "attach",
            ["port"] = 9229,
            ["skipFiles"] = new JsonArray("<node_internals>/**"),
        });
        return [.. configs.Select(c => DebugConfiguration.FromJson(JsonSerializer.SerializeToElement(c), folder))];
    }

    /// <summary>
    /// The <c>tsc: build</c> task the TypeScript template's preLaunchTask refers to (only when the folder has TypeScript). A PROCESS task: tsc
    /// gets the tsconfig path as one argument, so a folder whose path contains <c>$</c>, quotes or spaces is never re-read by a shell. (Windows
    /// keeps a shell task: tsc is a <c>.cmd</c> script there.)
    /// </summary>
    public async ValueTask<IReadOnlyList<(string RelativePath, string Content)>> ProvideInitialFilesAsync(Uri? folder, CancellationToken ct)
    {
        var layout = await InspectFolderAsync(folder, ct);
        if (layout.TypeScriptEntry is null) return [];
        var project = layout.HasTsConfig ? "${workspaceFolder}/tsconfig.json" : "${workspaceFolder}";
        var type = OperatingSystem.IsWindows() ? "shell" : "process";
        var tasks = $$"""
            {
                "version": "2.0.0",
                "tasks": [
                    {
                        "label": "tsc: build",
                        "type": "{{type}}",
                        "command": "tsc",
                        "args": ["-p", "{{project}}"],
                        "problemMatcher": "$tsc",
                        "group": { "kind": "build", "isDefault": true }
                    }
                ]
            }

            """;
        return [("tasks.json", tasks)];
    }

    /// <param name="OutDir">tsc's outDir as written (relative to the folder, possibly "../x" or absolute); null or empty: next to the sources.</param>
    internal sealed record FolderLayout(string? TypeScriptEntry, string? OutDir, string JavaScriptEntry, bool HasTsConfig);

    /// <summary>
    /// A path from tsconfig.json or package.json as the file means it, relative to the folder: only leading <c>./</c> segments and trailing
    /// slashes go. <c>../build</c>, <c>.out</c> and absolute paths keep their meaning (trimming every leading '.' and '/' turned them into
    /// other directories, so the generated outFiles matched nothing).
    /// </summary>
    internal static string CleanPath(string value)
    {
        var v = value.Trim().Replace('\\', '/');
        while (v.StartsWith("./", StringComparison.Ordinal)) v = v[2..].TrimStart('/');
        if (v == ".") return "";
        return v.Length > 1 ? v.TrimEnd('/') : v;
    }

    private static bool IsAbsolute(string path) => path.StartsWith('/') || (path.Length > 1 && path[1] == ':') || Path.IsPathRooted(path);

    /// <summary>A launch.json path for <paramref name="path"/>: under <c>${workspaceFolder}</c> when relative, as is when absolute.</summary>
    internal static string InFolder(string path) => IsAbsolute(path) ? path : "${workspaceFolder}/" + path;

    private static bool IsOutsideFolder(string path) => IsAbsolute(path) || path == ".." || path.StartsWith("../", StringComparison.Ordinal);

    /// <summary>Guesses the entry points and tsc's outDir from the folder (tsconfig.json, package.json "main", src/index.ts…).</summary>
    internal async Task<FolderLayout> InspectFolderAsync(Uri? folder, CancellationToken ct)
    {
        if (folder is not { IsFile: true } || _p.Workspace is not { } ws) return new FolderLayout(null, "out", "index.js", false);
        var root = folder.ToString().TrimEnd('/');
        Uri UriOf(string relative) => IsAbsolute(relative) ? new Uri(Path.GetFullPath(relative)) : new Uri(root + "/" + relative);
        async Task<string?> ReadAsync(string relative)
        {
            try
            {
                var uri = UriOf(relative);
                return await ws.FileSystem.StatAsync(uri, ct) is { Type: FileType.File } ? Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException) { return null; }
        }
        async Task<bool> ExistsAsync(string relative)
        {
            try { return await ws.FileSystem.StatAsync(UriOf(relative), ct) is { Type: FileType.File }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException) { return false; }
        }

        var tsconfig = await ReadAsync("tsconfig.json");
        string? outDir = "out";
        string? rootDir = null;
        if (tsconfig is not null)
        {
            outDir = null; // tsc without an outDir writes each .js next to its .ts
            try
            {
                var options = JsonNode.Parse(tsconfig, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["compilerOptions"];
                if (options?["outDir"] is JsonValue o && o.TryGetValue<string>(out var od)) outDir = CleanPath(od);
                if (options?["rootDir"] is JsonValue r && r.TryGetValue<string>(out var rd)) rootDir = CleanPath(rd);
            }
            catch (JsonException) { outDir = "out"; /* keep the defaults */ }
        }

        string? tsEntry = null;
        var dirs = new List<string>();
        if (rootDir is { Length: > 0 }) dirs.Add(rootDir + "/");
        dirs.AddRange(["src/", ""]);
        foreach (var dir in dirs.Distinct())
            foreach (var name in new[] { "index.ts", "main.ts", "app.ts", "server.ts" })
                if (tsEntry is null && await ExistsAsync(dir + name)) tsEntry = dir + name;
        if (tsEntry is null && tsconfig is not null) tsEntry = (rootDir is { Length: > 0 } ? rootDir + "/" : "src/") + "index.ts";

        var jsEntry = "index.js";
        if (await ReadAsync("package.json") is { } package)
        {
            try
            {
                if (JsonNode.Parse(package)?["main"] is JsonValue m && m.TryGetValue<string>(out var main) && CleanPath(main) is { Length: > 0 } clean) jsEntry = clean;
            }
            catch (JsonException) { }
        }
        return new FolderLayout(tsEntry, outDir, jsEntry, tsconfig is not null);
    }
}
