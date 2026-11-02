using System.Text.Json;
using CsCode.Core.Build;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Build.CMake;

/// <summary>
/// The CMake integration's user surface, started by the shell once its window exists (<see cref="Start"/>): the <c>cmake.*</c> commands (VS
/// Code CMake Tools' ids, so keybindings and launch.json files carry over), the status bar items (preset or build type, build button, launch
/// target, debug and run buttons), configure on open, and clangd pointed at the configured build directory. Every service it uses is optional.
/// Command handlers run where the command service calls them; the work they start runs on the service's lane.
/// </summary>
public sealed class CMakeHost : IDisposable
{
    public const string Configure = "cmake.configure";
    public const string CleanConfigure = "cmake.cleanConfigure";
    public const string Build = "cmake.build";
    public const string BuildAll = "cmake.buildAll";
    public const string BuildWithTarget = "cmake.buildWithTarget";
    public const string SetBuildTarget = "cmake.setDefaultTarget";
    public const string Clean = "cmake.clean";
    public const string CleanRebuild = "cmake.cleanRebuild";
    public const string Stop = "cmake.stop";
    public const string SelectConfigurePreset = "cmake.selectConfigurePreset";
    public const string SelectBuildPreset = "cmake.selectBuildPreset";
    public const string SelectBuildType = "cmake.setVariant";
    public const string SelectLaunchTarget = "cmake.selectLaunchTarget";
    public const string RunTarget = "cmake.launchTarget";
    public const string DebugTarget = "cmake.debugTarget";
    public const string LaunchTargetPath = "cmake.launchTargetPath";
    public const string LaunchTargetDirectory = "cmake.launchTargetDirectory";
    public const string LaunchTargetFilename = "cmake.launchTargetFilename";
    public const string BuildDirectory = "cmake.buildDirectory";
    public const string BuildType = "cmake.buildType";
    public const string ShowOutput = "cmake.showOutput";
    private const string Category = "CMake";

    private readonly IServiceProvider _sp;
    private readonly CMakeService _cmake;
    private readonly DisposableStore _store = new();
    private readonly ClangdCompileCommands _clangd;
    private readonly CFileCommands _files;
    private readonly HashSet<string> _askedToConfigure = new(StringComparer.Ordinal);
    private IStatusBarService? _status;
    private readonly List<(string Id, IDisposable Token)> _itemTokens = [];
    private readonly object _items = new();
    private string _itemsKey = "";
    private bool _started;

    public CMakeHost(IServiceProvider services, CMakeService cmake)
    {
        _sp = services;
        _cmake = cmake;
        _clangd = new ClangdCompileCommands(services.GetService<ILanguageServerManager>(), () => Config?.Get<bool?>(CMakeSettings.PointClangd) ?? true);
        _store.Add(_clangd);
        _files = new CFileCommands(services, new CMakeProcesses(services.GetService<Core.Diagnostics.IMemoryMonitor>()), Environment.GetEnvironmentVariable);
        _store.Add(_files);
    }

    private IConfigurationService? Config => _sp.GetService<IConfigurationService>();
    private IWindowService? Window => _sp.GetService<IWindowService>();

    /// <summary>Registers the commands and status bar items and, once the folders were looked at, offers to configure. Idempotent.</summary>
    public IDisposable Start()
    {
        if (_started) return Disposable.Empty;
        _started = true;
        if (_sp.GetService<ICommandService>() is { } commands)
        {
            RegisterCommands(commands);
            _files.Register(commands); // C without CMake: Build and Run Active File, Create compile_flags.txt
        }
        _status = _sp.GetService<IStatusBarService>();
        _store.Add(_cmake.ViewsChanged.Subscribe(OnViewsChanged));
        OnViewsChanged(_cmake.Views);
        _ = _cmake.Detected.ContinueWith(t => ConfigureOnOpenAsync(), TaskScheduler.Default).Unwrap();
        _store.Add(_cmake.ProjectsChanged.Subscribe(projects => _ = ConfigureOnOpenAsync()));
        return this;
    }

    // ------------------------------------------------------------------------------------------------ commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<CommandInvocation, Task<object?>> run) =>
            _store.Add(commands.Register(id, async inv => await run(inv).ConfigureAwait(false), new CommandMetadata(id, title, Category)));

