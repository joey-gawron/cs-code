using System.Runtime.CompilerServices;
using CsCode.Core.Platform;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>
/// The Vim registers <c>+</c> and <c>*</c> over <see cref="IClipboardService"/>. <c>+</c> is the clipboard, written as the editor's own Copy writes
/// it: into the clipboard ring, with CsCode's shape metadata (a linewise yank is whole lines, a blockwise yank a block with one piece per row) and
/// the document's line ends. <c>*</c> is the PRIMARY selection where the platform has one (Linux X11, also under XWayland) and the clipboard
/// elsewhere, as in Vim. Reads come back with <c>'\n'</c> line ends and the register type when it is known: a yank of CsCode's own (exactly, while
/// the clipboard or selection still holds its text), or the editor's whole-line and box copies.
/// <para>
/// The engine reads synchronously, inside key handling, and the service only asynchronously, so each register's content is cached. The cache
/// follows every copy CsCode makes anywhere at once (the service announces them: the editor's Ctrl+C, the terminal's copy, Copy Path...), and is
/// read afresh when a window is activated (both registers: another application may have copied), when an editor takes the focus (<c>+</c>: a
/// copy made while CsCode stayed active, e.g. <c>xclip</c> in the terminal) and, with the keys held until it completes, right after
/// <c>"+</c> / <c>"*</c>; a put of the unnamed register that stands for one of them waits for a read in flight (<see cref="VimIntegration"/>).
/// Writes update the cache at once; their line-end conversion and the platform call run off the UI thread, in order, and a refresh waits for
/// the writes before it. While Vim is off (<see cref="Enabled"/>) nothing is read or kept.
/// </para>
/// <para>
/// Threading: written and read by the engine on the UI thread; refreshes and the service's announcements publish from any thread. Each register
/// is published as one immutable snapshot (text and type together, <see cref="Read"/>), under the register's lock.
/// </para>
/// </summary>
internal sealed class VimClipboard : IDisposable
{
    /// <summary>What a register holds: text with '\n' line ends, and its type when known (null: the engine decides from the text).</summary>
    internal sealed record Content(string Text, VimRegisterType? Type);

    /// <summary>
    /// A register's content as the engine reads it. A copy CsCode made elsewhere is kept as the service's clipboard text and converted (line ends,
    /// shape) only when the engine reads it: the copy itself, on the UI thread, does not pay for a register Vim may never put.
    /// </summary>
    private sealed class Snapshot
    {
        private readonly Content? _written;
        private ClipboardText? _clip;
        private Content? _content;

        public Snapshot(Content content) => _content = content;

        public Snapshot(ClipboardText clip, Content? written)
        {
            _clip = clip;
            _written = written;
        }

        public Content Content
        {
            get
            {
                if (Volatile.Read(ref _content) is { } content) return content;
                // converted by another reader meanwhile: its result was published before the clipboard text was let go
                if (Volatile.Read(ref _clip) is not { } clip) return Volatile.Read(ref _content)!;
                Interlocked.CompareExchange(ref _content, FromClipboardText(clip, _written), null);
                Volatile.Write(ref _clip, null);
                return Volatile.Read(ref _content)!;
            }
        }
    }

    private sealed class Slot
    {
        /// <summary>Read without the lock (the engine's put); replaced under it.</summary>
        public volatile Snapshot? Cached;
        /// <summary>What CsCode wrote last: a read that returns exactly this text gets its type back.</summary>
        public Content? Written;
        /// <summary>Bumped (under the lock) by every write and copy, so a refresh that started before them does not replace them with older text.</summary>
        public int Version;
        /// <summary>The read in flight (UI thread).</summary>
        public Task? Refresh;
    }

    /// <summary>
    /// How long a write waits for the one before it, and a read for the writes before it: a platform clipboard that never finishes a write must
    /// not stop every later write and read (the cache still answers the engine).
    /// </summary>
    internal static readonly TimeSpan WriteWait = TimeSpan.FromSeconds(2);

    private static readonly object OwnWrite = new();

