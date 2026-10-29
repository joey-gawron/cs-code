using CsCode.Core.Theming;

namespace CsCode.Ui.Theming;

/// <summary>
/// WCAG 2.x contrast arithmetic over <see cref="Rgba"/>: relative luminance, the contrast ratio, alpha compositing (a translucent colour is
/// judged over the surface it is drawn on) and the minimal foreground change that reaches a target ratio. Pure; used by
/// <see cref="ThemeCompletion"/> to check (and repair) every chrome text colour against its background.
/// </summary>
public static class Contrast
{
    /// <summary>WCAG AA for normal-size text (the shell's text is 11-14 px).</summary>
    public const double AaNormalText = 4.5;

    /// <summary>Relative luminance (WCAG 2.x: sRGB linearised, Rec. 709 weights). Alpha is ignored: composite first.</summary>
    public static double Luminance(Rgba c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>(L1 + 0.05) / (L2 + 0.05) with L1 the lighter: 1 (same) to 21 (black on white).</summary>
    public static double Ratio(Rgba a, Rgba b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return la >= lb ? (la + 0.05) / (lb + 0.05) : (lb + 0.05) / (la + 0.05);
    }

    /// <summary><paramref name="top"/> drawn over the opaque <paramref name="bottom"/> (source-over); the result is opaque.</summary>
    public static Rgba Over(Rgba top, Rgba bottom)
    {
        if (top.A == 255) return top;
        var a = top.A / 255.0;
        static byte Mix(byte t, byte b, double a) => (byte)Math.Round(t * a + b * (1 - a));
        return new Rgba(Mix(top.R, bottom.R, a), Mix(top.G, bottom.G, a), Mix(top.B, bottom.B, a));
    }

    /// <summary>Linear mix in sRGB: 0 = <paramref name="a"/>, 1 = <paramref name="b"/> (alpha of <paramref name="a"/> kept).</summary>
    public static Rgba Mix(Rgba a, Rgba b, double t)
    {
        static byte M(byte x, byte y, double t) => (byte)Math.Clamp(Math.Round(x + (y - x) * t), 0, 255);
        return new Rgba(M(a.R, b.R, t), M(a.G, b.G, t), M(a.B, b.B, t), a.A);
    }

    private static readonly Rgba White = new(255, 255, 255);
    private static readonly Rgba Black = new(0, 0, 0);

    /// <summary>Repairs move a colour in 1 % steps: the smallest change that passes, never a visibly different colour where a nudge does.</summary>
    private const int Steps = 100;

    /// <summary>
    /// The colour closest to <paramref name="foreground"/> on its own side of the background (lighter text made lighter, darker text darker: the
    /// same hue mixed towards white or black in 1 % steps) whose contrast with the opaque <paramref name="background"/> reaches
    /// <paramref name="target"/>; opaque. Null when even white (or black) on that side cannot reach it: white text on a mid orange.
    /// </summary>
    public static Rgba? MoveForeground(Rgba foreground, Rgba background, double target = AaNormalText)
    {
        var fg = Over(foreground, background);
        if (Ratio(fg, background) >= target) return fg;
        var towards = Luminance(fg) >= Luminance(background) ? White : Black;
        for (var step = 1; step <= Steps; step++)
        {
            var candidate = Mix(fg, towards, (double)step / Steps);
            if (Ratio(candidate, background) >= target) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The colour closest to the opaque <paramref name="background"/> that gives <paramref name="foreground"/> the target contrast, moving the
    /// background away from the text (darker under light text, lighter under dark text); null when impossible.
    /// </summary>
    public static Rgba? MoveBackground(Rgba background, Rgba foreground, double target = AaNormalText)
    {
        var fg = Over(foreground, background);
        var towards = Luminance(fg) >= Luminance(background) ? Black : White;
        for (var step = 1; step <= Steps; step++)
        {
            var candidate = Mix(background, towards, (double)step / Steps);
            if (Ratio(Over(foreground, candidate), candidate) >= target) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The best foreground for a background when moving either one is not enough: white or black, whichever contrasts more. At least 4.58:1
    /// against any background (the worst case is a background of luminance 0.18).
    /// </summary>
    public static Rgba BestExtreme(Rgba background) => Ratio(White, background) >= Ratio(Black, background) ? White : Black;
}
