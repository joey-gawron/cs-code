using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.QuickInput;

/// <summary>
/// Opens the command palette / Quick Launch on the shared quick-input overlay and runs the chosen entry once the overlay has closed
/// (so a command that itself opens a picker is never fighting the palette for the overlay). Owns the shared workspace file index.
/// </summary>
public sealed class PaletteController : IDisposable
{
    private readonly QuickInputViewModel _quick;
    private readonly PaletteContext _ctx;
    private readonly IUiDispatcher _ui;
    private readonly IWindowService? _window;
    private readonly FileIndex _index;
    private readonly IDisposable? _memoryReg;

    public PaletteController(QuickInputViewModel quick, PaletteContext context, IUiDispatcher ui, IWindowService? window = null)
    {
        _quick = quick;
        _ctx = context;
        _ui = ui;
        _window = window;
        _index = new FileIndex(context.Workspace);
        _memoryReg = context.Memory?.Register(_index);
    }

    public FileIndex Index => _index;

    /// <summary>Opens the palette pre-filled with a mode prefix ("" = files, "&gt;" commands, ":" line, "@" symbols, "#" workspace symbols).</summary>
    public async Task ShowAsync(string prefix)
    {
        var picked = await await _ui.InvokeAsync(() => _quick.PickAsync(new PaletteSource(_ctx, _index), null, null, prefix)).ConfigureAwait(true);
        if (picked?.Accept is not { } accept) return;
        try { await accept().ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_window is not null) await _window.ShowMessageAsync(MessageSeverity.Error, ex.Message).ConfigureAwait(true);
        }
    }

    /// <summary>A fixed-list pick whose highlight changes are reported (live theme preview).</summary>
    public async Task<QuickPickItem?> PickWithPreviewAsync(IReadOnlyList<QuickPickItem> items, string title, string placeholder, Action<QuickPickEntry?> onActive)
    {
        using var sub = _quick.ActiveEntryChanged.Subscribe(onActive);
        var entry = await await _ui.InvokeAsync(() => _quick.PickAsync(new StaticQuickPickSource(items, new QuickPickOptions(Title: title, Placeholder: placeholder)), title, placeholder)).ConfigureAwait(true);
        return entry?.Tag as QuickPickItem;
    }

    public void Dispose()
    {
        _memoryReg?.Dispose();
        _index.Dispose();
    }
}
