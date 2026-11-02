using CsCode.Core.Build;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Build.CMake;

/// <summary>What the commands and the status bar show for a project, beyond the Core snapshot.</summary>
internal sealed record CMakeProjectView(
    CMakeProject Project,
    bool UsesPresets,
    IReadOnlyList<ConfigurePreset> ConfigurePresets,
    IReadOnlyList<BuildPreset> BuildPresets,
    string? PresetsError,
    string BuildType,
    string? LaunchTarget,
    string? BuildTarget,
    string? Activity);

/// <summary>The selections remembered per workspace for one project (keyed by its source directory).</summary>
internal sealed record PersistedProject(string? ConfigurePreset = null, string? BuildPreset = null, string? BuildType = null, string? LaunchTarget = null, string? BuildTarget = null);

/// <summary>
/// <see cref="ICMakeService"/>: detects the workspace folders' CMake projects, configures and builds them through the cmake CLI and reads their
/// targets from the File API. All state lives on the <see cref="CMakeLane"/>; public calls only enqueue. One operation (configure, build, clean)
/// runs per project at a time, later ones wait for it; <see cref="StopAsync"/> cancels the running one (its process tree is killed). Output goes to
/// the "CMake" output channel as batches; configure problems (<c>CMake Error at …</c>) and build problems (compiler diagnostics) go to the Error
/// List under per-project owners that each run clears first. Selections (presets, build type, launch and build target) are remembered per
/// workspace in <see cref="IStateStorage"/>.
/// </summary>
public sealed class CMakeService : ICMakeService, IDisposable
{
    public const string OutputChannelName = "CMake";
    internal const string StateKey = "cmake.projects";

