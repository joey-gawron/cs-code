using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using CsCode.Panels.Debug.Views;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>
/// The Debug Console under a flood: one apply pass per batch the pump delivers (one Add, at most one Remove), recycled self-drawing rows,
/// coloured rows, and REPL lines that keep their place after an oversize batch.
/// </summary>
public class ConsoleFloodTests
{
    private static string Lines(int from, int count) => string.Concat(Enumerable.Range(from, count).Select(i => "flood line " + i + "\n"));

    [AvaloniaFact]
    public async Task A_batch_of_thousands_of_lines_reaches_the_list_in_one_add_and_trimming_is_one_remove()
    {
        using var rig = new PanelRig();
        using var vm = new DebugConsoleViewModel(rig.Services);
        var changes = new List<(NotifyCollectionChangedAction Action, int Count, int Index)>();
        vm.Lines.CollectionChanged += (_, e) => changes.Add((e.Action, (e.NewItems ?? e.OldItems)?.Count ?? 0, e.Action == NotifyCollectionChangedAction.Add ? e.NewStartingIndex : e.OldStartingIndex));

        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", Lines(0, 3_000)));
        await PanelRig.Eventually(() => vm.Lines.Count == 3_000, what: "first batch");
        // Before: 150 lines per UI callback, 20 notifications (and as many row realisations) for one batch.
        Assert.Equal([(NotifyCollectionChangedAction.Add, 3_000, 0)], changes);

        changes.Clear();
        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", Lines(3_000, 4_000)));
        await PanelRig.Eventually(() => vm.Lines.Count > 0 && vm.Lines[^1].Text == "flood line 6999", what: "second batch");
        Assert.Equal([(NotifyCollectionChangedAction.Remove, 2_000, 0), (NotifyCollectionChangedAction.Add, 4_000, 1_000)], changes);
        Assert.Equal(DebugConsoleViewModel.MaxLines, vm.Lines.Count);
        Assert.Equal("flood line 2000", vm.Lines[0].Text);
    }

