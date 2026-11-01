using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.View;
using Location = CsCode.Core.Languages.Location;

namespace CsCode.Editor.Features;

/// <summary>
/// Peek (VS Code's peek view, Visual Studio's Peek Definition, Alt+F12): an editor embedded in the view right below the caret's line, showing
/// the definition (or the references, implementations...) without leaving the file. The lines below move down to make room — a zone the
/// height of the widget is reserved under the line (a zero-length spacer element whose height hangs below the line's text), so nothing is
/// covered — and the widget follows the line as the view scrolls. With several locations a list on the right picks which one the embedded
/// editor shows. The embedded editor is a full editor over the document when it is open in the shell (edits go straight into it, with its
/// undo, its language features), else read-only over the file's text read through the file system. Esc closes the peek (in the view, in the
/// list, in the embedded editor); Enter in the list, Enter in a read-only embedded editor, a double-click in either or a click on the title
/// opens the location and closes the peek.
/// </summary>
internal sealed class PeekFeature : IThemeAware, IDisposable
{
    public const string PeekDefinitionCommand = "editor.action.peekDefinition";
    public const string PeekDeclarationCommand = "editor.action.peekDeclaration";
    public const string PeekTypeDefinitionCommand = "editor.action.peekTypeDefinition";
    public const string PeekImplementationCommand = "editor.action.peekImplementation";
    public const string PeekReferencesCommand = "editor.action.referenceSearch.trigger";
    public const string CloseCommand = "closeReferenceSearch";
    /// <summary>VS Code's context key: true while a peek is open in the focused editor.</summary>
    public const string VisibleKey = "referenceSearchVisible";

    /// <summary>Embedded editors and the peek they belong to (Esc inside one, from a modal layer too, closes that peek).</summary>
    private static readonly ConditionalWeakTable<CodeEditorControl, PeekFeature> EmbeddedOwners = new();

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private Session? _session;
    private bool _disposed;

