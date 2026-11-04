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
using CsCode.Core.Theming;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using CsCode.Editor.View.Decorations;

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
    private EditorModalSurface? _modal;
    private ScrollDecorations? _decorations;
    private bool _allowMinimap = true;
    private bool _disposed, _disposing;

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

        Avalonia.Automation.AutomationProperties.SetName(Editor, "Editor: " + System.IO.Path.GetFileName(document.Uri.IsFile ? document.Uri.LocalPath : document.Uri.AbsolutePath));
        Controller = new EditorViewController(this);
        Palette = EditorPalette.From(factory.Themes);
        Settings = ReadSettings();

        ApplyPalette();
        ApplySettings(initial: true);
        AttachTokenization();

        Editor.TextArea.IndentationStrategy = new LanguageIndentationStrategy(this);
        // Multi-cursor first: with several carets its key and typing handlers must run before the typing path's (one caret: they return at once).
        MultiCursor = new MultiCursorController(this);
        _components.Add(MultiCursor);
        _components.Add(new TypingBehaviors(this));
        // Clipboard behaviour exists in large-file mode too (it is not a language feature).
        Clipboard = new EditorClipboard(this);
        _components.Add(Clipboard);
        _components.Add(new EditorChromeRenderers(this));
        _components.Add(new EditorFontFeatures(this)); // editor.fontLigatures: on unless turned off
        // the find panel's search: bounded, and re-run after edits only once the typing pauses, off the UI thread (editor-4)
        _components.Add(new FindSearch(this));
        foreach (var feature in _factory.CreateFeatures(this)) _components.Add(feature);
        // the overview ruler on the scrollbar and the minimap (after the features: they draw the features' marks)
        _decorations = new ScrollDecorations(this, grid);
        _components.Add(_decorations);

        // live updates: theme, settings, language, read-only
        _disposables.Add(factory.Themes.Changed.Subscribe(_ => Post(OnThemeChanged)));
        _disposables.Add(Services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects("editor") || e.Affects("files") || e.Affects("cscode.editor") || e.Affects("[" + Document.LanguageId + "]")) Post(OnSettingsChanged);
        }));
        _disposables.Add(document.LanguageIdChanged.Subscribe(_ => Post(OnLanguageChanged)));
        // a language without a grammar may get one (editor.syntaxEngine changed, a user grammar arrived): a plain view starts colouring
        _disposables.Add(factory.Tokens.GrammarAvailabilityChanged.Subscribe(_ =>
        {
            if (Tokens is null && !IsDisposed) AttachTokenization();
        }));
        _disposables.Add(factory.Languages.LanguageConfigurationChanged.Subscribe(id =>
        {
            if (id == Document.LanguageId) Post(OnSettingsChanged);
        }));

        // The editor's right-click menu is built by the host (the shell) from contributions; the routed request passes this view first, which
        // brings the caret-dependent context keys up to date (breakpoint at the line, can copy...), so building the menu never waits.
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        _disposables.Add(Disposable.Create(() => RemoveHandler(ContextRequestedEvent, OnContextRequested)));
        Editor.TextArea.GotFocus += OnTextAreaGotFocus;
        Editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        Editor.TextArea.SelectionChanged += OnSelectionChanged;
        Editor.PointerWheelChanged += OnPointerWheel;
        // The controller's TextInput tap (macro recording): tunnelling, so it sees the input before the text area applies it, and only input that
        // the window's own handlers (a modal layer such as Vim) left alone.
        Editor.TextArea.AddHandler(TextInputEvent, OnTextAreaTextInput, RoutingStrategies.Tunnel);
        // While the document holds an undo group open (macro playback), the text area's own Undo / Redo (Ctrl+Z / Ctrl+Y, TextEditor.Undo, so
        // also the controller) must not reach AvaloniaEdit's stack, which throws with a group open: these bindings come first and act on the
        // changes made inside the group. Without a held group they decline and AvaloniaEdit's bindings run as before.
        var undoBinding = new RoutedCommandBinding(ApplicationCommands.Undo, (_, e) => OnHeldUndoRedo(e, redo: false), OnCanHeldUndoRedo);
        var redoBinding = new RoutedCommandBinding(ApplicationCommands.Redo, (_, e) => OnHeldUndoRedo(e, redo: true), OnCanHeldUndoRedo);
        Editor.TextArea.CommandBindings.Insert(0, undoBinding);
        Editor.TextArea.CommandBindings.Insert(0, redoBinding);
        _disposables.Add(Disposable.Create(() =>
        {
            Editor.TextArea.CommandBindings.Remove(undoBinding);
            Editor.TextArea.CommandBindings.Remove(redoBinding);
            Editor.TextArea.RemoveHandler(TextInputEvent, OnTextAreaTextInput);
            Editor.TextArea.GotFocus -= OnTextAreaGotFocus;
            Editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
            Editor.TextArea.SelectionChanged -= OnSelectionChanged;
            Editor.PointerWheelChanged -= OnPointerWheel;
        }));
        factory.Register(this);
        factory.NotifyOpened(this);
    }

    public EditorDocument Document { get; }
    internal TextEditor Editor { get; }
    internal Canvas Overlay { get; }

    /// <summary>
    /// A layer over the editor for widgets the user works in (the peek's embedded editor and list): unlike <see cref="Overlay"/> its children
    /// take the pointer and the keyboard, while its own empty area lets the pointer through to the editor. Created on first use.
    /// </summary>
    internal Canvas WidgetLayer
    {
        get
        {
            if (_widgetLayer is not null) return _widgetLayer;
            _widgetLayer = new Canvas { ClipToBounds = true }; // no background: only its children are hit-testable
            ((Grid)Content!).Children.Add(_widgetLayer);
            return _widgetLayer;
        }
    }

    private Canvas? _widgetLayer;
    internal EditorServices Services { get; }
    internal EditorViewController Controller { get; }
    internal EditorPalette Palette { get; private set; }
    internal EditorSettings Settings { get; private set; }
    internal EditorViewFactory Factory => _factory;
    internal IUiThread Ui => Services.Ui;
    internal TokenizationSession? Tokens { get; private set; }

    /// <summary>
    /// Which engine colours this view right now: "TextMate", "tree-sitter" (<c>editor.syntaxEngine</c>, docs/TREE-SITTER.md), or null (plain
    /// text, large-file mode). Diagnostics only; may change after a setting or language change.
    /// </summary>
    public string? SyntaxEngine => Tokens is not { } t ? null : t.TreeSitter is not null ? "tree-sitter" : "TextMate";
    internal TextArea TextArea => Editor.TextArea;
    internal TextView TextView => Editor.TextArea.TextView;
    internal bool IsDisposed => _disposed;
    /// <summary>The extra carets of this view (Add Next Occurrence, Insert Cursor Above/Below, Alt+Click) and editing at all of them.</summary>
    internal MultiCursorController MultiCursor { get; }

    /// <summary>The overview ruler and the minimap of this view (docs/EDITOR.md §7f).</summary>
    internal ScrollDecorations? ScrollDecorations => _decorations;

    /// <summary>
    /// False hides the minimap of this view whatever <c>editor.minimap.enabled</c> says (the diff editor's panes, as in VS Code). UI thread.
    /// </summary>
    internal bool AllowMinimap
    {
        get => _allowMinimap;
        set
        {
            if (_allowMinimap == value) return;
            _allowMinimap = value;
            _decorations?.OnAllowMinimapChanged();
        }
    }

    /// <summary>Set by the overview ruler: features call <see cref="NotifyMarkersChanged"/> when the marks it draws changed.</summary>
    internal Action<Decorations.ScrollDecorations.Sources>? MarkersChanged { get; set; }

    /// <summary>
    /// A feature's marks changed (diagnostics, occurrences, find matches, source-control changes, breakpoints): only <paramref name="source"/>'s
    /// marks are collected again. UI thread.
    /// </summary>
    internal void NotifyMarkersChanged(Decorations.ScrollDecorations.Sources source) => MarkersChanged?.Invoke(source);

    /// <summary>Cut/Copy/Paste, the clipboard ring, PRIMARY and middle-click paste of this view.</summary>
    internal EditorClipboard Clipboard { get; }

    /// <summary>Opens the editor's context menu at the caret (Shift+F10, the Menu key, the <c>editor.action.showContextMenu</c> command).</summary>
    internal void ShowContextMenu() => TextArea.RaiseEvent(new ContextRequestedEventArgs());

    /// <summary>The caret's rectangle in this control's coordinates (where a keyboard-opened menu appears).</summary>
    internal Rect CaretBounds()
    {
        var caret = TextArea.Caret.CalculateCaretRectangle();
        var origin = TextView.TranslatePoint(new Point(caret.X - TextView.ScrollOffset.X, caret.Y - TextView.ScrollOffset.Y), this) ?? default;
        return new Rect(origin, new Size(Math.Max(1, caret.Width), Math.Max(1, caret.Height)));
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e) => EditorMenus.PrepareContextKeys(this);

    private void OnCanHeldUndoRedo(object? sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !_disposed && Document.HasOpenUndoGroup;

    private void OnHeldUndoRedo(ExecutedRoutedEventArgs e, bool redo)
    {
        e.Handled = true;
        if (_disposed || Document.UndoRedoHeld(redo) is not { } caret) return; // nothing of the group's left: the history before it stays as it is
        Editor.TextArea.ClearSelection();
        Editor.CaretOffset = Math.Clamp(caret, 0, Document.Buffer.TextLength);
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// Hooks for a modal editing layer (the shell's Vim adapter): typing path, caret shape, viewport, folds, popup state, highlights. Created on
    /// first use, disposed with the view; nothing changes in the editor until a layer calls <see cref="EditorModalSurface.Attach"/>.
    /// </summary>
    public EditorModalSurface Modal
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _modal ??= new EditorModalSurface(this);
        }
    }

    /// <summary>True while a modal layer is attached (typing bursts then never continue an undo group: the layer groups its changes).</summary>
    internal bool ModalAttached => _modal?.IsAttached == true;

    /// <summary>The attached layer's undo/redo, offered the editor's Undo/Redo first (true = handled).</summary>
    internal Func<bool, bool>? ModalUndoRedo => _modal?.UndoRedoHandler;

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
        var wrapBefore = Settings.WordWrap;
        Settings = ReadSettings();
        if (Settings.WordWrap != wrapBefore) _wordWrapOverride = null; // a change of editor.wordWrap ends Toggle Word Wrap's choice
        ApplySettings(initial: false);
        _factory.UpdateWordWrapKey(this);
    }

    // ---- Toggle Word Wrap and font zoom: this view's own, never written to the settings (VS Code) ----

    private bool? _wordWrapOverride;
    private double _fontZoom; // pixels the Ctrl+wheel zoom and the zoom keys added to editor.fontSize: this view's, not persisted

    /// <summary>Whether lines wrap in this view: Toggle Word Wrap's choice, else <c>editor.wordWrap</c>.</summary>
    internal bool WordWrapEnabled => _wordWrapOverride ?? Settings.WordWrap;

    /// <summary>
    /// Toggle Word Wrap's choice for this view (null: <c>editor.wordWrap</c> decides). Kept in the view state, so a recreated view keeps it; a
    /// change of the setting drops it. UI thread.
    /// </summary>
    internal bool? WordWrapOverride
    {
        get => _wordWrapOverride;
        set
        {
            _wordWrapOverride = value == Settings.WordWrap ? null : value;
            Editor.WordWrap = WordWrapEnabled;
            _factory.UpdateWordWrapKey(this);
        }
    }

    /// <summary>VS Code's Toggle Word Wrap: flips wrapping for this view only.</summary>
    internal void ToggleWordWrap() => WordWrapOverride = !WordWrapEnabled;

    /// <summary>
    /// Font zoom (Ctrl+wheel, Editor Font Zoom In / Out / Reset): <paramref name="steps"/> pixels larger or smaller, 0 back to <c>editor.fontSize</c>.
    /// Per view and not persisted; kept across settings changes as an offset from the setting. UI thread.
    /// </summary>
    internal void ZoomFont(int steps)
    {
        var size = steps == 0 ? Settings.FontSize : Math.Clamp(Editor.FontSize + steps, 6, 72);
        _fontZoom = size - Settings.FontSize;
        if (Editor.FontSize.Equals(size)) return;
        Editor.FontSize = size;
        RebuildStyleTable();
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
        TextArea.CaretBrush = _modal?.HidesNativeCaret == true ? Brushes.Transparent : p.Caret;
        TextView.CurrentLineBackground = p.CurrentLine;
        TextView.CurrentLineBorder = new Pen(p.CurrentLineBorder.Brush, 1);
        TextView.NonPrintableCharacterBrush = p.Whitespace;
        TextView.ColumnRulerPen = p.Ruler;
        TextView.LinkTextForegroundBrush = p.Link;
    }

    private void ApplySettings(bool initial)
    {
        var s = Settings;
        Editor.FontFamily = new FontFamily(FontFamilies.Normalize(s.FontFamily)); // 'Quoted' names and generic ones as settings write them
        Editor.FontSize = Math.Clamp(s.FontSize + _fontZoom, 6, 72);
        Editor.WordWrap = WordWrapEnabled;
        Editor.ShowLineNumbers = s.ShowLineNumbers;
        CachedLineNumberMargin.ReplaceStock(Editor); // the stock margin shapes every visible number again on each keystroke
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
        // URLs are underlined and opened by NavigationFeature (Ctrl+Click, through IWindowService.OpenExternalAsync, http/https only):
        // AvaloniaEdit's own hyperlink generator would start the browser itself, behind the shell's back.
        o.EnableHyperlinks = false;
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
        TextView.LineTransformers.Insert(0, _colorizer); // first: the grammar's colours are the base the others (semantic tokens, bracket pairs) paint over
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

    private void OnTextAreaTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || e.Source != TextArea || string.IsNullOrEmpty(e.Text)) return;
        Controller.RaiseTextInput(e.Text);
    }

    private void OnSelectionChanged(object? sender, EventArgs e) => Controller.RaiseSelectionChanged();

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!Settings.MouseWheelZoom || !EditorKeys.HasCommand(e.KeyModifiers)) return; // Ctrl+wheel, ⌘+wheel on macOS
        ZoomFont(e.Delta.Y > 0 ? 1 : -1); // zoom is per view and not persisted; the zoom keys share it
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
        if (_disposed || _disposing) return;
        _disposing = true;
        _factory.Unregister(this); // first, while the view is intact: listeners (the modal layer) tear their state down against a live view
        _disposed = true;
        _modal?.Dispose();
        _modal = null;
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
        TextArea.Caret.Hide(); // stops the caret's blink timer: a running DispatcherTimer would keep the whole text area alive
        RoutedCommandFocusRelease.Release(TextArea); // AvaloniaEdit's static "last focused element" would keep this view after the last tab closed
        Documents.UndoStackScrubber.Scrub(Document.Buffer, TextArea); // the document's undo history would otherwise pin this text area (and the view) forever
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
