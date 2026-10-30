using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Clipboard brief A2/A4/A5: <see cref="ClipboardService"/> over Avalonia's (headless) platform clipboard: both directions, Unicode, CRLF and
/// large text, CsCode's shape metadata only while the clipboard still holds CsCode's text, reads that never wait on a silent owner, the ring
/// (20 entries, 1 MB each, in memory only) and the PRIMARY selection backend.
/// </summary>
public class ClipboardServiceTests
{
    private static (ClipboardService Service, Window Window) Create(Func<IPrimarySelectionBackend?>? primary = null)
    {
        var window = new Window();
        window.Show();
        return (new ClipboardService(AvaloniaUiDispatcher.Instance, AvaloniaPlatformClipboard.Of(() => window), primary ?? (() => null)), window);
    }

    [AvaloniaFact]
    public async Task Text_goes_to_the_platform_clipboard_and_back_in_both_directions()
    {
        var (service, window) = Create();
        using var _ = service;
        await service.WriteAsync(ClipboardText.Plain("from CsCode"));
        Assert.Equal("from CsCode", await window.Clipboard!.TryGetTextAsync()); // what another application reads

        await window.Clipboard!.SetTextAsync("from elsewhere"); // another application copies
        Assert.Equal(ClipboardText.Plain("from elsewhere"), await service.ReadAsync());
        Assert.True(await service.HasTextAsync());
    }

