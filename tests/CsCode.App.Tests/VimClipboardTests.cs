using CsCode.App.Vim;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// <see cref="VimClipboard"/> on its own, over a clipboard service the test controls: the conversions both ways, the ordering of writes and
/// reads (a read sees the writes before it; a read that started before a write never replaces the written text), and a platform write that never
/// finishes (later writes and reads go ahead after <see cref="VimClipboard.WriteWait"/>). The real-app behaviour is in
/// <c>RealApp/Vim/VimClipboardRegisterTests</c>.
/// </summary>
public class VimClipboardTests
{
    private sealed class ControlledClipboard : IClipboardService
    {
        public List<ClipboardText> Written { get; } = [];
        public int Reads;
        public Func<ClipboardText, Task> Write { get; set; } = _ => Task.CompletedTask;
        public Func<Task<ClipboardText?>> Read { get; set; } = () => Task.FromResult<ClipboardText?>(null);
        public bool Primary { get; set; } = true;
        public Func<string>? PrimaryProvider { get; private set; }

        public async ValueTask<ClipboardText?> ReadAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return await Read().ConfigureAwait(false);
        }

        public async ValueTask WriteAsync(ClipboardText content, CancellationToken ct = default)
        {
            lock (Written) Written.Add(content);
            await Write(content).ConfigureAwait(false);
        }

