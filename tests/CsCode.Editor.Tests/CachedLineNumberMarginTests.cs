using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// AvaloniaEdit's line-number margin re-measured and re-rendered after every keystroke and shaped every visible number again each time.
/// <see cref="CachedLineNumberMargin"/> keeps the laid-out numbers: typing on a line lays out none, a new line lays out only the numbers that
/// appear, and what it draws is pixel for pixel what the stock margin drew.
/// </summary>
public class CachedLineNumberMarginTests
{
    private static string Lines(int count) => string.Join("\n", Enumerable.Range(0, count).Select(i => $"line {i} of some text"));

    private static CachedLineNumberMargin Margin(CodeEditorControl view) => Assert.Single(view.TextArea.LeftMargins.OfType<CachedLineNumberMargin>());

    private static byte[] Pixels(Avalonia.Controls.Window window, out int stride)
    {
        var frame = window.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
        frame.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        var wb = WriteableBitmap.Decode(ms);
        using var fb = wb.Lock();
        stride = fb.RowBytes;
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    [AvaloniaFact]
    public async Task The_editor_uses_the_caching_margin_also_after_line_numbers_are_turned_off_and_on()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(50), "a.txt");
        Margin(view);
        Assert.DoesNotContain(view.TextArea.LeftMargins, m => m.GetType() == typeof(LineNumberMargin));
        host.Config.Set("editor.lineNumbers", "off");
        await ViewHost.WaitFor(() => !view.TextArea.LeftMargins.OfType<LineNumberMargin>().Any(), "line numbers off");
        host.Config.Set("editor.lineNumbers", "on");
        await ViewHost.WaitFor(() => view.TextArea.LeftMargins.OfType<LineNumberMargin>().Any(), "line numbers on");
        Margin(view);
        Assert.Single(view.TextArea.LeftMargins.OfType<LineNumberMargin>());
    }

    [AvaloniaFact]
    public async Task Typing_lays_out_no_line_number_and_a_new_line_only_the_numbers_that_appear()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(400), "a.txt");
        view.Controller.SetSelection(TextRange.At(5, 4));
        ViewHost.Pump();
        var margin = Margin(view);
        var visible = view.TextView.VisualLines.Count;
        Assert.True(margin.LayoutsBuilt >= visible, $"the first frame lays out every visible number ({margin.LayoutsBuilt} for {visible} lines)");
        var built = margin.LayoutsBuilt;
        for (var i = 0; i < 20; i++)
        {
            view.TextArea.PerformTextInput("x");
            ViewHost.Pump();
        }
        Assert.Equal(built, margin.LayoutsBuilt);
        view.TextArea.PerformTextInput("\n"); // the lines below move down, but the numbers on screen stay 1..N
        ViewHost.Pump();
        Assert.InRange(margin.LayoutsBuilt - built, 0, 1);
        built = margin.LayoutsBuilt;
        var first = view.TextView.VisualLines[0].FirstDocumentLine.LineNumber;
        view.Editor.ScrollToLine(first + visible + 2); // three lines further down: three numbers come into view
        ViewHost.Pump();
        var scrolledBy = view.TextView.VisualLines[0].FirstDocumentLine.LineNumber - first;
        Assert.True(scrolledBy > 0, "the view scrolled");
        Assert.InRange(margin.LayoutsBuilt - built, 1, scrolledBy + 1);
    }

    [AvaloniaFact]
    public async Task A_new_line_number_colour_lays_the_numbers_out_again_in_it()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(100), "a.txt");
        ViewHost.Pump();
        var margin = Margin(view);
        var built = margin.LayoutsBuilt;
        var red = Color.FromRgb(250, 10, 10);
        view.Editor.LineNumbersForeground = new SolidColorBrush(red);
        margin.InvalidateVisual();
        ViewHost.Pump();
        Assert.True(margin.LayoutsBuilt - built >= view.TextView.VisualLines.Count, "every visible number is laid out again in the new brush");
        var region = new PixelRect(0, 0, (int)Math.Ceiling(margin.Bounds.Right + 40), (int)window.Bounds.Height);
        Assert.True(ViewTestHelpers.CountPixels(window.CaptureRenderedFrame()!, red, 60, region) > 20, "the numbers are drawn in the new colour");
    }

    [AvaloniaFact]
    public async Task The_numbers_are_drawn_exactly_where_and_as_AvaloniaEdits_margin_draws_them()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(3000), "a.txt");
        view.Controller.SetSelection(TextRange.At(1200, 0)); // four-digit numbers, scrolled
        ViewHost.Pump();
        var ours = Margin(view);
        var withCache = Pixels(window, out var stride);

        var margins = view.TextArea.LeftMargins;
        var index = margins.IndexOf(ours);
        var stock = new LineNumberMargin();
        stock.Bind(TemplatedControl.ForegroundProperty, view.Editor.GetBindingObservable(TextEditor.LineNumbersForegroundProperty));
        margins[index] = stock;
        ViewHost.Pump();
        Assert.Equal(ours.Bounds.Width, stock.Bounds.Width, 3);
        var withStock = Pixels(window, out _);

        Assert.Equal(withStock.Length, withCache.Length);
        var differing = 0;
        var firstRow = -1;
        for (var i = 0; i < withStock.Length; i++)
        {
            if (withStock[i] == withCache[i]) continue;
            differing++;
            if (firstRow < 0) firstRow = i / stride;
        }
        Assert.True(differing == 0, $"{differing} bytes differ from the stock margin's frame (first on row {firstRow})");
    }
}
