using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// Vim must not make typing slower than the plain editor, and its big operations must leave the dispatcher responsive (ARCHITECTURE §6: dispatcher
/// p99 &lt; 16 ms, max &lt; 50 ms; typing p99 ≤ 20 ms). Wall-clock numbers on a machine shared with other agents: each measurement takes the best of
/// up to three attempts and prints them all.
/// </summary>
public class VimPerformanceTests(ITestOutputHelper output)
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    private static (double P50, double P99, double Max) Stats(List<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        double Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return (Pct(0.5), Pct(0.99), sorted[^1]);
    }

    /// <summary>
    /// Times each character typed as a real key press + text input into the focused editor: the synchronous input handling, i.e. what one
    /// keystroke costs the UI thread (rendering, which headless tests do in software on this thread, is left out: it is the same either way).
    /// </summary>
    private static List<double> Type(VimApp app, string text)
    {
        var samples = new List<double>(text.Length);
        foreach (var c in text)
        {
            var key = c == ' ' ? Avalonia.Input.Key.Space : Avalonia.Input.Key.A + (char.ToLowerInvariant(c) - 'a');
            samples.Add(app.RaiseKey(key, Avalonia.Input.KeyModifiers.None, c.ToString()));
            Dispatcher.UIThread.RunJobs();
        }
        return samples;
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Typing_with_vim_on_costs_about_what_it_costs_with_vim_off()
    {
        var text = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"    public int Field{i} = {i}; // a line of C#-like text")) + "\n";
        await using var app = await VimApp.StartAsync(text, "big.txt", Quiet);
        var commands = app.Host.Get<ICommandService>();
        const string typed = "the quick brown fox jumps over the lazy dog and keeps typing for a while to get a steady measurement of keys ";
        (double P50, double P99, double Max) on = default, off = default;
        var ok = false;
        for (var attempt = 1; attempt <= 3 && !ok; attempt++)
        {
            // Vim on: Insert mode, characters through the engine and the editor's typing path
            app.Keys("1500Go<Esc>");
            app.Keys("i");
            Type(app, "warm up ");
            on = Stats(Type(app, typed + typed));
            app.Keys("<Esc>");

            // Vim off: the plain editor
            await commands.ExecuteAsync("vim.toggle");
            await DevScene.SettleAsync(2);
            await app.Host.Get<INavigationService>().OpenAsync(app.View.Document.Uri, Core.Text.TextRange.At(1500, 0));
            app.View.Modal.Controller.Focus();
            Type(app, "warm up ");
            off = Stats(Type(app, typed + typed));
            await commands.ExecuteAsync("vim.toggle");
            await app.FocusAsync();

            output.WriteLine($"attempt {attempt}: per key, Vim on p50 {on.P50:F2} ms p99 {on.P99:F2} ms max {on.Max:F1} ms; Vim off p50 {off.P50:F2} ms p99 {off.P99:F2} ms max {off.Max:F1} ms");
            ok = on.P99 <= 20 && on.P50 <= off.P50 * 1.5 + 0.5;
        }
        Assert.True(on.P99 <= 20, $"typing with Vim: p99 {on.P99:F2} ms (budget 20 ms)");
        Assert.True(on.P50 <= off.P50 * 1.5 + 0.5, $"typing with Vim: p50 {on.P50:F2} ms vs {off.P50:F2} ms without");
    }

    private sealed class Heartbeat : IDisposable
    {
        private readonly List<double> _latencies = new(4096);
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        public Heartbeat()
        {
            _thread = new Thread(() =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var scheduled = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() => _latencies.Add(Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds), DispatcherPriority.Normal);
                    _cts.Token.WaitHandle.WaitOne(10);
                }
            }) { IsBackground = true, Name = "vim-heartbeat" };
            _thread.Start();
        }

        public (double P50, double P99, double Max, int Count) Snapshot()
        {
            var (p50, p99, max) = Stats(_latencies);
            return (p50, p99, max, _latencies.Count);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _thread.Join(500);
            _cts.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task A_global_command_over_a_big_file_runs_in_slices_and_the_dispatcher_stays_responsive()
    {
        var text = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => i % 3 == 0 ? $"match {i} alpha" : $"other {i} beta")) + "\n";
        await using var app = await VimApp.StartAsync(text, "big.txt", Quiet);
        (double P50, double P99, double Max, int Count) result = default;
        (int Count, double Longest) slices = default;
        var keyMs = 0.0;
        var ok = false;
        for (var attempt = 1; attempt <= 3 && !ok; attempt++)
        {
            app.Keys("gg");
            await DevScene.SettleAsync(2);
            using (var beat = new Heartbeat())
            {
                await Task.Delay(100);
                app.Keys(":g/match/s/alpha/ALPHA");
                app.Vim.LongestSliceMs = 0;
                app.Vim.SliceCount = 0;
                keyMs = app.RaiseKey(Avalonia.Input.Key.Enter);              // the first slice runs inside the key, the rest on dispatcher ticks
                var done = await RealAppHost.WaitFor(() => !app.Engine.HasPendingWork, 60_000);
                await Task.Delay(100);
                Dispatcher.UIThread.RunJobs();
                result = beat.Snapshot();
                Assert.True(done, "the :g command did not finish");
            }
            Assert.Contains("match 9999 ALPHA", app.Text);
            Assert.DoesNotContain("match 9999 alpha", app.Text);
            slices = (app.Vim.SliceCount, app.Vim.LongestSliceMs);
            output.WriteLine($"attempt {attempt}: Enter key {keyMs:F1} ms; {slices.Count} slices, longest {slices.Longest:F1} ms; dispatcher latency (headless rendering included) p50 {result.P50:F1} ms p99 {result.P99:F1} ms max {result.Max:F1} ms over {result.Count} ticks");
            ok = keyMs < 50 && slices.Longest < 16 && result.Max < 50;
            app.Keys("u");                                                 // one undo step for the whole :g
            Assert.True(await RealAppHost.WaitFor(() => !app.Engine.HasPendingWork && app.Text.Contains("match 9999 alpha"), 30_000));
        }
        Assert.True(keyMs < 50, $"the key that started :g took {keyMs:F1} ms");
        Assert.True(slices.Count > 10, $"only {slices.Count} slices: the command did not run sliced");
        Assert.True(slices.Longest < 16, $"longest slice {slices.Longest:F1} ms");
        Assert.True(result.Max < 50, $"dispatcher latency max {result.Max:F1} ms during :g over 20k lines");
    }
}
