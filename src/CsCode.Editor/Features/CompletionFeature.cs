using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Markdown;
using CsCode.Editor.Threading;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// IntelliSense completion for one view. Requests go to every <see cref="ICompletionProvider"/> registered for the document, concurrently and
/// OFF the UI thread (a provider that blocks, burns CPU or never answers costs nothing but a "loading…" line after 300 ms); results are merged as
/// they arrive; filtering and ordering (fuzzy score, sortText, preselect) runs on the editor worker; the popup renders at most
/// <see cref="CompletionFilter.MaxShown"/> virtualised rows. Latest-wins everywhere: every keystroke supersedes the previous request/filter, and a
/// result that no longer matches the word being typed is dropped, never shown.
/// </summary>
internal sealed class CompletionFeature : ICompletionFeature, IThemeAware, ISettingsAware, IDisposable
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    private sealed class Session
    {
        public int Id { get; init; }
        public required TextAnchor Start { get; init; }
        public required TextPosition RequestPosition { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required CompletionTriggerKind Kind { get; init; }
        public string? TriggerCharacter { get; init; }
        public required int ProviderCount { get; init; }
        public int Answered { get; set; }
        /// <summary>The providers' answers as returned (never copied per item).</summary>
        public CompletionSource[] Sources { get; set; } = [];
        public int EntryCount => Sources.Sum(x => x.Items.Count);
        public bool Incomplete { get; set; }
        public List<FilteredCompletion> Filtered { get; set; } = [];
        public int Selected { get; set; }
        public Dictionary<CompletionItem, CompletionItem> Resolved { get; } = new(ReferenceEqualityComparer.Instance);
        public string LastRequestedPrefix { get; set; } = "";
        public bool PopupShown { get; set; }
        public bool Closed { get; set; }
    }

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly OverlayPopup _popup;
    private readonly ListBox _list;
    private readonly Border _docPanel;
    private readonly TextBlock _loading;
    private readonly Grid _content;
    private readonly object _filterKey = new();
    private Session? _session;
    private int _nextId;
    private CancellationTokenSource? _loadingTimer, _resolveTimer, _debounce;
    private bool _disposed;

    public CompletionFeature(CodeEditorControl view)
    {
        _v = view;
        _popup = new OverlayPopup(view, 640, 300);
        _list = new ListBox { MaxHeight = 280, MinWidth = 280, MaxWidth = 380, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), ItemTemplate = new FuncDataTemplate<FilteredCompletion>((f, _) => f is null ? null : BuildRow(f)) };
        _list.SelectionChanged += OnListSelectionChanged;
        Avalonia.Automation.AutomationProperties.SetName(_list, "Suggestions");
        _docPanel = new Border { MaxWidth = 300, MinWidth = 180, Padding = new Thickness(8, 6), IsVisible = false, BorderThickness = new Thickness(1, 0, 0, 0) };
        _loading = new TextBlock { Text = "Loading…", Margin = new Thickness(10, 4), FontStyle = FontStyle.Italic, FontSize = 12, IsVisible = false };
        _content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        var left = new StackPanel();
        left.Children.Add(_loading);
        left.Children.Add(_list);
        _content.Children.Add(left);
        Grid.SetColumn(_docPanel, 1);
        _content.Children.Add(_docPanel);
        ApplyTheme();

        _d.Add(view.PreTyping.Subscribe(OnPreTyping));
        if (view.Typed is { } typed) _d.Add(typed.Subscribe(OnTyped));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => OnCaretChanged()));
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        view.TextArea.LostFocus += OnLostFocus;
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            view.TextArea.LostFocus -= OnLostFocus;
        }));
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>One-line state for diagnostics and test failure messages.</summary>
    internal string DebugState => _session is { } s
        ? $"session#{s.Id} closed={s.Closed} entries={s.EntryCount} answered={s.Answered}/{s.ProviderCount} filtered={s.Filtered.Count} shown={s.PopupShown} start={(s.Start.IsDeleted ? -1 : s.Start.Offset)} caret={_v.Editor.CaretOffset} popupOpen={_popup.IsOpen}"
        : $"no session; popupOpen={_popup.IsOpen}";

    internal IReadOnlyList<string> VisibleLabels => _session?.Filtered.Select(f => f.Entry.Item.Label).ToList() ?? [];
    internal int SelectedIndex => _session?.Selected ?? -1;

    public void OnThemeChanged()
    {
        ApplyTheme();
        _popup.ApplyTheme();
    }

    public void OnSettingsChanged()
    {
        // settings are read per keystroke
    }

    private void ApplyTheme()
    {
        var p = _v.Palette;
        // The stock ListBoxItem template colours its selection for the application's light/dark variant, not for the editor theme: restyle it.
        _list.Styles.Clear();
        var item = new Style(x => x.OfType<ListBoxItem>());
        item.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(0)));
        item.Setters.Add(new Setter(ListBoxItem.MinHeightProperty, 22.0));
        var selected = new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>());
        selected.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, p.SuggestSelected));
        selected.Setters.Add(new Setter(Avalonia.Controls.Documents.TextElement.ForegroundProperty, p.SuggestSelectedForeground));
        var hover = new Style(x => x.OfType<ListBoxItem>().Class(":pointerover").Not(y => y.Class(":selected")).Template().OfType<ContentPresenter>());
        hover.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(Colors.White, 0.08)));
        _list.Styles.Add(item);
        _list.Styles.Add(selected);
        _list.Styles.Add(hover);
        _docPanel.BorderBrush = p.WidgetBorder;
        _loading.Foreground = p.DescriptionForeground;
        _list.Foreground = p.WidgetForeground;
        _list.FontFamily = _v.Editor.FontFamily;
        _list.FontSize = Math.Max(11, _v.Editor.FontSize - 1);
    }

    // ---- triggering ----

    public void Trigger() => Request(CompletionTriggerKind.Invoked, null);

    /// <summary>
    /// A commit character (e.g. '.' or '(') typed while an item is selected accepts it. This runs BEFORE the character is inserted, so the
    /// accepted text lands first and the editor then inserts the character after it — in the same undo group.
    /// </summary>
    private void OnPreTyping(string text)
    {
        var s = _session;
        if (_disposed || s is not { Closed: false } || text.Length != 1 || !_popup.IsOpen || s.Filtered.Count == 0) return;
        if (CommitCharsOf(s).Contains(text)) AcceptSelected();
    }

    private void OnTyped(string text)
    {
        if (_disposed || text.Length != 1 || _v.Document.IsReadOnly) return;
        var ch = text[0];
        var s = _session;
        if (!IsWordChar(ch)) _debounce?.Cancel(); // a pending "quick suggestion" was for the word that just ended
        if (s is { Closed: false })
        {
            if (IsWordChar(ch)) Refilter(s);
            else Close();
            // fall through: a trigger character typed while open starts a fresh request below
            if (s.Closed is false && _session == s) return;
        }

        var providers = _v.Services.Features.GetProviders<ICompletionProvider>(_v.Document);
        if (providers.Count == 0) return;
        var settings = _v.Settings;
        if (settings.SuggestOnTriggerCharacters && providers.Any(p => p.TriggerCharacters.Contains(text)))
        {
            Request(CompletionTriggerKind.TriggerCharacter, text);
            return;
        }
        if (IsWordChar(ch) && settings.QuickSuggestions != "off")
        {
            var caret = _v.Controller.Caret;
            var kind = _v.Tokens?.KindAt(caret.Line, caret.Character) ?? Theming.TokenKind.Other;
            if (kind is Theming.TokenKind.Comment or Theming.TokenKind.String) return; // VS Code default: no quick suggestions inside comments/strings
            DebouncedRequest(settings.QuickSuggestionsDelayMs);
        }
    }

    private void DebouncedRequest(int delayMs)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _v.Post(() =>
            {
                // the user kept typing: the newer keystroke has its own (re)scheduled request; and the word must still be there
                if (_session is null && !cts.IsCancellationRequested && InsideWord()) Request(CompletionTriggerKind.Invoked, null);
            });
        }, TaskScheduler.Default);
    }

    private void Request(CompletionTriggerKind kind, string? triggerCharacter)
    {
        if (_disposed) return;
        Close();
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<ICompletionProvider>(doc);
        if (providers.Count == 0) return;
        var caret = _v.Editor.CaretOffset;
        var start = kind == CompletionTriggerKind.TriggerCharacter ? caret : WordStart(caret);
        var anchor = doc.Buffer.CreateAnchor(start);
        anchor.MovementType = AnchorMovementType.BeforeInsertion;
        var session = new Session
        {
            Id = ++_nextId,
            Start = anchor,
            RequestPosition = doc.PositionAt(caret),
            Cts = new CancellationTokenSource(),
            Kind = kind,
            TriggerCharacter = triggerCharacter,
            ProviderCount = providers.Count,
        };
        _session = session;
        var context = new CompletionContext(kind, triggerCharacter);
        for (var order = 0; order < providers.Count; order++) Launch(session, providers[order], order, context);

        // a slow provider costs only this: after 300 ms a "loading…" line, nothing else
        _loadingTimer?.Cancel();
        _loadingTimer?.Dispose();
        var timer = _loadingTimer = new CancellationTokenSource();
        _ = Task.Delay(300, timer.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _v.Post(() =>
            {
                if (_session == session && !session.PopupShown && session.EntryCount == 0 && !session.Closed) ShowLoading(session);
            });
        }, TaskScheduler.Default);
    }

    private void Launch(Session s, ICompletionProvider provider, int order, CompletionContext context)
    {
        var doc = _v.Document;
        var position = s.RequestPosition;
        // Task.Run: a provider that does synchronous work before its first await must not do it on the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
                timeout.CancelAfter(ProviderTimeout);
                var list = await provider.ProvideCompletionAsync(doc, position, context, timeout.Token).ConfigureAwait(false);
                // the provider's list is kept as it is (50k items = one reference); nothing is copied per item
                var source = list is { Items.Count: > 0 } ? new CompletionSource(list.Items, provider, order) : null;
                var incomplete = list?.IsIncomplete ?? false;
                _v.Post(() => OnProviderResult(s, source, incomplete));
            }
            catch (OperationCanceledException)
            {
                _v.Post(() => OnProviderResult(s, null, false));
            }
            catch (Exception ex)
            {
                _v.Services.LoggerFactory.CreateLogger<CompletionFeature>().LogDebug(ex, "Completion provider failed");
                _v.Post(() => OnProviderResult(s, null, false));
            }
        });
    }

    private void OnProviderResult(Session s, CompletionSource? source, bool incomplete)
    {
        if (_session != s || s.Closed) return;
        s.Answered++;
        if (source is not null)
        {
            s.Sources = [.. s.Sources, source];
            s.Incomplete |= incomplete;
        }
        Refilter(s);
        if (s.Answered == s.ProviderCount && s.EntryCount == 0) Close(); // everyone answered with nothing
    }

    // ---- filtering (worker) → popup (UI) ----

    private void Refilter(Session s)
    {
        if (_session != s || s.Closed) return;
        var caret = _v.Editor.CaretOffset;
        var start = s.Start.Offset;
        if (caret < start || s.Start.IsDeleted)
        {
            Close();
            return;
        }
        var prefix = _v.Document.GetText(new TextRange(_v.Document.PositionAt(start), _v.Document.PositionAt(caret)));
        if (prefix.Any(c => !IsWordChar(c)) && s.Kind != CompletionTriggerKind.TriggerCharacter)
        {
            Close();
            return;
        }
        if (s.Incomplete && prefix != s.LastRequestedPrefix && s.Answered == s.ProviderCount) RequestMore(s, prefix);
        var sources = s.Sources;
        var caretPosition = _v.Document.PositionAt(caret);
        var lineText = _v.Document.GetLineText(caretPosition.Line);
        var lineBefore = lineText[..Math.Min(caretPosition.Character, lineText.Length)];
        _v.Factory.Worker.Post(WorkPriority.Interactive, _filterKey, ct =>
        {
            var filtered = CompletionFilter.Filter(sources, prefix, ct, lineBefore, caretPosition.Line);
            _v.Post(() => ApplyFiltered(s, prefix, sources, filtered));
        });
    }

    private void RequestMore(Session s, string prefix)
    {
        s.LastRequestedPrefix = prefix;
        s.Answered = 0;
        s.Sources = [];
        var context = new CompletionContext(CompletionTriggerKind.TriggerForIncompleteCompletions);
        var providers = _v.Services.Features.GetProviders<ICompletionProvider>(_v.Document);
        for (var order = 0; order < providers.Count; order++) Launch(s, providers[order], order, context);
    }

    private void ApplyFiltered(Session s, string prefix, CompletionSource[] computedFrom, List<FilteredCompletion> filtered)
    {
        if (_session != s || s.Closed || _disposed) return;
        // Computed from an older set of entries (a slower provider answered since): its result — above all an EMPTY one — is stale.
        // The merge queued a newer filter, so just drop this one.
        if (!ReferenceEquals(computedFrom, s.Sources)) return;
        // stale guard: the word may have changed while the worker was busy; a newer filter is queued for it
        var caret = _v.Editor.CaretOffset;
        if (caret < s.Start.Offset || _v.Document.GetText(new TextRange(_v.Document.PositionAt(s.Start.Offset), _v.Document.PositionAt(caret))) != prefix) return;

        s.Filtered = filtered;
        s.Selected = 0;
        if (filtered.Count == 0)
        {
            _popup.Hide();
            s.PopupShown = false;
            if (s.Answered == s.ProviderCount) Close();
            return;
        }
        _loading.IsVisible = false;
        _list.ItemsSource = filtered;
        _list.SelectedIndex = 0;
        s.PopupShown = true;
        var startPos = _v.Document.PositionAt(s.Start.Offset);
        _popup.ShowAt(_content, startPos);
        UpdateDocs(s);
    }

    private void ShowLoading(Session s)
    {
        _list.ItemsSource = null;
        _docPanel.IsVisible = false;
        _loading.IsVisible = true;
        s.PopupShown = true;
        _popup.ShowAt(_content, _v.Document.PositionAt(s.Start.Offset));
    }

    // ---- selection / docs / resolve ----

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RecolorMatches(e.RemovedItems, selected: false);
        RecolorMatches(e.AddedItems, selected: true);
        var s = _session;
        if (s is null || s.Closed || _list.SelectedIndex < 0) return;
        s.Selected = _list.SelectedIndex;
        UpdateDocs(s);
        _resolveTimer?.Cancel();
        _resolveTimer?.Dispose();
        var timer = _resolveTimer = new CancellationTokenSource();
        var entry = s.Filtered[s.Selected].Entry;
        _ = Task.Delay(120, timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = ResolveAsync(s, entry);
        }, TaskScheduler.Default);
    }

    /// <summary>The matched characters of the (de)selected rows switch between the normal and the focus highlight colour.</summary>
    private void RecolorMatches(System.Collections.IList items, bool selected)
    {
        var p = _v.Palette;
        foreach (var item in items.OfType<FilteredCompletion>())
        {
            if (_list.ContainerFromItem(item) is not Control container) continue;
            foreach (var grid in container.GetVisualDescendants().OfType<Grid>())
            {
                if (grid.Tag is not List<Run> runs) continue;
                foreach (var r in runs) r.Foreground = selected ? p.SuggestFocusHighlight : p.SuggestHighlight;
                break;
            }
        }
    }

    private async Task ResolveAsync(Session s, CompletionEntry entry)
    {
        if (s.Resolved.ContainsKey(entry.Item)) return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resolved = await Task.Run(async () => await entry.Provider.ResolveCompletionAsync(entry.Item, cts.Token).ConfigureAwait(false), cts.Token).ConfigureAwait(false);
            _v.Post(() =>
            {
                if (_session != s || s.Closed) return;
                s.Resolved[entry.Item] = resolved;
                if (s.Filtered.Count > s.Selected && ReferenceEquals(s.Filtered[s.Selected].Entry, entry)) UpdateDocs(s);
            });
        }
        catch (Exception)
        {
            // an unresolvable item is still insertable as it is
        }
    }

    private void UpdateDocs(Session s)
    {
        if (s.Filtered.Count == 0 || s.Selected >= s.Filtered.Count)
        {
            _docPanel.IsVisible = false;
            return;
        }
        var entry = s.Filtered[s.Selected].Entry;
        var item = s.Resolved.GetValueOrDefault(entry.Item) ?? entry.Item;
        if (item.Detail is null && item.Documentation is null)
        {
            _docPanel.IsVisible = false;
            return;
        }
        var style = new MarkdownStyle(_v.Editor.FontFamily, _v.Editor.FontFamily, Math.Max(11, _v.Editor.FontSize - 2), _v.Palette.WidgetForeground, _v.Palette.Link, _v.Palette.CodeBlockBackground, _v.Palette.DescriptionForeground);
        var stack = new StackPanel { Spacing = 6 };
        if (item.Detail is { Length: > 0 } detail)
            stack.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontFamily = _v.Editor.FontFamily, FontSize = style.FontSize, Foreground = _v.Palette.WidgetForeground });
        if (item.Documentation is { } doc)
        {
            var blocks = doc.IsMarkdown ? MarkdownParser.Parse(doc.Value) : [new MdParagraph([new MdText(doc.Value)])];
            stack.Children.Add(MarkdownView.Build(blocks, style, null, url => _ = _v.Services.Window?.OpenExternalAsync(new Uri(url, UriKind.RelativeOrAbsolute))));
        }
        _docPanel.Child = stack;
        _docPanel.IsVisible = true;
        // the popup grew or shrank: re-place it
        if (_popup.IsOpen) _popup.ShowAt(_content, _v.Document.PositionAt(s.Start.Offset));
    }

    // ---- keyboard ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var s = _session;
        if (s is null || s.Closed || !_popup.IsOpen) return;
        if (e.Key == Key.Escape)
        {
            Close(); // also while still "loading…": there are no items to navigate, but the user must be able to dismiss it
            e.Handled = true;
            return;
        }
        if (s.Filtered.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down: MoveSelection(1); e.Handled = true; break;
            case Key.Up: MoveSelection(-1); e.Handled = true; break;
            case Key.PageDown: MoveSelection(8); e.Handled = true; break;
            case Key.PageUp: MoveSelection(-8); e.Handled = true; break;
            case Key.Enter or Key.Tab when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                AcceptSelected();
                e.Handled = true;
                break;
        }
    }

    internal void MoveSelection(int delta)
    {
        var s = _session;
        if (s is null) return;
        var i = Math.Clamp(s.Selected + delta, 0, s.Filtered.Count - 1);
        _list.SelectedIndex = i;
        _list.ScrollIntoView(i);
    }

    private void OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Focus moved into our own popup (mouse click on a row) must not close it; anywhere else does.
        if (!_popup.Root.IsPointerOver) Close();
    }

    private void OnCaretChanged()
    {
        var s = _session;
        if (s is null || s.Closed) return;
        var caret = _v.Editor.CaretOffset;
        if (s.Start.IsDeleted || caret < s.Start.Offset) Close();
        else if (s.PopupShown || s.EntryCount > 0) Refilter(s);
    }

    // ---- accepting ----

    internal void AcceptSelected()
    {
        var s = _session;
        if (s is null || s.Closed || s.Filtered.Count == 0) return;
        var entry = s.Filtered[Math.Clamp(s.Selected, 0, s.Filtered.Count - 1)].Entry;
        var item = s.Resolved.GetValueOrDefault(entry.Item) ?? entry.Item;
        var requestPosition = s.RequestPosition;
        var start = s.Start.Offset;
        Close();
        Accept(item, start, requestPosition);
    }

    private void Accept(CompletionItem item, int wordStart, TextPosition requestPosition)
    {
        var doc = _v.Document;
        if (doc.IsReadOnly) return;
        var caretEnd = _v.Editor.CaretOffset;
        var start = wordStart;
        var end = caretEnd;
        string text = item.InsertText ?? item.Label;
        if (item.TextEdit is { } te && te.Range.Start.Line == te.Range.End.Line)
        {
            start = doc.OffsetAt(te.Range.Start);
            end = doc.OffsetAt(te.Range.End);
            if (te.Range.End == requestPosition || end < caretEnd && doc.PositionAt(end).Line == doc.PositionAt(caretEnd).Line && end >= wordStart) end = Math.Max(end, caretEnd);
            text = te.NewText;
        }
        start = Math.Clamp(start, 0, doc.Length);
        end = Math.Clamp(Math.Max(start, end), start, doc.Length);

        var additional = item.AdditionalTextEdits ?? [];
        var mainRange = new TextRange(doc.PositionAt(start), doc.PositionAt(end));
        var overlaps = additional.Any(a => a.Range.Start < mainRange.End && a.Range.End > mainRange.Start);
        if (overlaps) additional = [];

        var startAnchor = doc.Buffer.CreateAnchor(start);
        startAnchor.MovementType = AnchorMovementType.BeforeInsertion;
        var endAnchor = doc.Buffer.CreateAnchor(end);
        endAnchor.MovementType = AnchorMovementType.AfterInsertion;

        if (item.InsertTextFormat == InsertTextFormat.Snippet && _v.MultiCursor.IsActive && additional.Count == 0 && _v.Feature<SnippetFeature>() is { } multiSnippets)
        {
            // several carets: the snippet replaces the same prefix and suffix around each caret (VS Code's rule), all of it ONE undo step, and its
            // session has a tab-stop instance per caret
            var prefix = caretEnd - start;
            var suffix = end - caretEnd;
            var buffer = doc.Buffer;
            _ = multiSnippets.InsertAtCaretsAsync(text, c => (Math.Max(buffer.GetLineByOffset(c.Start).Offset, c.Start - prefix), Math.Min(buffer.GetLineByOffset(c.End).EndOffset, c.End + suffix)));
        }
        else if (item.InsertTextFormat == InsertTextFormat.Snippet)
        {
            // remove the typed word (plus any additional edits) in one step, then let the snippet session own the insertion
            var edits = new List<TextEdit>(additional) { TextEdit.Delete(mainRange) };
            doc.ApplyEdits(edits);
            var at = doc.PositionAt(startAnchor.Offset);
            if (_v.Feature<SnippetFeature>() is { } snippets) _ = snippets.InsertAsync(text, new TextRange(at, at));
            else doc.ApplyEdits([TextEdit.Insert(at, text)]);
        }
        else if (_v.MultiCursor.IsActive && additional.Count == 0)
        {
            // several carets: the same replacement around each (VS Code: the typed prefix and suffix lengths at the primary)
            _v.MultiCursor.ReplaceAroundCarets(caretEnd - start, end - caretEnd, text);
        }
        else
        {
            var edits = new List<TextEdit>(additional) { new(mainRange, text) };
            doc.ApplyEdits(edits);
            _v.SetSelection(TextRange.At(doc.PositionAt(startAnchor.Offset + text.Length).Line, doc.PositionAt(startAnchor.Offset + text.Length).Character));
        }
        if (item.Command is { } cmd && _v.Services.Commands is { } commands)
            _ = commands.ExecuteAsync(cmd.CommandId, (cmd.Arguments ?? []).Select(a => (object?)a).ToArray()).AsTask();
        _v.Editor.TextArea.Focus();
    }

    private static IReadOnlyList<string> CommitCharsOf(Session s) =>
        s.Filtered.Count == 0 ? [] : s.Filtered[Math.Clamp(s.Selected, 0, s.Filtered.Count - 1)].Entry.Item.CommitCharacters ?? [];

    public void Close()
    {
        _loadingTimer?.Cancel();
        _resolveTimer?.Cancel();
        _debounce?.Cancel();
        var s = _session;
        _session = null;
        if (s is not null)
        {
            s.Closed = true;
            s.Cts.Cancel(); // supersede: providers still working see their token cancelled
        }
        _list.ItemsSource = null;
        _docPanel.Child = null;
        _popup.Hide();
    }

    // ---- rendering a row ----

    private Control BuildRow(FilteredCompletion f)
    {
        var item = f.Entry.Item;
        var p = _v.Palette;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), Height = 22, Margin = new Thickness(2, 0) };
        grid.Children.Add(KindBadge(item.Kind));
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 6, 0) };
        var text = item.Label;
        var highlight = f.Positions.ToHashSet();
        var run = new System.Text.StringBuilder();
        var runHighlighted = false;
        var matchRuns = new List<Run>();
        var rowSelected = _session is { } cur && cur.Filtered.Count > cur.Selected && ReferenceEquals(cur.Filtered[cur.Selected], f);
        void FlushRun()
        {
            if (run.Length == 0) return;
            var r = new Run(run.ToString());
            if (runHighlighted)
            {
                // on the selected row the highlight must contrast with the selection colour (blue on blue is invisible): VS Code's "focus highlight"
                r.Foreground = rowSelected ? p.SuggestFocusHighlight : p.SuggestHighlight;
                r.FontWeight = FontWeight.Bold;
                matchRuns.Add(r);
            }
            if (item.Deprecated) r.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
            label.Inlines!.Add(r);
            run.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var h = highlight.Contains(i);
            if (h != runHighlighted)
            {
                FlushRun();
                runHighlighted = h;
            }
            run.Append(text[i]);
        }
        FlushRun();
        grid.Tag = matchRuns;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var detail = item.LabelDetail ?? item.LabelDescription ?? (item.Detail is { Length: <= 40 } d ? d : null);
        if (detail is not null)
        {
            var dt = new TextBlock { Text = detail, FontSize = Math.Max(10, _v.Editor.FontSize - 3), Foreground = p.DescriptionForeground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(dt, 2);
            grid.Children.Add(dt);
        }
        return grid;
    }

    /// <summary>A small coloured badge with a one/two-letter glyph per kind (generated: no icon font or third-party artwork is shipped).</summary>
    private static Control KindBadge(CompletionItemKind kind)
    {
        var (glyph, color) = kind switch
        {
            CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => ("ƒ", 0xB180D7),
            CompletionItemKind.Field or CompletionItemKind.Property => ("◆", 0x75BEFF),
            CompletionItemKind.Variable => ("x", 0x75BEFF),
            CompletionItemKind.Class or CompletionItemKind.Struct => ("C", 0xEE9D28),
            CompletionItemKind.Interface => ("I", 0x75BEFF),
            CompletionItemKind.Module => ("{}", 0xCCCCCC),
            CompletionItemKind.Enum or CompletionItemKind.EnumMember => ("E", 0xEE9D28),
            CompletionItemKind.Keyword or CompletionItemKind.Operator => ("k", 0xCCCCCC),
            CompletionItemKind.Snippet => ("▤", 0xCCCCCC),
            CompletionItemKind.Constant => ("π", 0x75BEFF),
            CompletionItemKind.TypeParameter => ("T", 0xEE9D28),
            CompletionItemKind.Event => ("↯", 0xEE9D28),
            CompletionItemKind.Color => ("●", 0xCCCCCC),
            CompletionItemKind.File or CompletionItemKind.Folder or CompletionItemKind.Reference => ("▭", 0xCCCCCC),
            CompletionItemKind.Unit or CompletionItemKind.Value => ("#", 0x75BEFF),
            _ => ("a", 0xCCCCCC),
        };
        var c = Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        return new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(c, 0.22),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = glyph, Foreground = new SolidColorBrush(c), FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    // ---- helpers ----

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private bool InsideWord()
    {
        var caret = _v.Editor.CaretOffset;
        return caret > 0 && IsWordChar(_v.Editor.Document.GetCharAt(caret - 1));
    }

    private int WordStart(int offset)
    {
        var doc = _v.Editor.Document;
        var i = offset;
        while (i > 0 && IsWordChar(doc.GetCharAt(i - 1))) i--;
        return i;
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        Close();
        _list.SelectionChanged -= OnListSelectionChanged;
        _popup.Dispose();
        _d.Dispose();
    }
}
