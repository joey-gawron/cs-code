using System.Text;
using CsCode.App.Services;
using CsCode.App.Services.X11;
using CsCode.App.Tests.Infrastructure;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Clipboard brief A5 (and A2's large text) on a REAL X server: a private Xvfb (never the user's display), <see cref="X11SelectionClient"/> as
/// the owner of PRIMARY and an independent second client (its own connection) as the reader, as another application would be. No xclip/xsel is
/// installed on this machine, so the second client is the same code on its own connection; the real app's CLIPBOARD (Avalonia's X11 clipboard,
/// independent code) is checked against it by the real-window clipboard test.
/// </summary>
public sealed class X11SelectionTests : IDisposable
{
    private readonly PrivateXServer? _x;
    private readonly string _why;
    private readonly List<IDisposable> _clients = new();

    public X11SelectionTests() => _x = PrivateXServer.TryStart(out _why);

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _x?.Dispose();
    }

    private async Task<X11SelectionClient> ClientAsync(string selection = "PRIMARY")
    {
        if (_x is null) Assert.Skip("no private X server: " + _why);
        var client = X11SelectionClient.TryStart(selection, _x!.Display) ?? throw new InvalidOperationException("libxcb could not be loaded");
        _clients.Add(client);
        Assert.True(await client.WhenReady.WaitAsync(TimeSpan.FromSeconds(10)), "connected to " + _x.Display);
        return client;
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 5_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
        return condition();
    }

    [Fact]
    public async Task PRIMARY_goes_from_an_owner_to_another_X_client_lazily_with_Unicode_and_line_endings_intact()
    {
        var owner = await ClientAsync();
        var reader = await ClientAsync();
        const string text = "selected 👩‍💻 é 中文 עברית\r\nline two\n";
        var produced = 0;
        owner.SetOwner(() => { Interlocked.Increment(ref produced); return text; });
        Assert.True(await Eventually(() => owner.IsOwner), "owner took PRIMARY");
        Assert.Equal(0, produced); // nothing is produced until someone asks
        Assert.Equal(text, await reader.ReadAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, produced);
    }

    [Fact]
    public async Task Ten_megabytes_of_PRIMARY_go_through_the_INCR_protocol()
    {
        var owner = await ClientAsync();
        var reader = await ClientAsync();
        var big = string.Concat(Enumerable.Repeat("0123456789abcdef€ūñ\n", 520_000)); // 10.4 M characters, more in UTF-8
        Assert.True(Encoding.UTF8.GetByteCount(big) > X11SelectionClient.IncrChunk * 10);
        owner.SetOwner(() => big);
        Assert.True(await Eventually(() => owner.IsOwner));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var read = await reader.ReadAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(big.Length, read?.Length);
        Assert.Equal(big, read);
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"10 MB over INCR took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Another_client_taking_PRIMARY_clears_the_previous_owner_and_its_own_reads_need_no_round_trip()
    {
        var first = await ClientAsync();
        var second = await ClientAsync();
        first.SetOwner(() => "first");
        Assert.True(await Eventually(() => first.IsOwner));
        second.SetOwner(() => "second");
        Assert.True(await Eventually(() => second.IsOwner && !first.IsOwner), "SelectionClear reached the first owner");
        Assert.Equal("second", await first.ReadAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("second", await second.ReadAsync(TimeSpan.FromSeconds(5)));
        second.SetOwner(null);
        Assert.True(await Eventually(() => !second.IsOwner));
        Assert.Null(await first.ReadAsync(TimeSpan.FromSeconds(2))); // nobody owns PRIMARY now
    }

    [Fact]
    public async Task An_owner_that_never_answers_costs_the_reader_its_timeout_and_nothing_more()
    {
        var reader = await ClientAsync();
        using var silent = SilentOwner.Take(_x!.Display, "PRIMARY");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await reader.ReadAsync(TimeSpan.FromMilliseconds(400)));
        Assert.InRange(sw.ElapsedMilliseconds, 300, 5_000);
        // The reader is still usable afterwards.
        var owner = await ClientAsync();
        owner.SetOwner(() => "after");
        Assert.True(await Eventually(() => owner.IsOwner));
        Assert.Equal("after", await reader.ReadAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_clipboard_service_offers_PRIMARY_through_X11_when_there_is_a_display()
    {
        if (_x is null) Assert.Skip("no private X server: " + _why);
        using var primary = X11PrimarySelection.TryCreate(_x!.Display)!;
        var service = new ClipboardService(Ui.Threading.AvaloniaUiDispatcher.Instance, () => null, () => primary);
        using var _ = service;
        Assert.True(service.IsPrimarySelectionSupported);
        service.SetPrimarySelection(() => "from the editor");
        var reader = await ClientAsync();
        string? read = null;
        for (var i = 0; i < 50 && read != "from the editor"; i++)
        {
            read = await reader.ReadAsync(TimeSpan.FromSeconds(1)); // the backend connects on its own thread: the first offer may still be on its way
            if (read != "from the editor") await Task.Delay(50);
        }
        Assert.Equal("from the editor", read);
        reader.SetOwner(() => "from another app");
        Assert.True(await Eventually(() => reader.IsOwner));
        Assert.Equal("from another app", await service.ReadPrimarySelectionAsync());
    }

    /// <summary>An X client that takes a selection and then never reads its events (a hung application).</summary>
    private sealed class SilentOwner : IDisposable
    {
        private IntPtr _c;

        public static SilentOwner Take(string display, string selection)
        {
            var c = Xcb.xcb_connect(display, out _);
            var screen = Xcb.xcb_setup_roots_iterator(Xcb.xcb_get_setup(c));
            var window = Xcb.xcb_generate_id(c);
            Xcb.xcb_create_window(c, 0, window, Xcb.ReadU32(screen.Data, 0), 0, 0, 1, 1, 0, Xcb.WindowClassInputOnly, 0, 0, []);
            var name = Encoding.ASCII.GetBytes(selection);
            var reply = Xcb.xcb_intern_atom_reply(c, Xcb.xcb_intern_atom(c, 0, (ushort)name.Length, name), IntPtr.Zero);
            var atom = Xcb.ReadU32(reply, 8);
            Xcb.Free(reply);
            Xcb.xcb_set_selection_owner(c, window, atom, Xcb.CurrentTime);
            Xcb.xcb_flush(c);
            return new SilentOwner { _c = c };
        }

        public void Dispose()
        {
            if (_c != IntPtr.Zero) Xcb.xcb_disconnect(_c);
            _c = IntPtr.Zero;
        }
    }
}
