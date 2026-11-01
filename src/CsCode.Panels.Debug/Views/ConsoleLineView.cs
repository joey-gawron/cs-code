using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using CsCode.Panels.Debug.Console;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// The colours the Debug Console's rows draw with, resolved once from the theme's <c>vsc:*</c> brushes by <see cref="ConsoleView"/> and
/// inherited by every row (<see cref="ConsoleLineView.BrushesProperty"/>): a row does no resource lookup of its own.
/// </summary>
public sealed class ConsoleBrushes(IBrush? error, IBrush? debugger, IBrush? important, IBrush? input, IBrush? info, IBrush? background, IReadOnlyList<IBrush?> ansi)
{
    public IBrush? Error { get; } = error;
    public IBrush? Debugger { get; } = debugger;
    public IBrush? Important { get; } = important;
    public IBrush? Input { get; } = input;
    public IBrush? Info { get; } = info;
    /// <summary>The panel's background (an inverse-video run's text colour when it has no background colour of its own).</summary>
    public IBrush? Background { get; } = background;
    /// <summary>The 16 ANSI colours (<c>terminal.ansiBlack</c> … <c>terminal.ansiBrightWhite</c>).</summary>
    public IReadOnlyList<IBrush?> Ansi { get; } = ansi;

    public static readonly string[] AnsiColorIds =
    [
        "terminal.ansiBlack", "terminal.ansiRed", "terminal.ansiGreen", "terminal.ansiYellow", "terminal.ansiBlue", "terminal.ansiMagenta", "terminal.ansiCyan", "terminal.ansiWhite",
        "terminal.ansiBrightBlack", "terminal.ansiBrightRed", "terminal.ansiBrightGreen", "terminal.ansiBrightYellow", "terminal.ansiBrightBlue", "terminal.ansiBrightMagenta", "terminal.ansiBrightCyan", "terminal.ansiBrightWhite",
    ];

    /// <summary>Every brush, for watching in-place colour changes (the theme mutates its brushes instead of replacing them).</summary>
    public IEnumerable<IBrush> All => new[] { Error, Debugger, Important, Input, Info, Background }.Concat(Ansi).OfType<IBrush>();
}

/// <summary>
/// One row of the Debug Console: draws a <see cref="ConsoleLine"/> (its <see cref="Visual.DataContext"/>) as a single text layout in the
/// category's colour, with the line's ANSI spans (colours, bold, italic, underline, strikethrough, inverse, dim) applied as style overrides.
/// It replaces a <c>TextBlock</c> with six class bindings: the list recycles a few dozen of these for any number of lines, and a recycled row
/// only builds one layout (no binding updates, no style re-evaluation from class changes), which is most of what the console's UI thread does
/// per frame under a flood.
/// </summary>
public sealed class ConsoleLineView : Control
{
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<ConsoleLineView>();
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<ConsoleLineView>();
    public static readonly StyledProperty<FontFeatureCollection?> FontFeaturesProperty = TextElement.FontFeaturesProperty.AddOwner<ConsoleLineView>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ConsoleLineView>();

    /// <summary>Inherited: set once on the list by <see cref="ConsoleView"/>.</summary>
    public static readonly AttachedProperty<ConsoleBrushes?> BrushesProperty =
        AvaloniaProperty.RegisterAttached<ConsoleLineView, Control, ConsoleBrushes?>("Brushes", inherits: true);

    private const double HorizontalPadding = 8;
    private ConsoleLine? _line;
    private TextLayout? _layout;

    static ConsoleLineView()
    {
        AffectsMeasure<ConsoleLineView>(FontFamilyProperty, FontSizeProperty, FontFeaturesProperty, BrushesProperty);
        AffectsRender<ConsoleLineView>(ForegroundProperty);
    }