    [AvaloniaFact]
    public async Task Many_events_within_one_frame_are_applied_together()
    {
        using var rig = new PanelRig();
        using var vm = new DebugConsoleViewModel(rig.Services);
        await Task.Delay(50); // the (empty) seed has been drained
        var adds = 0;
        vm.Lines.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) adds++; };
        var applied = vm.Applied;
        // One console.log per line: 2,000 events arriving faster than a frame.
        await Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++) rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", "log line " + i + "\n"));
        }, TestContext.Current.CancellationToken);
        await PanelRig.Eventually(() => vm.Lines.Count > 0 && vm.Lines[^1].Text == "log line 1999", what: "all lines");
        Assert.Equal(2_000, vm.Lines.Count);
        Assert.True(adds <= 4 && vm.Applied - applied == adds, $"{adds} adds, {vm.Applied - applied} apply passes for 2,000 events");
    }

    [AvaloniaFact]
    public async Task Rows_are_recycled_self_drawing_controls_and_follow_the_newest_line()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = (ConsoleView)sc.View("debug.console");
        var model = (DebugConsoleViewModel)view.DataContext!;
        var window = new Window { Width = 900, Height = 300, Content = view };
        window.Show();
        try
        {
            for (var batch = 0; batch < 20; batch++)
            {
                sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", Lines(batch * 1_000, 1_000)));
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            await PanelRig.Eventually(() => model.Lines.Count > 0 && model.Lines[^1].Text == "flood line 19999", what: "the flood shown");
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var rows = view.GetVisualDescendants().OfType<ConsoleLineView>().ToList();
            Assert.DoesNotContain(view.GetVisualDescendants(), v => v is TextBlock { DataContext: ConsoleLine });
            // ~14 rows fit in the window; the list keeps a few more realised. Never one control per line.
            Assert.InRange(rows.Count, 5, 60);
            Assert.Contains(rows, r => r.IsEffectivelyVisible && r.Line?.Text == "flood line 19999");
        }
        finally
        {
            window.Content = null;
            window.Close();
            view.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Ansi_colours_are_drawn_from_the_theme_and_the_text_is_shown_without_escapes()
    {
        TestTheme.Apply(VsThemes.Dark);
        using var sc = new Scenario();
        var view = (ConsoleView)sc.View("debug.console");
        var model = (DebugConsoleViewModel)view.DataContext!;
        var window = new Window { Width = 600, Height = 200, Content = view };
        window.Show();
        try
        {
            var block = new string('█', 12);
            sc.Rig.Debug.EmitOutput(new DebugOutputEventArgs(sc.Session, "stdout", $"\u001b[32m{block}\u001b[0m plain\n"));
            await PanelRig.Eventually(() => model.Lines.Any(l => l.Text == block + " plain"), what: "the line");
            for (var i = 0; i < 6; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(15, TestContext.Current.CancellationToken);
            }
            var row = view.GetVisualDescendants().OfType<ConsoleLineView>().Single(r => r.Line?.Text == block + " plain");
            var green = ((ISolidColorBrush)Avalonia.Application.Current!.FindResource("vsc:terminal.ansiGreen")!).Color;
            var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
            var origin = row.TranslatePoint(new Avalonia.Point(0, 0), window) ?? throw new InvalidOperationException("row not in the window");
            Assert.True(CountNear(frame, green, (int)origin.X, (int)origin.Y, (int)row.Bounds.Width, (int)row.Bounds.Height) > 20,
                $"no {green} pixels in the coloured row");
        }
        finally
        {
            window.Content = null;
            window.Close();
            view.Dispose();
        }
    }

    /// <summary>Pixels of <paramref name="frame"/> within the given window rectangle that are close to <paramref name="color"/>.</summary>
    private static int CountNear(Avalonia.Media.Imaging.WriteableBitmap frame, Color color, int x0, int y0, int width, int height)
    {
        using var fb = frame.Lock();
        var scale = fb.Size.Width / frame.Size.Width;
        var pixels = new byte[fb.RowBytes * fb.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, pixels, 0, pixels.Length);
        var rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
        var n = 0;
        for (var y = (int)(y0 * scale); y < Math.Min(fb.Size.Height, (int)((y0 + height) * scale)); y++)
            for (var x = (int)(x0 * scale); x < Math.Min(fb.Size.Width, (int)((x0 + width) * scale)); x++)
            {
                var i = y * fb.RowBytes + x * 4;
                var (r, g, b) = rgba ? (pixels[i], pixels[i + 1], pixels[i + 2]) : (pixels[i + 2], pixels[i + 1], pixels[i]);
                if (Math.Abs(r - color.R) < 12 && Math.Abs(g - color.G) < 12 && Math.Abs(b - color.B) < 12) n++;
            }
        return n;
    }

    [AvaloniaFact]
    public async Task A_repl_line_keeps_its_place_after_an_oversize_batch_whose_head_the_history_cut()
    {
        // Follow-up "REPL line placement after a flood": the real history keeps an oversize batch as `with { Output = tail }` (OutputHistory
        // cuts its head), a new instance with a new string. The line entered after the batch was replayed before it (it matched neither).
        using var rig = new PanelRig();
        rig.Session.Evaluator = (_, _, _) => new DebugEvaluateResult("42");
        rig.StopAt(2);
        var first = new DebugConsoleViewModel(rig.Services);
        var batch = new DebugOutputEventArgs(rig.Session, "stdout", Lines(0, 6_000));
        rig.Debug.EmitOutput(batch);
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "flood line 5999"), what: "the batch");
        first.Input = "x";
        await first.SubmitAsync();
        await PanelRig.Eventually(() => first.Lines.Any(l => l.Text == "42"), what: "the result");
        first.Dispose();

        // What the real OutputHistory holds after cutting the batch to fit its 5,000 lines (no console is open: the transcript stays).
        rig.Debug.ClearOutputHistory();
        var cut = batch with { Output = ConsoleOutputPump.TailLines(batch.Output, 4_000) };
        Assert.Equal(batch.Sequence, cut.Sequence);
        rig.Debug.EmitOutput(cut);
        rig.Debug.EmitOutput(new DebugOutputEventArgs(rig.Session, "stdout", "later\n"));

        using var second = new DebugConsoleViewModel(rig.Services);
        await PanelRig.Eventually(() => second.Lines.Any(l => l.Text == "later"), what: "re-seeded");
        string[] tail = [.. second.Lines.Skip(second.Lines.Count - 4).Select(l => l.Text)];
        Assert.Equal(["flood line 5999", "> x", "42", "later"], tail);
    }
}
