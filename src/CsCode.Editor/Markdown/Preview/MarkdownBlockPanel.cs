using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>
/// The preview's block list, virtualised: a vertical stack of the document's top-level blocks in which only the blocks near the viewport (one
/// viewport above and below it) have controls. Every other block takes its last measured height, or an estimate from its source lines until it
/// has been shown once, so the scroll extent is right without building a 10,000-block document. A block that leaves the window's reach loses its
/// controls (memory stays proportional to what is on screen, docs/MEMORY.md).
/// <para>
/// Blocks are identified by <see cref="PreviewBlockKey"/>: after an edit, <see cref="SetBlocks"/> keeps the controls of every block whose key is
/// unchanged, so typing in one paragraph rebuilds that paragraph only. When a block above the viewport turns out taller or shorter than its
/// estimate, <see cref="AnchorShift"/> reports the difference so the scroll viewer keeps the content under the viewport's top where it was.
/// </para>
/// Positions map to source lines both ways (<see cref="OffsetOfLine"/>, <see cref="LineAtOffset"/>): the scroll synchronisation with the editor.
/// UI thread only.
/// </summary>
public sealed class MarkdownBlockPanel : Panel
{
    private sealed class Slot(PreviewBlock block)
    {
        public PreviewBlock Block = block;
        public Control? Control;
        public double? Measured;
        public double Top;
        public double Height => Measured ?? Estimate;
        public double Estimate;
    }

    private readonly List<Slot> _slots = new();
    private Func<PreviewBlock, Control>? _factory;
    private Rect _viewport;
    private double _width = double.NaN;
    private double _spacing = 12;
    private double _lineHeight = 22;
    private bool _realizePending;

