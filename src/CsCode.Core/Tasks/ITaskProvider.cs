using System.Text.Json;

namespace CsCode.Core.Tasks;

/// <summary>
/// A task an <see cref="ITaskProvider"/> detected in a folder (VS Code's contributed tasks: rust-analyzer's cargo commands, npm's scripts). It
/// runs a program directly, without a shell, like a tasks.json <c>process</c> task. Its <see cref="Label"/> is <c>"{Source}: {Name}"</c>
/// (<c>"cargo: build"</c>): what Run Task lists and what <c>preLaunchTask</c>, <c>postDebugTask</c> and <c>dependsOn</c> name.
/// </summary>
/// <param name="Source">Who detected it, written before the name: <c>"cargo"</c>, <c>"cmake"</c>, <c>"zig"</c>.</param>
/// <param name="Name">The task's name within its source: <c>"build"</c>, <c>"test - mycrate"</c>.</param>
/// <param name="Command">The program: an absolute path, or a name looked up on CsCode's PATH (the login shell's on macOS).</param>
/// <param name="Args">Its arguments, passed as they are (no quoting, no <c>${…}</c> substitution).</param>
public sealed record ProvidedTask(string Source, string Name, string Command, IReadOnlyList<string> Args)
{
    public string Label => $"{Source}: {Name}";

    /// <summary><c>"build"</c>, <c>"test"</c> or null, as a tasks.json <c>group</c>. Detected tasks are never a group's default.</summary>
    public string? Group { get; init; }

    /// <summary>Shown under the label in Run Task; null shows the command line.</summary>
    public string? Detail { get; init; }

    /// <summary>The working directory (absolute); null runs in the folder the task was detected in.</summary>
    public string? Cwd { get; init; }

    /// <summary>Variables set on top of CsCode's environment.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Problem matchers by name, as tasks.json writes them (<c>"$rustc"</c>, <c>"$gcc"</c>).</summary>
    public IReadOnlyList<string> ProblemMatchers { get; init; } = [];

    /// <summary>A watch-style task that keeps running (tasks.json <c>isBackground</c>).</summary>
    public bool IsBackground { get; init; }

    /// <summary>
    /// The tasks.json object that names this task: its <c>"type"</c> is the provider's <see cref="ITaskProvider.Type"/>, its other members the
    /// provider's own (<c>{"type": "cargo", "command": "build"}</c>). A tasks.json entry whose members include all of these customises this task
    /// (label, group, problem matchers, presentation, dependsOn), and Run Task then lists only the customised one, as in VS Code.
    /// </summary>
    public JsonElement? Definition { get; init; }
}

/// <summary>
/// Detects a folder's tasks without a tasks.json (VS Code's <c>TaskProvider</c>; CsCode has no extensions, so each is built in). The task runner
/// (<see cref="ITaskService"/>, CsCode.Dap) asks every <see cref="ITaskProvider"/> registered in DI, lists their tasks after tasks.json's own in
/// <see cref="ITaskService.GetTasksAsync"/>, runs them by label in <see cref="ITaskService.RunAsync"/>, and hands a tasks.json entry of the
/// provider's <see cref="Type"/> to <see cref="ResolveTaskAsync"/>. Register one with <c>services.AddSingleton&lt;ITaskProvider, MyProvider&gt;()</c>.
/// Both methods run on the task runner's lane, never on the UI thread; a provider that runs a tool (<c>cargo metadata</c>) caches its answer
/// per folder and keeps it until the files it read change. A provider that fails or takes longer than ten seconds is skipped (and logged).
/// </summary>
public interface ITaskProvider
{
    /// <summary>The tasks.json <c>"type"</c> of its tasks (<c>"cargo"</c>).</summary>
    string Type { get; }

    /// <summary>The tasks detected in <paramref name="folder"/> (a workspace folder; <c>file:</c> for a local one); empty when none apply.</summary>
    ValueTask<IReadOnlyList<ProvidedTask>> ProvideTasksAsync(Uri folder, CancellationToken ct);

    /// <summary>
    /// The task a tasks.json entry of this provider's <see cref="Type"/> runs (<paramref name="definition"/> is the whole entry, platform block
    /// merged), or null when the entry does not describe one (the runner then reports it). Default: the detected task whose
    /// <see cref="ProvidedTask.Definition"/> the entry matches.
    /// </summary>
    async ValueTask<ProvidedTask?> ResolveTaskAsync(Uri folder, JsonElement definition, CancellationToken ct)
    {
        foreach (var task in await ProvideTasksAsync(folder, ct).ConfigureAwait(false))
            if (task.Definition is { } d && TaskDefinitionMatch.Matches(d, definition)) return task;
        return null;
    }
}

/// <summary>How a tasks.json entry is matched with a detected task's <see cref="ProvidedTask.Definition"/>.</summary>
public static class TaskDefinitionMatch
{
    /// <summary>True when every member of <paramref name="definition"/> is in <paramref name="entry"/> with an equal value.</summary>
    public static bool Matches(JsonElement definition, JsonElement entry)
    {
        if (definition.ValueKind != JsonValueKind.Object || entry.ValueKind != JsonValueKind.Object) return false;
        foreach (var member in definition.EnumerateObject())
            if (!entry.TryGetProperty(member.Name, out var value) || !JsonElement.DeepEquals(member.Value, value)) return false;
        return true;
    }
}