        public ValueTask<bool> HasTextAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
        public IReadOnlyList<ClipboardText> Ring => [];
        public IEventSource<ClipboardText> RingChanged { get; } = new Emitter<ClipboardText>();
        public bool IsPrimarySelectionSupported => Primary;
        public void SetPrimarySelection(Func<string>? provider) => PrimaryProvider = provider;
        public ValueTask<string?> ReadPrimarySelectionAsync(CancellationToken ct = default) => ValueTask.FromResult(PrimaryProvider?.Invoke());
    }

    private static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, what);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // ------------------------------------------------------------------ conversions

    [Theory]
    [InlineData(VimRegisterType.Line, "a\nb\n", "\r\n", "a\r\nb\r\n", ClipboardTextShape.FullLines)]
    [InlineData(VimRegisterType.Character, "a\nb", "\n", "a\nb", ClipboardTextShape.Normal)]
    [InlineData(VimRegisterType.Block, "ab\ncd", "\r\n", "ab\r\ncd", ClipboardTextShape.Block)]
    public void A_register_becomes_clipboard_text_with_its_shape_and_the_documents_line_ends(VimRegisterType type, string text, string eol, string expected, ClipboardTextShape shape)
    {
        var clip = VimClipboard.ToClipboardText(new VimClipboard.Content(text, type), eol);
        Assert.Equal(expected, clip.Text);
        Assert.Equal(shape, clip.Shape);
        if (type == VimRegisterType.Block) Assert.Equal(["ab", "cd"], clip.Pieces);
    }

    [Fact]
    public void Clipboard_text_becomes_a_register_with_newlines_and_the_type_its_shape_tells()
    {
        Assert.Equal(new VimClipboard.Content("a\nb\n", VimRegisterType.Line), VimClipboard.FromClipboardText(new ClipboardText("a\r\nb\r\n", ClipboardTextShape.FullLines), null));
        Assert.Equal(new VimClipboard.Content("ab\ncd", VimRegisterType.Block), VimClipboard.FromClipboardText(new ClipboardText("ab\r\ncd", ClipboardTextShape.Block, ["ab", "cd"]), null));
        // several carets' multi-line pieces are no Vim block: the text, typed by its final newline
        Assert.Equal(new VimClipboard.Content("a\nb\nc", null), VimClipboard.FromClipboardText(new ClipboardText("a\nb\nc", ClipboardTextShape.Block, ["a\nb", "c"]), null));
        Assert.Equal(new VimClipboard.Content("x\ny", null), VimClipboard.FromClipboardText(ClipboardText.Plain("x\ry"), null));
        // what CsCode wrote itself comes back with its exact type (a charwise text ending in a newline stays charwise)
        var written = new VimClipboard.Content("ghi\n", VimRegisterType.Character);
        Assert.Same(written, VimClipboard.FromClipboardText(ClipboardText.Plain("ghi\r\n"), written));
    }

    // ------------------------------------------------------------------ ordering

    [Fact]
    public async Task A_read_runs_after_the_writes_before_it()
    {
        var service = new ControlledClipboard();
        var writeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Write = _ => writeDone.Task;
        service.Read = () => Task.FromResult<ClipboardText?>(ClipboardText.Plain("A"));
        var clipboard = new VimClipboard(service);

        clipboard.Write('+', "A", VimRegisterType.Character, "\n");
        var refresh = clipboard.RefreshAsync('+');
        await UntilAsync(() => service.Written.Count == 1, "the write reached the service");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref service.Reads)); // still writing: the read waits
        writeDone.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref service.Reads));
        Assert.Equal("A", clipboard.Text('+'));
        Assert.Equal(VimRegisterType.Character, clipboard.Type('+')); // the text CsCode wrote: its type
    }

    [Fact]
    public async Task A_read_that_started_before_a_write_does_not_replace_the_written_text()
    {
        var service = new ControlledClipboard();
        var answer = new TaskCompletionSource<ClipboardText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Read = () => answer.Task;
        var clipboard = new VimClipboard(service);

        var refresh = clipboard.RefreshAsync('+');
        await UntilAsync(() => Volatile.Read(ref service.Reads) == 1, "the read started");
        clipboard.Write('+', "NEW", VimRegisterType.Line, "\n");
        answer.SetResult(ClipboardText.Plain("OLD"));
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("NEW", clipboard.Text('+'));
        Assert.Equal(VimRegisterType.Line, clipboard.Type('+'));

        await clipboard.RefreshAsync('+').WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // a later read applies
        Assert.Equal("OLD", clipboard.Text('+'));
        Assert.Null(clipboard.Type('+'));
    }

    [Fact]
    public async Task A_platform_write_that_never_finishes_holds_later_writes_and_reads_only_for_a_while()
    {
        var service = new ControlledClipboard();
        var never = new TaskCompletionSource();
        service.Write = c => c.Text == "stuck" ? never.Task : Task.CompletedTask;
        service.Read = () => Task.FromResult<ClipboardText?>(ClipboardText.Plain("after"));
        var clipboard = new VimClipboard(service);

        clipboard.Write('+', "stuck", VimRegisterType.Character, "\n");
        clipboard.Write('+', "next", VimRegisterType.Character, "\n");
        var refresh = clipboard.RefreshAsync('+');
        await UntilAsync(() => service.Written.Any(w => w.Text == "next"), "the second write went ahead", (int)(VimClipboard.WriteWait.TotalMilliseconds * 4));
        await refresh.WaitAsync(VimClipboard.WriteWait * 4, TestContext.Current.CancellationToken);
        Assert.Equal("after", clipboard.Text('+'));
        never.SetResult();
    }

    // ------------------------------------------------------------------ the selection

    [Fact]
    public async Task Star_is_the_selection_where_there_is_one_and_the_clipboard_otherwise()
    {
        var service = new ControlledClipboard();
        var clipboard = new VimClipboard(service);
        clipboard.Write('*', "a\nb", VimRegisterType.Block, "\r\n");
        Assert.Equal("a\r\nb", service.PrimaryProvider?.Invoke());   // offered with the document's line ends
        Assert.Empty(service.Written);
        await clipboard.RefreshAsync('*');
        Assert.Equal(("a\nb", (VimRegisterType?)VimRegisterType.Block), (clipboard.Text('*'), clipboard.Type('*'))); // its own text: its type
        Assert.Null(clipboard.Text('+'));

        service.Primary = false;
        clipboard.Write('*', "c", VimRegisterType.Character, "\n");
        await UntilAsync(() => service.Written.Count == 1, "the write reached the clipboard");
        Assert.Equal("c", service.Written[0].Text);
        Assert.Equal("c", clipboard.Text('+'));
    }
}
