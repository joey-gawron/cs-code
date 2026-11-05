using Avalonia.Input;
using Avalonia.Interactivity;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// Offers a plain left double-click in the text to the document's <see cref="IDoubleClickHandler"/>s before AvaloniaEdit selects the word there
/// (the search editor opens the result under the pointer, <c>search.searchEditor.doubleClickBehaviour</c>). A handler that takes it gets the caret
/// put at the click and no selection; otherwise the editor selects the word as always. Providers are looked up only on a double-click.
/// </summary>
internal sealed class DoubleClickFeature : IDisposable
{
    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private bool _disposed;

    public DoubleClickFeature(CodeEditorControl view)
    {
        _v = view;
        view.Editor.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _d.Add(Disposable.Create(() => view.Editor.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed)));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed || e.ClickCount != 2 || e.KeyModifiers != KeyModifiers.None) return;
        if (!e.GetCurrentPoint(_v.Editor).Properties.IsLeftButtonPressed) return;
        var handlers = _v.Services.Features.GetProviders<IDoubleClickHandler>(_v.Document);
        if (handlers.Count == 0) return;
        if (_v.Editor.GetPositionFromPoint(e.GetPosition(_v.Editor)) is not { } p) return;
        var doc = _v.Document;
        if (p.Line < 1 || p.Line > doc.LineCount) return;
        var line = p.Line - 1;
        var position = new TextPosition(line, Math.Clamp(p.Column - 1, 0, doc.GetLineText(line).Length));
        foreach (var handler in handlers)
        {
            bool taken;
            try { taken = handler.OnDoubleClick(doc, position); }
            catch (Exception ex)
            {
                _v.Services.LoggerFactory.CreateLogger<DoubleClickFeature>().LogDebug(ex, "Double-click handler failed");
                continue;
            }
            if (!taken) continue;
            e.Handled = true;
            _v.Controller.SetSelection(new TextRange(position, position), reveal: false);
            return;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d.Dispose();
    }
}