    public PeekFeature(CodeEditorControl view)
    {
        _v = view;
        // Tunnel, registered after the completion list's, parameter hints', snippets' and multi-cursor's own tunnel handlers: they take Esc first
        // when they have something to close (and mark it handled); AvaloniaEdit's text area would take it before a bubbling handler.
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        _d.Add(Disposable.Create(() => view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnHostKeyDown)));
    }

    public bool IsOpen => _session is not null;
    internal Control? Widget => _session?.Root;
    internal CodeEditorControl? Embedded => _session?.Editor;
    internal ListBox? List => _session?.List;
    internal IReadOnlyList<Location> Locations => _session?.Locations ?? [];
    internal int Index => _session?.Index ?? -1;
    internal int AnchorLine => _session is { } s ? s.Anchor.Line - 1 : -1;
    /// <summary>The height of the zone reserved under the anchor line (the widget's height).</summary>
    internal double ZoneHeight => _session?.Height ?? 0;
    /// <summary>Completes when the embedded editor shows the selected location (tests).</summary>
    internal Task Shown => _session?.Shown ?? Task.CompletedTask;

    /// <summary>The peek <paramref name="view"/> is the embedded editor of, or null.</summary>
    internal static PeekFeature? OwnerOf(CodeEditorControl view) => EmbeddedOwners.TryGetValue(view, out var owner) ? owner : null;

    /// <summary>Closes the peek this view shows, or the one it is embedded in (a modal layer's Esc). True when one was open.</summary>
    internal static bool CloseFor(CodeEditorControl view)
    {
        if (view.Feature<PeekFeature>() is { IsOpen: true } own) { own.Close(); return true; }
        if (OwnerOf(view) is { IsOpen: true } owner) { owner.Close(focusHost: true); return true; }
        return false;
    }

    // ------------------------------------------------------------------------------------------------ commands

    /// <summary>Peek Definition (and Declaration / Type Definition / Implementation): the providers' answer for the symbol at the caret.</summary>
    public async Task PeekDefinitionAsync(DefinitionKind kind = DefinitionKind.Definition)
    {
        if (_disposed || _v.Feature<NavigationFeature>() is not { } nav) return;
        var caret = _v.Editor.CaretOffset;
        var version = _v.Document.Version;
        var links = await nav.FindDefinitionsAsync(caret, kind, CancellationToken.None).ConfigureAwait(true);
        if (_disposed || _v.Document.Version != version) return;
        var what = kind switch { DefinitionKind.Declaration => "declaration", DefinitionKind.TypeDefinition => "type definition", DefinitionKind.Implementation => "implementation", _ => "definition" };
        await ShowAsync(links.Select(l => new Location(l.TargetUri, l.TargetSelectionRange)).ToList(), caret, what).ConfigureAwait(true);
    }

    /// <summary>Peek References (VS Code's Shift+F12): every reference of the symbol at the caret, the declaration included.</summary>
    public async Task PeekReferencesAsync()
    {
        if (_disposed || _v.Feature<NavigationFeature>() is not { } nav) return;
        var caret = _v.Editor.CaretOffset;
        var version = _v.Document.Version;
        var references = await nav.FindReferenceLocationsAsync(caret).ConfigureAwait(true);
        if (_disposed || _v.Document.Version != version) return;
        await ShowAsync(references, caret, "reference").ConfigureAwait(true);
    }

    /// <summary>Opens the peek under the line of <paramref name="offset"/> with <paramref name="locations"/>; none says so in the status bar.</summary>
    internal Task ShowAsync(IReadOnlyList<Location> locations, int offset, string what)
    {
        if (locations.Count == 0)
        {
            var word = FeatureSupport.WordAt(_v.Editor.Document, offset);
            FeatureSupport.Status(_v, word.End > word.Start ? $"No {what} found for '{_v.Editor.Document.GetText(word.Start, word.End - word.Start)}'" : $"No {what} found");
            return Task.CompletedTask;
        }
        Close();
        var line = _v.Editor.Document.GetLineByOffset(Math.Clamp(offset, 0, _v.Editor.Document.TextLength));
        // the locations in the providers' order; the first one is shown
        var session = _session = new Session(this, line, locations, what);
        session.Open();
        _v.Services.ContextKeys?.Set(VisibleKey, true);
        return session.Select(0);
    }

    /// <summary>Closes the peek (Esc). <paramref name="focusHost"/>: the keyboard goes back to the view (it was in the peek).</summary>
    public void Close(bool focusHost = false)
    {
        if (_session is not { } s) return;
        _session = null;
        var hadFocus = s.Root.IsKeyboardFocusWithin;
        s.Dispose();
        _v.Services.ContextKeys?.Set(VisibleKey, false);
        if ((focusHost || hadFocus) && !_disposed && !_v.IsDisposed) _v.TextArea.Focus();
    }

    /// <summary>Opens the selected location as Go to Definition would (this view, or a tab through the shell) and closes the peek.</summary>
    internal async Task OpenSelectedAsync()
    {
        if (_session is not { } s || s.Index < 0) return;
        var location = s.Locations[s.Index];
        Close();
        await FeatureSupport.OpenLocationAsync(_v, location.Uri, location.Range).ConfigureAwait(true);
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _session is null || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        Close();
        e.Handled = true;
    }

    public void OnThemeChanged() => _session?.ApplyTheme();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _d.Dispose();
    }

    private IBrush Brush(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(_v.Factory.Themes.GetColor(key)));

    // ------------------------------------------------------------------------------------------------ one open peek

    /// <summary>
    /// What one open peek owns: the zone under the anchor line (element generator + spacer), the widget on the view's widget layer, the
    /// embedded editor (and the read-only document it may have made), the subscriptions that keep the widget on its line. All released by
    /// <see cref="Dispose"/>.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private const double TitleHeight = 24;
        private const double ListWidth = 260;

        private readonly PeekFeature _owner;
        private readonly CodeEditorControl _v;
        private readonly DisposableStore _d = new();
        private readonly ZoneGenerator _zone;
        private readonly Border _spacer;
        private readonly Border _editorHost = new() { ClipToBounds = true };
        private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold, Cursor = new Cursor(StandardCursorType.Hand) };
        private readonly TextBlock _subtitle = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
        private readonly DockPanel _titleBar = new() { Height = TitleHeight, LastChildFill = true };
        private readonly Dictionary<Uri, string> _texts = [];
        private readonly string _what;
        private readonly HashSet<EditorDocument> _snapshots = [];
        private TargetHighlight? _highlight;
        private int _generation;
        private bool _disposed;

        public Session(PeekFeature owner, DocumentLine line, IReadOnlyList<Location> locations, string what)
        {
            _owner = owner;
            _v = owner._v;
            _what = what;
            Locations = locations;
            Anchor = _v.Editor.Document.CreateAnchor(line.EndOffset);
            Anchor.SurviveDeletion = false; // the anchor line deleted (the text around its end removed) closes the peek
            var tv = _v.TextView;
            var lineHeight = tv.DefaultLineHeight;
            var viewport = Math.Max(tv.Bounds.Height, lineHeight * 12);
            // VS Code's peek: about a third of the editor, at least 8 lines of the embedded editor and at most 18
            Height = Math.Round(Math.Clamp(viewport * 0.4, lineHeight * 8, lineHeight * 18) + TitleHeight);
            _spacer = new Border { Width = 1, Height = lineHeight + Height, IsHitTestVisible = false };
            TextBlock.SetBaselineOffset(_spacer, tv.DefaultBaseline); // the line's text stays on top; the rest of the spacer hangs below it
            _zone = new ZoneGenerator(this);

            Root = new Border { BorderThickness = new Thickness(0, 2, 0, 2), Height = Height, ClipToBounds = true };
            List = new ListBox
            {
                Width = ListWidth,
                SelectionMode = SelectionMode.Single,
                ItemsSource = Enumerable.Range(0, locations.Count).ToList(),
                ItemTemplate = new FuncDataTemplate<int>((i, _) => ItemFor(i), supportsRecycling: false),
                IsVisible = locations.Count > 1,
            };
            Avalonia.Automation.AutomationProperties.SetName(List, "Peek locations");
            Avalonia.Automation.AutomationProperties.SetName(Root, "Peek " + what);
        }

        public IReadOnlyList<Location> Locations { get; }
        public TextAnchor Anchor { get; }
        public double Height { get; }
        public Border Root { get; }
        public ListBox List { get; }
        public CodeEditorControl? Editor { get; private set; }
        public int Index { get; private set; } = -1;
        public Task Shown { get; private set; } = Task.CompletedTask;

        public void Open()
        {
            var close = new Button { Content = "✕", Padding = new Thickness(6, 0), Margin = new Thickness(2), MinHeight = 0, FontSize = 12, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(close, "Close (Escape)");
            Avalonia.Automation.AutomationProperties.SetName(close, "Close peek");
            close.Click += (_, _) => _owner.Close(focusHost: true);
            DockPanel.SetDock(close, Dock.Right);
            _titleBar.Children.Add(close);
            var titles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), Children = { _title, _subtitle } };
            _titleBar.Children.Add(titles);
            ToolTip.SetTip(_title, "Open (Enter)");
            _title.PointerPressed += (_, e) => { e.Handled = true; _ = _owner.OpenSelectedAsync(); };

            var body = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(List, Dock.Right);
            body.Children.Add(List);
            body.Children.Add(_editorHost);
            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_titleBar, Dock.Top);
            content.Children.Add(_titleBar);
            content.Children.Add(body);
            Root.Child = content;
            ApplyTheme();

            List.SelectionChanged += OnListSelection;
            List.DoubleTapped += OnOpenGesture;
            // Tunnel: the list and the embedded editor's text area would take Enter (and the text area Esc) before a bubbling handler; what the
            // embedded editor has open itself (completion list, parameter hints, snippet, several carets) keeps its keys (EditorWantsKey).
            Root.AddHandler(InputElement.KeyDownEvent, OnWidgetKeyDown, RoutingStrategies.Tunnel);
            _d.Add(Disposable.Create(() =>
            {
                List.SelectionChanged -= OnListSelection;
                List.DoubleTapped -= OnOpenGesture;
                Root.RemoveHandler(InputElement.KeyDownEvent, OnWidgetKeyDown);
            }));

            var layer = _v.WidgetLayer;
            layer.Children.Add(Root);
            var tv = _v.TextView;
            tv.ElementGenerators.Add(_zone);
            tv.ScrollOffsetChanged += OnViewMoved;
            tv.VisualLinesChanged += OnViewMoved;
            layer.SizeChanged += OnLayerSized;
            _d.Add(Disposable.Create(() =>
            {
                tv.ScrollOffsetChanged -= OnViewMoved;
                tv.VisualLinesChanged -= OnViewMoved;
                layer.SizeChanged -= OnLayerSized;
                tv.ElementGenerators.Remove(_zone);
                layer.Children.Remove(Root);
                Root.Child = null;
            }));
            _d.Add(_v.Document.Changed.Subscribe(_ => OnHostChanged()));
            RedrawAnchorLine();
            // the zone and the widget in sight, as VS Code reveals a peek
            _v.Post(Reveal);
        }

        private void Reveal()
        {
            if (_disposed) return;
            var tv = _v.TextView;
            tv.EnsureVisualLines();
            var top = tv.GetVisualTopByDocumentLine(Anchor.Line);
            var bottom = top + tv.GetOrConstructVisualLine(AnchorLine()).Height; // the line's (wrapped) rows and the zone under them
            var view = tv.ScrollOffset.Y;
            var viewport = tv.Bounds.Height;
            if (bottom > view + viewport) ScrollTo(Math.Min(top, bottom - viewport));
            else if (top < view) ScrollTo(top);
            Position();
        }

        /// <summary>Scrolls the view to <paramref name="y"/> (TextEditor.ScrollToVerticalOffset is a no-op for absolute positions).</summary>
        private void ScrollTo(double y) => ((Avalonia.Controls.Primitives.IScrollable)_v.TextArea).Offset = new Vector(_v.TextView.ScrollOffset.X, Math.Max(0, y));

        private void OnLayerSized(object? sender, SizeChangedEventArgs e) => Position();
        private void OnViewMoved(object? sender, EventArgs e) => Position();

        /// <summary>
        /// Puts the widget in the zone: right under the anchor line's text, across the editor. The zone hangs below the line's last row (the
        /// spacer sits at the line's end), so the widget's top is the visual line's bottom less the zone, whatever rows word wrap made.
        /// </summary>
        private void Position()
        {
            if (_disposed) return;
            var tv = _v.TextView;
            var layer = _v.WidgetLayer;
            var top = tv.GetVisualTopByDocumentLine(Anchor.Line);
            // a visual line only exists while it is in sight, and the zone is part of it: off screen, the one-row estimate puts the widget off screen too
            var y = (tv.GetVisualLine(Anchor.Line) is { } visual ? visual.VisualTop + visual.Height - Height : top + tv.DefaultLineHeight) - tv.ScrollOffset.Y;
            var origin = tv.TranslatePoint(new Point(0, y), layer) ?? new Point(0, y);
            var right = tv.TranslatePoint(new Point(tv.Bounds.Width, 0), layer)?.X ?? layer.Bounds.Width;
            Canvas.SetLeft(Root, 0);
            Canvas.SetTop(Root, origin.Y);
            Root.Width = Math.Max(120, right);
        }

        private DocumentLine AnchorLine() => _v.Editor.Document.GetLineByNumber(Math.Clamp(Anchor.Line, 1, _v.Editor.Document.LineCount));

        private void RedrawAnchorLine() => _v.TextView.Redraw(AnchorLine());

        /// <summary>An edit in the view: the anchor follows its line; a deleted line closes the peek.</summary>
        private void OnHostChanged()
        {
            if (_disposed) return;
            if (Anchor.IsDeleted) { _owner.Close(); return; }
            Position();
        }

        public void ApplyTheme()
        {
            var o = _owner;
            Root.BorderBrush = o.Brush("peekView.border");
            Root.Background = o.Brush("peekViewResult.background");
            _titleBar.Background = o.Brush("peekViewTitle.background");
            _title.Foreground = o.Brush("peekViewTitleLabel.foreground");
            _subtitle.Foreground = o.Brush("peekViewTitleDescription.foreground");
            List.Background = o.Brush("peekViewResult.background");
            List.Foreground = o.Brush("peekViewResult.fileForeground");
            if (Editor is { } e) e.Editor.Background = o.Brush("peekViewEditor.background");
            _highlight?.SetBrush(o.Brush("peekViewEditor.matchHighlightBackground"));
        }

        // -------------------------------------------------------------------------------- the list

        private Control ItemFor(int i)
        {
            var l = Locations[i];
            var file = FileName(l.Uri);
            var preview = PreviewOf(l);
            var name = new TextBlock { Text = $"{file}:{l.Range.Start.Line + 1}", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 6, 0) };
            var text = new TextBlock { Text = preview, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8, FontFamily = _v.Editor.FontFamily, FontSize = Math.Max(10, _v.Editor.FontSize - 1) };
            ToolTip.SetTip(name, l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.ToString());
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { name, text }, Height = 20 };
        }

        private string PreviewOf(Location l)
        {
            if (_v.Factory.TryGetOpenDocument(l.Uri) is { } open) return LineOf(open.GetLineText, open.LineCount, l.Range.Start.Line);
            if (_texts.TryGetValue(l.Uri, out var text)) return LineOf(i => NthLine(text, i), int.MaxValue, l.Range.Start.Line);
            return "";
        }

        private static string LineOf(Func<int, string> get, int count, int line) => line < count ? Trim(get(line)) : "";
        private static string Trim(string s) => s.Length > 200 ? s.Trim()[..Math.Min(s.Trim().Length, 200)] : s.Trim();

        private static string NthLine(string text, int n)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                var nl = text.IndexOf('\n', start);
                if (nl < 0) return "";
                start = nl + 1;
            }
            var end = text.IndexOf('\n', start);
            return (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r');
        }

        private void OnListSelection(object? sender, SelectionChangedEventArgs e)
        {
            if (List.SelectedItem is int i && i != Index) _ = Select(i);
        }

        private void OnOpenGesture(object? sender, TappedEventArgs e)
        {
            e.Handled = true;
            _ = _owner.OpenSelectedAsync();
        }

        private void OnWidgetKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            var inEditor = Editor is { } ed && e.Source is Visual src && (ReferenceEquals(src, ed) || ed.IsVisualAncestorOf(src));
            if (inEditor && EditorWantsKey(Editor!, e.Key)) return;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                _owner.Close(focusHost: true);
                return;
            }
            // Enter opens from the list, and from a read-only embedded editor (an editable one types the line break)
            var fromList = e.Source is Visual v && (ReferenceEquals(v, List) || List.IsVisualAncestorOf(v));
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && (fromList || (inEditor && Editor!.Document.IsReadOnly)))
            {
                e.Handled = true;
                _ = _owner.OpenSelectedAsync();
            }
        }

        /// <summary>The embedded editor's own popups and carets take Esc / Enter first, as in any editor.</summary>
        private static bool EditorWantsKey(CodeEditorControl editor, Key key) => key switch
        {
            Key.Escape => editor.Feature<CompletionFeature>()?.IsOpen == true || editor.Feature<SignatureFeature>()?.IsOpen == true
                || editor.Feature<SnippetFeature>()?.IsActive == true || editor.MultiCursor.IsActive,
            Key.Enter => editor.Feature<CompletionFeature>()?.IsOpen == true,
            _ => false,
        };

        // -------------------------------------------------------------------------------- the embedded editor

        /// <summary>Shows location <paramref name="index"/> in the embedded editor (a new editor when the document differs).</summary>
        public Task Select(int index)
        {
            if (_disposed || index < 0 || index >= Locations.Count) return Task.CompletedTask;
            Index = index;
            if (!Equals(List.SelectedItem, index)) List.SelectedItem = index;
            var location = Locations[index];
            _title.Text = FileName(location.Uri);
            _subtitle.Text = (Locations.Count > 1 ? $"{index + 1} of {Locations.Count} {_what}s · " : "") + Directory(location.Uri);
            return Shown = ShowAsync(location);
        }

        private async Task ShowAsync(Location location)
        {
            var generation = ++_generation;
            var doc = Editor?.Document is { } current && FeatureSupport.SameFile(current.Uri, location.Uri) ? current : await DocumentForAsync(location.Uri).ConfigureAwait(true);
            if (_disposed || generation != _generation) { if (doc is not null && !ReferenceEquals(doc, Editor?.Document)) Release(doc); return; }
            if (doc is null)
            {
                SetEditor(null);
                _editorHost.Child = new TextBlock { Text = "The file could not be read.", Margin = new Thickness(12), Foreground = _owner.Brush("descriptionForeground") };
                return;
            }
            if (!ReferenceEquals(Editor?.Document, doc)) SetEditor(doc);
            var editor = Editor!;
            var range = location.Range;
            _highlight!.Range = range;
            editor.Controller.SetSelection(new TextRange(range.Start, range.Start), reveal: false);
            ScrollTo(editor, Math.Max(0, range.Start.Line - 3)); // the target near the top, a few lines of context above (VS Code)
            editor.TextView.Redraw();
        }

        /// <summary>Scrolls a (possibly fresh) embedded editor to <paramref name="line"/>: again on its first layout passes, which make the extent.</summary>
        private void ScrollTo(CodeEditorControl editor, int line)
        {
            editor.Controller.ScrollToLine(line);
            if (Math.Abs(editor.Controller.TopLine - line) < 0.5) return;
            var passes = 0;
            void Retry(object? sender, EventArgs e)
            {
                if (_disposed || editor.IsDisposed || !ReferenceEquals(Editor, editor) || ++passes > 20) { editor.Editor.LayoutUpdated -= Retry; return; }
                editor.Controller.ScrollToLine(line);
                if (Math.Abs(editor.Controller.TopLine - line) < 0.5) editor.Editor.LayoutUpdated -= Retry;
            }
            editor.Editor.LayoutUpdated += Retry;
        }

        /// <summary>The document open in the shell (editable in place), else a read-only one over the file's text; null when it cannot be read.</summary>
        private async Task<EditorDocument?> DocumentForAsync(Uri uri)
        {
            if (_v.Factory.TryGetOpenDocument(uri) is { } open) return open;
            if (FeatureSupport.SameFile(uri, _v.Document.Uri)) return _v.Document;
            string text;
            if (!_texts.TryGetValue(uri, out var cached))
            {
                try
                {
                    var bytes = await _v.Services.FileSystem.ReadFileAsync(uri).ConfigureAwait(true);
                    text = await Task.Run(() => TextFileCodec.Decode(bytes, TextFileCodec.Detect(bytes))).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    return null;
                }
                if (_disposed) return null;
                _texts[uri] = text;
                List.ItemsSource = Enumerable.Range(0, Locations.Count).ToList(); // previews of that file's lines are known now
                List.SelectedItem = Index;
            }
            else text = cached;
            var firstLine = NthLine(text, 0);
            var languageId = _v.Factory.Languages.DetectLanguageId(uri, firstLine);
            var snapshot = _v.Factory.CreateSnapshotDocument(uri, text, languageId);
            _snapshots.Add(snapshot);
            return snapshot;
        }

        private void SetEditor(EditorDocument? doc)
        {
            if (Editor is { } old)
            {
                EmbeddedOwners.Remove(old);
                var oldDoc = old.Document;
                _editorHost.Child = null;
                old.DoubleTapped -= OnOpenGesture;
                old.Dispose();
                _highlight = null;
                Release(oldDoc);
            }
            Editor = null;
            if (doc is null) return;
            var editor = new CodeEditorControl(_v.Factory, doc);
            EmbeddedOwners.AddOrUpdate(editor, _owner);
            _highlight = new TargetHighlight(editor);
            editor.TextView.BackgroundRenderers.Add(_highlight);
            editor.AddComponent(_highlight);
            editor.DoubleTapped += OnOpenGesture;
            Avalonia.Automation.AutomationProperties.SetName(editor.Editor, "Peek editor: " + FileName(doc.Uri));
            _editorHost.Child = editor;
            Editor = editor;
            ApplyTheme();
        }

        private void Release(EditorDocument doc)
        {
            if (!_snapshots.Remove(doc)) return; // documents of the shell stay as they are
            _v.Factory.ReleaseSnapshotDocument(doc);
        }

        private static string FileName(Uri uri) => Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
        private static string Directory(Uri uri) => Path.GetDirectoryName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath) ?? "";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            SetEditor(null);
            foreach (var doc in _snapshots.ToList()) Release(doc);
            _d.Dispose();
            var line = Anchor.IsDeleted ? null : _v.Editor.Document?.GetLineByNumber(Math.Clamp(Anchor.Line, 1, _v.Editor.Document.LineCount));
            if (line is not null) _v.TextView.Redraw(line); else _v.TextView.Redraw();
        }

        /// <summary>The zone: a zero-length element at the end of the anchor line whose spacer hangs below the text.</summary>
        private sealed class ZoneGenerator(Session s) : VisualLineElementGenerator
        {
            public override int GetFirstInterestedOffset(int startOffset)
            {
                if (s._disposed || s.Anchor.IsDeleted) return -1;
                var line = CurrentContext.Document.GetLineByNumber(Math.Clamp(s.Anchor.Line, 1, CurrentContext.Document.LineCount));
                return line.EndOffset >= startOffset ? line.EndOffset : -1;
            }

            public override VisualLineElement? ConstructElement(int offset)
            {
                if (s._disposed) return null;
                // the spacer may still be the child of an earlier element of a visual line being replaced
                if (s._spacer.Parent is Panel p) p.Children.Remove(s._spacer);
                return new InlineObjectElement(0, s._spacer);
            }
        }
    }

    /// <summary>The peeked range in the embedded editor (VS Code's <c>peekViewEditor.matchHighlightBackground</c>).</summary>
    private sealed class TargetHighlight(CodeEditorControl editor) : IBackgroundRenderer, IDisposable
    {
        private IBrush _brush = Brushes.Transparent;
        public TextRange? Range { get; set; }
        public KnownLayer Layer => KnownLayer.Background;

        public void SetBrush(IBrush brush)
        {
            _brush = brush;
            editor.TextView.InvalidateLayer(KnownLayer.Background);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Range is not { } r || textView.Document is null) return;
            var doc = textView.Document;
            int Offset(TextPosition p)
            {
                if (p.Line >= doc.LineCount) return doc.TextLength;
                var line = doc.GetLineByNumber(p.Line + 1);
                return line.Offset + Math.Min(p.Character, line.Length);
            }
            var start = Offset(r.Start);
            var end = Offset(r.End);
            if (end <= start) end = Math.Min(doc.TextLength, start + 1);
            var builder = new BackgroundGeometryBuilder { CornerRadius = 1 };
            builder.AddSegment(textView, new TextSegment { StartOffset = start, EndOffset = end });
            if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(_brush, null, geometry);
        }

        public void Dispose() => editor.TextView.BackgroundRenderers.Remove(this);
    }
}
