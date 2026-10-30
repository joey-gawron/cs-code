using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Session;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Tasks;

/// <summary>
/// Minimum-viable <c>tasks.json</c> runner (<c>.cscode/</c> then <c>.vscode/</c>): <c>shell</c> and <c>process</c> tasks, <c>command</c>,
/// <c>args</c>, <c>options.cwd/env</c>, <c>dependsOn</c> (+ <c>dependsOrder</c> sequence/parallel), <c>group</c>, platform blocks, the same
/// <c>${…}</c> substitution as launch.json. Output streams (batched, bounded) to the "Tasks" output channel; success = exit code 0;
/// cancelling kills the whole process tree. With the integrated terminal composed (<see cref="DapPlatform.TaskTerminals"/>) a task runs in a
/// terminal named after it instead, following its <c>presentation</c> (TaskService.Terminal.cs). Problem matchers feed the Error List either
/// way (ProblemMatchers.cs). Task processes (a preLaunchTask's <c>tsc</c>, <c>cargo build</c>…) are reported to
/// <see cref="CsCode.Core.Diagnostics.IMemoryMonitor.TrackChildProcess"/>, and <see cref="Dispose"/> (app shutdown) kills every task still
/// running, with its process tree.
/// </summary>
public sealed partial class TaskService : ITaskService, IDisposable
{
    public const string ChannelName = "Tasks";
    private static readonly string[] Locations = [".cscode/tasks.json", ".vscode/tasks.json"];
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly DebugLane _lane;
    private readonly DapPlatform _p;
    private readonly ILogger _logger;
    private readonly HashSet<Process> _running = new();
    private int _disposed;

    /// <summary>Pids of the task processes running right now (diagnostics and tests).</summary>
    public IReadOnlyList<int> RunningProcessIds
    {
        get
        {
            lock (_running)
            {
                var pids = new List<int>(_running.Count);
                foreach (var p in _running)
                {
                    try { pids.Add(p.Id); } catch (InvalidOperationException) { }
                }
                return pids;
            }
        }
    }

