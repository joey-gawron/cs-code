using Avalonia;

namespace CsCode.App.Background;

/// <summary>
/// Where a session's window opens among the other CsCode windows shown (each session is a process of its own, so the window manager sees
/// unrelated windows and may stack them exactly). A window keeps its saved spot unless another window is there; otherwise, with others shown, it
/// opens <see cref="CascadeOffset"/> px down and right of the most recently shown one (past any window already at that spot), clamped to that
/// screen's working area. Pure, so the rule is tested without a window system; on macOS the positions are Avalonia's as on Linux.
/// </summary>
internal static class WindowPlacement
{
    internal const int CascadeOffset = 30;
    /// <summary>Two windows whose top-left corners are this close (px) count as one on top of the other.</summary>
    internal const int SameSpotTolerance = 8;

    internal readonly record struct OtherWindow(PixelPoint Position, DateTimeOffset? LastShown);
    internal readonly record struct ScreenArea(PixelRect WorkingArea, double Scaling);

    /// <summary>
    /// The position to give a window of <paramref name="size"/> (DIPs) that opens at <paramref name="saved"/> (null: no saved spot on a screen,
    /// the window manager's choice), or null to leave it there: no other window is shown, or none is at its saved spot.
    /// </summary>
    internal static PixelPoint? Place(PixelPoint? saved, Size size, IReadOnlyList<OtherWindow> others, IReadOnlyList<ScreenArea> screens)
    {
        if (others.Count == 0) return null;
        bool Taken(PixelPoint p) => others.Any(o => Math.Abs(o.Position.X - p.X) <= SameSpotTolerance && Math.Abs(o.Position.Y - p.Y) <= SameSpotTolerance);
        if (saved is { } s && !Taken(s)) return null;

        var newest = others.MaxBy(o => o.LastShown ?? DateTimeOffset.MinValue).Position;
        var p = Step(newest);
        // past the windows already cascaded from it (a third window opens below the second, not on it)
        for (var i = 0; i < others.Count && Taken(p); i++) p = Step(p);
        if (ScreenOf(newest, screens) is not { } screen) return p;

        var area = screen.WorkingArea;
        var clamped = Clamp(p, size, screen);
        // At the working area's far corner the cascade starts over from its top-left rather than stack there.
        if (clamped != p && Taken(clamped))
        {
            clamped = area.TopLeft;
            for (var i = 0; i < others.Count && Taken(clamped); i++) clamped = Clamp(Step(clamped), size, screen);
        }
        return clamped;
    }

    private static PixelPoint Step(PixelPoint p) => new(p.X + CascadeOffset, p.Y + CascadeOffset);

    private static ScreenArea? ScreenOf(PixelPoint p, IReadOnlyList<ScreenArea> screens)
    {
        foreach (var s in screens)
            if (s.WorkingArea.Contains(p)) return s;
        return screens.Count > 0 ? screens[0] : null;
    }

    /// <summary>Keeps the whole window inside the working area where it fits, else its top-left corner.</summary>
    private static PixelPoint Clamp(PixelPoint p, Size size, ScreenArea screen)
    {
        var area = screen.WorkingArea;
        var w = (int)Math.Ceiling(size.Width * screen.Scaling);
        var h = (int)Math.Ceiling(size.Height * screen.Scaling);
        var x = Math.Clamp(p.X, area.X, Math.Max(area.X, area.Right - w));
        var y = Math.Clamp(p.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
        return new PixelPoint(x, y);
    }
}
