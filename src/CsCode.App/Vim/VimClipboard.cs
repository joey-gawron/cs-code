using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace CsCode.App.Vim;

/// <summary>
/// The system clipboard (CLIPBOARD; on Linux the PRIMARY selection is not used: <c>"*</c> and <c>"+</c> both mean the clipboard) for the Vim
/// registers. The engine reads synchronously but the platform only reads asynchronously, so the text is cached: refreshed when the window is
/// activated, when an editor takes focus and, with keys held until it completes, right after <c>"+</c>/<c>"*</c> (<see cref="VimIntegration"/>).
/// Writes update the cache at once and reach the platform asynchronously.
/// </summary>
internal sealed class VimClipboard(Func<TopLevel?> topLevel)
{
    private Task? _refresh;

    /// <summary>The last known clipboard text.</summary>
    public string? Text { get; private set; }

    public void Write(string text)
    {
        Text = text;
        if (topLevel()?.Clipboard is not { } clipboard) return;
        async Task SetAsync()
        {
            try { await clipboard.SetTextAsync(text).ConfigureAwait(true); }
            catch (Exception) { /* the platform refused: the registers still hold the text */ }
        }
        _ = SetAsync();
    }

    /// <summary>Reads the platform clipboard into <see cref="Text"/>; concurrent calls share one read.</summary>
    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false } running) return running;
        if (topLevel()?.Clipboard is not { } clipboard) return Task.CompletedTask;
        async Task ReadAsync()
        {
            try
            {
                var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
                if (text is not null) Text = text;
            }
            catch (Exception) { /* unreadable (another application owns it in a format without text): keep the last text */ }
        }
        return _refresh = ReadAsync();
    }
}
