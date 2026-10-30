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
        /// <summary>Awaited before the write is announced (RingChanged): a late announcement.</summary>
        public Func<ClipboardText, Task> BeforeAnnounce { get; set; } = _ => Task.CompletedTask;
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
            // as the shell's service: the copy is announced before the platform write (synchronously, unless the test delays it)
            var announce = BeforeAnnounce(content);
            if (!announce.IsCompleted) await announce.ConfigureAwait(false);
            _ringChanged.Fire(content);
            await Write(content).ConfigureAwait(false);
        }

        public ValueTask<bool> HasTextAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
        public IReadOnlyList<ClipboardText> Ring => [];
        private readonly Emitter<ClipboardText> _ringChanged = new();
        public IEventSource<ClipboardText> RingChanged => _ringChanged;
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

    // ------------------------------------------------------------------ CsCode's other copies (vim-macros-clipboard-1)

    [Fact]
    public void A_copy_made_elsewhere_in_CsCode_is_the_plus_register_at_once()
    {
        var service = new ControlledClipboard();
        using var clipboard = new VimClipboard(service);
        clipboard.Write('+', "one\n", VimRegisterType.Line, "\n");
        // the editor's Ctrl+C, the terminal's copy, Copy Path...: no window activation, no editor focus change in between
        _ = service.WriteAsync(ClipboardText.Plain("two\r\nthree"));
        Assert.Equal(new VimClipboard.Content("two\nthree", null), clipboard.Read('+'));
        _ = service.WriteAsync(new ClipboardText("x\n", ClipboardTextShape.FullLines));
        Assert.Equal(new VimClipboard.Content("x\n", VimRegisterType.Line), clipboard.Read('+'));
        _ = service.WriteAsync(new ClipboardText("ab\r\ncd", ClipboardTextShape.Block, ["ab", "cd"]));
        Assert.Equal(new VimClipboard.Content("ab\ncd", VimRegisterType.Block), clipboard.Read('+'));
        Assert.Null(clipboard.Read('*'));                             // the selection is not the clipboard
    }

    [Fact]
    public async Task Vims_own_write_announced_late_does_not_replace_a_newer_yank()
    {
        var service = new ControlledClipboard();
        var announce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeAnnounce = c => c.Text == "A" ? announce.Task : Task.CompletedTask;
        using var clipboard = new VimClipboard(service);

        clipboard.Write('+', "A", VimRegisterType.Character, "\n");
        await UntilAsync(() => service.Written.Count == 1, "the first write reached the service");
        clipboard.Write('+', "B", VimRegisterType.Line, "\n");
        announce.SetResult();                                         // "A" is announced after "B" was yanked
        await UntilAsync(() => service.Written.Any(w => w.Text == "B"), "the second write reached the service");
        Assert.Equal(new VimClipboard.Content("B", VimRegisterType.Line), clipboard.Read('+'));
    }

    [Fact]
    public async Task A_read_in_flight_is_reported_until_it_completes()
    {
        var service = new ControlledClipboard();
        var answer = new TaskCompletionSource<ClipboardText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Read = () => answer.Task;
        using var clipboard = new VimClipboard(service);
        Assert.False(clipboard.IsRefreshing('+'));
        var refresh = clipboard.RefreshAsync('+');
        Assert.True(clipboard.IsRefreshing('+'));
        answer.SetResult(ClipboardText.Plain("now"));
        await refresh.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(clipboard.IsRefreshing('+'));
        Assert.Equal("now", clipboard.Text('+'));
    }

    [Fact]
    public void A_disposed_clipboard_is_not_retained_by_the_service_it_listened_to()
    {
        var service = new ControlledClipboard();
        CsCode.TestDoubles.LeakAssert.NotRetained(() =>
        {
            var clipboard = new VimClipboard(service);
            clipboard.Dispose();
            return [clipboard];
        });
        _ = service.WriteAsync(ClipboardText.Plain("after")); // nobody listens any more
    }

    // ------------------------------------------------------------------ Vim switched off (vim-macros-clipboard-6)

    [Fact]
    public async Task While_Vim_is_off_nothing_is_read_or_kept()
    {
        var service = new ControlledClipboard { Read = () => Task.FromResult<ClipboardText?>(ClipboardText.Plain("CLIP")) };
        var answer = new TaskCompletionSource<ClipboardText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var clipboard = new VimClipboard(service);
        clipboard.Write('+', "plus", VimRegisterType.Character, "\n");
        clipboard.Write('*', "star", VimRegisterType.Character, "\n");
        service.Read = () => answer.Task;
        var late = clipboard.RefreshAsync('+');                       // a read still in flight when Vim is switched off

        clipboard.Enabled = false;
        Assert.Null(clipboard.Read('+'));
        Assert.Null(clipboard.Read('*'));
        answer.SetResult(ClipboardText.Plain("LATE"));
        await late.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Null(clipboard.Read('+'));                             // not kept
        var reads = Volatile.Read(ref service.Reads);
        await clipboard.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(reads, Volatile.Read(ref service.Reads));        // window activation reads nothing
        _ = service.WriteAsync(ClipboardText.Plain("copied"));
        Assert.Null(clipboard.Read('+'));                             // CsCode's copies are not kept either

        clipboard.Enabled = true;
        service.Read = () => Task.FromResult<ClipboardText?>(ClipboardText.Plain("CLIP"));
        await clipboard.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("CLIP", clipboard.Text('+'));
    }

    // ------------------------------------------------------------------ "* and CsCode's own PRIMARY offers (vim-macros-clipboard-4 with -1)

    /// <summary>An in-process PRIMARY counting its reads; while <see cref="Gate"/> is set a read waits for it before it looks at the owner.</summary>
    private sealed class CountingPrimary : Services.IPrimarySelectionBackend
    {
        private readonly Services.InMemoryPrimarySelection _inner = new();
        public volatile TaskCompletionSource? Gate;
        public int Reads;

        public bool IsAvailable => true;
        public bool IsOwner => _inner.IsOwner;
        public void SetOwner(Func<string>? provider) => _inner.SetOwner(provider);
        public void SetExternal(string? text) => _inner.SetExternal(text);

        public async Task<string?> ReadAsync(TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (Gate is { } gate) await gate.Task.ConfigureAwait(false);
            return await _inner.ReadAsync(timeout, ct).ConfigureAwait(false);
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task Star_reads_other_selections_but_neither_vims_own_write_nor_an_editors_selection()
    {
        var primary = new CountingPrimary();
        using var service = new Services.ClipboardService(CsCode.Ui.Threading.AvaloniaUiDispatcher.Instance, () => null, () => primary);
        Func<string> editorSelection = () => "SELECTED";
        using var clipboard = new VimClipboard(service, offer => ReferenceEquals(offer, editorSelection)) { Enabled = true };
        var ct = TestContext.Current.CancellationToken;

        clipboard.Write('*', "yank", VimRegisterType.Line, "\n");
        await clipboard.RefreshAsync('*').WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, Volatile.Read(ref primary.Reads));           // Vim's own write owns PRIMARY: the register already holds it
        Assert.Equal(new VimClipboard.Content("yank", VimRegisterType.Line), clipboard.Read('*'));

        service.SetPrimarySelection(editorSelection);                 // an editor's selection (Vim's Visual mode): not "*
        await clipboard.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(0, Volatile.Read(ref primary.Reads));           // not even read (a whole selection is not joined for nothing)
        Assert.Equal("yank", clipboard.Text('*'));

        service.SetPrimarySelection(() => "terminal");                // the terminal's selection: read
        await clipboard.RefreshAsync('*').WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("terminal", clipboard.Text('*'));
        primary.SetExternal("other");                                 // another application's: read
        await clipboard.RefreshAsync('*').WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("other", clipboard.Text('*'));

        // an editor's offer made while a read is on its way (on X11 the claim may reach the server only then): the read returns it; not kept
        primary.SetExternal("next");
        var gate = primary.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = clipboard.RefreshAsync('*');
        service.SetPrimarySelection(editorSelection);
        gate.SetResult();
        await read.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("other", clipboard.Text('*'));
        primary.Gate = null;

        // Vim switched off and on while its own write still owns PRIMARY: the register was dropped, so it is read again
        clipboard.Write('*', "again", VimRegisterType.Character, "\n");
        clipboard.Enabled = false;
        clipboard.Enabled = true;
        var reads = Volatile.Read(ref primary.Reads);
        await clipboard.RefreshAsync('*').WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(reads + 1, Volatile.Read(ref primary.Reads));
        Assert.Equal("again", clipboard.Text('*'));
    }
}