    public static ConsoleBrushes? GetBrushes(Control control) => control.GetValue(BrushesProperty);
    public static void SetBrushes(Control control, ConsoleBrushes? value) => control.SetValue(BrushesProperty, value);

    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFeatureCollection? FontFeatures { get => GetValue(FontFeaturesProperty); set => SetValue(FontFeaturesProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The line shown (for tests and the accessibility name).</summary>
    public ConsoleLine? Line => _line;

    /// <summary>Text layouts built so far by this row (tests: one per line shown, not one per property change).</summary>
    internal int LayoutsBuilt { get; private set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var line = DataContext as ConsoleLine;
        if (ReferenceEquals(line, _line)) return;
        _line = line;
        DropLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The default (output) colour is the inherited foreground, which selection changes: it is baked into the layout like the others.
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == FontFeaturesProperty
            || change.Property == BrushesProperty || change.Property == ForegroundProperty)
            DropLayout();
    }

    private void DropLayout()
    {
        _layout?.Dispose();
        _layout = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout();
        return layout is null ? new Size(0, 0) : new Size(layout.WidthIncludingTrailingWhitespace + 2 * HorizontalPadding, layout.Height);
    }

    public override void Render(DrawingContext context)
    {
        if (Layout() is not { } layout) return;
        layout.Draw(context, new Point(HorizontalPadding, Math.Max(0, (Bounds.Height - layout.Height) / 2)));
    }

    private TextLayout? Layout()
    {
        if (_layout is not null || _line is null) return _layout;
        var brushes = GetValue(BrushesProperty);
        var foreground = KindBrush(_line.Kind, brushes) ?? Foreground ?? Brushes.Gray;
        var typeface = new Typeface(FontFamily, _line.Kind == ConsoleLineKind.Info ? FontStyle.Italic : FontStyle.Normal);
        var fontSize = FontSize > 0 ? FontSize : 12;
        _layout = new TextLayout(_line.Text, typeface, fontSize, foreground, fontFeatures: FontFeatures,
            textStyleOverrides: _line.Spans is { Count: > 0 } spans ? Overrides(spans, typeface, fontSize, foreground, brushes) : null);
        LayoutsBuilt++;
        return _layout;
    }

    private IBrush? KindBrush(ConsoleLineKind kind, ConsoleBrushes? b) => kind switch
    {
        ConsoleLineKind.Error or ConsoleLineKind.ResultError => b?.Error,
        ConsoleLineKind.Console => b?.Debugger,
        ConsoleLineKind.Important => b?.Important,
        ConsoleLineKind.Input => b?.Input,
        ConsoleLineKind.Info => b?.Info,
        _ => null,
    };

    private List<ValueSpan<TextRunProperties>> Overrides(IReadOnlyList<ConsoleSpan> spans, Typeface typeface, double fontSize, IBrush lineBrush, ConsoleBrushes? brushes)
    {
        var result = new List<ValueSpan<TextRunProperties>>(spans.Count);
        var length = _line!.Text.Length;
        foreach (var span in spans)
        {
            if (span.Start >= length) break;
            var style = span.Style;
            var fg = ColorBrush(style.Foreground, brushes) ?? lineBrush;
            var bg = ColorBrush(style.Background, brushes);
            if ((style.Flags & ConsoleTextFlags.Inverse) != 0) (fg, bg) = (bg ?? brushes?.Background ?? Brushes.Black, fg);
            if ((style.Flags & ConsoleTextFlags.Dim) != 0 && fg is ISolidColorBrush solid) fg = new ImmutableSolidColorBrush(solid.Color, solid.Opacity * 0.6);
            var face = new Typeface(typeface.FontFamily,
                (style.Flags & ConsoleTextFlags.Italic) != 0 ? FontStyle.Italic : typeface.Style,
                (style.Flags & ConsoleTextFlags.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal);
            TextDecorationCollection? decorations = (style.Flags & (ConsoleTextFlags.Underline | ConsoleTextFlags.Strikethrough)) switch
            {
                ConsoleTextFlags.Underline => TextDecorations.Underline,
                ConsoleTextFlags.Strikethrough => TextDecorations.Strikethrough,
                ConsoleTextFlags.None => null,
                _ => [.. TextDecorations.Underline, .. TextDecorations.Strikethrough],
            };
            result.Add(new ValueSpan<TextRunProperties>(span.Start, Math.Min(span.Length, length - span.Start),
                new GenericTextRunProperties(face, fontSize, decorations, fg, bg, fontFeatures: FontFeatures)));
        }
        return result;
    }

    private static IBrush? ColorBrush(int color, ConsoleBrushes? brushes)
    {
        if (color == ConsoleTextStyle.Default) return null;
        if (color is >= 0 and < 16) return brushes is { } b && color < b.Ansi.Count ? b.Ansi[color] : null;
        var rgb = ConsoleTextStyle.ToRgb(color);
        return new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LinePeer(this);

    private sealed class LinePeer(ConsoleLineView owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() => owner._line?.Text;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