        Reg(Configure, "CMake: Configure", async _ => await ConfigureCommandAsync(fresh: false).ConfigureAwait(false));
        Reg(CleanConfigure, "CMake: Delete Cache and Reconfigure", async _ => await ConfigureCommandAsync(fresh: true).ConfigureAwait(false));
        Reg(Build, "CMake: Build", async _ => await BuildCommandAsync(null).ConfigureAwait(false));
        Reg(BuildAll, "CMake: Build All", async _ => await BuildCommandAsync([]).ConfigureAwait(false));
        Reg(BuildWithTarget, "CMake: Build Target…", async _ =>
            await PickTargetAsync("Build Target", includeAll: true).ConfigureAwait(false) is { } t ? await BuildCommandAsync(t.Length == 0 ? [] : [t]).ConfigureAwait(false) : null);
        Reg(SetBuildTarget, "CMake: Set Build Target", async _ =>
        {
            if (await ProjectAsync().ConfigureAwait(false) is not { } view || await PickTargetAsync("Set Build Target", includeAll: true).ConfigureAwait(false) is not { } t) return null;
            await _cmake.SetBuildTargetAsync(view.Project.SourceDirectory, t.Length == 0 ? null : t).ConfigureAwait(false);
            return t;
        });
        Reg(Clean, "CMake: Clean", async _ => await ProjectAsync().ConfigureAwait(false) is { } v ? Report(await _cmake.CleanAsync(v.Project.SourceDirectory).ConfigureAwait(false)) : null);
        Reg(CleanRebuild, "CMake: Clean Rebuild", async _ =>
        {
            if (await ProjectAsync().ConfigureAwait(false) is not { } v) return null;
            var clean = await _cmake.CleanAsync(v.Project.SourceDirectory).ConfigureAwait(false);
            return clean.ExitCode is null ? null : await BuildCommandAsync(null).ConfigureAwait(false);
        });
        Reg(Stop, "CMake: Cancel Build", async _ =>
        {
            foreach (var v in _cmake.Views.Where(v => v.Activity is not null)) await _cmake.StopAsync(v.Project.SourceDirectory).ConfigureAwait(false);
            return null;
        });
        Reg(SelectConfigurePreset, "CMake: Select Configure Preset", async _ => await PickConfigurePresetAsync().ConfigureAwait(false));
        Reg(SelectBuildPreset, "CMake: Select Build Preset", async _ => await PickBuildPresetAsync().ConfigureAwait(false));
        Reg(SelectBuildType, "CMake: Select Build Type", async _ => await PickBuildTypeAsync().ConfigureAwait(false));
        Reg(SelectLaunchTarget, "CMake: Select Launch Target", async _ => await PickLaunchTargetAsync().ConfigureAwait(false));
        Reg(RunTarget, "CMake: Run Without Debugging", async _ => await RunAsync(debug: false).ConfigureAwait(false));
        Reg(DebugTarget, "CMake: Debug", async _ => await RunAsync(debug: true).ConfigureAwait(false));
        Reg(LaunchTargetPath, "CMake: Get Launch Target Path", async _ => await LaunchTargetAsync(build: true).ConfigureAwait(false) is { } t ? t.Executable : null);
        Reg(LaunchTargetDirectory, "CMake: Get Launch Target Directory", async _ =>
            await LaunchTargetAsync(build: false).ConfigureAwait(false) is { Executable: { } exe } ? Path.GetDirectoryName(exe) : null);
        Reg(LaunchTargetFilename, "CMake: Get Launch Target Filename", async _ =>
            await LaunchTargetAsync(build: false).ConfigureAwait(false) is { Executable: { } exe } ? Path.GetFileName(exe) : null);
        Reg(BuildDirectory, "CMake: Get Build Directory", async _ => (await ProjectAsync(ask: false).ConfigureAwait(false))?.Project.BuildDirectory);
        Reg(BuildType, "CMake: Get Build Type", async _ => (await ProjectAsync(ask: false).ConfigureAwait(false))?.Project.Configuration);
        Reg(ShowOutput, "CMake: Show Output", async inv =>
        {
            _ = _cmake.Channel; // creates the channel, so the Output window lists it
            if (_sp.GetService<IViewActivator>() is { } views) await views.ShowViewAsync("output").ConfigureAwait(false);
            return null;
        });
    }

    /// <summary>The project a command acts on: the only one, else the one of the active editor's file, else one the user picks.</summary>
    private async Task<CMakeProjectView?> ProjectAsync(bool ask = true)
    {
        await _cmake.Detected.ConfigureAwait(false);
        var views = _cmake.Views;
        if (views.Count <= 1)
        {
            if (views.Count == 0 && ask) await Notify(MessageSeverity.Info, "No CMake project is open: no workspace folder has a CMakeLists.txt at its root (or at cmake.sourceDirectory).").ConfigureAwait(false);
            return views.FirstOrDefault();
        }
        if (_sp.GetService<INavigationService>()?.ActiveDocument?.Uri is { IsFile: true } active && _cmake.ProjectFor(active.LocalPath) is { } p)
            return views.FirstOrDefault(v => v.Project.SourceDirectory == p.SourceDirectory);
        if (!ask || Window is not { } window) return views[0];
        var picked = await window.ShowQuickPickAsync([.. views.Select(v => new QuickPickItem(v.Project.Name, v.Project.SourceDirectory, Tag: v))],
            new QuickPickOptions(Title: "CMake project")).ConfigureAwait(false);
        return picked?.Tag as CMakeProjectView;
    }

    private async Task<object?> ConfigureCommandAsync(bool fresh)
    {
        if (await ProjectAsync().ConfigureAwait(false) is not { } view) return null;
        if (!await EnsurePresetAsync(view).ConfigureAwait(false)) return null;
        var result = fresh ? await _cmake.CleanConfigureAsync(view.Project.SourceDirectory).ConfigureAwait(false)
            : await _cmake.ConfigureAsync(view.Project).ConfigureAwait(false);
        if (result is null) await NotifyFailure($"CMake could not configure {view.Project.Name}.").ConfigureAwait(false);
        return result;
    }

    /// <summary>In presets mode a configure preset must be chosen first: asks for one when none is.</summary>
    private async Task<bool> EnsurePresetAsync(CMakeProjectView view)
    {
        if (!view.UsesPresets || view.Project.ConfigurePreset is not null) return true;
        if (view.PresetsError is { } error)
        {
            await Notify(MessageSeverity.Error, error).ConfigureAwait(false);
            return false;
        }
        return await PickConfigurePresetAsync(view).ConfigureAwait(false) is not null;
    }

    private async Task<object?> BuildCommandAsync(IReadOnlyList<string>? targets)
    {
        if (await ProjectAsync().ConfigureAwait(false) is not { } view) return null;
        if (!await EnsurePresetAsync(view).ConfigureAwait(false)) return null;
        targets ??= view.BuildTarget is { } t ? [t] : [];
        return Report(await _cmake.BuildAsync(view.Project, targets).ConfigureAwait(false));
    }

    private CMakeBuildResult Report(CMakeBuildResult result)
    {
        if (result.ExitCode is null) return result; // cancelled: nothing to say
        if (result.Error is { } error) _ = NotifyFailure(error);
        else if (!result.Succeeded) _ = NotifyFailure(result.Errors > 0
            ? $"The build failed with {result.Errors} error{(result.Errors == 1 ? "" : "s")} (see the Error List)."
            : $"The build failed with exit code {result.ExitCode}: see the CMake output.");
        return result;
    }

    // ------------------------------------------------------------------------------------------------ pickers

    private async Task<string?> PickConfigurePresetAsync(CMakeProjectView? view = null)
    {
        view ??= await ProjectAsync().ConfigureAwait(false);
        if (view is null || Window is not { } window) return null;
        if (!view.UsesPresets)
        {
            await Notify(MessageSeverity.Info, $"{view.Project.Name} has no CMakePresets.json or CMakeUserPresets.json (or cmake.useCMakePresets is \"never\").").ConfigureAwait(false);
            return null;
        }
        if (view.ConfigurePresets.Count == 0)
        {
            await Notify(MessageSeverity.Warning, view.PresetsError ?? $"{view.Project.Name}'s presets define no visible configure preset for this host.").ConfigureAwait(false);
            return null;
        }
        var picked = await window.ShowQuickPickAsync(
            [.. view.ConfigurePresets.Select(p => new QuickPickItem(p.DisplayName, p.Name == p.DisplayName ? p.Description : p.Name, p.BinaryDir, p.Name == view.Project.ConfigurePreset, p.Name))],
            new QuickPickOptions(Title: "Select Configure Preset", Placeholder: view.Project.Name)).ConfigureAwait(false);
        if (picked?.Tag is not string name) return null;
        await _cmake.SelectConfigurePresetAsync(view.Project.SourceDirectory, name).ConfigureAwait(false);
        return name;
    }

    private async Task<string?> PickBuildPresetAsync()
    {
        if (await ProjectAsync().ConfigureAwait(false) is not { } view || Window is not { } window) return null;
        var presets = view.BuildPresets.Where(b => b.ConfigurePreset is null || b.ConfigurePreset == view.Project.ConfigurePreset).ToList();
        var items = new List<QuickPickItem> { new("[Default]", "cmake --build <build directory>", Picked: view.Project.BuildPreset is null, Tag: "") };
        items.AddRange(presets.Select(p => new QuickPickItem(p.DisplayName, p.Description ?? p.Name, p.Configuration, p.Name == view.Project.BuildPreset, p.Name)));
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Select Build Preset")).ConfigureAwait(false);
        if (picked?.Tag is not string name) return null;
        await _cmake.SelectBuildPresetAsync(view.Project.SourceDirectory, name.Length == 0 ? null : name).ConfigureAwait(false);
        return name;
    }

    private async Task<string?> PickBuildTypeAsync()
    {
        if (await ProjectAsync().ConfigureAwait(false) is not { } view || Window is not { } window) return null;
        if (view.UsesPresets)
        {
            // as in CMake Tools: with presets the configure preset decides the build type
            return await PickConfigurePresetAsync(view).ConfigureAwait(false);
        }
        var picked = await window.ShowQuickPickAsync([.. CMakeSettings.BuildTypes.Select(t => new QuickPickItem(t, Picked: t == view.BuildType, Tag: t))],
            new QuickPickOptions(Title: "Select Build Type")).ConfigureAwait(false);
        if (picked?.Tag is not string type) return null;
        await _cmake.SetBuildTypeAsync(view.Project.SourceDirectory, type).ConfigureAwait(false);
        // a single-config build directory takes the new type at its next configure
        if (view.Project.State == CMakeProjectState.Configured) _ = ConfigureAfterChangeAsync(view.Project.SourceDirectory);
        return type;
    }

    private async Task ConfigureAfterChangeAsync(string sourceDir)
    {
        if (_cmake.Projects.FirstOrDefault(p => p.SourceDirectory == sourceDir) is { } project) await _cmake.ConfigureAsync(project).ConfigureAwait(false);
    }

    /// <summary>A target of the project; "" stands for all (when <paramref name="includeAll"/>).</summary>
    private async Task<string?> PickTargetAsync(string title, bool includeAll)
    {
        if (await ConfiguredAsync().ConfigureAwait(false) is not { } view || Window is not { } window) return null;
        var items = new List<QuickPickItem>();
        if (includeAll) items.Add(new QuickPickItem("all", "the default target", Tag: ""));
        items.AddRange(view.Project.Targets.Where(t => t.Type != CMakeTargetType.InterfaceLibrary)
            .Select(t => new QuickPickItem(t.Name, Describe(t.Type), t.Artifacts.FirstOrDefault(), t.Name == view.BuildTarget, t.Name)));
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: title)).ConfigureAwait(false);
        return picked?.Tag as string;
    }

    private async Task<CMakeTarget?> PickLaunchTargetAsync(CMakeProjectView? view = null)
    {
        view ??= await ConfiguredAsync().ConfigureAwait(false);
        if (view is null || Window is not { } window) return null;
        var executables = view.Project.Targets.Where(t => t.Executable is not null).ToList();
        if (executables.Count == 0)
        {
            await Notify(MessageSeverity.Info, $"{view.Project.Name} has no executable target.").ConfigureAwait(false);
            return null;
        }
        var picked = await window.ShowQuickPickAsync(
            [.. executables.Select(t => new QuickPickItem(t.Name, Path.GetRelativePath(view.Project.BuildDirectory, t.Executable!), Picked: t.Name == view.LaunchTarget, Tag: t))],
            new QuickPickOptions(Title: "Select Launch Target")).ConfigureAwait(false);
        if (picked?.Tag is not CMakeTarget target) return null;
        await _cmake.SetLaunchTargetAsync(view.Project.SourceDirectory, target.Name).ConfigureAwait(false);
        return target;
    }

    /// <summary>The project, configured first when it never was (the targets come from the configure).</summary>
    private async Task<CMakeProjectView?> ConfiguredAsync()
    {
        if (await ProjectAsync().ConfigureAwait(false) is not { } view) return null;
        if (view.Project.State == CMakeProjectState.Configured) return view;
        if (!await EnsurePresetAsync(view).ConfigureAwait(false)) return null;
        if (await _cmake.ConfigureAsync(view.Project).ConfigureAwait(false) is null)
        {
            await NotifyFailure($"CMake could not configure {view.Project.Name}.").ConfigureAwait(false);
            return null;
        }
        return _cmake.Views.FirstOrDefault(v => v.Project.SourceDirectory == view.Project.SourceDirectory);
    }

    /// <summary>The launch target (asked for when none is chosen or it is gone), built first when <paramref name="build"/> and cmake.buildBeforeRun.</summary>
    private async Task<CMakeTarget?> LaunchTargetAsync(bool build)
    {
        if (await ConfiguredAsync().ConfigureAwait(false) is not { } view) return null;
        var target = view.Project.Targets.FirstOrDefault(t => t.Name == view.LaunchTarget && t.Executable is not null);
        var executables = view.Project.Targets.Where(t => t.Executable is not null).ToList();
        if (target is null && executables.Count == 1)
        {
            target = executables[0];
            await _cmake.SetLaunchTargetAsync(view.Project.SourceDirectory, target.Name).ConfigureAwait(false);
        }
        target ??= await PickLaunchTargetAsync(view).ConfigureAwait(false);
        if (target is null || !build || !(Config?.Get<bool?>(CMakeSettings.BuildBeforeRun) ?? true)) return target;
        var result = Report(await _cmake.BuildAsync(view.Project, [target.Name]).ConfigureAwait(false));
        if (!result.Succeeded) return null;
        // the build may have reconfigured: take the target's current artifact
        return _cmake.Projects.FirstOrDefault(p => p.SourceDirectory == view.Project.SourceDirectory)?.Targets.FirstOrDefault(t => t.Name == target.Name) ?? target;
    }

    private async Task<object?> RunAsync(bool debug)
    {
        if (await LaunchTargetAsync(build: true).ConfigureAwait(false) is not { Executable: { } exe } target) return null;
        var cwd = Path.GetDirectoryName(exe)!;
        if (debug)
        {
            if (_sp.GetService<IDebugService>() is not { } debugger) return null;
            var config = DebugConfigurationFor(target, cwd, Config, _cmake.Projects.FirstOrDefault(p => p.Targets.Contains(target))?.SourceDirectory);
            return await debugger.StartAsync(config).ConfigureAwait(false);
        }
        if (_sp.GetService<ITerminalService>() is not { } terminals) return null;
        var terminal = await terminals.CreateAsync(new TerminalCreateOptions
        {
            Name = "CMake: " + target.Name,
            ShellPath = exe,
            ShellArgs = DebugArgs(Config),
            Cwd = cwd,
            IsTransient = true,
            ReuseWhenIdle = true,
        }).ConfigureAwait(false);
        await terminals.ShowAsync(terminal).ConfigureAwait(false);
        return terminal.ProcessId;
    }

    /// <summary>The debugger <c>cscode.cmake.debugger</c> names (as a debug type): <c>lldb</c> (CodeLLDB), <c>lldb-dap</c> or <c>gdb</c>; unset: CodeLLDB on macOS, GDB elsewhere.</summary>
    internal static string DebugType(string? setting, bool macOS)
    {
        var chosen = setting?.Trim().ToLowerInvariant() is { Length: > 0 } s ? s : macOS ? "lldb" : "gdb";
        return chosen switch { "codelldb" or "lldb" => "lldb", "lldb-dap" or "lldbdap" => "lldb-dap", _ => "gdb" };
    }

    /// <summary>The source languages CodeLLDB is told about: <c>cpp</c> when the target has C++ sources, else <c>c</c>.</summary>
    internal static string SourceLanguage(CMakeTarget target) =>
        target.Sources.Any(s => Path.GetExtension(s).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx" or ".c++" or ".mm") ? "cpp" : "c";

    /// <summary>The configuration Debug Target starts: the program and its directory, the chosen debugger, and <c>cmake.debugConfig</c> on top.</summary>
    internal static DebugConfiguration DebugConfigurationFor(CMakeTarget target, string cwd, IConfigurationService? config, string? folder)
    {
        var type = DebugType(CMakeSettings.String(config, CMakeSettings.Debugger), OperatingSystem.IsMacOS());
        var props = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(type),
            ["request"] = JsonSerializer.SerializeToElement("launch"),
            ["name"] = JsonSerializer.SerializeToElement($"CMake: Debug {target.Name}"),
            ["program"] = JsonSerializer.SerializeToElement(target.Executable),
            ["args"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
            ["cwd"] = JsonSerializer.SerializeToElement(cwd),
        };
        if (type == "lldb") props["sourceLanguages"] = JsonSerializer.SerializeToElement(new[] { SourceLanguage(target) });
        if (config?.GetRaw(CMakeSettings.DebugConfig) is { ValueKind: JsonValueKind.Object } extra)
            foreach (var p in extra.EnumerateObject())
                if (p.Name is not ("type" or "request" or "program")) props[p.Name] = p.Value.Clone();
        Uri? folderUri = folder is null ? null : new Uri(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        return new DebugConfiguration(type, "launch", $"CMake: Debug {target.Name}", props, folderUri);
    }

    private static IReadOnlyList<string> DebugArgs(IConfigurationService? config) =>
        config?.GetRaw(CMakeSettings.DebugConfig) is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    private static string Describe(CMakeTargetType type) => type switch
    {
        CMakeTargetType.Executable => "executable",
        CMakeTargetType.StaticLibrary => "static library",
        CMakeTargetType.SharedLibrary => "shared library",
        CMakeTargetType.ModuleLibrary => "module library",
        CMakeTargetType.ObjectLibrary => "object library",
        CMakeTargetType.InterfaceLibrary => "interface library",
        _ => "utility",
    };

    // ------------------------------------------------------------------------------------------------ configure on open

    private async Task ConfigureOnOpenAsync()
    {
        foreach (var view in _cmake.Views)
        {
            var source = view.Project.SourceDirectory;
            if (_cmake.ConfiguredOnRequest(source)) continue; // New Project configured it with its preset: neither asked nor configured again
            lock (_askedToConfigure) if (view.Project.State is CMakeProjectState.Configuring || !_askedToConfigure.Add(source)) continue;
            var setting = Config?.Get<bool?>(CMakeSettings.ConfigureOnOpen);
            if (setting == false) continue;
            if (setting is null)
            {
                if (view.Project.State == CMakeProjectState.Configured || Window is not { } window) continue;
                var answer = await window.ShowMessageAsync(MessageSeverity.Info, $"Configure the CMake project '{view.Project.Name}'?",
                    [new("Configure"), new("Always"), new("Never"), new("Not Now", IsCloseAffordance: true)]).ConfigureAwait(false);
                if (answer is "Always" or "Never" && Config is { } config)
                    await config.UpdateAsync(CMakeSettings.ConfigureOnOpen, answer == "Always", ConfigurationTarget.User).ConfigureAwait(false);
                if (answer is not ("Configure" or "Always")) continue;
            }
            if (!await EnsurePresetAsync(view).ConfigureAwait(false)) continue;
            if (_cmake.Projects.FirstOrDefault(p => p.SourceDirectory == source) is { } current) await _cmake.ConfigureAsync(current).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------ status bar and clangd

    /// <summary>
    /// Shows the latest views (any thread: the lane's events and <see cref="Start"/>'s first call may race). Each call reads
    /// <see cref="CMakeService.Views"/> under the lock rather than the views it was handed, so the last one to run shows the newest state; with
    /// the views it was handed, Start's first call could land after the lane's and clear the items until the next change.
    /// </summary>
    private void OnViewsChanged(IReadOnlyList<CMakeProjectView> _)
    {
        lock (_items)
        {
            var views = _cmake.Views;
            _clangd.Update(views.Select(v => v.Project).FirstOrDefault(p => p.State == CMakeProjectState.Configured && File.Exists(Path.Combine(p.BuildDirectory, "compile_commands.json")))?.BuildDirectory);
            if (_status is null) return;
            // the items follow the project of the active editor; with one project, that one
            var view = views.Count == 1 ? views[0]
                : _sp.GetService<INavigationService>()?.ActiveDocument?.Uri is { IsFile: true } u && _cmake.ProjectFor(u.LocalPath) is { } p
                    ? views.FirstOrDefault(v => v.Project.SourceDirectory == p.SourceDirectory) ?? views.FirstOrDefault()
                    : views.FirstOrDefault();
            var models = view is null ? [] : StatusItems(view);
            var key = string.Join('\u0001', models.Select(m => m.Id + "\u0002" + m.Text + "\u0002" + m.Tooltip + "\u0002" + m.CommandId));
            if (key == _itemsKey) return;
            _itemsKey = key;
            // Set replaces an item by id: set the new ones first, then remove only the ids that are gone
            var next = models.Select(m => (m.Id, Token: _status.Set(m))).ToList();
            foreach (var (id, token) in _itemTokens) if (next.All(n => n.Id != id)) token.Dispose();
            _itemTokens.Clear();
            _itemTokens.AddRange(next);
        }
    }

    internal static IReadOnlyList<StatusBarItemModel> StatusItems(CMakeProjectView view)
    {
        var p = view.Project;
        var config = view.UsesPresets ? p.ConfigurePreset ?? "No Configure Preset" : view.BuildType;
        var state = p.State switch
        {
            CMakeProjectState.Configuring => "$(sync~spin) ",
            CMakeProjectState.ConfigureFailed => "$(warning) ",
            _ => "",
        };
        var configTip = view.UsesPresets ? $"CMake {p.Name}: configure preset (click to change)" : $"CMake {p.Name}: build type (click to change)";
        var items = new List<StatusBarItemModel>
        {
            new("cmake.config", $"{state}$(tools) CMake: [{config}]", configTip + (p.State == CMakeProjectState.ConfigureFailed ? " — the last configure failed" : ""),
                view.UsesPresets ? SelectConfigurePreset : SelectBuildType, StatusBarAlignment.Left, 24),
            view.Activity is { } activity
                ? new("cmake.build", $"$(sync~spin) {activity}…", "Click to cancel", Stop, StatusBarAlignment.Left, 23)
                : new("cmake.build", $"$(gear) Build{(view.BuildTarget is { } bt ? $" [{bt}]" : "")}", $"Build {view.BuildTarget ?? "all"} (CMake: Build)", Build, StatusBarAlignment.Left, 23),
        };
        if (p.Targets.Any(t => t.Executable is not null) || view.LaunchTarget is not null)
        {
            items.Add(new("cmake.launchTarget", $"[{view.LaunchTarget ?? "Select Launch Target"}]", "The target Run and Debug start (click to change)", SelectLaunchTarget, StatusBarAlignment.Left, 22));
            items.Add(new("cmake.debugTarget", "$(debug-alt)", "Debug the launch target", DebugTarget, StatusBarAlignment.Left, 21));
            items.Add(new("cmake.runTarget", "$(play)", "Run the launch target in the terminal", RunTarget, StatusBarAlignment.Left, 20));
        }
        return items;
    }

    // ------------------------------------------------------------------------------------------------ messages

    private async Task Notify(MessageSeverity severity, string message)
    {
        if (Window is { } window) await window.ShowMessageAsync(severity, message).ConfigureAwait(false);
    }

    private async Task NotifyFailure(string message)
    {
        if (Window is not { } window) return;
        var answer = await window.ShowMessageAsync(MessageSeverity.Error, message, [new("Show Output")]).ConfigureAwait(false);
        if (answer == "Show Output" && _sp.GetService<ICommandService>() is { } commands) await commands.ExecuteAsync(ShowOutput).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_items)
        {
            foreach (var (_, token) in _itemTokens) token.Dispose();
            _itemTokens.Clear();
            _itemsKey = "\u0000disposed";
        }
        _store.Dispose();
    }
}
