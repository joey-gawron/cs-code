using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CsCode.App.Views;

/// <summary>
/// The application icon: original artwork (a blue rounded tile with a white "C" bracket pair), drawn in code and rasterised at startup so no binary
/// asset is committed. It deliberately resembles no Microsoft or Visual Studio mark.
/// </summary>
public static class AppIcon
{
    public static WindowIcon Create(int size = 64)
    {
        var s = size;
        var canvas = new Canvas { Width = s, Height = s };
        canvas.Children.Add(new Rectangle { Width = s, Height = s, RadiusX = s * 0.2, RadiusY = s * 0.2, Fill = new SolidColorBrush(Color.Parse("#1F6FB2")) });
        var stroke = new SolidColorBrush(Color.Parse("#F2F6FA"));
        var w = s * 0.085;
        // A "C" opening to the right, flanked by a small chevron: reads as a code editor mark.
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Stroke = stroke, StrokeThickness = w, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse($"M {s * 0.62:F1},{s * 0.30:F1} A {s * 0.22:F1},{s * 0.22:F1} 0 1 0 {s * 0.62:F1},{s * 0.70:F1}"),
        });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Stroke = stroke, StrokeThickness = w, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse($"M {s * 0.68:F1},{s * 0.42:F1} L {s * 0.80:F1},{s * 0.50:F1} L {s * 0.68:F1},{s * 0.58:F1}"),
        });
        canvas.Measure(new Size(s, s));
        canvas.Arrange(new Rect(0, 0, s, s));
        var bitmap = new RenderTargetBitmap(new PixelSize(s, s));
        bitmap.Render(canvas);
        return new WindowIcon(bitmap);
    }
}
