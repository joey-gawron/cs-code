using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.Sessions;

namespace CsCode.App.Background;

public sealed partial class SessionWindowController
{
    /// <summary>
    /// The warm spare's last start-up step: everything the first frame of a claimed session would otherwise do for the first time (applying the
    /// window's templates and styles, the first layout pass, the first Skia render with its glyph and geometry caches, and the JIT of all of it)
    /// happens now, off screen: the hidden window is laid out at its size and rendered into a bitmap that is thrown away. No native window is
    /// shown. Runs in small steps at background priority, so a claim arriving meanwhile is not held up for long. Not warmed: the compositor's
    /// first frame of the real window (a different path from the bitmap render; it needs a shown window), measured in docs/SESSIONS.md.
    /// </summary>
    internal async Task WarmUpSpareAsync()
    {
        SessionTrace.Mark("host: spare warm-up");
        try
        {
            var size = new Size(double.IsNaN(_window.Width) ? 1280 : _window.Width, double.IsNaN(_window.Height) ? 800 : _window.Height);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _window.ApplyTemplate();
                _window.Measure(size);
                _window.Arrange(new Rect(size));
            }, DispatcherPriority.Background);
            if (!_host.IsUnclaimedSpare) return; // claimed meanwhile: its own show does the rest
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                bitmap.Render(_window);
            }, DispatcherPriority.Background);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Only an optimisation: a spare that could not warm up is still a spare.
            SessionTrace.Mark("host: spare warm-up failed: " + ex.Message);
        }
        SessionTrace.Mark("host: spare warm");
    }
}
