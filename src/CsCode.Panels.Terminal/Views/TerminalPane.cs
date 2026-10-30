using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using DockSide = Avalonia.Controls.Dock;
using Orientation = Avalonia.Layout.Orientation;

namespace CsCode.Panels.Terminal.Views;

/// <summary>
/// One pane of the Terminal window: a terminal's <see cref="TerminalControl"/> with its own scrollbar. A group that is not split shows one
/// pane; a split group shows its terminals' panes side by side (VS Code's horizontal split in the bottom panel).
/// </summary>
public sealed class TerminalPane : DockPanel, IDisposable
{
    private readonly ScrollBar _scroll;
    private bool _updatingScroll;

    internal TerminalPane(TerminalPanelRuntime runtime, TerminalSession? session)
    {
        Session = session;
        Control = new TerminalControl(runtime);
        _scroll = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 0, ViewportSize = 1, AllowAutoHide = false };
        _scroll.Scroll += (_, e) =>
        {
            if (_updatingScroll || Control.Snapshot is not { } snap) return;
            Control.ScrollTo(snap.ScrollbackTop + (long)Math.Round(e.NewValue));
        };
        SetDock(_scroll, DockSide.Right);
        Children.Add(_scroll);
        Children.Add(Control);
        Control.FrameDrawn += OnFrameDrawn;
        Control.Session = session;
    }

    /// <summary>The terminal this pane shows (null: the empty pane of a window without terminals).</summary>
    public TerminalSession? Session { get; }

    public TerminalControl Control { get; }

    internal ScrollBar ScrollBar => _scroll;

    /// <summary>What the view subscribed to for this pane's terminal (disposed with the pane).</summary>
    internal IDisposable? Subscription { get; set; }

    private void OnFrameDrawn(TerminalSnapshot snap)
    {
        _updatingScroll = true;
        try
        {
            var lines = snap.ScreenTop - snap.ScrollbackTop;
            _scroll.Maximum = Math.Max(0, lines);
            _scroll.ViewportSize = snap.RowCount;
            _scroll.Value = Math.Clamp(snap.ViewportTop - snap.ScrollbackTop, 0, _scroll.Maximum);
            _scroll.IsEnabled = lines > 0;
        }
        finally { _updatingScroll = false; }
    }

    public void Dispose()
    {
        Subscription?.Dispose();
        Subscription = null;
        Control.FrameDrawn -= OnFrameDrawn;
        Control.Dispose();
    }
}
