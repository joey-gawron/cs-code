using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Editor.Languages;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// One editor view: an AvaloniaEdit <c>TextEditor</c> over the document's rope, plus everything that makes it a code editor — syntax colouring
/// from the document's tokenisation session, settings-driven rendering, editing behaviours, language-feature UI. Several views may show one
/// document (split). A view owns NOTHING that outlives it: <see cref="Dispose"/> unhooks every handler, renderer, transformer, margin and
/// subscription it added, so the shell can destroy and recreate views of inactive tabs freely.
///
/// Threading: the control and its document live on the UI thread. A keystroke is: input → buffer mutation (+ O(1) bookkeeping) → return.
/// Colouring, folding, bracket maps, completion filtering… are computed on the editor worker from snapshots and arrive as small, versioned,
/// coalesced updates; none of it is awaited here.
/// </summary>
public sealed class CodeEditorControl : UserControl, IDisposable
{
    private readonly EditorViewFactory _factory;
    private readonly DisposableStore _disposables = new();
    private readonly List<IDisposable> _components = new();
    private TextMateColorizer? _colorizer;
    private ScopeStyleTable? _styleTable;
    private bool _disposed;

    internal CodeEditorControl(EditorViewFactory factory, EditorDocument document)
    {
        EditorStyles.Ensure();
        _factory = factory;
        Document = document;
        Services = factory.Services;

        Editor = new TextEditor
        {
            Document = document.Buffer,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            IsReadOnly = document.IsReadOnly,
            Focusable = true,
        };
        Overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        var grid = new Grid();
        grid.Children.Add(Editor);
        grid.Children.Add(Overlay);
        Content = grid;
        Focusable = false;

        Controller = new EditorViewController(this);
        Palette = EditorPalette.From(factory.Themes);
        Settings = ReadSettings();

        ApplyPalette();
        ApplySettings(initial: true);
        AttachTokenization();

        Editor.TextArea.IndentationStrategy = new LanguageIndentationStrategy(this);
        _components.Add(new TypingBehaviors(this));
        _components.Add(new EditorChromeRenderers(this));
        foreach (var feature in _factory.CreateFeatures(this)) _components.Add(feature);

        // live updates: theme, settings, language, read-only
        _disposables.Add(factory.Themes.Changed.Subscribe(_ => Post(OnThemeChanged)));
        _disposables.Add(Services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects("editor") || e.Affects("files") || e.Affects("cscode.editor") || e.Affects("[" + Document.LanguageId + "]")) Post(OnSettingsChanged);
        }));
        _disposables.Add(document.LanguageIdChanged.Subscribe(_ => Post(OnLanguageChanged)));
        _disposables.Add(factory.Languages.LanguageConfigurationChanged.Subscribe(id =>
        {
            if (id == Document.LanguageId) Post(OnSettingsChanged);
        }));

        Editor.TextArea.GotFocus += OnTextAreaGotFocus;
        Editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        Editor.TextArea.SelectionChanged += OnSelectionChanged;
        Editor.PointerWheelChanged += OnPointerWheel;
        _disposables.Add(Disposable.Create(() =>
        {
            Editor.TextArea.GotFocus -= OnTextAreaGotFocus;
            Editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
            Editor.TextArea.SelectionChanged -= OnSelectionChanged;
            Editor.PointerWheelChanged -= OnPointerWheel;
        }));
        factory.Register(this);
    }

    public EditorDocument Document { get; }
    internal TextEditor Editor { get; }
    internal Canvas Overlay { get; }
    internal EditorServices Services { get; }
    internal EditorViewController Controller { get; }
    internal EditorPalette Palette { get; private set; }
    internal EditorSettings Settings { get; private set; }
    internal EditorViewFactory Factory => _factory;
    internal IUiThread Ui => Services.Ui;
    internal TokenizationSession? Tokens { get; private set; }
    internal TextArea TextArea => Editor.TextArea;
    internal TextView TextView => Editor.TextArea.TextView;
    internal bool IsDisposed => _disposed;

    // ---- helpers used by components ----

    internal void Post(Action action) => Ui.Post(() =>
    {
        if (!_disposed) action();
    });

    internal TextPosition PositionOf(int offset) => Document.PositionAt(offset);

    internal EditingContext GetEditingContext()
    {
        var ext = _factory.Languages.GetExtended(Document.LanguageId);
        return new EditingContext(ext.Base, Settings.TabSize, Settings.InsertSpaces, Document.EndOfLine, ext.EnterRules);
    }

    internal ExtendedLanguageConfiguration LanguageConfiguration => _factory.Languages.GetExtended(Document.LanguageId);

    /// <summary>Applies a plan as ONE undo unit and moves the selection to where the plan says (UI thread).</summary>
    internal void ApplyPlan(EditPlan plan)
    {
        if (Document.IsReadOnly) return;
        if (!Document.ApplyEdits(plan.Edits)) return;
        SetSelection(plan.Selection);
    }

    internal void SetSelection(TextRange range)
    {
        var a = Document.OffsetAt(range.Start);
        var b = Document.OffsetAt(range.End);
        var area = Editor.TextArea;
        if (a == b)
        {
            area.ClearSelection();
            Editor.CaretOffset = b;
        }
        else
        {
            area.Selection = Selection.Create(area, a, b);
            Editor.CaretOffset = b;
        }
    }

    // ---- settings / theme ----

    private EditorSettings ReadSettings()
    {
        // .editorconfig facts were resolved when the document opened and are kept on it
        return EditorSettings.Read(Services.Config, Document.LanguageId, Document.EditorConfigProperties);
    }

    private void OnSettingsChanged()
    {
        Settings = ReadSettings();
        ApplySettings(initial: false);
    }

    private void OnThemeChanged()
    {
        Palette = EditorPalette.From(_factory.Themes);
        ApplyPalette();
        RebuildStyleTable();
        foreach (var c in _components.OfType<IThemeAware>()) c.OnThemeChanged();
        TextView.Redraw();
    }

    private void OnLanguageChanged()
    {
        Settings = ReadSettings();
        ApplySettings(initial: false);
        AttachTokenization();
    }

    private void ApplyPalette()
    {
        var p = Palette;
        Editor.Background = p.Background;
        Editor.Foreground = p.Foreground;
        Editor.LineNumbersForeground = p.LineNumber;
        Editor.SearchResultsBrush = p.FindMatch;
        TextArea.SelectionBrush = p.Selection;
        TextArea.SelectionForeground = null;
        TextArea.CaretBrush = p.Caret;
        TextView.CurrentLineBackground = p.CurrentLine;
        TextView.CurrentLineBorder = new Pen(p.CurrentLineBorder.Brush, 1);
        TextView.NonPrintableCharacterBrush = p.Whitespace;
        TextView.ColumnRulerPen = p.Ruler;
        TextView.LinkTextForegroundBrush = p.Link;
    }

    private void ApplySettings(bool initial)
    {
        var s = Settings;
        Editor.FontFamily = new FontFamily(s.FontFamily);
        Editor.FontSize = s.FontSize;
        Editor.WordWrap = s.WordWrap;
        Editor.ShowLineNumbers = s.ShowLineNumbers;
        Editor.IsReadOnly = Document.IsReadOnly;
        var o = Editor.Options;
        o.IndentationSize = s.TabSize;
        o.ConvertTabsToSpaces = s.InsertSpaces;
        o.ShowSpaces = o.ShowTabs = s.RenderWhitespace is "all" or "boundary";
        o.ShowEndOfLine = s.RenderWhitespace == "all";
        o.ShowColumnRulers = s.Rulers.Count > 0;
        o.ColumnRulerPositions = s.Rulers.ToArray();
        o.HighlightCurrentLine = s.HighlightActiveLine;
        o.LineHeightFactor = s.LineHeightFactor;
        o.CutCopyWholeLine = true;
        o.AllowScrollBelowDocument = true;
        o.EnableHyperlinks = true;
        o.RequireControlModifierForHyperlinkClick = true;
        o.EnableEmailHyperlinks = false;
        o.EnableRectangularSelection = true;
        Document.Buffer.UndoStack.SizeLimit = s.UndoLimit;
        if (!initial) RebuildStyleTable();
        foreach (var c in _components.OfType<ISettingsAware>()) c.OnSettingsChanged();
    }

    // ---- syntax colouring ----

    private void AttachTokenization()
    {
        _colorizer?.Dispose();
        if (_colorizer is not null) TextView.LineTransformers.Remove(_colorizer);
        _colorizer = null;
        Tokens?.DetachView();
        Tokens = null;
        if (Document.IsLargeFile) return; // large-file mode: plain text, no tokenisation at all
        var session = _factory.GetTokenization(Document);
        if (session is null) return;
        session.AttachView();
        Tokens = session;
        _styleTable = BuildStyleTable(session);
        _colorizer = new TextMateColorizer(session, TextView, _styleTable);
        TextView.LineTransformers.Add(_colorizer);
    }

    private ScopeStyleTable BuildStyleTable(TokenizationSession session)
    {
        var theme = _factory.Themes.Current;
        var fg = _factory.Themes.GetColor("editor.foreground");
        return new ScopeStyleTable(theme, fg, session.Interner, Editor.FontFamily);
    }

    private void RebuildStyleTable()
    {
        if (Tokens is null || _colorizer is null) return;
        _styleTable = BuildStyleTable(Tokens);
        _colorizer.SetTable(_styleTable);
    }

    // ---- events ----

    private void OnTextAreaGotFocus(object? sender, RoutedEventArgs e) => _factory.NotifyFocused(this);

    private void OnCaretChanged(object? sender, EventArgs e) => Controller.RaiseCaretChanged();

    private void OnSelectionChanged(object? sender, EventArgs e) => Controller.RaiseSelectionChanged();

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!Settings.MouseWheelZoom || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        Editor.FontSize = Math.Clamp(Editor.FontSize + (e.Delta.Y > 0 ? 1 : -1), 6, 72); // zoom is per view and not persisted
        RebuildStyleTable();
        e.Handled = true;
    }

    internal void AddComponent(IDisposable component) => _components.Add(component);

    /// <summary>Fires for each typed character BEFORE it is inserted (completion uses it to accept on commit characters).</summary>
    internal Emitter<string> PreTyping { get; } = new();

    /// <summary>Characters typed into this view (after the text was entered or the auto-close/over-type handled it): features key off this.</summary>
    internal IEventSource<string>? Typed { get; set; }

    /// <summary>The feature component of type <typeparamref name="T"/> of this view, or null (e.g. when the document is in large-file mode).</summary>
    internal T? Feature<T>() where T : class => _components.OfType<T>().FirstOrDefault();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _factory.Unregister(this);
        for (var i = _components.Count - 1; i >= 0; i--) _components[i].Dispose();
        _components.Clear();
        if (_colorizer is not null)
        {
            TextView.LineTransformers.Remove(_colorizer);
            _colorizer.Dispose();
            _colorizer = null;
        }
        Tokens?.DetachView();
        Tokens = null;
        _disposables.Dispose();
        TextArea.IndentationStrategy = null!;
        Editor.Document = null!; // releases the buffer: the TextEditor's internals unsubscribe from it
        Content = null;
        Controller.Dispose();
    }
}

