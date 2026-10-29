using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Snippets;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The snippet session of one view: inserts a resolved snippet as ONE undo unit, anchors every tab stop in the buffer (so they follow edits),
/// walks them with Tab / Shift+Tab, keeps mirrors in sync while the user types into the primary placeholder (applying <c>${1/…/…/}</c>
/// transforms), highlights the active stop, and ends with Esc, after the final stop, or when the caret leaves the snippet.
/// </summary>
internal sealed class SnippetFeature : ISnippetFeature, IThemeAware, IDisposable
{
    private sealed class Stop(int index, IReadOnlyList<Occurrence> occurrences, IReadOnlyList<string>? choices)
    {
        public int Index { get; } = index;
        public IReadOnlyList<Occurrence> Occurrences { get; } = occurrences;
        public IReadOnlyList<string>? Choices { get; } = choices;
        public Occurrence Primary => Occurrences.First(o => o.IsPrimary);
    }

    private sealed record Occurrence(TextAnchor Start, TextAnchor End, bool IsPrimary, SnippetTransform? Transform);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly SnippetRenderer _renderer;
    private List<Stop>? _stops;
    private TextAnchor? _spanStart, _spanEnd;
    private int _current;
    private bool _updatingMirrors;
    private bool _disposed;

    public SnippetFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new SnippetRenderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _d.Add(view.Document.Changed.Subscribe(_ => OnDocumentChanged()));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => OnCaretChanged()));
        _d.Add(Disposable.Create(() => view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown)));
    }

    public bool IsActive => _stops is not null;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Selection);

    // ---- insertion ----

    public void Insert(string snippetBody) => _ = InsertAsync(snippetBody, null);

    /// <summary>Replaces <paramref name="replace"/> (default: the selection / caret) with the snippet and starts the tab-stop session.</summary>
    internal async Task InsertAsync(string body, TextRange? replace)
    {
        if (_v.Document.IsReadOnly || _disposed) return;
        var doc = _v.Document;
        var range = replace ?? _v.Controller.Selections[0];
        var clipboard = body.Contains("CLIPBOARD", StringComparison.Ordinal) ? await ReadClipboardAsync().ConfigureAwait(true) : null;
        if (_disposed) return;

        var a = doc.OffsetAt(range.Start);
        var b = doc.OffsetAt(range.End);
        var line = doc.PositionAt(a).Line;
        var lineText = doc.GetLineText(line);
        var baseIndent = lineText[..Math.Min(lineText.Length, LeadingWhitespace(lineText))];
        var languageConfig = _v.LanguageConfiguration.Base;
        var workspace = _v.Services.Provider?.GetService(typeof(IWorkspaceService)) as IWorkspaceService;
        var ctx = new SnippetVariableContext
        {
            FilePath = doc.IsUntitled ? doc.Uri.AbsolutePath : doc.Uri.LocalPath,
            WorkspaceName = workspace?.Name,
            WorkspaceFolder = workspace?.PrimaryFolder?.LocalPath,
            SelectedText = doc.GetText(range),
            CurrentLine = lineText,
            LineIndex = line,
            CurrentWord = WordAt(lineText, doc.PositionAt(a).Character),
            Clipboard = () => clipboard,
            LineComment = languageConfig.LineComment,
            BlockCommentStart = languageConfig.BlockComment?.Open,
            BlockCommentEnd = languageConfig.BlockComment?.Close,
        };
        var indentation = new SnippetIndentation(baseIndent, _v.Settings.TabSize, _v.Settings.InsertSpaces, doc.EndOfLine);
        var resolved = SnippetResolver.Resolve(body, ctx, indentation);

        End(); // a new snippet replaces an active one
        if (!doc.ApplyEdits([new TextEdit(range, resolved.Text)])) return;
        _v.Editor.CaretOffset = a + resolved.Text.Length;
        if (!resolved.HasTabstops) return;

        var buffer = doc.Buffer;
        _spanStart = buffer.CreateAnchor(a);
        _spanStart.MovementType = AnchorMovementType.BeforeInsertion;
        _spanEnd = buffer.CreateAnchor(a + resolved.Text.Length);
        _spanEnd.MovementType = AnchorMovementType.AfterInsertion;
        _stops = new List<Stop>(resolved.Groups.Count);
        foreach (var g in resolved.Groups)
        {
            var occurrences = g.Ranges.Select(r =>
            {
                var s = buffer.CreateAnchor(a + r.Start);
                s.MovementType = AnchorMovementType.BeforeInsertion;
                var e = buffer.CreateAnchor(a + r.Start + r.Length);
                e.MovementType = AnchorMovementType.AfterInsertion;
                return new Occurrence(s, e, r.IsPrimary, r.Transform);
            }).ToList();
            _stops.Add(new Stop(g.Index, occurrences, g.Choices));
        }
        _current = 0;
        Activate();
    }

    // ---- navigation ----

    private void Activate()
    {
        if (_stops is null) return;
        var stop = _stops[_current];
        var p = stop.Primary;
        var area = _v.TextArea;
        if (p.Start.Offset == p.End.Offset)
        {
            area.ClearSelection();
            _v.Editor.CaretOffset = p.End.Offset;
        }
        else
        {
            area.Selection = Selection.Create(area, p.Start.Offset, p.End.Offset);
            _v.Editor.CaretOffset = p.End.Offset;
        }
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
        if (stop.Choices is { Count: > 1 } choices) _ = OfferChoicesAsync(stop, choices);
        // the final stop ($0) ends the session
        if (stop.Index == 0) End();
    }

    private async Task OfferChoicesAsync(Stop stop, IReadOnlyList<string> choices)
    {
        var window = _v.Services.Window;
        if (window is null) return;
        var picked = await window.ShowQuickPickAsync(choices.Select(c => new QuickPickItem(c)).ToList(), new QuickPickOptions(Title: "Choose a value", Placeholder: "")).ConfigureAwait(true);
        if (picked is null || _stops is null || _disposed) return;
        var p = stop.Primary;
        if (p.Start.IsDeleted || p.End.IsDeleted) return;
        _v.Document.ApplyEdits([new TextEdit(new TextRange(_v.Document.PositionAt(p.Start.Offset), _v.Document.PositionAt(p.End.Offset)), picked.Label)]);
    }

    private bool Move(int delta)
    {
        if (_stops is null) return false;
        var next = _current + delta;
        if (next < 0) return true; // Shift+Tab on the first stop: stay (and swallow the key)
        if (next >= _stops.Count)
        {
            End();
            return false; // past the end: let Tab behave normally
        }
        _current = next;
        Activate();
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_stops is null || _disposed) return;
        if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == 0)
        {
            if (Move((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1)) e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            End();
            e.Handled = true;
        }
    }

    // ---- keeping mirrors in sync ----

    private void OnDocumentChanged()
    {
        if (_stops is null || _updatingMirrors || _disposed) return;
        var stop = _stops[_current];
        if (stop.Occurrences.Count < 2) return;
        var primary = stop.Primary;
        if (primary.Start.IsDeleted || primary.End.IsDeleted) return;
        var doc = _v.Document;
        var typed = doc.GetText(new TextRange(doc.PositionAt(primary.Start.Offset), doc.PositionAt(primary.End.Offset)));
        var edits = new List<TextEdit>();
        foreach (var m in stop.Occurrences.Where(o => !o.IsPrimary))
        {
            if (m.Start.IsDeleted || m.End.IsDeleted) continue;
            var text = m.Transform is null ? typed : SnippetTransforms.Apply(typed, m.Transform);
            var range = new TextRange(doc.PositionAt(m.Start.Offset), doc.PositionAt(m.End.Offset));
            if (doc.GetText(range) != text) edits.Add(new TextEdit(range, text));
        }
        if (edits.Count == 0) return;
        _updatingMirrors = true;
        try
        {
            var caret = _v.Editor.CaretOffset;
            doc.ApplyEdits(edits); // inside the typing group when this change came from a keystroke: ONE Ctrl+Z
            _v.Editor.CaretOffset = Math.Min(caret, doc.Length);
        }
        finally
        {
            _updatingMirrors = false;
        }
    }

    private void OnCaretChanged()
    {
        if (_stops is null || _spanStart is null || _spanEnd is null || _updatingMirrors) return;
        var caret = _v.Editor.CaretOffset;
        if (caret < _spanStart.Offset || caret > _spanEnd.Offset) End(); // left the snippet: the session is over
        else _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    internal void End()
    {
        if (_stops is null) return;
        _stops = null;
        _spanStart = _spanEnd = null;
        if (!_disposed) _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    // ---- highlight of the active stop ----

    private IEnumerable<(int Start, int End, bool Active)> HighlightRanges()
    {
        if (_stops is null) yield break;
        for (var i = 0; i < _stops.Count; i++)
        {
            var stop = _stops[i];
            if (stop.Index == 0) continue;
            foreach (var o in stop.Occurrences)
                if (!o.Start.IsDeleted && !o.End.IsDeleted && o.End.Offset >= o.Start.Offset) yield return (o.Start.Offset, o.End.Offset, i == _current);
        }
    }

    private sealed class SnippetRenderer(SnippetFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (!owner.IsActive) return;
            var fill = owner._v.Palette.SnippetTabstop;
            var pen = new Pen(owner._v.Palette.WidgetBorder, 1);
            foreach (var (start, end, active) in owner.HighlightRanges())
            {
                var segment = new TextSegment { StartOffset = start, EndOffset = end };
                var any = false;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    any = true;
                    context.DrawRectangle(active ? fill : null, active ? null : pen, rect);
                }
                if (!any && start == end)
                {
                    var p = textView.GetVisualPosition(new AvaloniaEdit.TextViewPosition(textView.Document.GetLocation(start)), VisualYPosition.LineTop) - textView.ScrollOffset;
                    context.DrawRectangle(fill, null, new Rect(p.X, p.Y, 2, textView.DefaultLineHeight));
                }
            }
        }
    }

    private static int LeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return i;
    }

    private static string WordAt(string line, int col)
    {
        col = Math.Clamp(col, 0, line.Length);
        var a = col;
        var b = col;
        while (a > 0 && (char.IsLetterOrDigit(line[a - 1]) || line[a - 1] == '_')) a--;
        while (b < line.Length && (char.IsLetterOrDigit(line[b]) || line[b] == '_')) b++;
        return line[a..b];
    }

    private async Task<string?> ReadClipboardAsync()
    {
        var top = Avalonia.Controls.TopLevel.GetTopLevel(_v);
        if (top?.Clipboard is not { } clipboard) return null;
        try
        {
            return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null; // the clipboard may be unavailable (headless, remote session): an empty variable, not a failed insert
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        End();
        _v.TextView.BackgroundRenderers.Remove(_renderer);
        _d.Dispose();
    }
}
