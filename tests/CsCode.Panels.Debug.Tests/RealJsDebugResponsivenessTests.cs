using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Views;
using CsCode.TestDoubles;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// The matrix row "a program printing 1M lines keeps the UI responsive", for real: js-debug runs the TypeScript sample's flood mode (1,000,000
/// lines written to stdout, captured with <c>outputCapture: "std"</c>) while the Debug Console is on screen in a headless window. Two DAP
/// connections (parent + child) feed the debug lane; the UI thread must stay within the hostile budgets (dispatcher p99 &lt; 16 ms, max &lt; 50 ms),
/// with the same three-attempt allowance as <see cref="ResponsivenessTests"/> for a machine shared with other agents.
/// </summary>
[Trait("Category", "RealAdapter")]
public class RealJsDebugResponsivenessTests
{
    private const double P99BudgetMs = 16;
    private const double MaxBudgetMs = 50;

    [AvaloniaFact(Timeout = 300_000)]
    public async Task A_typescript_program_printing_a_million_lines_keeps_the_ui_within_budget()
    {
        var attempts = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (p99, max, samples, lines, elapsed) = await FloodOnceAsync();
            var line = $"1M-line js-debug flood (attempt {attempt}): dispatcher p99 {p99:F1} ms, max {max:F1} ms, {samples} samples; console kept {lines} lines; session took {elapsed} ms";
            attempts.Add(line);
            RealJsDebugPanelsTests.Note(line);
            if (samples > 20 && p99 < P99BudgetMs && max < MaxBudgetMs) return;
        }
        Assert.Fail("dispatcher latency exceeded the budget on all attempts: " + string.Join(" / ", attempts));
    }

    private static async Task<(double P99, double Max, int Samples, int Lines, long ElapsedMs)> FloodOnceAsync()
    {
        TestTheme.Apply(VsThemes.Dark);
        await using var app = await RealJsDebugPanelsTests.App.CreateAsync();
        var view = (ConsoleView)app.Tools["debug.console"].CreateView(app.Provider);
        var console = (DebugConsoleViewModel)view.DataContext!;
        var window = new Window { Width = 900, Height = 300, Content = view };
        window.Show();
        try
        {
            // Warm-up (as in ResponsivenessTests): the first session and the first console rows are JIT/template work, not steady state.
            _ = await app.Debug.StartAsync(app.Config("flood", "2000")) ?? throw new InvalidOperationException("no session");
            await PanelRig.Eventually(() => app.Debug.State == DebugState.Inactive && console.Lines.Any(l => l.Text == "flood done"), 60_000, "warm-up session");
            console.Clear();
            await Task.Delay(300);

            var latencies = new List<double>();
            using var cts = new CancellationTokenSource();
            var heartbeat = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var sent = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() => { lock (latencies) latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds); }, DispatcherPriority.Normal);
                    try { await Task.Delay(1, cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            });
            var sw = Stopwatch.StartNew();
            _ = await app.Debug.StartAsync(app.Config("flood", "1000000")) ?? throw new InvalidOperationException("no session");
            await PanelRig.Eventually(() => app.Debug.State == DebugState.Inactive, 120_000, "the flood ran to its end");
            await PanelRig.Eventually(() => console.Lines.Any(l => l.Text == "flood done"), 20_000, "the last line reached the console");
            sw.Stop();
            cts.Cancel();
            await heartbeat;
            await Task.Delay(60);
            double[] sorted;
            lock (latencies) sorted = [.. latencies.Order()];
            Assert.True(console.Lines.Count <= DebugConsoleViewModel.MaxLines);
            return (sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.99)], sorted.Length == 0 ? 0 : sorted[^1], sorted.Length, console.Lines.Count, sw.ElapsedMilliseconds);
        }
        finally
        {
            window.Content = null;
            window.Close();
            view.Dispose();
        }
    }
}
