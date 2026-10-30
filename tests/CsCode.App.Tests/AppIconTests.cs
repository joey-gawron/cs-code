using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The app icon is the user's artwork (assets/branding) everywhere: every committed PNG is an Avalonia resource byte for byte, the Windows
/// executable carries the .ico, the window has an icon, and the title-bar logo is the 16 px PNG at 100 % and the 32 px one at 200 %, copied
/// pixel for pixel (docs/briefs/app-icon-and-install.md, MUST 1). The title bar at both scalings is saved to .scratch/screenshots
/// (title-bar-100.png, title-bar-200.png and 8x/4x enlargements of the logo) for a person to judge how the small sizes read.
/// </summary>
public class AppIconTests
{
    private static string Branding(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repository root not found"), "assets", "branding", file);
    }

    [AvaloniaFact]
    public void Every_committed_png_is_a_resource_of_its_size_and_unmodified()
    {
        Assert.Equal([16, 32, 48, 64, 128, 256, 512, 1024], AppIcon.Sizes);
        foreach (var size in AppIcon.Sizes)
        {
            using var stream = AssetLoader.Open(AppIcon.PngUri(size));
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.Equal(File.ReadAllBytes(Branding($"cscode-{size}.png")), copy.ToArray());
            Assert.Equal(new PixelSize(size, size), AppIcon.Bitmap(size).PixelSize);
        }
        using var ico = AssetLoader.Open(AppIcon.IcoUri);
        Assert.Equal(new FileInfo(Branding("cscode.ico")).Length, ico.Length);
        Assert.Same(AppIcon.Bitmap(16), AppIcon.Bitmap(16)); // decoded once
        Assert.Throws<ArgumentOutOfRangeException>(() => AppIcon.Bitmap(20));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(16.000001, 16)]
    [InlineData(17, 32)]
    [InlineData(24, 32)] // a 16 px logo at 150 %: the 32 px artwork, scaled down
    [InlineData(32, 32)]
    [InlineData(100, 128)]
    [InlineData(5000, 1024)]
    public void The_smallest_committed_size_that_covers_the_pixels_is_chosen(double pixels, int expected) =>
        Assert.Equal(expected, AppIcon.SizeFor(pixels));

    [AvaloniaFact]
    public void Window_and_tray_icons_are_made_from_the_artwork()
    {
        Assert.NotNull(AppIcon.Create());
        Assert.NotNull(AppIcon.Create(32));
        Assert.NotNull(AppIcon.Create(4096));
    }