    private readonly IClipboardService _service;
    private readonly Slot _clipboard = new(), _selection = new();
    /// <summary>The clipboard texts Vim itself handed to the service (weakly: the ring and the platform own them).</summary>
    private readonly ConditionalWeakTable<ClipboardText, object> _own = new();
    private readonly IDisposable _copies;
    private Task _writes = Task.CompletedTask;
    private volatile bool _enabled = true;

    public VimClipboard(IClipboardService service)
    {
        _service = service;
        // The shell's service announces every write (a copy over the ring's 1 MB too); another implementation, the copies its ring takes.
        var copies = service is Services.ClipboardService shell ? shell.Written : service.RingChanged;
        _copies = copies.Subscribe(OnCopied);
    }

    /// <summary>Whether <c>*</c> is a register of its own (PRIMARY) rather than another name for the clipboard.</summary>
    public bool HasSelection => _service.IsPrimarySelectionSupported;

    /// <summary>
    /// False while Vim is off: window activations and editor focus read nothing, CsCode's copies are not kept, and what was kept (possibly a whole
    /// selection of a large file) is dropped. Set by <see cref="VimIntegration"/> on the UI thread.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) return;
            foreach (var slot in (Slot[])[_clipboard, _selection])
                lock (slot)
                {
                    slot.Version++; // a read in flight is not kept either
                    slot.Cached = null;
                    slot.Written = null;
                }
        }
    }

    private bool IsSelection(char register) => register == '*' && HasSelection;

    private Slot SlotOf(char register) => IsSelection(register) ? _selection : _clipboard;

    /// <summary>The last known content of <paramref name="register"/> (<c>'+'</c> or <c>'*'</c>): the text and type of one copy, or null.</summary>
    public Content? Read(char register) => SlotOf(register).Cached?.Content;

    /// <summary>The last known text of <paramref name="register"/> (<c>'+'</c> or <c>'*'</c>).</summary>
    public string? Text(char register) => Read(register)?.Text;

    /// <summary>The register type of <see cref="Text"/> when known.</summary>
    public VimRegisterType? Type(char register) => Read(register)?.Type;

    /// <summary>The read of <paramref name="register"/> in flight, or null (UI thread): a put of the register waits for it.</summary>
    public Task? ReadInFlight(char register) => SlotOf(register).Refresh is { IsCompleted: false } read ? read : null;

    /// <summary>Whether a read of <paramref name="register"/> is in flight (UI thread).</summary>
    public bool IsRefreshing(char register) => ReadInFlight(register) is not null;

    /// <summary>Writes <paramref name="register"/>; <paramref name="eol"/> is the line end of the document the text came from.</summary>
    public void Write(char register, string text, VimRegisterType type, string eol)
    {
        var content = new Content(text, type);
        var selection = IsSelection(register);
        var slot = selection ? _selection : _clipboard;
        lock (slot)
        {
            slot.Version++;
            slot.Cached = new Snapshot(content);
            slot.Written = content;
        }
        if (selection)
        {
            // offered lazily, like the editor's selection: the line ends are converted only when someone pastes it (on the selection's thread)
            _service.SetPrimarySelection(() => ClipboardTextExtensions.NormalizeLineEndings(text, eol));
            return;
        }
        _writes = WriteAfterAsync(_writes, content, eol);
    }

    private async Task WriteAfterAsync(Task previous, Content content, string eol)
    {
        try { await previous.WaitAsync(WriteWait).ConfigureAwait(false); }
        catch (Exception) { /* the previous write failed, or still hangs: this one goes ahead */ }
        try
        {
            // a register holds up to vim.maxRegisterChars: its line ends are converted on the pool, not inside the key
            var clip = await Task.Run(() => ToClipboardText(content, eol)).ConfigureAwait(false);
            _own.AddOrUpdate(clip, OwnWrite); // announced back to OnCopied, which must not take it for a newer copy
            await _service.WriteAsync(clip).ConfigureAwait(false);
        }
        catch (Exception) { /* the platform refused: the registers still hold the text */ }
    }

    /// <summary>A copy made anywhere in CsCode (any thread): it is what the clipboard holds now.</summary>
    private void OnCopied(ClipboardText clip)
    {
        // Vim's own write: the register already holds it, and a later yank may have replaced it since
        if (!_enabled || _own.TryGetValue(clip, out _)) return;
        var slot = _clipboard;
        lock (slot)
        {
            if (!_enabled) return;
            slot.Version++;
            slot.Cached = new Snapshot(clip, slot.Written);
        }
    }

    /// <summary>A register as CsCode's clipboard text: linewise is whole lines, blockwise a block with one piece per row; line ends as the document's.</summary>
    internal static ClipboardText ToClipboardText(Content content, string eol)
    {
        var text = ClipboardTextExtensions.NormalizeLineEndings(content.Text, eol);
        return content.Type switch
        {
            VimRegisterType.Line => new ClipboardText(text, ClipboardTextShape.FullLines),
            VimRegisterType.Block => new ClipboardText(text, ClipboardTextShape.Block, content.Text.Split('\n')),
            _ => new ClipboardText(text),
        };
    }

    /// <summary>CsCode's clipboard text as a register: '\n' line ends; whole lines are linewise, a box of single-line pieces blockwise.</summary>
    internal static Content FromClipboardText(ClipboardText clip, Content? written)
    {
        var text = ClipboardTextExtensions.NormalizeLineEndings(clip.Text, "\n");
        if (written is not null && string.Equals(written.Text, text, StringComparison.Ordinal)) return written;
        if (clip.Shape == ClipboardTextShape.Block && clip.Pieces is { Count: > 0 } pieces && pieces.All(p => p.AsSpan().IndexOfAny('\r', '\n') < 0))
            return new Content(string.Join('\n', pieces), VimRegisterType.Block);
        return new Content(text, clip.Shape == ClipboardTextShape.FullLines ? VimRegisterType.Line : null);
    }

    /// <summary>Reads <paramref name="register"/> into the cache; concurrent calls share one read. Nothing while Vim is off.</summary>
    public Task RefreshAsync(char register)
    {
        if (!_enabled) return Task.CompletedTask;
        return IsSelection(register) ? Refresh(_selection, selection: true) : Refresh(_clipboard, selection: false);
    }

    /// <summary>Reads both registers (the selection only where it is a register of its own). Nothing while Vim is off.</summary>
    public Task RefreshAllAsync()
    {
        if (!_enabled) return Task.CompletedTask;
        return HasSelection ? Task.WhenAll(RefreshAsync('+'), RefreshAsync('*')) : RefreshAsync('+');
    }

    private Task Refresh(Slot slot, bool selection)
    {
        if (slot.Refresh is { IsCompleted: false } running) return running;
        int version;
        lock (slot) version = slot.Version;
        return slot.Refresh = ReadAsync(slot, selection, _writes, version);
    }

    /// <param name="pendingWrites">The clipboard writes started before this read: it reads after them.</param>
    /// <param name="version">The slot's version when the read started: a write or copy after that makes the result stale.</param>
    private async Task ReadAsync(Slot slot, bool selection, Task pendingWrites, int version)
    {
        try
        {
            if (!selection)
            {
                try { await pendingWrites.WaitAsync(WriteWait).ConfigureAwait(false); }
                catch (Exception) { /* failed (or hanging) writes leave the clipboard as it was */ }
            }
            Content? content;
            Content? written;
            lock (slot) written = slot.Written;
            if (selection)
                content = await _service.ReadPrimarySelectionAsync().ConfigureAwait(false) is { } text ? FromSelectionText(text, written) : null;
            else
                content = await _service.ReadAsync().ConfigureAwait(false) is { } clip ? FromClipboardText(clip, written) : null;
            // null: nothing readable (empty, another application's non-text data, no answer in time): keep the last known text
            if (content is null) return;
            lock (slot)
                if (_enabled && slot.Version == version) slot.Cached = new Snapshot(content);
        }
        catch (Exception) { /* unreadable: keep the last known text */ }
    }

    private static Content? FromSelectionText(string text, Content? written)
    {
        if (text.Length == 0) return null;
        var normalized = ClipboardTextExtensions.NormalizeLineEndings(text, "\n");
        return written is not null && string.Equals(written.Text, normalized, StringComparison.Ordinal) ? written : new Content(normalized, null);
    }

    public void Dispose() => _copies.Dispose();
}
