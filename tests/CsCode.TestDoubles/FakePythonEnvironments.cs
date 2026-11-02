using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Python;

namespace CsCode.TestDoubles;

/// <summary>
/// An <see cref="IPythonEnvironmentService"/> that remembers the picks it is given (per folder) and answers them back as the active interpreter;
/// nothing is discovered or probed. <see cref="Throw"/> makes every call fail.
/// </summary>
public sealed class FakePythonEnvironments : IPythonEnvironmentService
{
    private readonly Emitter<Uri?> _changed = new();

    public ConcurrentDictionary<Uri, string> Picks { get; } = new();

    public Exception? Throw { get; set; }

    public PythonEnvironment? GetActive(Uri? resource) =>
        resource is not null && Picks.TryGetValue(resource, out var exe) ? new PythonEnvironment(exe, PythonEnvironmentKind.Venv, "3.14.0", Path.GetDirectoryName(Path.GetDirectoryName(exe)), ".venv") : null;

    public ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) =>
        Throw is { } ex ? ValueTask.FromException<PythonEnvironment?>(ex) : ValueTask.FromResult(GetActive(resource));

    public ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<PythonEnvironment>>(GetActive(resource) is { } active ? [active] : []);

    public ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default)
    {
        if (Throw is { } ex) return ValueTask.FromException(ex);
        if (resource is null) return ValueTask.CompletedTask;
        if (executable is null) Picks.TryRemove(resource, out _);
        else Picks[resource] = executable;
        _changed.Fire(resource);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) =>
        ValueTask.FromResult<PythonEnvironment?>(new PythonEnvironment(executable, PythonEnvironmentKind.Custom, "3.14.0", null));

    public ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default) => ValueTask.FromResult(false);

    public IEventSource<Uri?> ActiveChanged => _changed;
}
