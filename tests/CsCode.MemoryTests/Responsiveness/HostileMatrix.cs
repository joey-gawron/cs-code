using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Profiling.Testing.Load;

namespace CsCode.MemoryTests.Responsiveness;

/// <summary>
/// What a hostile load gets to work with: the services of the stack under test, reached only through Core interfaces, and the document being
/// typed into, and a way to run something on the UI thread (commands act on the focused editor). Any host can supply it — the real app
/// (<see cref="AppHostileMatrixTests"/>), a module's own composition, or the integrator's.
/// </summary>
public sealed record HostileContext(
    IDiagnosticService Diagnostics,
    IOutputService Output,
    ILanguageFeatureRegistry Languages,
    ICommandService Commands,
    Uri Document,
    string LanguageId,
    Func<Func<Task>, Task> OnUiThread);

/// <summary>
/// One hostile load of the matrix (MUST 9): started before typing begins, running for the measured window, stopped by disposing what
/// <see cref="Start"/> returned. Loads are expressed against interfaces only.
/// </summary>
public sealed record HostileLoad(string Name, string Description, Func<HostileContext, CancellationToken, IAsyncDisposable> Start)
{
    public override string ToString() => Name;
}

/// <summary>
/// The hostile-load scenario matrix (ARCHITECTURE §6 "prove it"): flooding producers, giant payloads, slow / blocking / hanging / crashing
/// providers and CPU burners, each while the user types. Budgets for every row: dispatcher latency p99 ≤ 16 ms and max ≤ 50 ms, typing latency
/// p99 ≤ 20 ms, the managed heap back within 64 MB of where it started once the load is gone, and the UI queues drained within 3 s.
/// </summary>
public static class HostileMatrix
{
    public const double DispatcherP99Ms = 16, DispatcherMaxMs = 50, TypingP99Ms = 20, HeapGrowthMb = 64;

    public static readonly IReadOnlyList<HostileLoad> Loads =
    [
        new("diagnostics-flood", "200 publishes/s of 500 diagnostics each, over 20 files including the one being typed in", (c, ct) =>
        {
            var files = Enumerable.Range(0, 19).Select(i => new Uri($"file:///hostile/f{i}.cs")).Append(c.Document).ToArray();
            return Run(ct2 => Flooder.DiagnosticsAsync(c.Diagnostics, files, 500, 200, TimeSpan.FromHours(1), "hostile", ct2), ct, () => c.Diagnostics.Clear("hostile"));
        }),
        new("output-flood", "20,000 lines/s of 200 characters into an output channel", (c, ct) =>
        {
            var channel = c.Output.GetOrCreate("Hostile");
            return Run(ct2 => Flooder.OutputAsync(channel, 200, 20_000, TimeSpan.FromHours(1), ct2), ct, channel.Dispose);
        }),
        new("giant-completion", "a completion provider answering 50,000 items, triggered as the user types", (c, ct) =>
            Provider(c, GiantPayload.Completion(), ct)),
        new("slow-completion", "a completion provider answering after 2 s", (c, ct) =>
            Provider(c, SlowResponder.Completion(TimeSpan.FromSeconds(2)), ct)),
        new("blocking-completion", "a completion provider that BLOCKS its calling thread for 200 ms (must never be the UI thread)", (c, ct) =>
            Provider(c, SlowResponder.Completion(TimeSpan.FromMilliseconds(200), blockCaller: true), ct)),
        new("hanging-completion", "a completion provider that never answers", (c, ct) =>
        {
            var hanger = new Hanger();
            return Provider(c, hanger.Completion(), ct, hanger.Release);
        }),
        new("crashing-completion", "a completion provider that throws on every call", (c, ct) =>
            Provider(c, CrashLooper.Completion(), ct)),
        new("cpu-burner", "busy child processes on a quarter of the cores at nice 10 (as language servers run: below normal priority)", (c, ct) =>
        {
            var burner = CpuBurner.ChildProcesses(Math.Max(2, Environment.ProcessorCount / 4), nice: 10); // a quarter of the cores: this machine is shared
            return new AsyncAction(() => burner.Dispose());
        }),
    ];

    private static IAsyncDisposable Run(Func<CancellationToken, Task> flood, CancellationToken ct, Action? cleanup = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = flood(cts.Token);
        return new AsyncAction(async () =>
        {
            cts.Cancel();
            try { await task; } catch (OperationCanceledException) { }
            cts.Dispose();
            cleanup?.Invoke();
        });
    }

    /// <summary>Registers a hostile completion provider for the document's language and asks for completion every 300 ms (as typing "." would).</summary>
    private static IAsyncDisposable Provider(HostileContext c, ICompletionProvider provider, CancellationToken ct, Action? release = null)
    {
        var registration = c.Languages.Register(DocumentSelector.ForLanguage(c.LanguageId), provider);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trigger = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await c.OnUiThread(async () => await c.Commands.ExecuteAsync(KnownCommands.TriggerSuggest)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* a crashing provider must not stop the trigger loop */ }
                try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { break; }
            }
        });
        return new AsyncAction(async () =>
        {
            cts.Cancel();
            await trigger;
            release?.Invoke();
            registration.Dispose();
            cts.Dispose();
        });
    }

    private sealed class AsyncAction(Func<Task> dispose) : IAsyncDisposable
    {
        public AsyncAction(Action dispose) : this(() => { dispose(); return Task.CompletedTask; }) { }
        public async ValueTask DisposeAsync() => await dispose();
    }
}
