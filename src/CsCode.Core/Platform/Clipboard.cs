using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

/// <summary>How a piece of copied text was selected, so a paste can put it back the same way (Visual Studio's clipboard tags).</summary>
public enum ClipboardTextShape
{
    /// <summary>An ordinary (stream) selection.</summary>
    Normal,
    /// <summary>Whole lines copied with an empty selection (Ctrl+C on a caret): a paste inserts them above the caret's line.</summary>
    FullLines,
    /// <summary>A box (column) selection: one row per line, pasted as a block.</summary>
    Block,
}

/// <summary>
/// Text on the clipboard plus what CsCode knows about it. <see cref="Shape"/> and <see cref="Pieces"/> survive only inside CsCode: they are
/// returned by <see cref="IClipboardService.ReadAsync"/> while the system clipboard still holds exactly the text CsCode wrote; text that another
/// application put there always reads back as <see cref="ClipboardTextShape.Normal"/> with no pieces.
/// </summary>
/// <param name="Text">The plain text (line endings as copied).</param>
/// <param name="Shape">Stream, whole lines or block.</param>
/// <param name="Pieces">One entry per selection (caret) the text was copied from, when there were several; a paste into as many carets puts one piece at each.</param>
/// <param name="Html">Optional HTML rendition (Copy with formatting); written as <c>text/html</c> next to the text when the platform supports it.</param>
public sealed record ClipboardText(string Text, ClipboardTextShape Shape = ClipboardTextShape.Normal, IReadOnlyList<string>? Pieces = null, string? Html = null)
{
    public static ClipboardText Plain(string text) => new(text);

    /// <summary>
    /// Visual width (tabs expanded) of the indentation of the line the copy started on, when an editor copied it: a multi-line paste keeps the
    /// copied lines' indentation relative to that line. Null for text from elsewhere (the paste then infers it from the text).
    /// </summary>
    public int? SourceIndent { get; init; }
}

/// <summary>Which X11-style selection to address. <see cref="Primary"/> exists on Linux/X11 (select to copy, middle-click to paste) only.</summary>
public enum ClipboardSelection { Clipboard, Primary }

/// <summary>Limits of the clipboard ring (Visual Studio's Ctrl+Shift+V history).</summary>
public static class ClipboardLimits
{
    /// <summary>How many copies the ring keeps (newest first).</summary>
    public const int RingCapacity = 20;

    /// <summary>
    /// The largest copy the ring keeps, in bytes of managed memory (1 MB = 524,288 UTF-16 characters). A bigger copy is still put on the system
    /// clipboard, it is only left out of the ring, so the ring never holds more than 20 MB.
    /// </summary>
    public const int RingEntryMaxBytes = 1024 * 1024;
}

/// <summary>
/// The system clipboard (and, on Linux, the PRIMARY selection) for every module: the editor, the panels, the shell. Implemented by the shell
/// (CsCode.App) over the window's platform clipboard; optional in DI (a module without it falls back to <see cref="IWindowService.CopyToClipboardAsync"/>).
/// <para>
/// Threading: every member may be called from any thread and returns without waiting for the clipboard owner (another process may take a while
/// to answer, or never): reads and writes complete asynchronously, reads time out (empty result) rather than hang. Copies always go to the LOCAL
/// clipboard, also for documents of a remote workspace.
/// </para>
/// <para>
/// Memory: the clipboard ring (<see cref="Ring"/>) lives in memory only and is never persisted (a copied secret must not land on disk); it keeps
/// <see cref="ClipboardLimits.RingCapacity"/> entries of at most <see cref="ClipboardLimits.RingEntryMaxBytes"/> each.
/// </para>
/// </summary>
public interface IClipboardService
{
    /// <summary>Reads the clipboard: null when it holds no text (or the owner did not answer in time).</summary>
    ValueTask<ClipboardText?> ReadAsync(CancellationToken ct = default);

    /// <summary>Puts text on the clipboard and at the top of the ring.</summary>
    ValueTask WriteAsync(ClipboardText content, CancellationToken ct = default);

    /// <summary>Whether the clipboard currently holds text, without reading it (menus enable Paste from this). False when unknown in time.</summary>
    ValueTask<bool> HasTextAsync(CancellationToken ct = default);

    /// <summary>Recent copies, newest first (in memory only).</summary>
    IReadOnlyList<ClipboardText> Ring { get; }

    /// <summary>Fires (on any thread) after a copy entered the ring.</summary>
    IEventSource<ClipboardText> RingChanged { get; }

    /// <summary>True when the platform has a PRIMARY selection that this service can own and read (Linux X11, including XWayland).</summary>
    bool IsPrimarySelectionSupported { get; }

    /// <summary>
    /// Makes CsCode the owner of the PRIMARY selection. The text is produced lazily, only when another application (or a middle-click) asks for it,
    /// by <paramref name="provider"/>, which is called on a background thread and must therefore read immutable state (a document snapshot).
    /// Null gives up the selection. A no-op where <see cref="IsPrimarySelectionSupported"/> is false. Never blocks.
    /// </summary>
    void SetPrimarySelection(Func<string>? provider);

    /// <summary>Reads the PRIMARY selection (CsCode's own without a round trip when it owns it): null when empty, unsupported or not answered in time.</summary>
    ValueTask<string?> ReadPrimarySelectionAsync(CancellationToken ct = default);
}

/// <summary>Helpers shared by clipboard producers and consumers.</summary>
public static class ClipboardTextExtensions
{
    /// <summary>Converts every line ending (CRLF, CR, LF) in <paramref name="text"/> to <paramref name="eol"/>; returns the input when nothing changes.</summary>
    public static string NormalizeLineEndings(string text, string eol)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(eol);
        if (eol == "\n" && text.AsSpan().IndexOf('\r') < 0) return text; // the common case, vectorised: a 10 MB paste costs ~1 ms here
        var needs = false;
        for (var i = 0; i < text.Length && !needs; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                needs = eol != (crlf ? "\r\n" : "\r");
                if (crlf) i++;
            }
            else if (c == '\n') needs = eol != "\n";
        }
        if (!needs) return text;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(eol);
            }
            else if (c == '\n') sb.Append(eol);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Splits text into lines on CRLF, CR or LF (a trailing line ending gives a final empty line).</summary>
    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines.ToArray();
    }
}
