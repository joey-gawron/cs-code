using System.Globalization;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.MemoryTests.Toolkit;
using CsCode.Profiling.Testing;
using CsCode.Profiling.Testing.Load;
using Xunit;

namespace CsCode.MemoryTests.Responsiveness;

/// <summary>
/// The Core-level subset of the hostile matrix (MUST 9), on a simulated UI thread (a dedicated thread with a FIFO queue, <see cref="SimulatedUiThread"/>):
/// a <see cref="DiagnosticService"/> flood and an <see cref="Emitter{T}"/> fan-out from producer threads, consumed the way ARCHITECTURE §6 requires
/// (one-way, coalesced latest-wins per key, small work per UI callback). Budgets: dispatcher p99 ≤ 16 ms, max ≤ 50 ms; typing p99 ≤ 20 ms; the
/// queue drained and the heap bounded afterwards. Best of three attempts (shared machine), every attempt's numbers in the output.
/// </summary>
public sealed class CoreResponsivenessTests(ITestOutputHelper output)
{
    /// <summary>A UI-side consumer of the diagnostics store: coalesces Changed into one pending UI job and reads only the open document's entries there.</summary>
    private sealed class ErrorSquiggles : IDisposable
    {
        private readonly SimulatedUiThread _ui;
        private readonly IDiagnosticService _diagnostics;
        private readonly Uri _open;
        private readonly IDisposable _sub;
        private int _pending;
        public int Updates;
        public int Shown;

        public ErrorSquiggles(SimulatedUiThread ui, IDiagnosticService diagnostics, Uri open)
        {
            (_ui, _diagnostics, _open) = (ui, diagnostics, open);
            _sub = diagnostics.Changed.Subscribe(uris =>
            {
                if (!uris.Contains(_open) || Interlocked.Exchange(ref _pending, 1) != 0) return; // latest wins: one job in flight
                _ui.Post(() =>
                {
                    Volatile.Write(ref _pending, 0);
                    Shown = _diagnostics.Get(_open).Count;
                    Updates++;
                });
            });
        }

        public void Dispose() => _sub.Dispose();
    }

    private async Task Best(string name, Func<Task<(bool Ok, string Report)>> attempt)
    {
        var failures = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var (ok, report) = await attempt();
            output.WriteLine($"{name} attempt {i} (load {LoadAverage.OneMinute()}): {report}");
            if (ok) return;
            failures.Add(report);
        }
        Assert.Fail(name + ": budgets missed on every attempt:\n" + string.Join('\n', failures));
    }

    [Fact]
    public Task A_diagnostics_flood_keeps_the_simulated_ui_thread_and_typing_within_budget() => Best("diagnostics flood", async () =>
    {
        using var ui = new SimulatedUiThread();
        var diagnostics = new DiagnosticService();
        var open = new Uri("file:///hostile/open.cs");
        var files = Enumerable.Range(0, 19).Select(i => new Uri($"file:///hostile/f{i}.cs")).Append(open).ToArray();
        using var squiggles = new ErrorSquiggles(ui, diagnostics, open);
        var doc = new CsCode.Core.Text.InMemoryTextDocument(open, "csharp");
        FullGc.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        using var probe = new ResponsivenessProbe(ui.Post).Start();
        var typing = new TypingLatencyProbe(ui.Post, c => doc.ApplyEdits([CsCode.Core.Text.TextEdit.Insert(doc.PositionAt(doc.Length), c.ToString())]));
        var flood = Flooder.DiagnosticsAsync(diagnostics, files, 500, 2000, TimeSpan.FromSeconds(3));
        await typing.TypeAsync(new string('x', 50), TimeSpan.FromMilliseconds(50));
        var floodResult = await flood;
        var typed = await typing.DrainAsync();
        var dispatcher = await probe.StopAsync();
        diagnostics.Clear("flooder");
        FullGc.Collect();
        var heapMb = (GC.GetTotalMemory(true) - heapBefore) / 1024.0 / 1024.0;
        var ok = dispatcher.P99Ms <= 16 && dispatcher.MaxMs <= 50 && typed.Total.P99Ms <= 20 && typed.Missing == 0 && dispatcher.Pending == 0 && heapMb < 64;
        return (ok, string.Create(CultureInfo.InvariantCulture,
            $"{floodResult.Messages} publishes ({floodResult.PerSecond:F0}/s × 500 diagnostics), {squiggles.Updates} UI updates; {dispatcher}; {typed}; heap {heapMb:+0.0;-0.0} MB"));
    });

    [Fact]
    public Task An_emitter_fan_out_to_a_thousand_subscribers_keeps_the_simulated_ui_thread_within_budget() => Best("emitter fan-out", async () =>
    {
        using var ui = new SimulatedUiThread();
        var emitter = new Emitter<int>();
        using var store = new DisposableStore();
        var pending = new int[1000];
        var applied = 0L;
        for (var i = 0; i < 1000; i++)
        {
            var slot = i;
            // each subscriber is a view-model: it coalesces its own updates into at most one queued UI job
            store.Add(emitter.Subscribe(v =>
            {
                if (Interlocked.Exchange(ref pending[slot], 1) != 0) return;
                ui.Post(() => { Volatile.Write(ref pending[slot], 0); Interlocked.Increment(ref applied); });
            }));
        }
        using var probe = new ResponsivenessProbe(ui.Post).Start();
        var doc = new CsCode.Core.Text.InMemoryTextDocument(new Uri("file:///fanout.txt"), "plaintext");
        var typing = new TypingLatencyProbe(ui.Post, c => doc.ApplyEdits([CsCode.Core.Text.TextEdit.Insert(doc.PositionAt(doc.Length), c.ToString())]));
        var fire = Flooder.RunAsync(n => emitter.Fire((int)n), 2000, TimeSpan.FromSeconds(3));
        await typing.TypeAsync(new string('y', 50), TimeSpan.FromMilliseconds(50));
        var fired = await fire;
        var typed = await typing.DrainAsync();
        var dispatcher = await probe.StopAsync(TimeSpan.FromSeconds(3));
        var ok = dispatcher.P99Ms <= 16 && dispatcher.MaxMs <= 50 && typed.Total.P99Ms <= 20 && typed.Missing == 0 && dispatcher.Pending == 0;
        return (ok, string.Create(CultureInfo.InvariantCulture,
            $"{fired.Messages} fires ({fired.PerSecond:F0}/s) to 1000 subscribers = {fired.Messages * 1000} deliveries, {Interlocked.Read(ref applied)} UI jobs; {dispatcher}; {typed}"));
    });
}
