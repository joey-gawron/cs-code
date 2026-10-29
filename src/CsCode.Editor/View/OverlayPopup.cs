using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Text;

namespace CsCode.Editor.View;

/// <summary>
/// A popup (completion list, hover, signature help, lightbulb menu) hosted INSIDE the editor control's overlay canvas rather than in a native
/// popup window: it is clipped to the editor like VS Code's widgets, follows the control while it moves, can never outlive it, and is part of the
/// rendered frame (headless tests and screenshots see it). Positioned relative to a caret or pointer anchor; flips above when there is no room below.
/// </summary>
internal sealed class OverlayPopup : IDisposable
{
    private readonly CodeEditorControl _view;
    private readonly Border _root;
    private bool _disposed;

    public OverlayPopup(CodeEditorControl view, double maxWidth, double maxHeight)
    {
        _view = view;
        _root = new Border
        {
            IsVisible = false,
            IsHitTestVisible = true,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 8, Color = Color.FromArgb(0x60, 0, 0, 0) }),
            ClipToBounds = true,
        };
        ApplyTheme();
        view.Overlay.Children.Add(_root);
    }

    public bool IsOpen => _root.IsVisible && !_disposed;
    public Rect Bounds => new(Canvas.GetLeft(_root), Canvas.GetTop(_root), _root.Bounds.Width, _root.Bounds.Height);
    public Control Root => _root;

    public void ApplyTheme()
    {
        var p = _view.Palette;
        _root.Background = p.WidgetBackground;
        _root.BorderBrush = p.WidgetBorder;
        Avalonia.Controls.Documents.TextElement.SetForeground(_root, p.WidgetForeground);
    }

    /// <summary>Shows <paramref name="content"/> at a text position: below the line when it fits, otherwise above.</summary>
    public void ShowAt(Control content, TextPosition position)
    {
        var anchor = AnchorFor(position);
        Show(content, anchor.Bottom, anchor.Top);
    }

    /// <summary>Shows at explicit editor-relative coordinates: <paramref name="below"/> is the top-left to use under the anchor, <paramref name="above"/> the bottom-left over it.</summary>
    public void Show(Control content, Point below, Point above)
    {
        if (_disposed) return;
        _root.Child = content;
        var overlay = _view.Overlay.Bounds.Size;
        _root.Measure(new Size(Math.Max(1, overlay.Width - 8), Math.Max(1, overlay.Height - 8)));
        var size = _root.DesiredSize;
        var x = Math.Clamp(below.X, 0, Math.Max(0, overlay.Width - size.Width - 2));
        var y = below.Y;
        if (y + size.Height > overlay.Height && above.Y - size.Height >= 0) y = above.Y - size.Height;
        y = Math.Clamp(y, 0, Math.Max(0, overlay.Height - size.Height));
        Canvas.SetLeft(_root, x);
        Canvas.SetTop(_root, y);
        _root.IsVisible = true;
    }

    public void Hide()
    {
        if (_disposed || !_root.IsVisible) return;
        _root.IsVisible = false;
        _root.Child = null; // drop the content (and anything it references) while hidden
    }

    /// <summary>The editor-relative rectangle of the line containing <paramref name="position"/>'s character: bottom-left and top-left anchors.</summary>
    public (Point Top, Point Bottom) AnchorFor(TextPosition position)
    {
        var textView = _view.TextView;
        var tvp = new AvaloniaEdit.TextViewPosition(position.Line + 1, position.Character + 1);
        var top = textView.GetVisualPosition(tvp, VisualYPosition.LineTop) - textView.ScrollOffset;
        var bottom = textView.GetVisualPosition(tvp, VisualYPosition.LineBottom) - textView.ScrollOffset;
        var t = textView.TranslatePoint(top, _view.Overlay) ?? top;
        var b = textView.TranslatePoint(bottom, _view.Overlay) ?? bottom;
        return (t, b);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Overlay.Children.Remove(_root);
        _root.Child = null;
    }
}
