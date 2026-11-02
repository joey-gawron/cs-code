using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Tasks;

// Detected tasks (lang-2): every ITaskProvider in DI (DapPlatform.TaskProviders) adds its tasks after tasks.json's, and resolves the tasks.json
// entries of its type, which customise one of its tasks (VS Code's TaskProvider.provideTasks / resolveTask).
public sealed partial class TaskService
{
    /// <summary>How long a provider may take to answer before its tasks are left out (it runs a tool: <c>cargo metadata</c>).</summary>
    internal static TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// tasks.json's tasks — an entry of a provider's type resolved through that provider (its label, group, presentation, problem matchers and
    /// dependsOn kept) — then each provider's detected tasks that no entry customises and whose label tasks.json does not already use. On the lane.
    /// </summary>
    private async Task<TasksFile> WithProvidedTasksAsync(Uri? folder, TasksFile file, CancellationToken ct)
    {
        var providers = folder is null ? [] : _p.TaskProviders?.Invoke()?.ToList() ?? [];
        if (providers.Count == 0)
            return file.Tasks.Any(t => t.Label.Length == 0) ? file with { Tasks = [.. file.Tasks.Where(t => t.Label.Length > 0)] } : file;

        var tasks = new List<TaskModel>(file.Tasks.Count);
        var entries = new List<JsonElement>();
        foreach (var t in file.Tasks)
        {
            var provider = t.Entry is null ? null : providers.FirstOrDefault(p => string.Equals(p.Type, t.Type, StringComparison.Ordinal));
            if (provider is null)
            {
                if (t.Label.Length > 0) tasks.Add(t); // an unknown type with a label and command runs as a shell task, as before
                continue;
            }
            var entry = JsonSerializer.SerializeToElement(t.Entry);
            entries.Add(entry);
            var resolved = await AskAsync(provider, (p, c) => p.ResolveTaskAsync(folder!, entry, c), ct);
            tasks.Add(resolved is null
                ? t with
                {
                    Label = t.Label.Length > 0 ? t.Label : $"{t.Type}: {entry}",
                    Error = $"The '{t.Type}' task {(t.Label.Length > 0 ? $"'{t.Label}' " : "")}of tasks.json does not match any task the '{t.Type}' provider detected.",
                }
                : Customise(t, resolved));
        }

        foreach (var provider in providers)
        {
            foreach (var d in await AskAsync<IReadOnlyList<ProvidedTask>>(provider, async (p, c) => await p.ProvideTasksAsync(folder!, c), ct) ?? [])
            {
                if (d.Definition is { } definition && entries.Any(e => TaskDefinitionMatch.Matches(definition, e))) continue;
                if (tasks.Any(t => t.Label == d.Label)) continue;
                tasks.Add(FromProvided(d));
            }
        }
        return file with { Tasks = tasks };
    }

    /// <summary>Asks a provider on the lane, bounded by <see cref="ProviderTimeout"/>; null when it fails or is too slow (logged).</summary>
    private async Task<T?> AskAsync<T>(ITaskProvider provider, Func<ITaskProvider, CancellationToken, ValueTask<T?>> ask, CancellationToken ct) where T : class
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ProviderTimeout);
        try
        {
            return await ask(provider, bounded.Token).AsTask().WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("The '{Type}' task provider took more than {Seconds} s: its tasks are left out", provider.Type, ProviderTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The '{Type}' task provider failed", provider.Type);
            return null;
        }
    }

    private static TaskModel FromProvided(ProvidedTask d) =>
        new(d.Label, ProviderType(d), d.Command, d.Args, d.Cwd, d.Env ?? new Dictionary<string, string>(), null, [], [], false, d.Group, false,
            d.Detail ?? CommandLine(d), null, Matchers(d), d.IsBackground)
        {
            Source = d.Source,
            Provided = true,
        };

    /// <summary>A tasks.json entry customising a detected task: what to run is the provider's, how to present it and what it needs are the entry's.</summary>
    private static TaskModel Customise(TaskModel entry, ProvidedTask d)
    {
        var env = new Dictionary<string, string>(d.Env ?? new Dictionary<string, string>());
        foreach (var (k, v) in entry.Env) env[k] = v;
        return entry with
        {
            Label = entry.Label.Length > 0 ? entry.Label : d.Label,
            Command = d.Command,
            Args = d.Args,
            Cwd = entry.Cwd ?? d.Cwd,
            Env = env,
            ShellExecutable = null,
            ShellArgs = [],
            Group = entry.Group ?? d.Group,
            Detail = entry.Detail ?? d.Detail ?? CommandLine(d),
            ProblemMatcher = entry.ProblemMatcher ?? Matchers(d),
            IsBackground = entry.IsBackground || d.IsBackground,
            Provided = true,
        };
    }

    private static string ProviderType(ProvidedTask d) =>
        d.Definition is { ValueKind: JsonValueKind.Object } def && def.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : d.Source;

    private static JsonNode? Matchers(ProvidedTask d) => d.ProblemMatchers.Count == 0 ? null : new JsonArray([.. d.ProblemMatchers.Select(m => (JsonNode)JsonValue.Create(m))]);

    private static string CommandLine(ProvidedTask d) => string.Join(' ', new[] { Path.GetFileName(d.Command) }.Concat(d.Args.Select(ShellQuote)));
}