    [AvaloniaFact]
    public async Task Unicode_CRLF_and_10_MB_survive_the_round_trip_unchanged()
    {
        var (service, window) = Create();
        using var _ = service;
        const string unicode = "👩‍💻 é ñ 中文 עברית العربية\r\nsecond\rthird\n";
        await service.WriteAsync(ClipboardText.Plain(unicode));
        Assert.Equal(unicode, (await service.ReadAsync())!.Text); // line endings are the paste's business, not the clipboard's

        var big = string.Concat(Enumerable.Repeat(new string('z', 1023) + "\n", 10 * 1024)); // 10 MB of text
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await service.WriteAsync(ClipboardText.Plain(big));
        var back = await service.ReadAsync();
        Assert.Equal(big.Length, back!.Text.Length);
        Assert.Equal(big, back.Text);
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"10 MB round trip took {sw.ElapsedMilliseconds} ms");
        Assert.DoesNotContain(service.Ring, r => r.Text.Length == big.Length); // over 1 MB: on the clipboard, not in the ring
    }

    [AvaloniaFact]
    public async Task CsCodes_shape_survives_only_while_the_clipboard_holds_CsCodes_text()
    {
        var (service, window) = Create();
        using var _ = service;
        var lines = new ClipboardText("line\n", ClipboardTextShape.FullLines) { SourceIndent = 4 };
        await service.WriteAsync(lines);
        var read = await service.ReadAsync();
        Assert.Equal(ClipboardTextShape.FullLines, read!.Shape);
        Assert.Equal(4, read.SourceIndent);

        var block = new ClipboardText("a\nb", ClipboardTextShape.Block, ["a", "b"]);
        await service.WriteAsync(block);
        Assert.Equal(["a", "b"], (await service.ReadAsync())!.Pieces);

        await window.Clipboard!.SetTextAsync("line\n"); // the same text, but another application owns it now... and it is identical:
        Assert.Equal(ClipboardTextShape.Normal, (await service.ReadAsync())!.Shape); // the last write was the block, so this is plain
        await window.Clipboard!.SetTextAsync("other");
        Assert.Equal(ClipboardText.Plain("other"), await service.ReadAsync());
    }

    [AvaloniaFact]
    public async Task A_read_never_waits_longer_than_the_timeout_for_a_clipboard_owner_that_does_not_answer()
    {
        // A platform clipboard whose owner never answers (a hung X11 client).
        var hanging = new HangingClipboard();
        var service = new ClipboardService(AvaloniaUiDispatcher.Instance, () => hanging);
        using var _ = service;
        var previous = ClipboardService.ReadTimeout;
        ClipboardService.ReadTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var read = service.ReadAsync().AsTask();
            var ticks = 0;
            while (!read.IsCompleted)
            {
                ticks++; // the UI thread keeps running while the owner is silent
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.Null(await read);
            Assert.InRange(sw.ElapsedMilliseconds, 250, 3_000);
            Assert.True(ticks > 5);
            Assert.False(await service.HasTextAsync());
        }
        finally
        {
            ClipboardService.ReadTimeout = previous;
        }
    }

    [AvaloniaFact]
    public async Task The_ring_keeps_the_last_20_copies_newest_first_each_at_most_1_MB_in_memory_only()
    {
        var (service, _) = Create();
        using var s = service;
        var fired = 0;
        using var sub = service.RingChanged.Subscribe(_ => fired++);
        for (var i = 0; i < 25; i++) await service.WriteAsync(ClipboardText.Plain("copy " + i));
        Assert.Equal(ClipboardLimits.RingCapacity, service.Ring.Count);
        Assert.Equal("copy 24", service.Ring[0].Text);
        Assert.Equal("copy 5", service.Ring[^1].Text);
        Assert.Equal(25, fired);

        await service.WriteAsync(ClipboardText.Plain("copy 10")); // copied again: moves to the top, no duplicate
        Assert.Equal("copy 10", service.Ring[0].Text);
        Assert.Single(service.Ring, r => r.Text == "copy 10");

        var justUnder = new string('x', ClipboardLimits.RingEntryMaxBytes / sizeof(char));
        await service.WriteAsync(ClipboardText.Plain(justUnder));
        Assert.Equal(justUnder, service.Ring[0].Text);
        await service.WriteAsync(ClipboardText.Plain(justUnder + "x"));
        Assert.Equal(justUnder, service.Ring[0].Text); // one character over: left out

        await service.WriteAsync(new ClipboardText("<b>t</b>-text", Html: "<b>t</b>"));
        Assert.Null(service.Ring[0].Html);
        Assert.DoesNotContain(typeof(ClipboardService).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IStateStorage));
    }

    [AvaloniaFact]
    public async Task PRIMARY_is_produced_lazily_by_the_owner_and_read_back()
    {
        var primary = new InMemoryPrimarySelection();
        var (service, _) = Create(() => primary);
        using var s = service;
        Assert.True(service.IsPrimarySelectionSupported);
        var calls = 0;
        service.SetPrimarySelection(() => { calls++; return "selected"; });
        Assert.Equal(0, calls); // nothing is computed until someone pastes
        Assert.Equal("selected", await service.ReadPrimarySelectionAsync());
        Assert.Equal(1, calls);
        primary.SetExternal("from another app");
        Assert.Equal("from another app", await service.ReadPrimarySelectionAsync());
    }

    [AvaloniaFact]
    public async Task Without_an_X_display_there_is_no_PRIMARY_and_the_calls_are_harmless()
    {
        // the test run has no display (it must never use the user's); macOS never connects to X (XQuartz's launchd agent sets DISPLAY there)
        if (OperatingSystem.IsLinux()) Assert.Null(Environment.GetEnvironmentVariable("DISPLAY"));
        var service = new ClipboardService(AvaloniaUiDispatcher.Instance, () => null);
        using var _ = service;
        Assert.False(service.IsPrimarySelectionSupported);
        service.SetPrimarySelection(() => "x");
        Assert.Null(await service.ReadPrimarySelectionAsync());
        Assert.Null(await service.ReadAsync()); // no window yet
        await service.WriteAsync(ClipboardText.Plain("kept in the ring"));
        Assert.Equal("kept in the ring", service.Ring[0].Text);
    }

    private sealed class HangingClipboard : IPlatformClipboard
    {
        public Task<string?> TryGetTextAsync() => new TaskCompletionSource<string?>().Task;
        public Task<bool> HasTextAsync() => new TaskCompletionSource<bool>().Task;
        public Task SetAsync(string text, string? html) => Task.CompletedTask;
    }
}
