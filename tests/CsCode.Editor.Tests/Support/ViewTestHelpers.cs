using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using CsCode.Editor.View;

namespace CsCode.Editor.Tests.Support;

/// <summary>Helpers shared by the headless view tests: typing, key presses, pointer positions, popup text and pixel inspection.</summary>
public static class ViewTestHelpers
{
    public static void Type(CodeEditorControl v, string text)
    {
        foreach (var ch in text) v.TextArea.PerformTextInput(ch.ToString());
    }

    public static void Key(Window w, Key key, RawInputModifiers modifiers = RawInputModifiers.None) => w.KeyPress(key, modifiers, PhysicalKey.None, null);

    /// <summary>Window-relative point at the middle of the character at <paramref name="offset"/>.</summary>
    public static Point PointOf(Window window, CodeEditorControl view, int offset)
    {
        var pos = view.Document.PositionAt(offset);
        var tv = new AvaloniaEdit.TextViewPosition(pos.Line + 1, pos.Character + 1);
        var local = view.TextView.GetVisualPosition(tv, VisualYPosition.TextMiddle) - view.TextView.ScrollOffset;
        var inWindow = view.TextView.TranslatePoint(new Point(local.X + 2, local.Y), window) ?? local;
        return inWindow;
    }

    /// <summary>The text shown by every text block under <paramref name="root"/> (inline runs included), one per line.</summary>
    public static string TextOf(Control root)
    {
        var parts = new List<string>();
        var blocks = root.GetVisualDescendants().OfType<TextBlock>().ToList();
        if (root is TextBlock self) blocks.Insert(0, self);
        foreach (var tb in blocks)
        {
            var text = tb.Inlines is { Count: > 0 } ? string.Concat(tb.Inlines.Select(InlineText)) : tb.Text;
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    private static string InlineText(Inline i) => i switch
    {
        Run r => r.Text ?? "",
        Span s => string.Concat(s.Inlines.Select(InlineText)),
        _ => "",
    };

    /// <summary>The bold runs under <paramref name="root"/> — the active parameter of a signature.</summary>
    public static IReadOnlyList<string> BoldRuns(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Inlines is not null)
            .SelectMany(t => t.Inlines!.OfType<Run>()).Where(r => r.FontWeight == FontWeight.Bold).Select(r => r.Text ?? "").ToList();

    /// <summary>Number of pixels in <paramref name="frame"/> (inside <paramref name="region"/>) within <paramref name="tolerance"/> per channel of <paramref name="target"/>.</summary>
    public static int CountPixels(Bitmap frame, Color target, int tolerance, PixelRect? region = null) => ScanPixels(frame, target, tolerance, region).Count;

    /// <summary>The first and last pixel rows of <paramref name="frame"/> (inside <paramref name="region"/>) that have a pixel of <paramref name="target"/>; null when none does.</summary>
    public static (int Top, int Bottom)? PixelRows(Bitmap frame, Color target, int tolerance, PixelRect? region = null) =>
        ScanPixels(frame, target, tolerance, region) is { Count: > 0 } s ? (s.Top, s.Bottom) : null;

    private static (int Count, int Top, int Bottom) ScanPixels(Bitmap frame, Color target, int tolerance, PixelRect? region)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        var wb = WriteableBitmap.Decode(ms);
        using var fb = wb.Lock();
        var area = region ?? new PixelRect(0, 0, fb.Size.Width, fb.Size.Height);
        var (count, top, bottom) = (0, -1, -1);
        var bgra = fb.Format == PixelFormat.Bgra8888;
        var row = new byte[fb.RowBytes];
        for (var y = Math.Max(0, area.Y); y < Math.Min(fb.Size.Height, area.Bottom); y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, row.Length);
            for (var x = Math.Max(0, area.X); x < Math.Min(fb.Size.Width, area.Right); x++)
            {
                var o = x * 4;
                var (r, g, b) = bgra ? (row[o + 2], row[o + 1], row[o]) : (row[o], row[o + 1], row[o + 2]);
                if (Math.Abs(r - target.R) > tolerance || Math.Abs(g - target.G) > tolerance || Math.Abs(b - target.B) > tolerance) continue;
                count++;
                if (top < 0) top = y;
                bottom = y;
            }
        }
        return (count, top, bottom);
    }
}
