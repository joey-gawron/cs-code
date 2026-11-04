using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The colour picker's controls (VS Code's layout): a header with the new colour, the current presentation's text on it (a click cycles
/// through the presentations), and the original colour (a click goes back to it); under it a saturation/value square for the hue, an alpha
/// strip and a hue strip. Pure UI: it reports what the user picks (<see cref="ColorPicked"/>, <see cref="PresentationClicked"/>,
/// <see cref="OriginalClicked"/>) and shows what it is told; <see cref="ColorPickerSession"/> does the rest.
/// </summary>
internal sealed class ColorPickerWidget : Border
{
    public const double PickerWidth = 260;
    private const double HeaderHeight = 26;
    private const double BodyHeight = 150;
    private const double StripWidth = 18;
    private const double Gap = 8;

    private readonly ColorFill _new = new();
    private readonly ColorFill _original = new();
    private readonly TextBlock _label;
    private readonly SaturationValueBox _box = new();
    private readonly ColorStrip _alpha = new(ColorStrip.StripKind.Alpha);
    private readonly ColorStrip _hue = new(ColorStrip.StripKind.Hue);
    private HsvaColor _color;

    public ColorPickerWidget(CodeEditorControl view, HsvaColor original)
    {
        Focusable = true;
        IsTabStop = true;
        Width = PickerWidth;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);
        ClipToBounds = true;
        BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 8, Color = Color.FromArgb(0x60, 0, 0, 0) });
        Avalonia.Automation.AutomationProperties.SetName(this, "Color Picker");

        _label = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = view.Editor.FontFamily,
            FontSize = 12,
            IsHitTestVisible = false,
        };
        var newCell = new Panel { Cursor = new Cursor(StandardCursorType.Hand) };
        newCell.Children.Add(_new);
        newCell.Children.Add(_label);
        ToolTip.SetTip(newCell, "Click to toggle color options (rgb/hsl/hex)");
        newCell.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(newCell).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            PresentationClicked?.Invoke();
        };
        _original.Cursor = new Cursor(StandardCursorType.Hand);
        _original.Width = 74;
        ToolTip.SetTip(_original, "Click to go back to the original color");
        _original.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_original).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            OriginalClicked?.Invoke();
        };
        var header = new DockPanel { Height = HeaderHeight };
        DockPanel.SetDock(_original, Dock.Right);
        header.Children.Add(_original);
        header.Children.Add(newCell);

        _box.Changed += (s, v) => Pick(_color with { S = s, V = v });
        _hue.Changed += h => Pick(_color with { H = h });
        _alpha.Changed += a => Pick(_color with { A = a });
        var body = new Grid
        {
            Height = BodyHeight,
            Margin = new Thickness(Gap),
            ColumnDefinitions = new ColumnDefinitions($"*,{Gap},{StripWidth},{Gap},{StripWidth}"),
        };
        Grid.SetColumn(_alpha, 2);
        Grid.SetColumn(_hue, 4);
        body.Children.Add(_box);
        body.Children.Add(_alpha);
        body.Children.Add(_hue);

        var stack = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);
        stack.Children.Add(body);
        Child = stack;

        _original.Color = original.ToAvalonia();
        ApplyTheme(view.Palette);
        SetColor(original);
    }

    /// <summary>The user picked a colour (square, strips). Not raised by <see cref="SetColor"/>.</summary>
    public event Action<HsvaColor>? ColorPicked;
    /// <summary>The header's presentation text was clicked.</summary>
    public event Action? PresentationClicked;
    /// <summary>The original colour in the header was clicked.</summary>
    public event Action? OriginalClicked;

    public HsvaColor PickedColor => _color;
    public string Label => _label.Text ?? "";

    internal Control SaturationBox => _box;
    internal Control HueStrip => _hue;
    internal Control AlphaStrip => _alpha;
    internal Control PresentationCell => (Control)_label.Parent!;
    internal Control OriginalCell => _original;

    public void ApplyTheme(EditorPalette p)
    {
        Background = p.WidgetBackground;
        BorderBrush = p.WidgetBorder;
    }

    public void SetColor(HsvaColor color)
    {
        _color = color;
        var c = color.ToAvalonia();
        _new.Color = c;
        _box.Set(color.H, color.S, color.V);
        _hue.Value = color.H;
        _alpha.Value = color.A;
        _alpha.Opaque = Avalonia.Media.Color.FromRgb(c.R, c.G, c.B);
        var (r, g, b) = color.ToRgb();
        // dark text on light colours (and on the light checkerboard of a translucent one), as VS Code's header does
        _label.Foreground = color.A < 0.5 || ColorFormats.Luminance(r, g, b) > 0.4 ? Brushes.Black : Brushes.White;
    }

    public void SetLabel(string text) => _label.Text = text;

    /// <summary>
    /// Drops the listeners (the session, and through it the view): a widget that still had the keyboard when its view was destroyed stays
    /// remembered by the window's focus scope until focus moves, and must not keep the view with it.
    /// </summary>
    public void Detach()
    {
        ColorPicked = null;
        PresentationClicked = null;
        OriginalClicked = null;
    }

    private void Pick(HsvaColor color)
    {
        SetColor(color);
        ColorPicked?.Invoke(color);
    }

    // ---- the parts --------------------------------------------------------------------------------------------------

    /// <summary>Draws the light/dark squares that show through a translucent colour.</summary>
    internal static void DrawCheckerboard(DrawingContext context, Rect rect, double cell)
    {
        context.FillRectangle(Brushes.White, rect);
        using (context.PushClip(rect))
        {
            var grey = new ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
            var row = 0;
            for (var y = rect.Top; y < rect.Bottom; y += cell, row++)
                for (var x = rect.Left + (row % 2) * cell; x < rect.Right; x += 2 * cell)
                    context.FillRectangle(grey, new Rect(x, y, cell, cell));
        }
    }

    /// <summary>A rectangle of one colour, over a checkerboard when it is translucent.</summary>
    private sealed class ColorFill : Control
    {
        private Color _color;

        public Color Color
        {
            get => _color;
            set
            {
                if (_color == value) return;
                _color = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            if (_color.A < 255) DrawCheckerboard(context, rect, 5);
            context.FillRectangle(new ImmutableSolidColorBrush(_color), rect);
        }
    }

    /// <summary>Base of the square and the strips: a press starts a drag (the pointer is captured), every move reports a value.</summary>
    private abstract class DragSurface : Control
    {
        protected DragSurface() => Cursor = new Cursor(StandardCursorType.Cross);

        protected abstract void Report(Point p);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(this);
            e.Handled = true;
            Report(e.GetPosition(this));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!ReferenceEquals(e.Pointer.Captured, this)) return;
            e.Handled = true;
            Report(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        }

        protected static double Unit(double v, double length) => length <= 0 ? 0 : Math.Clamp(v / length, 0, 1);
    }

    /// <summary>Saturation left to right, value top to bottom, for one hue.</summary>
    private sealed class SaturationValueBox : DragSurface
    {
        private double _h, _s, _v;
        public event Action<double, double>? Changed;

        public void Set(double h, double s, double v)
        {
            (_h, _s, _v) = (h, s, v);
            InvalidateVisual();
        }

        protected override void Report(Point p) => Changed?.Invoke(Unit(p.X, Bounds.Width), 1 - Unit(p.Y, Bounds.Height));

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            context.FillRectangle(new ImmutableSolidColorBrush(new HsvaColor(_h, 1, 1, 1).ToAvalonia()), rect);
            context.FillRectangle(Gradient(Colors.White, Color.FromArgb(0, 255, 255, 255), horizontal: true), rect);
            context.FillRectangle(Gradient(Color.FromArgb(0, 0, 0, 0), Colors.Black, horizontal: false), rect);
            var at = new Point(_s * rect.Width, (1 - _v) * rect.Height);
            context.DrawEllipse(null, new Pen(Brushes.Black, 1), at, 5, 5);
            context.DrawEllipse(null, new Pen(Brushes.White, 1.5), at, 4, 4);
        }
    }

    internal static LinearGradientBrush Gradient(Color from, Color to, bool horizontal) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = horizontal ? new RelativePoint(1, 0, RelativeUnit.Relative) : new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };

    /// <summary>A vertical strip: the hue (0° at the top, as VS Code) or the alpha (opaque at the top).</summary>
    private sealed class ColorStrip(ColorStrip.StripKind kind) : DragSurface
    {
        public enum StripKind { Hue, Alpha }

        private double _value;
        private Color _opaque = Colors.White;
        public event Action<double>? Changed;

        public double Value
        {
            get => _value;
            set
            {
                _value = value;
                InvalidateVisual();
            }
        }

        public Color Opaque
        {
            get => _opaque;
            set
            {
                if (_opaque == value) return;
                _opaque = value;
                InvalidateVisual();
            }
        }

        protected override void Report(Point p)
        {
            var unit = Unit(p.Y, Bounds.Height);
            Changed?.Invoke(kind == StripKind.Hue ? Math.Min(unit * 360, 359.999) : 1 - unit);
        }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            if (kind == StripKind.Hue)
            {
                var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative) };
                for (var i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(new HsvaColor(i * 60 % 360, 1, 1, 1).ToAvalonia(), i / 6d));
                context.FillRectangle(brush, rect);
            }
            else
            {
                DrawCheckerboard(context, rect, 4);
                context.FillRectangle(Gradient(_opaque, Color.FromArgb(0, _opaque.R, _opaque.G, _opaque.B), horizontal: false), rect);
            }
            var y = (kind == StripKind.Hue ? _value / 360 : 1 - _value) * rect.Height;
            var marker = new Rect(-1, Math.Clamp(y - 2, -1, rect.Height - 3), rect.Width + 2, 4);
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), marker);
            context.DrawRectangle(null, new Pen(Brushes.White, 1), marker.Deflate(1));
        }
    }
}