    private readonly CMakeLane _lane = new();
    private readonly ICMakeProcesses _processes;
    private readonly IConfigurationService? _config;
    private readonly IWorkspaceService? _workspace;
    private readonly IStateStorage? _state;
    private readonly IDiagnosticService? _diagnostics;
    private readonly IOutputService? _outputs;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _environment;
    private readonly DisposableStore _store = new();
    private readonly Emitter<IReadOnlyList<CMakeProject>> _changed = new();
    private readonly Emitter<IReadOnlyList<CMakeProjectView>> _viewsChanged = new();
    private readonly Emitter<CMakeBuildFinished> _built = new();
    private readonly List<ProjectState> _projects = []; // lane only
    private readonly List<PendingConfigure> _pending = []; // ConfigureWhenOpenedAsync's requests for projects not open yet (locked)
    private readonly HashSet<string> _configuredOnRequest = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase); // locked
    private IReadOnlyList<CMakeProject> _snapshot = [];
    private IReadOnlyList<CMakeProjectView> _views = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _detected;
    private IOutputChannel? _channel;
    private volatile bool _disposed;

    public CMakeService(IWorkspaceService? workspace, IConfigurationService? config, IStateStorage? state, IDiagnosticService? diagnostics,
        IOutputService? outputs, IDocumentManager? documents = null, IMemoryMonitor? memory = null, ILoggerFactory? loggers = null)
        : this(workspace, config, state, diagnostics, outputs, documents, new CMakeProcesses(memory), loggers, null) { }

    internal CMakeService(IWorkspaceService? workspace, IConfigurationService? config, IStateStorage? state, IDiagnosticService? diagnostics,
        IOutputService? outputs, IDocumentManager? documents, ICMakeProcesses processes, ILoggerFactory? loggers, Func<string, string?>? environment)
    {
        _workspace = workspace;
        _config = config;
        _state = state;
        _diagnostics = diagnostics;
        _outputs = outputs;
        _processes = processes;
        _logger = loggers?.CreateLogger("CsCode.CMake") ?? NullLogger.Instance;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        if (config is not null)
        {
            _store.Add(config.RegisterProperties(CMakeSettings.Properties()));
            _store.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(CMakeSettings.SourceDirectory) || e.Affects(CMakeSettings.BuildDirectory) || e.Affects(CMakeSettings.UseCMakePresets) || e.Affects(CMakeSettings.Generator))
                    _ = RedetectAsync();
            }));
        }
        if (workspace is not null) _store.Add(workspace.FoldersChanged.Subscribe(e => _ = RedetectAsync()));
        if (documents is not null) _store.Add(documents.Saved.Subscribe(e => OnSaved(e.Document.Uri)));
        _detected = RedetectAsync();
    }

    // ------------------------------------------------------------------------------------------------ ICMakeService

    public IReadOnlyList<CMakeProject> Projects => Volatile.Read(ref _snapshot);

    public IEventSource<IReadOnlyList<CMakeProject>> ProjectsChanged => _changed;

    public IEventSource<CMakeBuildFinished> BuildFinished => _built;

    public CMakeProject? ProjectFor(string path)
    {
        var full = Path.GetFullPath(path);
        return Projects.Where(p => IsUnder(full, p.SourceDirectory) || IsUnder(full, p.BuildDirectory))
            .OrderByDescending(p => p.SourceDirectory.Length).FirstOrDefault();
    }

    public ValueTask<CMakeProject?> ConfigureAsync(CMakeProject project, CancellationToken ct = default) =>
        new(OnLane(p => Enqueue(p, op => ConfigureCoreAsync(p, op, fresh: false), ct), project.SourceDirectory, (CMakeProject?)null));

    public ValueTask<CMakeBuildResult> BuildAsync(CMakeProject project, IReadOnlyList<string>? targets = null, CancellationToken ct = default) =>
        new(OnLane(p => Enqueue(p, op => BuildCoreAsync(p, targets ?? [], op), ct), project.SourceDirectory, new CMakeBuildResult(null, Error: "The project is not open.")));

    public ValueTask<CMakeProject?> ConfigureWhenOpenedAsync(string sourceDirectory, string configurePreset, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.FromResult<CMakeProject?>(null);
        var pending = new PendingConfigure(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)), configurePreset,
            new TaskCompletionSource<CMakeProject?>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        // no await before the request is recorded: a detection queued after this call (the folder opening) must find it
        _lane.Post(() =>
        {
            if (_disposed)
            {
                pending.Done.TrySetResult(null);
                return;
            }
            if (Find(pending.SourceDir) is { } open)
            {
                if (!Claim(open, pending)) return;
                Publish();
                StartPending(open, pending);
                return;
            }
            lock (_pending) _pending.Add(pending); // the next detection that finds it claims it
        });
        return new(pending.Done.Task.WaitAsync(ct));
    }

    // ------------------------------------------------------------------------------------------------ for the host

    /// <summary>Every project with its presets and selections (any thread).</summary>
    internal IReadOnlyList<CMakeProjectView> Views => Volatile.Read(ref _views);

    /// <summary>Fires (on the lane) whenever <see cref="Views"/> changes: selections, activity, state.</summary>
    internal IEventSource<IReadOnlyList<CMakeProjectView>> ViewsChanged => _viewsChanged;

    /// <summary>Completes once the folders have been looked at (tests and configure-on-open wait for it).</summary>
    internal Task Detected => _detected;

    internal Task<CMakeProject?> CleanConfigureAsync(string sourceDir, CancellationToken ct = default) =>
        OnLane(p => Enqueue(p, op => ConfigureCoreAsync(p, op, fresh: true), ct), sourceDir, (CMakeProject?)null);

    internal Task<CMakeBuildResult> CleanAsync(string sourceDir, CancellationToken ct = default) =>
        OnLane(p => Enqueue(p, op => BuildCoreAsync(p, ["clean"], op), ct), sourceDir, new CMakeBuildResult(null, Error: "The project is not open."));

    /// <summary>Cancels the project's running configure or build (its process tree is killed).</summary>
    internal Task StopAsync(string sourceDir) => OnLane(p =>
    {
        p.Current?.Cancel();
        return Task.FromResult(0);
    }, sourceDir, 0);

    internal Task SelectConfigurePresetAsync(string sourceDir, string? preset) => Update(sourceDir, p => ChooseConfigurePreset(p, preset));

    /// <summary>Whether the project was configured through <see cref="ConfigureWhenOpenedAsync"/>: configure-on-open leaves it alone (any thread).</summary>
    internal bool ConfiguredOnRequest(string sourceDir)
    {
        lock (_configuredOnRequest) return _configuredOnRequest.Contains(Path.TrimEndingDirectorySeparator(sourceDir));
    }

    private bool ChooseConfigurePreset(ProjectState p, string? preset)
    {
        if (p.ConfigurePreset == preset) return false;
        p.ConfigurePreset = preset;
        // a build preset made for another configure preset no longer applies
        if (p.BuildPreset is { } bp && p.Presets.Build.FirstOrDefault(b => b.Name == bp) is { } b && b.ConfigurePreset != preset) p.BuildPreset = null;
        Reload(p);
        return true;
    }

    internal Task SelectBuildPresetAsync(string sourceDir, string? preset) => Update(sourceDir, p =>
    {
        if (p.BuildPreset == preset) return false;
        p.BuildPreset = preset;
        return true;
    });

    internal Task SetBuildTypeAsync(string sourceDir, string buildType) => Update(sourceDir, p =>
    {
        if (p.BuildType == buildType) return false;
        p.BuildType = buildType;
        Reload(p);
        return true;
    });

    internal Task SetLaunchTargetAsync(string sourceDir, string? target) => Update(sourceDir, p =>
    {
        if (p.LaunchTarget == target) return false;
        p.LaunchTarget = target;
        return true;
    });

    internal Task SetBuildTargetAsync(string sourceDir, string? target) => Update(sourceDir, p =>
    {
        if (p.BuildTarget == target) return false;
        p.BuildTarget = target;
        return true;
    });

    /// <summary>Shows the output channel's text so far (the host reveals the Output window).</summary>
    internal IOutputChannel? Channel => _channel ??= _outputs?.GetOrCreate(OutputChannelName);

    // ------------------------------------------------------------------------------------------------ state

    private sealed class ProjectState(string folder, string sourceDir)
    {
        public string Folder { get; } = folder;
        public string SourceDir { get; } = sourceDir;
        public CMakePresets Presets { get; set; } = new();
        public bool UsePresets { get; set; }
        public string? ConfigurePreset { get; set; }
        public string? BuildPreset { get; set; }
        public string BuildType { get; set; } = CMakeSettings.DefaultBuildType;
        public string? LaunchTarget { get; set; }
        public string? BuildTarget { get; set; }
        public string BuildDir { get; set; } = "";
        public CMakeProjectState State { get; set; }
        public FileApiReply? Reply { get; set; }
        public long Generation { get; set; }
        public string? Activity { get; set; }
        public Task Busy { get; set; } = Task.CompletedTask;
        public CancellationTokenSource? Current { get; set; }
        public CMakeProject? Snapshot { get; set; }

        public ConfigurePreset? SelectedConfigurePreset => UsePresets && ConfigurePreset is { } n ? Presets.Configure.FirstOrDefault(c => c.Name == n) : null;
        public BuildPreset? SelectedBuildPreset => UsePresets && BuildPreset is { } n ? Presets.Build.FirstOrDefault(b => b.Name == n) : null;

        /// <summary>What <c>--config</c> and <c>ctest -C</c> take: a build preset's configuration, the preset's build type, else the chosen build type.</summary>
        public string? Configuration => UsePresets
            ? SelectedBuildPreset?.Configuration ?? SelectedConfigurePreset?.BuildType ?? Reply?.Configurations.FirstOrDefault(c => c.Length > 0)
            : BuildType;
    }

    private Task<T> OnLane<T>(Func<ProjectState, Task<T>> work, string sourceDir, T missing) => _lane.RunAsync(async () =>
    {
        await _detected;
        var p = Find(sourceDir);
        return p is null ? missing : await work(p);
    });

    private Task Update(string sourceDir, Func<ProjectState, bool> change) => OnLane(p =>
    {
        if (change(p))
        {
            Persist(p);
            Publish();
        }
        return Task.FromResult(0);
    }, sourceDir, 0);

    private ProjectState? Find(string sourceDir)
    {
        var full = Path.GetFullPath(sourceDir);
        return _projects.FirstOrDefault(p => PathsEqual(p.SourceDir, full));
    }

    /// <summary>Runs <paramref name="op"/> after the project's running operation (lane); its token is cancelled by <see cref="StopAsync"/> or <paramref name="ct"/>.</summary>
    private Task<T> Enqueue<T>(ProjectState p, Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        var previous = p.Busy;
        var task = RunAfter();
        p.Busy = task;
        return task;

        async Task<T> RunAfter()
        {
            try { await previous; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            p.Current = cts;
            try { return await op(cts.Token); }
            finally { if (ReferenceEquals(p.Current, cts)) p.Current = null; }
        }
    }

    // ------------------------------------------------------------------------------------------------ detection

    private Task RedetectAsync() => _lane.RunAsync(() =>
    {
        if (_disposed) return Task.CompletedTask;
        var folders = (_workspace?.Folders ?? []).Where(f => f.IsFile).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.LocalPath))).ToList();
        var persisted = _state?.Get<Dictionary<string, PersistedProject>>(StateKey, StorageScope.Workspace) ?? [];
        var next = new List<ProjectState>();
        foreach (var folder in folders)
        {
            var configured = CMakeSettings.String(_config, CMakeSettings.SourceDirectory) ?? "${workspaceFolder}";
            var source = CMakeSettings.Substitute(configured, folder, null, null, _environment);
            if (!File.Exists(Path.Combine(source, "CMakeLists.txt"))) continue;
            var existing = _projects.FirstOrDefault(p => PathsEqual(p.SourceDir, source) && PathsEqual(p.Folder, folder));
            if (existing is null)
            {
                existing = new ProjectState(folder, source);
                if (persisted.TryGetValue(source, out var saved))
                {
                    existing.ConfigurePreset = saved.ConfigurePreset;
                    existing.BuildPreset = saved.BuildPreset;
                    existing.BuildType = saved.BuildType ?? CMakeSettings.DefaultBuildType;
                    existing.LaunchTarget = saved.LaunchTarget;
                    existing.BuildTarget = saved.BuildTarget;
                }
            }
            Reload(existing);
            next.Add(existing);
        }
        foreach (var gone in _projects.Except(next)) gone.Current?.Cancel();
        _projects.Clear();
        _projects.AddRange(next);
        var claimed = ClaimPending(next);
        Publish(); // a claimed project is first seen configuring: configure-on-open skips it
        foreach (var (p, pending) in claimed) StartPending(p, pending);
        return Task.CompletedTask;
    });

    // ------------------------------------------------------------------------------------------------ configure when opened

    /// <summary>A <see cref="ConfigureWhenOpenedAsync"/> request: the project's source directory (full, no trailing separator) and preset.</summary>
    private sealed record PendingConfigure(string SourceDir, string Preset, TaskCompletionSource<CMakeProject?> Done, CancellationToken Ct);

    private List<(ProjectState, PendingConfigure)> ClaimPending(List<ProjectState> projects)
    {
        var claimed = new List<(ProjectState, PendingConfigure)>();
        lock (_pending)
            foreach (var pending in _pending.ToList())
                if (projects.FirstOrDefault(p => PathsEqual(p.SourceDir, pending.SourceDir)) is { } p)
                {
                    _pending.Remove(pending);
                    if (Claim(p, pending)) claimed.Add((p, pending));
                }
        return claimed;
    }

    /// <summary>Selects the request's preset (remembered) and marks the project configuring, before it is published (lane). False: dropped.</summary>
    private bool Claim(ProjectState p, PendingConfigure pending)
    {
        if (pending.Ct.IsCancellationRequested)
        {
            pending.Done.TrySetCanceled(pending.Ct);
            return false;
        }
        if (!p.UsePresets || p.Presets.Configure.All(c => c.Name != pending.Preset))
        {
            Write($"[cmake] {Name(p)} has no configure preset '{pending.Preset}': it is configured as any other project.");
            pending.Done.TrySetResult(null);
            return false;
        }
        ChooseConfigurePreset(p, pending.Preset);
        Persist(p);
        p.State = CMakeProjectState.Configuring;
        p.Activity = "Configuring";
        lock (_configuredOnRequest) _configuredOnRequest.Add(p.SourceDir);
        return true;
    }

    private void StartPending(ProjectState p, PendingConfigure pending) =>
        _ = Enqueue(p, op => ConfigureCoreAsync(p, op, fresh: false), pending.Ct).ContinueWith(t =>
        {
            if (t.IsCanceled) pending.Done.TrySetCanceled();
            else if (t.Exception is { } ex) pending.Done.TrySetException(ex.InnerExceptions);
            else pending.Done.TrySetResult(t.Result);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Re-reads the presets, recomputes the build directory and reads an existing File API reply there (lane).</summary>
    private void Reload(ProjectState p)
    {
        p.Presets = CMakePresets.Load(p.SourceDir, _environment);
        var mode = CMakeSettings.String(_config, CMakeSettings.UseCMakePresets) ?? "auto";
        p.UsePresets = mode switch { "always" => true, "never" => false, _ => p.Presets.Exists };
        if (p.UsePresets && p.ConfigurePreset is { } name && p.Presets.Configure.All(c => c.Name != name)) p.ConfigurePreset = null;
        // a single visible configure preset needs no choice
        if (p.UsePresets && p.ConfigurePreset is null && p.Presets.Configure.Count == 1) p.ConfigurePreset = p.Presets.Configure[0].Name;
        var buildDir = p.SelectedConfigurePreset?.BinaryDir
            ?? CMakeSettings.Substitute(CMakeSettings.String(_config, CMakeSettings.BuildDirectory) ?? CMakeSettings.DefaultBuildDirectory, p.Folder,
                p.SelectedConfigurePreset?.BuildType ?? p.BuildType, Generator(p), _environment);
        if (!PathsEqual(buildDir, p.BuildDir))
        {
            p.BuildDir = buildDir;
            p.Reply = null;
            p.State = CMakeProjectState.NotConfigured;
        }
        if (p.State != CMakeProjectState.Configuring && File.Exists(Path.Combine(p.BuildDir, "CMakeCache.txt")) && CMakeFileApi.Read(p.BuildDir, p.Configuration) is { } reply)
        {
            if (p.Reply is null || !SameTargets(p.Reply, reply)) p.Generation++;
            p.Reply = reply;
            p.State = CMakeProjectState.Configured;
        }
    }

    private string? Generator(ProjectState p) =>
        p.SelectedConfigurePreset?.Generator ?? CMakeSettings.String(_config, CMakeSettings.Generator) ?? (CMakeTools.Find("ninja", null, _environment) is not null ? "Ninja" : null);

    private void OnSaved(Uri uri)
    {
        if (!uri.IsFile) return;
        var path = uri.LocalPath;
        var name = Path.GetFileName(path);
        var isPresets = name is "CMakePresets.json" or "CMakeUserPresets.json";
        var isList = name == "CMakeLists.txt" || name.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase);
        if (!isPresets && !isList) return;
        _lane.Post(() =>
        {
            foreach (var p in _projects.Where(p => IsUnder(path, p.SourceDir) && !IsUnder(path, p.BuildDir)).ToList())
            {
                if (isPresets)
                {
                    Reload(p);
                    Publish();
                }
                else if (p.State == CMakeProjectState.Configured && (_config?.Get<bool?>(CMakeSettings.ConfigureOnEdit) ?? true))
                {
                    _ = Enqueue(p, op => ConfigureCoreAsync(p, op, fresh: false), CancellationToken.None);
                }
            }
        });
    }

    // ------------------------------------------------------------------------------------------------ configure

    internal string? CMakeProgram => CMakeTools.Find("cmake", CMakeSettings.String(_config, CMakeSettings.CMakePath), _environment);

    /// <summary>The configure command line for <paramref name="p"/>'s current selections (pure; tests check it).</summary>
    private CMakeCommand ConfigureCommand(ProjectState p, string cmake, bool fresh)
    {
        var args = new List<string> { "-S", p.SourceDir };
        if (p.SelectedConfigurePreset is { } preset)
        {
            args.Add("--preset");
            args.Add(preset.Name);
            if (preset.BinaryDir is null) { args.Add("-B"); args.Add(p.BuildDir); }
        }
        else
        {
            args.Add("-B");
            args.Add(p.BuildDir);
            // the generator of an existing cache cannot change; cmake keeps the cached one when -G is left out
            if (Generator(p) is { } gen && (fresh || !File.Exists(Path.Combine(p.BuildDir, "CMakeCache.txt"))))
            {
                args.Add("-G");
                args.Add(gen);
                if (gen.StartsWith("Ninja", StringComparison.Ordinal) && CMakeTools.Find("ninja", null, _environment) is { } ninja && !CMakeTools.IsOnPath(ninja, _environment))
                    args.Add("-DCMAKE_MAKE_PROGRAM=" + ninja);
            }
            args.Add("-DCMAKE_BUILD_TYPE=" + p.BuildType);
        }
        if (fresh) args.Add("--fresh");
        args.Add("-DCMAKE_EXPORT_COMPILE_COMMANDS=ON");
        foreach (var (k, v) in CMakeSettings.CacheSettings(_config)) args.Add($"-D{k}={v}");
        args.AddRange(CMakeSettings.Strings(_config, CMakeSettings.ConfigureArgs));
        return new CMakeCommand(cmake, args, p.SourceDir);
    }

    internal Task<CMakeCommand?> ConfigureCommandAsync(string sourceDir, bool fresh = false) =>
        OnLane(p => Task.FromResult(CMakeProgram is { } cmake ? ConfigureCommand(p, cmake, fresh) : null), sourceDir, (CMakeCommand?)null);

    private async Task<CMakeProject?> ConfigureCoreAsync(ProjectState p, CancellationToken ct, bool fresh)
    {
        if (CMakeProgram is not { } cmake)
        {
            Write($"[cmake] cmake was not found (setting {CMakeSettings.CMakePath}, PATH, {string.Join(", ", CMakeTools.WellKnownDirectories)}).");
            p.State = CMakeProjectState.ConfigureFailed;
            Publish();
            return null;
        }
        if (p.UsePresets && p.SelectedConfigurePreset is null)
        {
            Write(p.Presets.Error ?? $"[cmake] {Name(p)}: select a configure preset first (CMake: Select Configure Preset).");
            return null;
        }
        Reload(p);
        var command = ConfigureCommand(p, cmake, fresh);
        try { CMakeFileApi.WriteQuery(p.BuildDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"[cmake] Could not create {p.BuildDir}: {ex.Message}");
            p.State = CMakeProjectState.ConfigureFailed;
            Publish();
            return null;
        }
        p.State = CMakeProjectState.Configuring;
        p.Activity = "Configuring";
        Publish();
        Write($"[cmake] Configuring {Name(p)}{(p.SelectedConfigurePreset is { } cp ? $" (preset {cp.Name})" : $" ({p.BuildType})")}");
        Write("[cmake] " + command);
        var parser = new BuildOutputParser(_diagnostics, ConfigureOwner(p), p.BuildDir, p.SourceDir);
        parser.Start();
        var exit = await RunAsync(command, parser, ct);
        parser.Complete();
        p.Activity = null;
        if (exit == 0 && CMakeFileApi.Read(p.BuildDir, p.Configuration) is { } reply)
        {
            if (p.Reply is null || !SameTargets(p.Reply, reply)) p.Generation++;
            p.Reply = reply;
            p.State = CMakeProjectState.Configured;
            Write($"[cmake] Configured {Name(p)}: {reply.Targets.Count} target{(reply.Targets.Count == 1 ? "" : "s")} in {p.BuildDir}");
        }
        else
        {
            p.State = exit is null ? (p.Reply is not null ? CMakeProjectState.Configured : CMakeProjectState.NotConfigured) : CMakeProjectState.ConfigureFailed;
            Write(exit switch
            {
                null => "[cmake] Configure cancelled.",
                0 => $"[cmake] cmake succeeded but wrote no File API reply in {CMakeFileApi.ReplyDirectory(p.BuildDir)}.",
                _ => $"[cmake] Configure failed with exit code {exit}.",
            });
        }
        Publish();
        return p.State == CMakeProjectState.Configured && exit == 0 ? p.Snapshot : null;
    }

    // ------------------------------------------------------------------------------------------------ build

    private CMakeCommand BuildCommand(ProjectState p, string cmake, IReadOnlyList<string> targets)
    {
        var args = new List<string> { "--build" };
        if (p.SelectedBuildPreset is { } bp)
        {
            args.Add("--preset");
            args.Add(bp.Name);
        }
        else
        {
            args.Add(p.BuildDir);
            if (p.Reply?.MultiConfig == true && p.Configuration is { Length: > 0 } config) { args.Add("--config"); args.Add(config); }
        }
        if (targets.Count > 0)
        {
            args.Add("--target");
            args.AddRange(targets);
        }
        if (_config?.Get<int?>(CMakeSettings.ParallelJobs) is > 0 and var jobs) { args.Add("--parallel"); args.Add(jobs.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        args.AddRange(CMakeSettings.Strings(_config, CMakeSettings.BuildArgs));
        var toolArgs = CMakeSettings.Strings(_config, CMakeSettings.BuildToolArgs);
        if (toolArgs.Count > 0) { args.Add("--"); args.AddRange(toolArgs); }
        return new CMakeCommand(cmake, args, p.SourceDir);
    }

    internal Task<CMakeCommand?> BuildCommandAsync(string sourceDir, IReadOnlyList<string> targets) =>
        OnLane(p => Task.FromResult(CMakeProgram is { } cmake ? BuildCommand(p, cmake, targets) : null), sourceDir, (CMakeCommand?)null);

    private async Task<CMakeBuildResult> BuildCoreAsync(ProjectState p, IReadOnlyList<string> targets, CancellationToken ct)
    {
        CMakeBuildResult result;
        if (p.State != CMakeProjectState.Configured || p.Reply is null)
        {
            if (await ConfigureCoreAsync(p, ct, fresh: false) is null)
            {
                result = new CMakeBuildResult(ct.IsCancellationRequested ? null : -1, Error: ct.IsCancellationRequested ? null : $"{Name(p)} could not be configured: see the CMake output.");
                Finish(p, targets, result);
                return result;
            }
        }
        var cmake = CMakeProgram!;
        var command = BuildCommand(p, cmake, targets);
        var what = targets.Count == 0 ? "all" : string.Join(", ", targets);
        p.Activity = targets is ["clean"] ? "Cleaning" : $"Building {what}";
        Publish();
        Write($"[build] {(targets is ["clean"] ? "Cleaning" : "Building")} {Name(p)} ({what})");
        Write("[build] " + command);
        var parser = new BuildOutputParser(_diagnostics, BuildOwner(p), p.BuildDir, p.SourceDir);
        parser.Start();
        var started = DateTime.UtcNow;
        var exit = await RunAsync(command, parser, ct);
        parser.Complete();
        p.Activity = null;
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        Write(exit switch
        {
            null => "[build] Build cancelled.",
            0 => $"[build] Build finished in {seconds:0.0} s{Counts(parser)}.",
            _ => $"[build] Build failed with exit code {exit} after {seconds:0.0} s{Counts(parser)}.",
        });
        // a build re-runs cmake when a CMakeLists.txt changed: pick up its new reply
        if (exit is not null && CMakeFileApi.Read(p.BuildDir, p.Configuration) is { } reply && !SameTargets(p.Reply!, reply))
        {
            p.Generation++;
            p.Reply = reply;
        }
        Publish();
        result = new CMakeBuildResult(exit, parser.Errors, parser.Warnings);
        Finish(p, targets, result);
        return result;

        static string Counts(BuildOutputParser parser) =>
            parser.Errors + parser.Warnings == 0 ? "" : $": {parser.Errors} error{(parser.Errors == 1 ? "" : "s")}, {parser.Warnings} warning{(parser.Warnings == 1 ? "" : "s")}";
    }

    private void Finish(ProjectState p, IReadOnlyList<string> targets, CMakeBuildResult result)
    {
        if (p.Snapshot is { } snapshot) _built.Fire(new CMakeBuildFinished(snapshot, targets, result));
    }

    private async Task<int?> RunAsync(CMakeCommand command, BuildOutputParser parser, CancellationToken ct)
    {
        try
        {
            // lines arrive in batches on the runner's threads: the channel takes them at once, the parser on the lane (it is not thread-safe)
            return await _processes.RunAsync(command, lines =>
            {
                Channel?.Append(string.Join('\n', lines.Select(BuildOutputParser.StripEscapes)) + "\n");
                _lane.Post(() =>
                {
                    foreach (var line in lines) parser.Line(line);
                    parser.Flush();
                });
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // the last batch was posted to the lane before the run returned: yield once so it reaches the parser before the caller completes it
            await Task.Yield();
        }
    }

    // ------------------------------------------------------------------------------------------------ publishing

    private void Publish()
    {
        if (_disposed) return;
        var projects = new List<CMakeProject>(_projects.Count);
        var views = new List<CMakeProjectView>(_projects.Count);
        var changed = _projects.Count != Projects.Count;
        foreach (var p in _projects)
        {
            var ctest = p.Reply?.CTestPath is { } fromReply && File.Exists(fromReply) ? fromReply
                : CMakeTools.FindCTest(CMakeProgram, CMakeSettings.String(_config, CMakeSettings.CTestPath));
            var project = new CMakeProject(p.SourceDir, p.BuildDir, p.State, p.UsePresets ? p.ConfigurePreset : null, p.UsePresets ? p.BuildPreset : null,
                p.Configuration, p.Reply?.Generator ?? Generator(p), p.Reply?.Targets ?? [], ctest, p.Generation);
            if (p.Snapshot is null || !SameSnapshot(p.Snapshot, project)) { p.Snapshot = project; changed = true; }
            projects.Add(p.Snapshot);
            views.Add(new CMakeProjectView(p.Snapshot, p.UsePresets, p.Presets.Configure, p.Presets.Build, p.Presets.Error, p.BuildType, p.LaunchTarget, p.BuildTarget, p.Activity));
        }
        Volatile.Write(ref _views, views);
        _viewsChanged.Fire(views);
        if (!changed) return;
        Volatile.Write(ref _snapshot, projects);
        _changed.Fire(projects);
    }

    private static bool SameSnapshot(CMakeProject a, CMakeProject b) =>
        a with { Targets = b.Targets } == b && ReferenceEquals(a.Targets, b.Targets);

    private static bool SameTargets(FileApiReply a, FileApiReply b) =>
        a.Targets.Count == b.Targets.Count && a.Targets.Zip(b.Targets).All(t => t.First.Name == t.Second.Name && t.First.Type == t.Second.Type
            && t.First.Artifacts.SequenceEqual(t.Second.Artifacts) && t.First.Dependencies.SequenceEqual(t.Second.Dependencies)
            && t.First.Sources.SequenceEqual(t.Second.Sources) && t.First.LinkLibraries.SequenceEqual(t.Second.LinkLibraries));

    private void Persist(ProjectState p)
    {
        if (_state is null) return;
        var all = _state.Get<Dictionary<string, PersistedProject>>(StateKey, StorageScope.Workspace) ?? [];
        all[p.SourceDir] = new PersistedProject(p.ConfigurePreset, p.BuildPreset, p.BuildType, p.LaunchTarget, p.BuildTarget);
        _state.Set(StateKey, all, StorageScope.Workspace);
    }

    private void Write(string line)
    {
        Channel?.AppendLine(line);
        _logger.LogDebug("{Line}", line);
    }

    internal static string ConfigureOwner(string sourceDir) => "cmake.configure:" + sourceDir;

    internal static string BuildOwner(string sourceDir) => "cmake.build:" + sourceDir;

    private static string ConfigureOwner(ProjectState p) => ConfigureOwner(p.SourceDir);

    private static string BuildOwner(ProjectState p) => BuildOwner(p.SourceDir);

    private static string Name(ProjectState p) => Path.GetFileName(p.SourceDir.TrimEnd(Path.DirectorySeparatorChar));

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string path, string dir)
    {
        if (dir.Length == 0) return false;
        var d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return path.StartsWith(d, comparison) || PathsEqual(path, dir);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel(); // every running configure or build is linked to it: their process trees are killed
        lock (_pending)
        {
            foreach (var pending in _pending) pending.Done.TrySetResult(null);
            _pending.Clear();
        }
        _store.Dispose();
        _changed.Clear();
        _viewsChanged.Clear();
        _built.Clear();
        _lane.Dispose();
    }
}