    public MarkdownBlockPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            ScheduleRealize();
        };
    }

    /// <summary>Raised during layout when blocks above the viewport changed height by this many pixels (positive: content moved down).</summary>
    public event Action<double>? AnchorShift;

    /// <summary>Number of blocks that currently have controls (tests and the memory budget).</summary>
    public int RealizedCount => _slots.Count(s => s.Control is not null);

    public int BlockCount => _slots.Count;

    /// <summary>The controls of block <paramref name="index"/>, or null when it is not realised.</summary>
    public Control? RealizedControl(int index) => index >= 0 && index < _slots.Count ? _slots[index].Control : null;

    /// <summary>Space below every block, and the height of one source line in estimates.</summary>
    public void SetMetrics(double spacing, double lineHeight)
    {
        _spacing = spacing;
        _lineHeight = lineHeight;
        foreach (var s in _slots) s.Estimate = EstimateOf(s.Block);
        InvalidateMeasure();
    }

    /// <summary>
    /// Shows <paramref name="blocks"/>. With <paramref name="reuse"/> the controls (and known heights) of blocks whose key is unchanged are kept;
    /// without it (a theme or font change) everything is rebuilt.
    /// </summary>
    public void SetBlocks(IReadOnlyList<PreviewBlock> blocks, Func<PreviewBlock, Control> factory, bool reuse)
    {
        _factory = factory;
        var old = new Dictionary<PreviewBlockKey, Queue<Slot>>();
        foreach (var s in _slots)
        {
            if (!reuse) { Drop(s); continue; }
            if (!old.TryGetValue(s.Block.Key, out var q)) old[s.Block.Key] = q = new Queue<Slot>();
            q.Enqueue(s);
        }
        _slots.Clear();
        foreach (var b in blocks)
        {
            if (reuse && old.TryGetValue(b.Key, out var q) && q.Count > 0)
            {
                var kept = q.Dequeue();
                kept.Block = b;
                kept.Estimate = EstimateOf(b);
                _slots.Add(kept);
            }
            else _slots.Add(new Slot(b) { Estimate = EstimateOf(b) });
        }
        foreach (var q in old.Values)
            while (q.Count > 0) Drop(q.Dequeue());
        ComputeTops();
        Realize();
        InvalidateMeasure();
    }

    /// <summary>Drops every control (the view is going away).</summary>
    public void Clear()
    {
        foreach (var s in _slots) Drop(s);
        _slots.Clear();
        _factory = null;
        InvalidateMeasure();
    }

    private void Drop(Slot s)
    {
        if (s.Control is null) return;
        Children.Remove(s.Control);
        s.Control = null;
    }

    private double EstimateOf(PreviewBlock b)
    {
        var lines = Math.Max(1, b.LineCount);
        return b.Block switch
        {
            Markdig.Syntax.HeadingBlock h => _lineHeight * (h.Level <= 2 ? 2.0 : 1.4),
            Markdig.Syntax.ThematicBreakBlock => _lineHeight * 0.6,
            Markdig.Syntax.CodeBlock => lines * _lineHeight * 0.95 + 20,
            _ => lines * _lineHeight,
        };
    }

    // ---------------------------------------------------------------- positions and lines

    /// <summary>Top of block <paramref name="index"/> in panel coordinates.</summary>
    public double TopOf(int index) => index >= 0 && index < _slots.Count ? _slots[index].Top : 0;

    /// <summary>The vertical offset that shows source line <paramref name="line"/> (0-based, fractional) at the top: interpolated inside its block.</summary>
    public double OffsetOfLine(double line)
    {
        if (_slots.Count == 0) return 0;
        var i = IndexAtLine(line);
        var s = _slots[i];
        if (line < s.Block.StartLine) return s.Top;
        var within = Math.Clamp((line - s.Block.StartLine) / (s.Block.LineCount), 0, 1);
        // Between two blocks (blank lines): move through the gap below this block instead of jumping.
        if (line > s.Block.EndLine + 1 && i + 1 < _slots.Count)
        {
            var next = _slots[i + 1];
            var gap = next.Block.StartLine - (s.Block.EndLine + 1);
            var t = gap <= 0 ? 1 : Math.Clamp((line - s.Block.EndLine - 1) / gap, 0, 1);
            return s.Top + s.Height + t * (next.Top - s.Top - s.Height);
        }
        return s.Top + within * s.Height;
    }

    /// <summary>The source line (0-based, fractional) shown at vertical offset <paramref name="y"/>.</summary>
    public double LineAtOffset(double y)
    {
        if (_slots.Count == 0) return 0;
        int lo = 0, hi = _slots.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_slots[mid].Top <= y) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var s = _slots[found];
        if (y <= s.Top + s.Height || found + 1 >= _slots.Count)
            return s.Block.StartLine + Math.Clamp((y - s.Top) / Math.Max(1, s.Height), 0, 1) * s.Block.LineCount;
        var next = _slots[found + 1];
        var gapPixels = Math.Max(1, next.Top - s.Top - s.Height);
        var gapLines = next.Block.StartLine - (s.Block.EndLine + 1);
        return s.Block.EndLine + 1 + Math.Clamp((y - s.Top - s.Height) / gapPixels, 0, 1) * Math.Max(0, gapLines);
    }

    private int IndexAtLine(double line)
    {
        int lo = 0, hi = _slots.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_slots[mid].Block.StartLine <= line) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // ---------------------------------------------------------------- realisation

    private void ScheduleRealize()
    {
        if (_realizePending) return;
        _realizePending = true;
        // In the next layout pass: the viewport event arrives during arrange, when children must not change.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _realizePending = false;
            if (Realize()) InvalidateMeasure();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    /// <summary>Creates the controls of the blocks near the viewport and drops the far ones. True when anything changed.</summary>
    private bool Realize()
    {
        if (_factory is null || _slots.Count == 0) return false;
        var vp = _viewport;
        if (vp.Height <= 0 || double.IsInfinity(vp.Height)) vp = new Rect(0, Math.Max(0, vp.Y), Math.Max(1, vp.Width), 1000); // not in a window yet
        var keepTop = vp.Y - vp.Height;
        var keepBottom = vp.Bottom + vp.Height;
        var changed = false;
        foreach (var s in _slots)
        {
            var near = s.Top + s.Height >= keepTop && s.Top <= keepBottom;
            if (near && s.Control is null)
            {
                s.Control = _factory(s.Block);
                Children.Add(s.Control);
                changed = true;
            }
            else if (!near && s.Control is not null && (s.Top + s.Height < keepTop - vp.Height || s.Top > keepBottom + vp.Height))
            {
                // dropped only well outside the kept band, so a small scroll back and forth does not rebuild blocks
                Drop(s);
                changed = true;
            }
        }
        return changed;
    }

    private void ComputeTops()
    {
        var y = 0.0;
        foreach (var s in _slots)
        {
            s.Top = y;
            y += s.Height + _spacing;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        if (!double.IsNaN(_width) && Math.Abs(width - _width) > 0.5)
        {
            // Text wraps differently at another width: heights of blocks without controls fall back to estimates.
            foreach (var s in _slots)
                if (s.Control is null) s.Measured = null;
        }
        _width = width;

        // The block at the viewport's top stays put: blocks above it that change height shift the content, which the scroll viewer undoes.
        var anchor = -1;
        var anchorTop = 0.0;
        for (var i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Top + _slots[i].Height + _spacing > _viewport.Y)
            {
                anchor = i;
                anchorTop = _slots[i].Top;
                break;
            }
        }

        foreach (var s in _slots)
        {
            if (s.Control is null) continue;
            s.Control.Measure(new Size(width, double.PositiveInfinity));
            s.Measured = s.Control.DesiredSize.Height;
        }
        ComputeTops();
        if (anchor > 0 && _viewport.Y > 0)
        {
            var shift = _slots[anchor].Top - anchorTop;
            if (Math.Abs(shift) > 0.5) AnchorShift?.Invoke(shift);
        }
        var total = _slots.Count == 0 ? 0 : _slots[^1].Top + _slots[^1].Height;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var s in _slots)
            s.Control?.Arrange(new Rect(0, s.Top, finalSize.Width, s.Height));
        return finalSize;
    }
}
