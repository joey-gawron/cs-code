using Avalonia.Controls;
using Avalonia.Input.Platform;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Editor.Fallbacks;

/// <summary>
/// The editor's clipboard when no <see cref="IClipboardService"/> is registered (the editor hosted on its own, the editor's own tests): the
/// clipboard of the window the active view is in, the shape of CsCode's last copy, and an in-memory ring. No PRIMARY selection.
/// Called on the UI thread only (from the views).
/// </summary>
internal sealed class LocalClipboardService(Func<TopLevel?> topLevel) : IClipboardService
{
    private readonly Emitter<ClipboardText> _ringChanged = new();
    private readonly List<ClipboardText> _ring = new();
    private ClipboardText? _last;

    public async ValueTask<ClipboardText?> ReadAsync(CancellationToken ct = default)
    {
        if (topLevel()?.Clipboard is not { } clipboard) return _last;
        var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        if (text is null) return null;
        return _last is { } last && last.Text == text ? last : ClipboardText.Plain(text);
    }

    public async ValueTask WriteAsync(ClipboardText content, CancellationToken ct = default)
    {
        _last = content;
        if ((long)content.Text.Length * sizeof(char) <= ClipboardLimits.RingEntryMaxBytes && content.Text.Length > 0)
        {
            _ring.RemoveAll(r => r.Text == content.Text);
            _ring.Insert(0, content);
            if (_ring.Count > ClipboardLimits.RingCapacity) _ring.RemoveAt(_ring.Count - 1);
            _ringChanged.Fire(content);
        }
        if (topLevel()?.Clipboard is { } clipboard) await clipboard.SetTextAsync(content.Text).ConfigureAwait(true);
    }

    public async ValueTask<bool> HasTextAsync(CancellationToken ct = default) => await ReadAsync(ct).ConfigureAwait(true) is { Text.Length: > 0 };

    public IReadOnlyList<ClipboardText> Ring => _ring.ToArray();
    public IEventSource<ClipboardText> RingChanged => _ringChanged;
    public bool IsPrimarySelectionSupported => false;
    public void SetPrimarySelection(Func<string>? provider) { }
    public ValueTask<string?> ReadPrimarySelectionAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
}