    /// <summary>The C# compiler copies each image of the .ico into the assembly's Win32 resources (RT_ICON), which the apphost of a Windows build takes.</summary>
    [Fact]
    public void The_executable_carries_every_image_of_the_ico()
    {
        var ico = File.ReadAllBytes(Branding("cscode.ico"));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2)); // an icon file
        int count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(6, count);
        var assembly = File.ReadAllBytes(typeof(App).Assembly.Location);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.True(assembly.AsSpan().IndexOf(ico.AsSpan(offset, length)) >= 0, $"image {i} ({ico[entry]} px; 0 = 256) of cscode.ico is not in cscode.dll");
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_title_bar_logo_is_the_artwork_at_100_and_200_percent()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS shows the app's name in the system menu bar and no logo in the title bar");
        await using var host = await RealAppHost.Create().StartAsync();
        Assert.NotNull(host.Window.Icon);
        var logo = host.Window.GetVisualDescendants().OfType<AppLogo>().Single();
        Assert.Equal(16, logo.PixelSize);
        Assert.True(logo.FindAncestorOfType<TitleBarView>() is not null, "the logo is not in the title bar");

        var shots = DevHost.ScreenshotDirectory();
        try
        {
            foreach (var scaling in new[] { 1.0, 2.0 })
            {
                host.Window.SetRenderScaling(scaling);
                await DevScene.SettleAsync(10);
                var expected = (int)(16 * scaling);
                Assert.Equal(expected, logo.PixelSize);
                using var frame = Capture(host);
                var origin = logo.TranslatePoint(default, host.Window) ?? throw new InvalidOperationException("the logo is not in the window");
                var x = (int)Math.Round(origin.X * scaling);
                var y = (int)Math.Round(origin.Y * scaling);
                var percent = (int)(scaling * 100);
                var bar = (int)Math.Round(30 * scaling);
                Save(Crop(frame, 0, 0, Math.Min(frame.PixelSize.Width, (int)(640 * scaling)), bar, 1), Path.Combine(shots, $"title-bar-{percent}.png"));
                Save(Crop(frame, x - 4, y - 4, expected + 8, expected + 8, 64 / expected * 2), Path.Combine(shots, $"title-bar-logo-{percent}-x{64 / expected * 2}.png"));

                // Every opaque pixel of the committed PNG is on screen unchanged (the transparent corners show the title bar through).
                using var source = AssetLoader.Open(AppIcon.PngUri(expected));
                using var artwork = WriteableBitmap.Decode(source);
                var want = Pixels(artwork);
                var got = Pixels(frame);
                var compared = 0;
                for (var py = 0; py < expected; py++)
                    for (var px = 0; px < expected; px++)
                    {
                        var w = want[py, px];
                        if (w.A != 255) continue;
                        var g = got[y + py, x + px];
                        Assert.True(Math.Abs(w.R - g.R) <= 1 && Math.Abs(w.G - g.G) <= 1 && Math.Abs(w.B - g.B) <= 1,
                            $"{percent} %: pixel ({px},{py}) is {g} on screen, {w} in cscode-{expected}.png");
                        compared++;
                    }
                Assert.True(compared > expected * expected / 2, $"only {compared} opaque pixels compared");
            }
        }
        finally { host.Window.SetRenderScaling(1); }
    }

    /// <summary>The logo listens to its window's scaling while it is shown; taken out of the window, the window must not keep it alive.</summary>
    [AvaloniaFact]
    public void A_logo_removed_from_its_window_is_not_retained_by_the_window()
    {
        var host = new Avalonia.Controls.StackPanel();
        var window = new Avalonia.Controls.Window { Content = host, Width = 200, Height = 100 };
        window.Show();
        try
        {
            TestDoubles.LeakAssert.NotRetained(() =>
            {
                var logo = new AppLogo { Width = 16, Height = 16 };
                host.Children.Add(logo);
                window.UpdateLayout();
                Assert.Equal(16, logo.PixelSize);
                host.Children.Remove(logo);
                Dispatcher.UIThread.RunJobs();
                return [logo];
            });
        }
        finally { window.Close(); }
    }

    private static WriteableBitmap Capture(RealAppHost host)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
    }

    private readonly record struct Rgba(byte R, byte G, byte B, byte A)
    {
        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    private static Rgba[,] Pixels(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        var rgba = fb.Format == PixelFormat.Rgba8888;
        Assert.True(rgba || fb.Format == PixelFormat.Bgra8888, $"unexpected pixel format {fb.Format}");
        var result = new Rgba[fb.Size.Height, fb.Size.Width];
        for (var y = 0; y < fb.Size.Height; y++)
            for (var x = 0; x < fb.Size.Width; x++)
            {
                var i = y * fb.RowBytes + x * 4;
                result[y, x] = rgba ? new Rgba(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) : new Rgba(bytes[i + 2], bytes[i + 1], bytes[i], bytes[i + 3]);
            }
        return result;
    }

    /// <summary>A copy of a region of <paramref name="frame"/>, each pixel repeated <paramref name="zoom"/> times in both directions (nearest neighbour).</summary>
    private static WriteableBitmap Crop(WriteableBitmap frame, int left, int top, int width, int height, int zoom)
    {
        var pixels = Pixels(frame);
        var result = new WriteableBitmap(new PixelSize(width * zoom, height * zoom), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = result.Lock();
        var row = new byte[fb.RowBytes];
        for (var y = 0; y < height * zoom; y++)
        {
            for (var x = 0; x < width * zoom; x++)
            {
                var sx = Math.Clamp(left + x / zoom, 0, pixels.GetLength(1) - 1);
                var sy = Math.Clamp(top + y / zoom, 0, pixels.GetLength(0) - 1);
                var p = pixels[sy, sx];
                row[x * 4] = p.B; row[x * 4 + 1] = p.G; row[x * 4 + 2] = p.R; row[x * 4 + 3] = p.A;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return result;
    }

    private static void Save(WriteableBitmap bitmap, string path)
    {
        using (bitmap) bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
