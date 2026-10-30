using Avalonia;
using Avalonia.Headless.XUnit;
using CsCode.App.Background;
using CsCode.Sessions.Ipc;
using Xunit;
using static CsCode.App.Background.WindowPlacement;

namespace CsCode.App.Tests.Background;

/// <summary>
/// A new session's window opened exactly on top of the window shown before it (each session is its own process, so the window manager placed
/// both alike, or both restored one saved spot). The rule is pure and tested here without a window system; the last test runs it through a real
/// session process's activation, with another "process" answering hello in its runtime directory.
/// </summary>
public class WindowPlacementTests
{
    private static readonly ScreenArea Screen = new(new PixelRect(0, 25, 1920, 1055), 1.0);
    private static readonly Size WindowSize = new(1200, 800);
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddYears(56);

    private static OtherWindow At(int x, int y, int minutes = 0) => new(new PixelPoint(x, y), T0.AddMinutes(minutes));

    [Fact]
    public void Alone_the_window_keeps_its_saved_spot_or_the_window_managers()
    {
        Assert.Null(Place(new PixelPoint(100, 100), WindowSize, [], [Screen]));
        Assert.Null(Place(null, WindowSize, [], [Screen]));
    }

    [Fact]
    public void A_saved_spot_no_other_window_holds_is_kept()
    {
        Assert.Null(Place(new PixelPoint(500, 300), WindowSize, [At(100, 100)], [Screen]));
    }

    [Fact]
    public void Without_a_saved_spot_it_opens_down_and_right_of_the_newest_window()
    {
        Assert.Equal(new PixelPoint(330, 230), Place(null, WindowSize, [At(100, 100, minutes: 1), At(300, 200, minutes: 5), At(600, 50, minutes: 2)], [Screen]));
    }

    [Fact]
    public void A_saved_spot_another_window_holds_is_given_up()
    {
        Assert.Equal(new PixelPoint(130, 130), Place(new PixelPoint(100, 100), WindowSize, [At(100, 100)], [Screen]));
        Assert.Equal(new PixelPoint(134, 127), Place(new PixelPoint(102, 98), WindowSize, [At(104, 97)], [Screen])); // a few pixels off is the same spot
    }

    [Fact]
    public void It_cascades_past_windows_already_cascaded()
    {
        Assert.Equal(new PixelPoint(160, 160), Place(null, WindowSize, [At(100, 100, minutes: 9), At(130, 130, minutes: 1)], [Screen]));
    }

    [Fact]
    public void It_is_clamped_to_the_working_area()
    {
        // 1200 × 800 at 1.5: 1800 × 1200 px, wider and taller than what is left right of and below 300,200
        var hiDpi = Screen with { Scaling = 1.5 };
        Assert.Equal(new PixelPoint(120, 25), Place(null, WindowSize, [At(300, 200)], [hiDpi]));
        Assert.Equal(new PixelPoint(720, 280), Place(null, WindowSize, [At(900, 400)], [Screen]));
        // On the screen the newest window is on, not the first one.
        var right = new ScreenArea(new PixelRect(1920, 0, 2560, 1400), 1.0);
        Assert.Equal(new PixelPoint(2030, 130), Place(null, WindowSize, [At(2000, 100)], [Screen, right]));
    }

    [Fact]
    public void At_the_working_areas_corner_the_cascade_starts_over_at_its_top_left()
    {
        // 720,280 is as far down and right as the window fits; another window already there sends this one to the top-left corner.
        Assert.Equal(new PixelPoint(0, 25), Place(null, WindowSize, [At(700, 270, minutes: 5), At(720, 280, minutes: 1)], [Screen]));
    }

    [Fact]
    public void Without_screens_it_is_still_offset()
    {
        Assert.Equal(new PixelPoint(130, 130), Place(null, WindowSize, [At(100, 100)], []));
    }

    /// <summary>Another CsCode "process": answers hello as a shown session whose window is at <paramref name="x"/>,<paramref name="y"/>.</summary>
    private sealed class ShownWindow : IIpcHandler, IDisposable
    {
        private readonly IpcServer _server;
        private readonly int _pid, _x, _y;

        public ShownWindow(RuntimeDirectory runtime, int pid, int x, int y)
        {
            (_pid, _x, _y) = (pid, x, y);
            Assert.Null(runtime.Ensure());
            _server = new IpcServer(runtime.EndpointOf(pid), this);
            _server.Start();
        }

        public Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct) => Task.FromResult(request.Type == IpcVerbs.Hello
            ? new IpcMessage
            {
                Type = IpcVerbs.Hello, Ok = true, Pid = _pid, Session = "other", State = ProcessStates.Shown, ProtocolVersion = IpcMessage.ProtocolVersionCurrent,
                LastShownUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WindowX = _x, WindowY = _y,
            }
            : IpcMessage.Failure("unexpected " + request.Type));

        public void Dispose() => _server.Dispose();
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_session_window_opens_offset_from_the_shown_one_and_reports_where_it_is()
    {
        ShownWindow? other = null;
        try
        {
            await using var s = await InProcessSession.StartAsync("p", runtimeReady: runtime => other = new ShownWindow(runtime, Environment.ProcessId + 100_000, 100, 100));
            var window = await s.Run.MainWindowAsync();
            Assert.Equal(new PixelPoint(130, 130), window.Position);

            // ...and its own hello says where it is, for the next window to open.
            var hello = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Hello, ProtocolVersion = IpcMessage.ProtocolVersionCurrent });
            Assert.Equal((130, 130), (hello.WindowX, hello.WindowY));
        }
        finally { other?.Dispose(); }
    }
}