/// <summary>Components that rebuild their brushes when the theme changes.</summary>
internal interface IThemeAware
{
    void OnThemeChanged();
}

/// <summary>Components that re-read settings when they change.</summary>
internal interface ISettingsAware
{
    void OnSettingsChanged();
}

/// <summary>Auto-indent for Enter, driven by the language configuration (see <see cref="AutoIndent"/>).</summary>
internal sealed class LanguageIndentationStrategy(CodeEditorControl view) : AvaloniaEdit.Indentation.IIndentationStrategy
{
    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var previous = line.PreviousLine;
        if (previous is null) return;
        var ctx = view.GetEditingContext();
        var before = document.GetText(previous.Offset, previous.Length);
        var after = document.GetText(line.Offset, line.Length);
        var beforePrevious = previous.PreviousLine is { } pp ? document.GetText(pp.Offset, pp.Length) : null;
        var result = AutoIndent.ForEnter(before, after, ctx, beforePrevious);

        var existing = 0;
        while (existing < after.Length && after[existing] is ' ' or '\t') existing++;
        var indent = result.NewLineIndent + result.AppendText;
        document.Replace(line.Offset, existing, indent);
        var caret = line.Offset + indent.Length;
        if (result.ClosingLineIndent is { } closing)
        {
            // {|}: the tail ("}") moves to its own line at the outer indentation; the caret stays on the indented line
            var eol = TextUtilities.GetNewLineFromDocument(document, line.LineNumber);
            document.Insert(caret, eol + closing, AnchorMovementType.BeforeInsertion);
        }
        view.Editor.CaretOffset = caret;
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        // Re-indenting whole ranges is done by the language server's formatter (formatDocument/formatSelection), not here.
    }
}
