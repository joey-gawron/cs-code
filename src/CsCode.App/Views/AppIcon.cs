using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CsCode.App.Views;

/// <summary>
/// The application icon: the user's artwork (<c>assets/branding</c>: a seal on a dark blue rounded tile), embedded as Avalonia resources in every
/// size it comes in (<c>avares://cscode/Assets/Branding/cscode-&lt;size&gt;.png</c>, 16 to 1024 px, and <c>cscode.ico</c>). It is never redrawn,
/// recoloured or composed here: each consumer takes the committed PNG whose pixel size fits, so 16 and 32 px are the artist's own small renderings,
/// not a run-time downscale. The same files are the Windows executable's icon (<c>ApplicationIcon</c>) and the installed icon theme
/// (<c>tools/install.sh</c>: hicolor 16-512 px and scalable).
/// </summary>
public static class AppIcon
{
    /// <summary>The pixel sizes of the committed PNGs, ascending.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 32, 48, 64, 128, 256, 512, 1024];

    /// <summary>
    /// The largest window icon Avalonia's X11 backend keeps: it sets <c>_NET_WM_ICON</c> to ONE image and scales a bigger bitmap down to 128 px.
    /// The window therefore gets the 128 px PNG as it is; KDE and other desktops that match the window to <c>cscode.desktop</c>
    /// (<c>StartupWMClass=cscode</c>) take the taskbar and Alt+Tab icons from the installed icon theme, which has every size.
    /// </summary>
    public const int X11WindowIconSize = 128;

    /// <summary>The Dock icon on macOS (Avalonia sets the application image from the window icon; the Dock shows up to 256 pt at 2x).</summary>
    public const int MacWindowIconSize = 512;

    private static readonly Bitmap?[] Decoded = new Bitmap?[Sizes.Count];
    private static readonly Lock Gate = new();

    /// <summary>The resource of one committed size.</summary>
    public static Uri PngUri(int size) => Sizes.Contains(size)
        ? new Uri($"avares://cscode/Assets/Branding/cscode-{size}.png")
        : throw new ArgumentOutOfRangeException(nameof(size), size, "no committed PNG of this size");

    /// <summary>The Windows icon (16-256 px in one file).</summary>
    public static Uri IcoUri { get; } = new("avares://cscode/Assets/Branding/cscode.ico");

    /// <summary>The smallest committed size with at least <paramref name="pixels"/> pixels (the largest when none is that big).</summary>
    public static int SizeFor(double pixels)
    {
        foreach (var size in Sizes)
            if (size >= pixels - 0.01) return size; // 16 * 1.0000001 still takes the 16 px artwork
        return Sizes[^1];
    }

    /// <summary>
    /// The committed PNG of <paramref name="size"/> (one of <see cref="Sizes"/>), decoded on first use and kept: the title bar needs one or two
    /// small ones (a few KB each); the large ones are decoded only when something asks for them.
    /// </summary>
    public static Bitmap Bitmap(int size)
    {
        var index = -1;
        for (var i = 0; i < Sizes.Count && index < 0; i++) if (Sizes[i] == size) index = i;
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(size), size, "no committed PNG of this size");
        lock (Gate)
        {
            if (Decoded[index] is { } cached) return cached;
            using var stream = AssetLoader.Open(PngUri(size));
            return Decoded[index] = new Bitmap(stream);
        }
    }

    /// <summary>
    /// The window icon: Windows gets the .ico (the system picks 16, 32, 48 or 256 px for the title bar, taskbar and Alt+Tab); X11 the 128 px PNG
    /// (<see cref="X11WindowIconSize"/>); macOS the 512 px one. Each call loads its own copy, so the platform may keep or release it.
    /// </summary>
    public static WindowIcon Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowIcon(AssetLoader.Open(IcoUri));
        return new WindowIcon(AssetLoader.Open(PngUri(OperatingSystem.IsMacOS() ? MacWindowIconSize : X11WindowIconSize)));
    }

    /// <summary>An icon of about <paramref name="pixels"/> pixels (the tray): the committed PNG <see cref="SizeFor"/> picks.</summary>
    public static WindowIcon Create(int pixels) => new(AssetLoader.Open(PngUri(SizeFor(pixels))));
}

/// <summary>
/// The app icon as a control (the title bar's logo). It draws the committed PNG whose pixel size covers the control at the window's render
/// scaling: a 16 px logo is the 16 px artwork copied 1:1 at 100 %, the 32 px artwork at 200 %, the 32 px one scaled down (high quality) at 150 %,
/// never an enlarged small bitmap. A change of scaling (the window moved to another monitor) redraws it.
/// </summary>
public sealed class AppLogo : Control
{
    private TopLevel? _topLevel;

    /// <summary>The committed size drawn at the current size and scaling.</summary>
    public int PixelSize => AppIcon.SizeFor(DevicePixels);

    private double DevicePixels => Math.Max(Bounds.Width, Bounds.Height) * (_topLevel?.RenderScaling ?? 1);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The window outlives the logo only when the title bar is rebuilt; either way it must not keep this control alive.
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var size = PixelSize;
        var bitmap = AppIcon.Bitmap(size);
        // At an exact size the artwork is copied pixel for pixel (any filter would soften it); otherwise it is scaled down with mipmaps.
        var exact = Math.Abs(size - DevicePixels) < 0.01;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = exact ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(Bounds.Size));
    }
}
