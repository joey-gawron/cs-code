namespace CsCode.Profiling.Testing;

/// <summary>
/// A workload the soak runner repeats (MUST 5): <see cref="InitializeAsync"/> once (start the app, open a folder…), then
/// <see cref="RunIterationAsync"/> many times — each iteration must leave the system in the logical state it found it (open then close, start then
/// stop), so anything that accumulates is a leak — and <see cref="IAsyncDisposable.DisposeAsync"/> at the end. The runner continues on the
/// caller's synchronization context, so a scenario started on a (headless) UI thread runs every iteration there. Integrators add UI, LSP, DAP and
/// remote scenarios by implementing this; the runner does not change.
/// </summary>
public interface IScenario : IAsyncDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken ct);
    Task RunIterationAsync(int iteration, CancellationToken ct);
}

/// <summary>Ready-made <see cref="IScenario"/>s from delegates.</summary>
public static class Scenario
{
    public static IScenario Create(string name, Action<int> iteration, Action? setup = null, Action? teardown = null) =>
        new DelegateScenario(name, (i, _) => { iteration(i); return Task.CompletedTask; },
            _ => { setup?.Invoke(); return Task.CompletedTask; }, () => { teardown?.Invoke(); return Task.CompletedTask; });

    public static IScenario CreateAsync(string name, Func<int, CancellationToken, Task> iteration, Func<CancellationToken, Task>? setup = null, Func<Task>? teardown = null) =>
        new DelegateScenario(name, iteration, setup ?? (_ => Task.CompletedTask), teardown ?? (() => Task.CompletedTask));

    private sealed class DelegateScenario(string name, Func<int, CancellationToken, Task> iteration, Func<CancellationToken, Task> setup, Func<Task> teardown) : IScenario
    {
        public string Name { get; } = name;
        public Task InitializeAsync(CancellationToken ct) => setup(ct);
        public Task RunIterationAsync(int iteration1, CancellationToken ct) => iteration(iteration1, ct);
        public async ValueTask DisposeAsync() => await teardown();
    }
}
