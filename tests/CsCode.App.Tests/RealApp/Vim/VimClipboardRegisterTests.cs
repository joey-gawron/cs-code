using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// T9/T15 in the REAL app (every module, the real editor, Vim and the shell's <see cref="ClipboardService"/>): <c>"+</c> is the clipboard through
/// <see cref="IClipboardService"/> (the ring, CsCode's shape metadata both ways, the document's line ends out and <c>'\n'</c> in), <c>"*</c> the
/// PRIMARY selection (an in-process one here: <see cref="InMemoryPrimarySelection"/>, which stands for another application's selection too),
/// and the clipboard where the platform has no PRIMARY. Keys go in as real key events (<see cref="VimApp"/>).
/// </summary>
public class VimClipboardRegisterTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    /// <summary>The shell's clipboard service over the window's (headless) clipboard and a PRIMARY the test controls (null: a platform without one).</summary>
    private static Action<IServiceCollection> Clipboard(InMemoryPrimarySelection? primary) => services =>
        services.AddSingleton(sp => new ClipboardService(sp.GetRequiredService<IUiDispatcher>(),
            AvaloniaPlatformClipboard.Of(() => sp.GetRequiredService<WindowHolder>().Window), () => primary));

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> accept, string what)
    {
        var last = await read();
        for (var i = 0; i < 100 && !accept(last); i++)
        {
            await DevScene.SettleAsync(2);
            last = await read();
        }
        Assert.True(accept(last), $"{what}: got {last}");
        return last;
    }

    private static Task<ClipboardText?> ClipboardAsync(VimApp app) => app.Host.Get<IClipboardService>().ReadAsync().AsTask();

    /// <summary>The window's platform clipboard, counting reads; while <see cref="Gate"/> is set a read waits for it (an owner slow to answer).</summary>
    private sealed class GatedPlatformClipboard : IPlatformClipboard
    {
        public Func<IPlatformClipboard?> Inner { get; set; } = () => null;
        public volatile TaskCompletionSource? Gate;
        public int Reads;

        public async Task<string?> TryGetTextAsync()
        {
            Interlocked.Increment(ref Reads);
            var inner = Inner();
            if (Gate is { } gate) await gate.Task.ConfigureAwait(true);
            return inner is null ? null : await inner.TryGetTextAsync().ConfigureAwait(true);
        }

        public Task<bool> HasTextAsync() => Inner()?.HasTextAsync() ?? Task.FromResult(false);
        public Task SetAsync(string text, string? html) => Inner()?.SetAsync(text, html) ?? Task.CompletedTask;
    }

    private static Action<IServiceCollection> Gated(GatedPlatformClipboard platform, InMemoryPrimarySelection? primary) => services =>
        services.AddSingleton(sp =>
        {
            platform.Inner = AvaloniaPlatformClipboard.Of(() => sp.GetRequiredService<WindowHolder>().Window);
            return new ClipboardService(sp.GetRequiredService<IUiDispatcher>(), () => platform, () => primary);
        });

    private const string SystemClipboard = Quiet + ",\n\"vim.useSystemClipboard\": true";

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Plus_yanks_reach_the_clipboard_and_the_ring_as_whole_lines_text_and_blocks()
    {
        await using var app = await VimApp.StartAsync("first line\nsecond line\nab\ncd\n", settings: Quiet, configure: Clipboard(new InMemoryPrimarySelection()));
        var service = app.Host.Get<IClipboardService>();

        app.Keys("\"+yy");
        var lines = await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "first line\n", "\"+yy on the clipboard");
        Assert.Equal(ClipboardTextShape.FullLines, lines!.Shape);   // the editor's Ctrl+V puts it above the caret's line, as whole lines
        Assert.Equal("first line\n", (await app.Host.Window.Clipboard!.TryGetTextAsync())); // other applications see it too

        app.Keys("j\"+yiw");
        var word = await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "second", "\"+yiw on the clipboard");
        Assert.Equal(ClipboardTextShape.Normal, word!.Shape);

        app.Keys("j0<C-v>jl\"+y");
        var block = await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "ab\ncd", "a blockwise \"+y on the clipboard");
        Assert.Equal(ClipboardTextShape.Block, block!.Shape);
        Assert.Equal(["ab", "cd"], block.Pieces);

        Assert.Equal(["ab\ncd", "second", "first line\n"], service.Ring.Take(3).Select(r => r.Text)); // newest first (Ctrl+Shift+V)
        Assert.Equal("first line\nsecond line\nab\ncd\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Plus_put_reads_what_another_application_copied_with_its_line_ends_converted()
    {
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: Quiet, configure: Clipboard(new InMemoryPrimarySelection()));
        var platform = app.Host.Window.Clipboard!;

        await platform.SetTextAsync("x\r\ny");                     // a Windows-style copy from elsewhere: charwise, CRLF
        await DevScene.SettleAsync(2);
        await app.KeysAsync("\"+P", 10);                          // the keys after "+ wait for the clipboard read
        Assert.Equal("x\nyone\ntwo\n", app.Text);                   // no stray '\r' in the document

        await platform.SetTextAsync("whole line\r\n");              // a final line end: linewise, as Vim reads other applications' text
        await DevScene.SettleAsync(2);
        await app.KeysAsync("gg\"+p", 10);
        Assert.Equal("x\nwhole line\nyone\ntwo\n", app.Text);
        Assert.DoesNotContain(app.Host.Get<IClipboardService>().Ring, r => r.Text.Contains("whole", StringComparison.Ordinal)); // reads never enter the ring
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Plus_put_keeps_the_shape_of_the_editors_own_copies_and_of_its_own_yanks()
    {
        await using var app = await VimApp.StartAsync("abc\ndef\nghi\n", settings: Quiet, configure: Clipboard(new InMemoryPrimarySelection()));
        var service = app.Host.Get<IClipboardService>();

        // the editor's box copy (Ctrl+C on a column selection writes exactly this): "+P puts it as a block
        await service.WriteAsync(new ClipboardText("12\n34", ClipboardTextShape.Block, ["12", "34"]));
        await app.KeysAsync("gg\"+P", 10);
        Assert.Equal("12abc\n34def\nghi\n", app.Text);

        // the editor's whole-line copy (Ctrl+C on a caret): "+p puts it below the caret's line
        await service.WriteAsync(new ClipboardText("whole\n", ClipboardTextShape.FullLines));
        await app.KeysAsync("\"+p", 10);
        Assert.Equal("12abc\nwhole\n34def\nghi\n", app.Text);

        // a charwise yank that ends with a line end stays charwise through the clipboard (a final newline alone would read as linewise)
        app.Keys("4G0v$\"+y");                                    // "ghi" plus its line end
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "ghi\n", "the charwise yank on the clipboard");
        await app.KeysAsync("gg0\"+p", 10);                       // charwise: after the caret's character, not below the line
        Assert.Equal("1ghi\n2abc\nwhole\n34def\nghi\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Star_yank_and_put_use_the_PRIMARY_selection_and_leave_the_clipboard_alone()
    {
        var primary = new InMemoryPrimarySelection();
        await using var app = await VimApp.StartAsync("alpha beta\nab\ncd\n", settings: Quiet, configure: Clipboard(primary));
        var service = app.Host.Get<IClipboardService>();
        await service.WriteAsync(ClipboardText.Plain("ON-THE-CLIPBOARD"));

        app.Keys("w\"*yiw");
        await DevScene.SettleAsync(3);
        Assert.NotNull(primary.Provider);
        Assert.Equal("beta", primary.Provider!());                  // what a middle-click in another application gets
        Assert.Equal("ON-THE-CLIPBOARD", (await ClipboardAsync(app))?.Text);
        Assert.DoesNotContain(service.Ring, r => r.Text == "beta"); // the selection is not a copy

        primary.SetExternal("FROM-SELECTION");                      // another application's selection
        await app.KeysAsync("0\"*P", 10);
        Assert.Equal("FROM-SELECTIONalpha beta\nab\ncd\n", app.Text);

        app.Keys("j0<C-v>jl\"*y");                                // a block through the selection comes back as a block
        await DevScene.SettleAsync(3);
        Assert.Equal("ab\ncd", primary.Provider!());
        await app.KeysAsync("gg0\"*P", 10);
        Assert.Equal("abFROM-SELECTIONalpha beta\ncdab\ncd\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Without_a_PRIMARY_selection_star_is_the_clipboard_as_in_Vim()
    {
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: Quiet, configure: Clipboard(primary: null));
        Assert.False(app.Host.Get<IClipboardService>().IsPrimarySelectionSupported);
        app.Keys("\"*yy");
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "one\n", "\"*yy on the clipboard");
        await app.Host.Window.Clipboard!.SetTextAsync("pasted");
        await DevScene.SettleAsync(2);
        await app.KeysAsync("j\"*P", 10);
        Assert.Equal("one\npastedtwo\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Vim_useSystemClipboard_puts_unnamed_yanks_on_the_clipboard_not_the_selection()
    {
        var primary = new InMemoryPrimarySelection();
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: Quiet + ",\n\"vim.useSystemClipboard\": true", configure: Clipboard(primary));
        app.Keys("yy");
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "one\n", "yy on the clipboard");
        Assert.Null(primary.Provider);
        await app.Host.Window.Clipboard!.SetTextAsync("outside");    // copied in another application while CsCode was in the background
        await app.Vim.Clipboard.RefreshAllAsync();                  // what activating the window does
        await app.KeysAsync("jp", 3);                               // the unnamed register is the clipboard: no "+ (and no wait) needed
        Assert.Equal("one\ntoutsidewo\n", app.Text);
        Assert.Null(primary.Provider);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Activating_the_window_reads_both_registers_for_the_commands_that_do_not_wait()
    {
        var primary = new InMemoryPrimarySelection();
        await using var app = await VimApp.StartAsync("x\n", settings: Quiet, configure: Clipboard(primary));
        await app.Host.Window.Clipboard!.SetTextAsync("CLIP");
        primary.SetExternal("SEL");
        app.Host.Window.Activate();
        await DevScene.SettleAsync(5);
        await app.KeysAsync("A<C-r>+<C-r>*<Esc>", 3);               // Insert-mode Ctrl+R reads the registers without a "+/"* prefix to wait on
        Assert.Equal("xCLIPSEL\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_yank_from_a_CRLF_document_has_the_documents_line_ends_on_the_clipboard_and_puts_back_without_extra_ones()
    {
        await using var app = await VimApp.StartAsync("first\r\nsecond\r\n", settings: Quiet, configure: Clipboard(new InMemoryPrimarySelection()));
        app.Keys("\"+yj");
        var clip = await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "first\r\nsecond\r\n", "the CRLF lines on the clipboard");
        Assert.Equal(ClipboardTextShape.FullLines, clip!.Shape);
        await app.KeysAsync("\"+P", 10);
        Assert.Equal("first\nsecond\nfirst\nsecond\n", app.Text);
        Assert.DoesNotContain("\r\r", app.View.Document.GetText(), StringComparison.Ordinal);
        Assert.Equal("\r\n", app.View.Document.EndOfLine);
    }

    // ------------------------------------------------------------------ the '+' cache stays current (vim-macros-clipboard-1)

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_copy_made_elsewhere_in_CsCode_is_what_p_puts_with_vim_useSystemClipboard()
    {
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: SystemClipboard, configure: Clipboard(new InMemoryPrimarySelection()));
        app.Keys("yy");
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "one\n", "yy on the clipboard");
        // the terminal's copy, Explorer's Copy Path, the editor's Ctrl+C...: all go through the service, without an activation or focus change
        await app.Host.Get<IClipboardService>().WriteAsync(ClipboardText.Plain("TWO"));
        await app.KeysAsync("jp", 3);
        Assert.Equal("one\ntTWOwo\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_editors_copy_in_Visual_mode_is_what_p_puts_next()
    {
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: SystemClipboard, configure: Clipboard(new InMemoryPrimarySelection()));
        app.Keys("yy");
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "one\n", "yy on the clipboard");
        app.Keys("jviw");
        Assert.Equal("-- VISUAL --", app.ModeText);
        app.Keys("<C-c>");                                        // an application key in Visual mode: the editor's Copy
        await EventuallyAsync(() => ClipboardAsync(app), c => c?.Text == "two", "the editor's copy on the clipboard");
        await app.KeysAsync("<Esc>p", 3);
        Assert.Equal("one\ntwotwo\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_vim_useSystemClipboard_p_waits_for_a_clipboard_read_in_flight()
    {
        var platform = new GatedPlatformClipboard();
        await using var app = await VimApp.StartAsync("one\ntwo\n", settings: SystemClipboard, configure: Gated(platform, primary: null));
        await app.Host.Window.Clipboard!.SetTextAsync("outside");  // copied in another application
        var gate = platform.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = app.Vim.Clipboard.RefreshAllAsync();       // what activating the window starts; the owner is slow to answer
        app.Keys("jp");
        Assert.True(app.Vim.HeldKeyCount > 0, "p did not wait for the clipboard read");
        Assert.Equal("one\ntwo\n", app.Text);
        gate.SetResult();
        platform.Gate = null;
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await DevScene.SettleAsync(5);
        Assert.Equal(0, app.Vim.HeldKeyCount);
        Assert.Equal("one\ntoutsidewo\n", app.Text);

        app.Keys("P");                                            // no read in flight: nothing waits
        Assert.Equal(0, app.Vim.HeldKeyCount);
        Assert.Equal("one\ntoutsidoutsideewo\n", app.Text);  // before the caret, which p left on the last character it put
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Focusing_the_same_editor_again_reads_the_clipboard_changed_meanwhile()
    {
        await using var app = await VimApp.StartAsync("x\n", settings: Quiet, configure: Clipboard(new InMemoryPrimarySelection()));
        var shell = app.Host.Shell;
        shell.Tools.Show("output");
        await DevScene.SettleAsync(5);
        shell.Tools.Get("output")!.LiveView!.GetVisualDescendants().OfType<Control>().First(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled).Focus();
        // e.g. `xclip` run in the integrated terminal: CsCode stays the active window, so no activation reads it
        await app.Host.Window.Clipboard!.SetTextAsync("CHANGED");
        await app.FocusAsync(5);                                  // back into the same editor
        await app.KeysAsync("A<C-r>+<Esc>", 3);                    // Ctrl+R in Insert mode has no "+ to wait on
        Assert.Equal("xCHANGED\n", app.Text);
    }

    // ------------------------------------------------------------------ Vim off (vim-macros-clipboard-6)

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Switching_Vim_off_drops_the_clipboard_registers_and_reads_nothing_more()
    {
        var platform = new GatedPlatformClipboard();
        var primary = new InMemoryPrimarySelection();
        await using var app = await VimApp.StartAsync("one\n", settings: Quiet, configure: Gated(platform, primary));
        app.Keys("\"+yy\"*yy");
        Assert.Equal("one\n", app.Vim.Clipboard.Text('+'));
        Assert.Equal("one\n", app.Vim.Clipboard.Text('*'));

        await app.Host.RunAsync("vim.toggle");
        Assert.True(await RealAppHost.WaitFor(() => !app.Vim.Enabled));
        Assert.Null(app.Vim.Clipboard.Read('+'));
        Assert.Null(app.Vim.Clipboard.Read('*'));
        var reads = Volatile.Read(ref platform.Reads);
        primary.SetExternal("SELECTED");
        await app.Vim.Clipboard.RefreshAllAsync();                // what activating the window does
        await app.Host.Get<IClipboardService>().WriteAsync(ClipboardText.Plain("copied"));
        await DevScene.SettleAsync(3);
        Assert.Equal(reads, Volatile.Read(ref platform.Reads));
        Assert.Null(app.Vim.Clipboard.Read('+'));
        Assert.Null(app.Vim.Clipboard.Read('*'));

        await app.Host.RunAsync("vim.toggle");                    // on again: the registers are read afresh
        Assert.True(await RealAppHost.WaitFor(() => app.Vim.Enabled && app.Vim.Clipboard.Text('+') == "copied" && app.Vim.Clipboard.Text('*') == "SELECTED"));
    }
}
