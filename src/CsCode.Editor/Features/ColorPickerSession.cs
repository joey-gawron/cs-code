using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// One opening of the colour picker over a colour of the document (or, from the command with no colour at the caret, over the selection:
/// VS Code's standalone picker). Each colour the user picks asks the colour's provider for its presentations
/// (<see cref="IDocumentColorProvider.ProvideColorPresentationsAsync"/>, off the UI thread, latest wins: while one request is out, newer
/// colours only replace what is asked next) and applies the chosen presentation's edit at once. All the edits of a session are ONE undo step
/// (the first opens a step, the others continue it; a save in between starts a new one, which is counted); Escape undoes them and closes,
/// Enter or a click elsewhere keeps them. An edit from anywhere else (typing in another view, a language server, undo) ends the session,
/// keeping what it applied: the colour's range would no longer be known.
/// </summary>
internal sealed class ColorPickerSession : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(300);

    private readonly ColorDecoratorFeature _owner;
    private readonly CodeEditorControl _v;
    private readonly IDocumentColorProvider _provider;
    private readonly ColorPickerWidget _widget;
    private readonly HsvaColor _original;
    private readonly ColorInformation _originalInfo;
    private readonly int _originalStart;
    private readonly string _originalText;
    private readonly DisposableStore _d = new();
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<ColorPresentation> _presentations = [];
    private HsvaColor _color;
    private int _start, _end; // the colour's text now (document offsets)
    private int _index = -1; // the chosen presentation; -1 until the first answer guessed it from the text
    private int _generation; // bumped whenever something new is wanted (a colour, a presentation)
    private bool _applyWanted; // false only while the opening request (labels, no edit) is the last thing asked
    private bool _running, _applying, _closed;
    private int _steps; // undo steps the session's edits made
    private CancellationTokenSource? _hoverClose;

    public ColorPickerSession(ColorDecoratorFeature owner, CodeEditorControl view, IDocumentColorProvider provider, ColorInformation color, int start, int end, bool hover)
    {
        _owner = owner;
        _v = view;
        _provider = provider;
        _start = start;
        _end = end;
        _original = HsvaColor.FromRgba(color.Red, color.Green, color.Blue, color.Alpha);
        _originalInfo = color;
        _originalStart = start;
        _color = _original;
        _originalText = end > start ? view.Document.Buffer.GetText(start, end - start) : "";
        IsHoverOpened = hover;
        _widget = new ColorPickerWidget(view, _original);
        _widget.SetLabel(_originalText);
        _widget.ColorPicked += c => SetColor(c);
        _widget.PresentationClicked += CyclePresentation;
        _widget.OriginalClicked += () => SetColor(_original);
        _widget.KeyDown += OnWidgetKeyDown;
        _widget.AddHandler(InputElement.PointerPressedEvent, OnWidgetPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _widget.PointerEntered += OnWidgetEntered;
        _widget.PointerExited += OnWidgetExited;
        view.WidgetLayer.Children.Add(_widget);
        view.TextView.ScrollOffsetChanged += OnViewMoved;
        view.TextView.VisualLinesChanged += OnViewMoved;
        _d.Add(Disposable.Create(() =>
        {
            view.TextView.ScrollOffsetChanged -= OnViewMoved;
            view.TextView.VisualLinesChanged -= OnViewMoved;
            _widget.KeyDown -= OnWidgetKeyDown;
            _widget.RemoveHandler(InputElement.PointerPressedEvent, OnWidgetPressed);
            _widget.PointerEntered -= OnWidgetEntered;
            _widget.PointerExited -= OnWidgetExited;
        }));
        _d.Add(view.Document.Changed.Subscribe(_ =>
        {
            if (!_applying) Close(); // someone else edited: keep what was applied, the range is no longer ours to know
        }));
        Place();
        Want(apply: false); // the labels for the colour as it is, and which presentation matches its text
    }

    public bool IsOpen => !_closed;
    /// <summary>Opened by resting the pointer on a swatch and not used yet: it closes when the pointer leaves it.</summary>
    public bool IsHoverOpened { get; private set; }
    public HsvaColor Color => _color;
    public string Label => _widget.Label;
    public (int Start, int End) Range => (_start, _end);
    public IReadOnlyList<ColorPresentation> Presentations => _presentations;
    public int PresentationIndex => _index;
    internal ColorPickerWidget Widget => _widget;
    /// <summary>Completes when the presentations asked so far have been answered and applied (tests).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public void Focus()
    {
        IsHoverOpened = false;
        _hoverClose?.Cancel();
        _widget.Focus();
    }

    /// <summary>The user picked <paramref name="color"/> (the square, a strip, the original): its presentation is asked for and applied.</summary>
    public void SetColor(HsvaColor color)
    {
        if (_closed) return;
        Used();
        _color = color;
        _widget.SetColor(color);
        Want(apply: true);
    }

    /// <summary>The next presentation (rgb → hsl → hex …), applied at once (a click on the header's text).</summary>
    public void CyclePresentation()
    {
        if (_closed || _presentations.Count == 0) return;
        Used();
        _index = (_index + 1) % _presentations.Count;
        _widget.SetLabel(_presentations[_index].Label);
        Want(apply: true);
    }

    /// <summary>Escape: the session's edits are undone and the picker closes.</summary>
    public void Revert()
    {
        if (_closed) return;
        if (_steps > 0)
        {
            _cts.Cancel(); // no answer in flight may apply after this
            var doc = _v.Document;
            _applying = true; // our own changes: they do not end the session as someone else's would
            try
            {
                for (var i = 0; i < _steps && doc.CanUndo; i++) doc.Undo();
            }
            finally
            {
                _applying = false;
            }
            _steps = 0;
            _owner.OnPicked(this, _originalStart, _originalStart + _originalText.Length, _originalInfo); // the swatch shows the original again
        }
        Close();
    }

    /// <summary>Enter or a click elsewhere: the picker closes, what it applied stays.</summary>
    public void Commit() => Close();

    private void Used()
    {
        IsHoverOpened = false;
        _hoverClose?.Cancel();
    }

    private void Want(bool apply)
    {
        _generation++;
        _applyWanted |= apply;
        if (!_running) Pending = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            var served = -1;
            while (!_closed && served != _generation)
            {
                served = _generation;
                var doc = _v.Document;
                var version = doc.Version;
                var (r, g, b) = _color.ToRgb();
                var info = new ColorInformation(new TextRange(doc.PositionAt(_start), doc.PositionAt(_end)), r, g, b, _color.A);
                var list = await FeatureSupport.CallAsync(_v, c => _provider.ProvideColorPresentationsAsync(doc, info, c), _cts.Token, Deadline).ConfigureAwait(true);
                if (_closed) return;
                if (served != _generation) continue; // something newer is wanted: ask for that instead (latest wins)
                if (doc.Version != version)
                {
                    served = -1; // the answer is for older text: never applied, asked again
                    continue;
                }
                if (list is not { Count: > 0 })
                {
                    _presentations = [];
                    continue; // nothing to write it with: the picker shows the colour without editing (and says so in the header)
                }
                _presentations = list;
                if (_index < 0) _index = ColorFormats.Guess([.. list.Select(p => p.Label)], _originalText);
                _index = Math.Clamp(_index, 0, list.Count - 1);
                _widget.SetLabel(list[_index].Label);
                if (_applyWanted) Apply(list[_index], info);
            }
        }
        finally
        {
            _running = false;
        }
    }

    private void Apply(ColorPresentation presentation, ColorInformation info)
    {
        var doc = _v.Document;
        if (doc.IsReadOnly) return;
        var range = info.Range;
        var main = presentation.TextEdit ?? new TextEdit(range, presentation.Label);
        var more = presentation.AdditionalTextEdits ?? [];
        if (more.Count == 0 && doc.GetText(main.Range) == main.NewText) return; // already written so: no empty undo step
        var mainStart = doc.OffsetAt(main.Range.Start);
        var shift = 0; // additional edits before the colour move it
        foreach (var e in more)
        {
            var s = doc.OffsetAt(e.Range.Start);
            var en = doc.OffsetAt(e.Range.End);
            if (en <= mainStart) shift += e.NewText.Length - (en - s);
        }
        List<TextEdit> edits = [main, .. more];
        _applying = true;
        bool ok, continued;
        try
        {
            continued = doc.BeginUndoGroup(continueLast: _steps > 0);
            try
            {
                ok = doc.ApplyEdits(edits);
            }
            finally
            {
                doc.EndUndoGroup();
            }
        }
        finally
        {
            _applying = false;
        }
        if (!ok) return; // overlapping edits from the provider: nothing applied
        if (!continued) _steps++;
        _start = mainStart + shift;
        _end = _start + main.NewText.Length;
        _owner.OnPicked(this, _start, _end, info with { Range = new TextRange(doc.PositionAt(_start), doc.PositionAt(_end)) });
        Place();
    }

    // ---- placement and input ----------------------------------------------------------------------------------------

    private void OnViewMoved(object? sender, EventArgs e) => Place();

    /// <summary>Under the colour's line (above it when there is no room below), like the hover.</summary>
    private void Place()
    {
        if (_closed) return;
        var at = _v.Document.PositionAt(Math.Min(_start, _v.Document.Buffer.TextLength));
        var (top, bottom) = OverlayPopup.AnchorFor(_v, at);
        _widget.Measure(Size.Infinity);
        var size = _widget.DesiredSize;
        var area = _v.WidgetLayer.Bounds.Size is { Width: > 0 } s ? s : _v.Bounds.Size;
        var x = Math.Clamp(bottom.X, 0, Math.Max(0, area.Width - size.Width - 2));
        var y = bottom.Y + 2;
        if (y + size.Height > area.Height && top.Y - size.Height - 2 >= 0) y = top.Y - size.Height - 2;
        Canvas.SetLeft(_widget, x);
        Canvas.SetTop(_widget, y);
    }

    private void OnWidgetKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Revert();
                _v.TextArea.Focus();
                break;
            case Key.Enter:
                e.Handled = true;
                Commit();
                _v.TextArea.Focus();
                break;
        }
    }

    private void OnWidgetPressed(object? sender, PointerPressedEventArgs e)
    {
        Used();
        if (!_widget.IsKeyboardFocusWithin) _widget.Focus(); // Escape and Enter reach it from now on
    }

    private void OnWidgetEntered(object? sender, PointerEventArgs e) => _hoverClose?.Cancel();

    private void OnWidgetExited(object? sender, PointerEventArgs e) => PointerLeft();

    /// <summary>The pointer left the swatch or the picker: a picker the hover opened closes unless the pointer comes back soon.</summary>
    public void PointerLeft()
    {
        if (_closed || !IsHoverOpened) return;
        _hoverClose?.Cancel();
        var cts = _hoverClose = new CancellationTokenSource();
        _ = Task.Delay(HoverGrace, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested && IsHoverOpened && !_widget.IsPointerOver) Close();
            });
        }, TaskScheduler.Default);
    }

    public void PointerBack() => _hoverClose?.Cancel();

    public void ApplyTheme() => _widget.ApplyTheme(_v.Palette);

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        var hadFocus = _widget.IsKeyboardFocusWithin;
        _cts.Cancel();
        _hoverClose?.Cancel();
        _d.Dispose();
        _v.WidgetLayer.Children.Remove(_widget);
        _widget.Detach();
        _owner.OnSessionClosed(this);
        if (hadFocus && !_v.IsDisposed) _v.TextArea.Focus(); // the keyboard goes back to the text
    }

    public void Dispose() => Close();
}
