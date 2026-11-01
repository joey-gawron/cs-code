using System.Reflection;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;

namespace CsCode.Editor.Features;

/// <summary>
/// Keeps an edit inside an expanded fold from rebuilding every visible line. AvaloniaEdit 12's <c>FoldingSection</c> redraws its whole range
/// whenever an edit changes its length (<c>OnSegmentChanged</c> → <c>FoldingManager.Redraw(section)</c>), folded or not; in code nearly every
/// keystroke is inside a namespace, class or method region, so every keystroke threw away all ~60 visual lines of a full-height editor and the
/// next layout shaped them again (text formatting, ligatures, colouring): 60 lines built per key instead of 1–2. An expanded fold contributes
/// nothing to a visual line (the folding element generator only acts on folded sections; the margin redraws itself from the visual lines), so
/// that redraw is wasted.
///
/// While a change is applied the filter hides the manager's text views (its internal <c>TextViews</c> list, the only thing
/// <c>Redraw(section)</c> walks), unless a FOLDED section overlaps the edited range up to the end of its line — the range AvaloniaEdit
/// revalidates, where the collapsed lines and the redraw matter — and puts them back as soon as the document's <c>Changed</c> event arrives
/// (subscribed after the manager's own handler), at the end of the update, at the next change, and on dispose. Reflection against 12.0.0:
/// when the member is missing the filter does nothing and AvaloniaEdit behaves as before.
/// </summary>
internal sealed class FoldRedrawFilter : IDisposable
{
    private static readonly PropertyInfo? TextViewsProperty =
        typeof(FoldingManager).GetProperty("TextViews", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly FoldingManager _manager;
    private readonly TextDocument _document;
    private readonly List<TextView>? _views;
    private readonly List<TextView> _hidden = new();
    private bool _disposed;

    public FoldRedrawFilter(FoldingManager manager, TextDocument document)
    {
        _manager = manager;
        _document = document;
        _views = TextViewsProperty?.GetValue(manager) as List<TextView>;
        if (_views is null) return;
        // after FoldingManager's constructor: its (weak) Changed handler was attached first, so ours runs after it
        document.Changing += OnChanging;
        document.Changed += OnChanged;
        document.UpdateFinished += OnUpdateFinished;
    }

    /// <summary>False when the AvaloniaEdit layout differs and the filter is inert (tests).</summary>
    internal bool IsActive => _views is not null;

    /// <summary>How many changes were applied with the redraw suppressed (tests, diagnostics).</summary>
    internal int Suppressed { get; private set; }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        Restore(); // a change inside one update: decide afresh for each
        if (_disposed || _views!.Count == 0 || AnyFoldedNear(e.Offset, e.Offset + e.RemovalLength)) return;
        _hidden.AddRange(_views);
        _views.Clear();
        Suppressed++;
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e) => Restore();

    private void OnUpdateFinished(object? sender, EventArgs e) => Restore();

    private void Restore()
    {
        if (_hidden.Count == 0) return;
        _views!.AddRange(_hidden);
        _hidden.Clear();
    }

    /// <summary>A folded section overlapping [from, end of the line holding <paramref name="to"/>] (before the change is applied).</summary>
    private bool AnyFoldedNear(int from, int to)
    {
        var lineEnd = _document.GetLineByOffset(Math.Min(to, _document.TextLength)).EndOffset;
        foreach (var f in _manager.GetFoldingsContaining(from))
            if (f.IsFolded) return true;
        // sections starting inside the range: few (one line's worth), walked start by start
        for (var f = _manager.GetNextFolding(from); f is not null && f.StartOffset <= lineEnd; f = _manager.GetNextFolding(f.StartOffset + 1))
        {
            foreach (var same in _manager.GetFoldingsAt(f.StartOffset))
                if (same.IsFolded) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
        if (_views is null) return;
        _document.Changing -= OnChanging;
        _document.Changed -= OnChanged;
        _document.UpdateFinished -= OnUpdateFinished;
    }
}
