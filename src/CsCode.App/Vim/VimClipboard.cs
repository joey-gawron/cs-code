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
/// The engine reads synchronously, inside key handling, and the service only asynchronously, so each register's content is cached: refreshed
/// when the window is activated (both registers), when an editor takes focus (<c>+</c>: the selection is CsCode's own while it has focus, and
/// reading it would produce text the editor offers lazily) and, with the keys held until it completes, right after <c>"+</c> / <c>"*</c>
/// (<see cref="VimIntegration"/>). Writes update the cache at once; their line-end conversion and the platform call run off the UI thread, in
/// order, and a refresh waits for the writes before it. UI-thread object (the refreshes complete off it and publish immutable snapshots).
/// </para>
/// </summary>
internal sealed class VimClipboard(IClipboardService service)
{
    /// <summary>What a register holds: text with '\n' line ends, and its type when known (null: the engine decides from the text).</summary>
    internal sealed record Content(string Text, VimRegisterType? Type);

    private sealed class Slot
    {
        public volatile Content? Cached;
        /// <summary>What CsCode wrote last: a read that returns exactly this text gets its type back.</summary>
        public volatile Content? Written;
        /// <summary>Bumped by every write, so a refresh that started before a write does not replace its text with the older one.</summary>
        public int Version;
        public Task? Refresh;
    }

    private readonly Slot _clipboard = new(), _selection = new();
    private Task _writes = Task.CompletedTask;

    /// <summary>Whether <c>*</c> is a register of its own (PRIMARY) rather than another name for the clipboard.</summary>
    public bool HasSelection => service.IsPrimarySelectionSupported;

    private bool IsSelection(char register) => register == '*' && HasSelection;

    private Slot SlotOf(char register) => IsSelection(register) ? _selection : _clipboard;

    /// <summary>The last known text of <paramref name="register"/> (<c>'+'</c> or <c>'*'</c>).</summary>
    public string? Text(char register) => SlotOf(register).Cached?.Text;

    /// <summary>The register type of <see cref="Text"/> when known.</summary>
    public VimRegisterType? Type(char register) => SlotOf(register).Cached?.Type;

    /// <summary>Writes <paramref name="register"/>; <paramref name="eol"/> is the line end of the document the text came from.</summary>
    public void Write(char register, string text, VimRegisterType type, string eol)
    {
        var content = new Content(text, type);
        var selection = IsSelection(register);
        var slot = selection ? _selection : _clipboard;
        Interlocked.Increment(ref slot.Version);
        slot.Cached = slot.Written = content;
        if (selection)
        {
            // offered lazily, like the editor's selection: the line ends are converted only when someone pastes it (on the selection's thread)
            service.SetPrimarySelection(() => ClipboardTextExtensions.NormalizeLineEndings(text, eol));
            return;
        }
        _writes = WriteAfterAsync(_writes, content, eol);
    }

    private async Task WriteAfterAsync(Task previous, Content content, string eol)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* the previous write failed on its own */ }
        try
        {
            // a register holds up to vim.maxRegisterChars: its line ends are converted on the pool, not inside the key
            var clip = await Task.Run(() => ToClipboardText(content, eol)).ConfigureAwait(false);
            await service.WriteAsync(clip).ConfigureAwait(false);
        }
        catch (Exception) { /* the platform refused: the registers still hold the text */ }
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

    /// <summary>Reads <paramref name="register"/> into the cache; concurrent calls share one read.</summary>
    public Task RefreshAsync(char register) => IsSelection(register) ? Refresh(_selection, selection: true) : Refresh(_clipboard, selection: false);

    /// <summary>Reads both registers (the selection only where it is a register of its own).</summary>
    public Task RefreshAllAsync() => HasSelection ? Task.WhenAll(RefreshAsync('+'), RefreshAsync('*')) : RefreshAsync('+');

    private Task Refresh(Slot slot, bool selection)
    {
        if (slot.Refresh is { IsCompleted: false } running) return running;
        return slot.Refresh = ReadAsync(slot, selection, _writes, Volatile.Read(ref slot.Version));
    }

    /// <param name="pendingWrites">The clipboard writes started before this read: it reads after them.</param>
    /// <param name="version">The slot's version when the read started: a write after that makes the result stale.</param>
    private async Task ReadAsync(Slot slot, bool selection, Task pendingWrites, int version)
    {
        try
        {
            if (!selection)
            {
                try { await pendingWrites.ConfigureAwait(false); }
                catch (Exception) { /* failed writes leave the clipboard as it was */ }
            }
            Content? content;
            if (selection)
                content = await service.ReadPrimarySelectionAsync().ConfigureAwait(false) is { } text
                    ? FromSelectionText(text, slot.Written)
                    : null;
            else
                content = await service.ReadAsync().ConfigureAwait(false) is { } clip ? FromClipboardText(clip, slot.Written) : null;
            // null: nothing readable (empty, another application's non-text data, no answer in time): keep the last known text
            if (content is not null && Volatile.Read(ref slot.Version) == version) slot.Cached = content;
        }
        catch (Exception) { /* unreadable: keep the last known text */ }
    }

    private static Content? FromSelectionText(string text, Content? written)
    {
        if (text.Length == 0) return null;
        var normalized = ClipboardTextExtensions.NormalizeLineEndings(text, "\n");
        return written is not null && string.Equals(written.Text, normalized, StringComparison.Ordinal) ? written : new Content(normalized, null);
    }
}