    /// <summary>
    /// Kills every task still running (whole process trees) and refuses new ones. Non-blocking: signals only, no waiting, so the container can
    /// dispose it on the UI thread at shutdown.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Process[] running;
        lock (_running) running = [.. _running];
        foreach (var p in running) KillTree(p);
        TerminateTerminalRuns();
    }

    public TaskService(DebugLane lane, DapPlatform platform)
    {
        _lane = lane;
        _p = platform;
        _logger = platform.Logger("CsCode.Dap.Tasks");
    }

    private sealed record TaskModel(
        string Label, string Type, string? Command, IReadOnlyList<string> Args, string? Cwd, IReadOnlyDictionary<string, string> Env,
        string? ShellExecutable, IReadOnlyList<string> ShellArgs, IReadOnlyList<string> DependsOn, bool Sequence, string? Group, bool IsDefault, string? Detail,
        JsonObject? Presentation = null, JsonNode? ProblemMatcher = null, bool IsBackground = false, int InstanceLimit = 1);

    private sealed record TasksFile(IReadOnlyList<TaskModel> Tasks, IReadOnlyDictionary<string, JsonElement> Inputs);

    // ------------------------------------------------------------------ public API

    public ValueTask<IReadOnlyList<TaskDefinition>> GetTasksAsync(Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(async () =>
        {
            var file = await LoadAsync(folder ?? _p.PrimaryFolder, ct);
            return (IReadOnlyList<TaskDefinition>)[.. file.Tasks.Select(t => new TaskDefinition(t.Label, t.Type, t.Group, t.IsDefault, t.Detail))];
        }, ct));

    public ValueTask<TaskResult> RunAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default) =>
        new(_lane.RunAsync(() => RunCoreAsync(taskLabel, folder ?? _p.PrimaryFolder, ct), ct));

    // ------------------------------------------------------------------ loading

    private async Task<TasksFile> LoadAsync(Uri? folder, CancellationToken ct)
    {
        if (folder is null || _p.Workspace is not { } ws) return new TasksFile([], new Dictionary<string, JsonElement>());
        foreach (var rel in Locations)
        {
            var uri = new Uri(folder.ToString().TrimEnd('/') + "/" + rel);
            if (await ws.FileSystem.StatAsync(uri, ct) is not { Type: FileType.File }) continue;
            var text = Encoding.UTF8.GetString(await ws.FileSystem.ReadFileAsync(uri, ct));
            return Parse(text, uri.ToString());
        }
        return new TasksFile([], new Dictionary<string, JsonElement>());
    }

    private static TasksFile Parse(string text, string path)
    {
        JsonObject root;
        try { root = JsonNode.Parse(text, documentOptions: Lenient) as JsonObject ?? throw new JsonException("root is not an object"); }
        catch (JsonException ex) { throw new InvalidOperationException($"{path}: invalid tasks.json ({ex.Message})", ex); }

        var globalOptions = root["options"] as JsonObject;
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root["inputs"] is JsonArray ins)
            foreach (var i in ins.OfType<JsonObject>())
                if (i["id"]?.GetValue<string>() is { } id) inputs[id] = JsonSerializer.SerializeToElement(i);

        var tasks = new List<TaskModel>();
        if (root["tasks"] is JsonArray arr)
            foreach (var node in arr.OfType<JsonObject>())
            {
                var t = VariableResolver.MergePlatformBlock(node);
                var label = t["label"]?.GetValue<string>() ?? t["taskName"]?.GetValue<string>();
                if (label is null) continue; // (npm/typescript auto-detected tasks need a provider: not supported)
                var options = t["options"] as JsonObject ?? globalOptions;
                var shell = options?["shell"] as JsonObject;
                var env = new Dictionary<string, string>();
                if (options?["env"] is JsonObject e) foreach (var (k, v) in e) if (v is not null) env[k] = v.ToString();

                string? group = null;
                var isDefault = false;
                switch (t["group"])
                {
                    case JsonValue gv when gv.TryGetValue<string>(out var gs): group = gs; break;
                    case JsonObject go: group = go["kind"]?.GetValue<string>(); isDefault = go["isDefault"]?.GetValue<bool>() ?? false; break;
                }

                var depends = new List<string>();
                switch (t["dependsOn"])
                {
                    case JsonValue dv when dv.TryGetValue<string>(out var ds): depends.Add(ds); break;
                    case JsonArray da: depends.AddRange(da.Select(d => d is JsonObject dobj ? dobj["task"]?.GetValue<string>() : d?.GetValue<string>()).OfType<string>()); break;
                }

                tasks.Add(new TaskModel(
                    label,
                    t["type"]?.GetValue<string>() ?? "shell",
                    t["command"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null,
                    [.. (t["args"] as JsonArray ?? []).Select(a => a is JsonObject ao ? ao["value"]?.ToString() ?? "" : a?.ToString() ?? "")],
                    options?["cwd"]?.GetValue<string>(),
                    env,
                    shell?["executable"]?.GetValue<string>(),
                    [.. (shell?["args"] as JsonArray ?? []).Select(a => a?.ToString() ?? "")],
                    depends,
                    string.Equals(t["dependsOrder"]?.GetValue<string>(), "sequence", StringComparison.OrdinalIgnoreCase),
                    group, isDefault,
                    t["detail"]?.GetValue<string>(),
                    MergePresentation(root["presentation"] as JsonObject, t["presentation"] as JsonObject),
                    t["problemMatcher"]?.DeepClone(),
                    t["isBackground"] is JsonValue bg && bg.TryGetValue<bool>(out var background) && background,
                    t["runOptions"] is JsonObject ro && ro["instanceLimit"] is JsonValue il && il.TryGetValue<int>(out var limit) && limit > 1 ? limit : 1));
            }
        return new TasksFile(tasks, inputs);
    }

    // ------------------------------------------------------------------ running

    private async Task<TaskResult> RunCoreAsync(string label, Uri? folder, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return new TaskResult(label, -1, Error: "The task runner has shut down.");
        TasksFile file;
        try { file = await LoadAsync(folder, ct); }
        catch (InvalidOperationException ex) { return new TaskResult(label, -1, Error: ex.Message); }
        if (file.Tasks.All(t => t.Label != label))
            return new TaskResult(label, -1, Error: $"There is no task named '{label}' in tasks.json{(folder is null ? " (no folder is open)" : "")}.");

        var channel = _p.Output?.GetOrCreate(ChannelName);
        var resolver = new VariableResolver(_p.CreateVariableContext(folder, file.Inputs, ct2 => ValueTask.FromResult(file.Tasks.FirstOrDefault(t => t.Group == "build" && t.IsDefault)?.Label)));
        try
        {
            return await RunTaskAsync(file, label, folder, resolver, channel, [], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TaskResult(label, -1, Cancelled: true);
        }
        catch (VariableResolutionException ex)
        {
            return new TaskResult(label, -1, Error: ex.Message);
        }
    }

    /// <summary>
    /// Runs a task (its dependencies first), or joins the run of it already in progress: VS Code runs a task at most
    /// <c>runOptions.instanceLimit</c> times at once (default 1), so a second Ctrl+Shift+B, a preLaunchTask that is already running and a
    /// dependency two branches of a dependsOn graph share wait for the running instance instead of starting the same command again (two builds
    /// writing the same obj/bin, two collectors clobbering each other's problems).
    /// </summary>
    private Task<TaskResult> RunTaskAsync(TasksFile file, string label, Uri? folder, VariableResolver resolver, IOutputChannel? channel, HashSet<string> path, CancellationToken ct)
    {
        var task = file.Tasks.FirstOrDefault(t => t.Label == label);
        if (task is null) return Task.FromException<TaskResult>(new InvalidOperationException($"There is no task named '{label}'."));
        if (path.Contains(label)) return Task.FromResult(new TaskResult(label, -1, Error: $"Circular task dependency: {string.Join(" → ", path)} → {label}."));
        var key = KeyOf(folder, label);
        TaskInstance instance;
        lock (_active)
        {
            if (_active.TryGetValue(key, out var running) && running.Count >= task.InstanceLimit)
                return JoinAsync(running[^1], ct);
            instance = new TaskInstance(label, task.InstanceLimit, task.IsBackground && task.Command is not null);
            if (running is null) _active[key] = running = [];
            running.Add(instance);
        }
        instance.Result = RunInstanceAsync(key, instance, task, file, folder, resolver, channel, [.. path], ct);
        return instance.Result;
    }

    private async Task<TaskResult> RunInstanceAsync(string key, TaskInstance instance, TaskModel task, TasksFile file, Uri? folder, VariableResolver resolver,
        IOutputChannel? channel, HashSet<string> path, CancellationToken ct)
    {
        var label = task.Label;
        // The caller's token stops the run until RunAsync returns (for a background task: until it is ready); Terminate Task stops it any time.
        var stop = instance.Stop.Token;
        using var link = ct.Register(static s => ((TaskInstance)s!).Terminate(), instance);
        path.Add(label);
        try
        {
            // ---- dependencies first (parallel by default, like VS Code)
            if (task.DependsOn.Count > 0)
            {
                TaskResult? failure = null;
                if (task.Sequence)
                {
                    foreach (var dep in task.DependsOn)
                    {
                        var r = await RunTaskAsync(file, dep, folder, resolver, channel, path, stop);
                        if (!r.Succeeded) { failure = r; break; }
                    }
                }
                else
                {
                    var results = await Task.WhenAll(task.DependsOn.Select(dep => RunTaskAsync(file, dep, folder, resolver, channel, path, stop)));
                    failure = results.FirstOrDefault(r => !r.Succeeded);
                }
                if (failure is not null)
                    return failure with { Label = label, Error = failure.Error ?? $"Dependency '{failure.Label}' failed with exit code {failure.ExitCode}." };
            }
            if (task.Command is null) return new TaskResult(label, 0); // a pure "group" task made of dependencies

            return await ExecuteAsync(task, folder, resolver, channel, instance, key, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new TaskResult(label, -1, Cancelled: true); // Terminate Task (the caller's own cancellation propagates as before)
        }
        finally
        {
            if (!instance.Ready) End(key, instance); // a background task that is ready ends with its process
        }
    }

    // ------------------------------------------------------------------ running instances

    /// <summary>Running instances per folder and label (VS Code's active tasks). Locked: <see cref="IsActive"/> reads it from any thread.</summary>
    private readonly Dictionary<string, List<TaskInstance>> _active = new(StringComparer.Ordinal);

    private string KeyOf(Uri? folder, string label) => (folder ?? _p.PrimaryFolder)?.ToString().TrimEnd('/') + "\n" + label;

    /// <summary>One run of a task in progress, from its start until its process ended (a background task stays after it was ready).</summary>
    private sealed class TaskInstance(string label, int limit, bool background)
    {
        public string Label => label;
        public int Limit => limit;
        public bool IsBackground => background;
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>What RunAsync reports: the result, or for a background task its readiness.</summary>
        public Task<TaskResult> Result = Task.FromResult(new TaskResult(label, -1));
        /// <summary>A background task was ready (its matchers went idle, or it could not be tracked): RunAsync has returned, the process runs on.</summary>
        public bool Ready;
        /// <summary>A background task's current cycle of work (between its matchers' begin and end); null while idle. Touched on the lane only.</summary>
        public TaskCompletionSource<TaskResult>? Cycle;

        public void Terminate()
        {
            try { Stop.Cancel(); }
            catch (AggregateException) { /* a registered kill threw: the process is going anyway */ }
        }
    }

    /// <summary>What a second run of a running task waits for: its result; a background task that is ready: the end of its current cycle, if busy.</summary>
    private static async Task<TaskResult> JoinAsync(TaskInstance running, CancellationToken ct)
    {
        var task = !running.IsBackground || !running.Ready ? running.Result
            : running.Cycle is { } busy ? busy.Task
            : Task.FromResult(new TaskResult(running.Label, 0));
        return await task.WaitAsync(ct);
    }

    /// <summary>The instance's process is gone: another run starts afresh (removed before its result is seen, so Restart Task can rely on it).</summary>
    private void End(string key, TaskInstance instance)
    {
        lock (_active)
        {
            if (_active.TryGetValue(key, out var list) && list.Remove(instance) && list.Count == 0) _active.Remove(key);
        }
        instance.Ended.TrySetResult();
    }

    public bool IsActive(string taskLabel, Uri? folder = null)
    {
        lock (_active) return _active.TryGetValue(KeyOf(folder, taskLabel), out var list) && list.Count > 0 && list.Count >= list[^1].Limit;
    }

    public async ValueTask TerminateAsync(string taskLabel, Uri? folder = null, CancellationToken ct = default)
    {
        TaskInstance[] running;
        lock (_active) running = _active.TryGetValue(KeyOf(folder, taskLabel), out var list) ? [.. list] : [];
        if (running.Length == 0) return;
        // the kills run on the lane (killing a process tree reads /proc): never on the caller's (UI) thread
        _lane.Post(() => { foreach (var r in running) r.Terminate(); });
        await Task.WhenAll(running.Select(r => r.Ended.Task)).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A background task's readiness as its matchers report it (on the lane): followed from before its program starts.</summary>
    private sealed class Readiness
    {
        public readonly TaskCompletionSource<TaskResult?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>A cycle began: the task can be tracked (no "cannot be tracked" after five seconds).</summary>
        public bool Tracked;
    }

    /// <summary>
    /// A background task (<c>isBackground</c>) is ready for whoever waits on it (a preLaunchTask) when its problem matchers' background patterns
    /// go idle (VS Code's "inactive": the watch task's first compilation ended), not when its process exits, which it does not. Installed before
    /// the program starts: its first lines are matched while the start is still returning (a terminal's start waits for the UI to show it), and a
    /// fast watcher can begin and end its first cycle then. Null for a task that is not a background one. On the lane.
    /// </summary>
    private static Readiness? TrackReadiness(TaskInstance instance, TaskModel task, ProblemCollector? problems)
    {
        if (!instance.IsBackground) return null;
        var readiness = new Readiness();
        if (problems is not { IsWatching: true }) return readiness;
        problems.BackgroundBegan = () =>
        {
            readiness.Tracked = true;
            instance.Cycle ??= new TaskCompletionSource<TaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        };
        problems.BackgroundEnded = () =>
        {
            var idle = new TaskResult(task.Label, 0);
            var cycle = instance.Cycle;
            instance.Cycle = null;
            cycle?.TrySetResult(idle);
            readiness.Ready.TrySetResult(idle);
        };
        if (problems.IsBusy) problems.BackgroundBegan(); // activeBegins: busy from the start
        return readiness;
    }

    /// <summary>
    /// Completes when the background task is ready (<see cref="TrackReadiness"/>), with null when its process ended first. Without background
    /// patterns it cannot be tracked: after five seconds without a begin VS Code's error is the result, marked <see cref="TaskResult.NotTracked"/>
    /// (the debugger asks Debug Anyway / Abort; a plain Run Task shows nothing), and the task runs on. On the lane.
    /// </summary>
    private static Task<TaskResult?> WhenReadyAsync(Readiness readiness, TaskModel task, Task finished)
    {
        var ready = readiness.Ready;
        _ = UntrackedAfterAsync();
        _ = finished.ContinueWith(_ => ready.TrySetResult(null), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return ready.Task;

        async Task UntrackedAfterAsync()
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(5)), finished);
            if (!readiness.Tracked && !finished.IsCompleted)
                ready.TrySetResult(new TaskResult(task.Label, -1,
                    Error: $"The task '{task.Label}' cannot be tracked: make sure to have a problem matcher with background patterns defined") { NotTracked = true });
        }
    }

    /// <summary>
    /// Waits for a run's process (<paramref name="finished"/>), or for a background task only until it is ready (<see cref="WhenReadyAsync"/>):
    /// then the rest of the run (the process, its output and problems, the instance's end) carries on without the caller.
    /// </summary>
    private static async Task<TaskResult> AwaitRunAsync(TaskInstance instance, TaskModel task, Readiness? readiness, Task<TaskResult> finished)
    {
        if (readiness is null) return await finished;
        var ready = await WhenReadyAsync(readiness, task, finished);
        if (ready is null || finished.IsCompleted) return await finished;
        instance.Ready = true;
        return ready;
    }

    /// <summary>A background task's process ended: whoever waits on its current cycle gets its result.</summary>
    private static void FinishCycle(TaskInstance instance, TaskResult result)
    {
        var cycle = instance.Cycle;
        instance.Cycle = null;
        cycle?.TrySetResult(result);
    }

    private async Task<TaskResult> ExecuteAsync(TaskModel task, Uri? folder, VariableResolver resolver, IOutputChannel? channel, TaskInstance instance, string key,
        CancellationToken ct)
    {
        var command = await resolver.ResolveAsync(task.Command!, ct);
        var args = new List<string>();
        foreach (var a in task.Args) args.Add(await resolver.ResolveAsync(a, ct));
        var folderPath = folder is { IsFile: true } ? DapPlatform.NormalizeDir(folder.LocalPath) : null;
        var cwd = task.Cwd is null ? folderPath : await resolver.ResolveAsync(task.Cwd, ct);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (cwd is not null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        // CsCode's own DOTNET_ROOT_<ARCH> (dotnet run/test) must not reach the task's .NET programs (T27); the task's env applies on top
        InheritedDotnetRoot.Fold(psi.Environment);
        var taskEnv = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in task.Env) psi.Environment[k] = taskEnv[k] = await resolver.ResolveAsync(v, ct);

        if (string.Equals(task.Type, "process", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = command;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            var line = string.Join(' ', new[] { command }.Concat(args.Select(ShellQuote)));
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = task.ShellExecutable ?? "cmd.exe";
                foreach (var a in task.ShellArgs.Count > 0 ? task.ShellArgs : ["/d", "/c"]) psi.ArgumentList.Add(a);
            }
            else
            {
                psi.FileName = task.ShellExecutable ?? "/bin/sh";
                foreach (var a in task.ShellArgs.Count > 0 ? task.ShellArgs : ["-c"]) psi.ArgumentList.Add(a);
            }
            psi.ArgumentList.Add(line);
        }

        var problems = StartProblemMatchers(task, folderPath, psi.WorkingDirectory is { Length: > 0 } wd ? wd : folderPath);
        var readiness = TrackReadiness(instance, task, problems); // before the start: its first output may already be a whole cycle
        // In a terminal the task's own env only: a local terminal folds CsCode's DOTNET_ROOT_<ARCH> in what it inherits, before the user's
        // terminal.integrated.env and inheritEnv apply (TerminalLaunchResolver), and a remote workspace's terminal starts from the remote host's
        if (_p.TaskTerminals?.Invoke() is { } terminals)
            return await ExecuteInTerminalAsync(terminals, task, psi, taskEnv, channel, problems, readiness, instance, key, ct);

        channel?.AppendLine($"> Executing task: {task.Label} <");
        Process process;
        try { process = await Task.Run(() => Process.Start(psi) ?? throw new InvalidOperationException("the process did not start"), ct); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            channel?.AppendLine($"The task '{task.Label}' could not be started: {ex.Message}");
            return new TaskResult(task.Label, -1, Error: $"Could not start '{command}': {ex.Message}");
        }
        return await AwaitRunAsync(instance, task, readiness, RunProcessAsync(process, task, channel, problems, instance, key, ct));
    }

    /// <summary>The process path: its output to the "Tasks" channel and the matchers, until it exits (or is killed), then its result.</summary>
    private async Task<TaskResult> RunProcessAsync(Process process, TaskModel task, IOutputChannel? channel, ProblemCollector? problems, TaskInstance instance,
        string key, CancellationToken ct)
    {
        using var owned = process;
        lock (_running) _running.Add(process);
        using var tracked = _p.Memory?.TrackChildProcess($"task ({task.Label})", process.Id);
        if (Volatile.Read(ref _disposed) != 0) KillTree(process); // shut down while it was starting
        var result = new TaskResult(task.Label, -1, Cancelled: true);
        try
        {
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            var pumps = new List<BoundedTextPump>();
            var readers = new[] { process.StandardOutput, process.StandardError }.Select(r =>
            {
                var pump = new BoundedTextPump(_lane, text => { channel?.Append(text); problems?.Append(text); });
                pumps.Add(pump);
                return PumpAsync(r, pump);
            }).ToArray();

            try
            {
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); // drain remaining output
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                channel?.AppendLine($"> Task '{task.Label}' was cancelled.");
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
                if (instance.Ready) return result; // a background task terminated after it was ready: nobody awaits this any more
                throw;
            }
            catch (TimeoutException) { /* a grandchild keeps the pipes open: the task itself has finished */ }

            if (problems is not null)
            {
                await DrainLaneAsync(); // the pumps' last flushes are queued on the lane: the matchers see all output first
                problems.Complete();
            }
            if (Volatile.Read(ref _disposed) != 0) return result; // killed by the shutdown
            await RevealProblemsAsync(task, problems);
            channel?.AppendLine($"> Task '{task.Label}' finished with exit code {process.ExitCode}.");
            return result = new TaskResult(task.Label, process.ExitCode);
        }
        finally
        {
            lock (_running) _running.Remove(process);
            if (instance.IsBackground)
            {
                FinishCycle(instance, result);
                End(key, instance);
            }
        }
    }

    private static async Task PumpAsync(StreamReader reader, BoundedTextPump pump)
    {
        var buf = new char[4096];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                if (n <= 0) break;
                pump.Append(buf.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { pump.Complete(); }
    }

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Quotes an argument for /bin/sh unless it is already quoted or plainly safe (VS Code's default quoting for shell tasks).</summary>
    internal static string ShellQuote(string arg)
    {
        if (arg.Length == 0) return "''";
        if ((arg.StartsWith('"') && arg.EndsWith('"') && arg.Length > 1) || (arg.StartsWith('\'') && arg.EndsWith('\'') && arg.Length > 1)) return arg;
        if (arg.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '=' or '+' or ',' or '@' or '%' or '$' or '{' or '}')) return arg;
        if (OperatingSystem.IsWindows()) return "\"" + arg.Replace("\"", "\\\"") + "\"";
        return "'" + arg.Replace("'", "'\\''") + "'";
    }
}
