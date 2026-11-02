using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Markdown;
using CsCode.Editor.Threading;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// Hover for one view: after the pointer dwells on a position (<c>editor.hover.delay</c>, 300 ms) the hover providers are asked — off the UI
/// thread, in parallel, with a deadline — and the answers are merged with the diagnostics under the pointer, the debugger's value for the
/// expression (while stopped) and the hidden text of a collapsed region. Markdown is parsed and fenced code is TextMate-coloured on the editor
/// worker; the UI only builds controls. Results for a position the pointer has left, or a document version that has moved on, are dropped.
/// </summary>
internal sealed class HoverFeature : IThemeAware, IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1500);

    private readonly CodeEditorControl _v;
    private readonly OverlayPopup _popup;
    private readonly DisposableStore _d = new();
    private readonly object _key = new();
    private CancellationTokenSource? _dwell, _hide, _request;
    private (int Start, int End) _word = (-1, -1);
    private bool _disposed;

    public HoverFeature(CodeEditorControl view)
    {
        _v = view;
        _popup = new OverlayPopup(view, 580, 300);
        Avalonia.Automation.AutomationProperties.SetName(_popup.Root, "Hover");
        view.Editor.PointerMoved += OnPointerMoved;
        view.Editor.PointerExited += OnPointerExited;
        view.TextView.ScrollOffsetChanged += OnScrolled;
        view.TextArea.KeyDown += OnKeyDown;
        _popup.Root.PointerEntered += OnPopupEntered;
        _popup.Root.PointerExited += OnPopupExited;
        _d.Add(view.Document.Changed.Subscribe(_ => Hide()));
        _d.Add(Disposable.Create(() =>
        {
            view.Editor.PointerMoved -= OnPointerMoved;
            view.Editor.PointerExited -= OnPointerExited;
            view.TextView.ScrollOffsetChanged -= OnScrolled;
            view.TextArea.KeyDown -= OnKeyDown;
            _popup.Root.PointerEntered -= OnPopupEntered;
            _popup.Root.PointerExited -= OnPopupExited;
        }));
    }

    public bool IsOpen => _popup.IsOpen;
    internal Control PopupRoot => _popup.Root;

    /// <summary>Shows the hover for the position at <paramref name="offset"/> right away (what the dwell timer does after the delay) — for tests and commands.</summary>
    internal Task HoverAtAsync(int offset)
    {
        var word = WordAt(offset);
        _word = word;
        return ShowAsync(offset, word);
    }

    public void OnThemeChanged() => _popup.ApplyTheme();

    // ---- pointer tracking ----

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_disposed) return;
        var pos = _v.Editor.GetPositionFromPoint(e.GetPosition(_v.Editor));
        if (pos is not { } p || p.IsAtEndOfLine || p.Line < 1 || p.Line > _v.Document.LineCount)
        {
            CancelDwell();
            ScheduleHide();
            return;
        }
        var doc = _v.Document;
        var offset = doc.OffsetAt(new TextPosition(p.Line - 1, p.Column - 1));
        var word = WordAt(offset);
        if (word == _word) return; // still over the same word: keep a pending dwell / the open popup
        _word = word;
        Hide();
        CancelHide();
        _dwell?.Cancel();
        _dwell?.Dispose();
        var cts = _dwell = new CancellationTokenSource();
        _ = Task.Delay(Math.Max(0, _v.Settings.HoverDelayMs), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _v.Post(() =>
            {
                if (!cts.IsCancellationRequested && _word == word) _ = ShowAsync(offset, word);
            });
        }, TaskScheduler.Default);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        CancelDwell();
        _word = (-1, -1);
        ScheduleHide();
    }

    private void OnPopupEntered(object? sender, PointerEventArgs e) => CancelHide();

    private void OnPopupExited(object? sender, PointerEventArgs e) => ScheduleHide();

    private void OnScrolled(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object? sender, KeyEventArgs e) => Hide();

    private void CancelDwell()
    {
        _dwell?.Cancel();
        _request?.Cancel();
    }

    private void ScheduleHide()
    {
        if (!_popup.IsOpen) return;
        _hide?.Cancel();
        _hide?.Dispose();
        var cts = _hide = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!_popup.Root.IsPointerOver) Hide();
            });
        }, TaskScheduler.Default);
    }

    private void CancelHide() => _hide?.Cancel();

    public void Hide()
    {
        _request?.Cancel();
        _popup.Hide();
    }

    private (int Start, int End) WordAt(int offset)
    {
        var doc = _v.Editor.Document;
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (offset >= doc.TextLength || !IsWord(doc.GetCharAt(offset))) return (offset, offset);
        var a = offset;
        var b = offset;
        while (a > 0 && IsWord(doc.GetCharAt(a - 1))) a--;
        while (b < doc.TextLength && IsWord(doc.GetCharAt(b))) b++;
        return (a, b);
    }

    // ---- gathering ----

    internal async Task ShowAsync(int offset, (int Start, int End) word)
    {
        if (_disposed) return;
        _request?.Cancel();
        var cts = _request = new CancellationTokenSource();
        var doc = _v.Document;
        var version = doc.Version;
        var position = doc.PositionAt(offset);
        var providers = _v.Services.Features.GetProviders<IHoverProvider>(doc);
        var diagnostics = _v.Feature<DiagnosticsFeature>()?.At(offset) ?? [];
        var folded = _v.Feature<FoldingFeature>()?.FoldedTextAt(offset);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            deadline.CancelAfter(Deadline);
            var hoverTasks = providers.Select(p => Task.Run(async () =>
            {
                try
                {
                    return await p.ProvideHoverAsync(doc, position, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _v.Services.LoggerFactory.CreateLogger<HoverFeature>().LogDebug(ex, "Hover provider failed");
                    return null;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, deadline.Token)).ToList();
            var debugTask = EvaluateAsync(doc, offset, word, deadline.Token);

            var hovers = new List<Hover>();
            foreach (var t in hoverTasks)
            {
                try
                {
                    if (await t.ConfigureAwait(true) is { } h) hovers.Add(h);
                }
                catch (OperationCanceledException)
                {
                    // deadline: the provider took too long; the rest is still shown
                }
            }
            var evaluated = await debugTask.ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed || _word != word || doc.Version != version) return; // stale: never show

            var contents = hovers.SelectMany(h => h.Contents).Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
            if (contents.Count == 0 && diagnostics.Count == 0 && evaluated is null && folded is null) return;

            var prepared = await PrepareAsync(contents, folded, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed || _word != word || doc.Version != version) return;
            Present(offset, diagnostics, prepared, evaluated);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer hover
        }
    }

    /// <param name="Trusted">Per content: <see cref="MarkupContent.IsTrusted"/> (its <c>command:</c> links run commands).</param>
    private sealed record Prepared(IReadOnlyList<IReadOnlyList<MdBlock>> Contents, IReadOnlyDictionary<MdCodeBlock, IReadOnlyList<CodeSpan>> Spans, MdCodeBlock? Folded,
        IReadOnlyList<bool> Trusted);

    /// <summary>Markdown parsing + code colouring on the editor worker (the UI only builds controls from the result).</summary>
    private async Task<Prepared> PrepareAsync(List<MarkupContent> contents, string? folded, CancellationToken ct)
    {
        var language = _v.Document.LanguageId;
        var factory = _v.Factory;
        var theme = factory.Themes.Current;
        var fg = factory.Themes.GetColor("editor.foreground");
        return await factory.Worker.RunAsync(WorkPriority.Interactive, token =>
        {
            var parsed = contents.Select(c => c.IsMarkdown ? MarkdownParser.Parse(c.Value) : [new MdParagraph([new MdText(c.Value)])]).ToList();
            var foldedBlock = folded is null ? null : new MdCodeBlock(language, folded);
            var all = parsed.SelectMany(p => p).ToList();
            if (foldedBlock is not null) all.Add(foldedBlock);
            var spans = CodeHighlighter.HighlightBlocks(all, factory.Tokens, factory.Languages, theme, fg, language, token);
            return new Prepared(parsed, spans, foldedBlock, [.. contents.Select(c => c.IsTrusted)]);
        }, ct).ConfigureAwait(true);
    }

    private async Task<(string Expression, DebugEvaluateResult Result)?> EvaluateAsync(EditorDocument doc, int offset, (int Start, int End) word, CancellationToken ct)
    {
        var debug = _v.Services.Debug;
        if (debug is not { State: DebugState.Stopped, ActiveSession: { } session } || debug.StopLocation is not { } stop) return null;
        string expression;
        var sel = _v.Editor.SelectionLength > 0 ? (Start: _v.Editor.SelectionStart, End: _v.Editor.SelectionStart + _v.Editor.SelectionLength) : (Start: -1, End: -1);
        if (sel.Start >= 0 && offset >= sel.Start && offset <= sel.End) expression = doc.GetText(new TextRange(doc.PositionAt(sel.Start), doc.PositionAt(sel.End)));
        else if (word.End > word.Start) expression = doc.GetText(new TextRange(doc.PositionAt(word.Start), doc.PositionAt(word.End)));
        else return null;
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 200) return null;
        try
        {
            var result = await Task.Run(async () => await session.EvaluateAsync(expression, stop.FrameId, "hover", ct).ConfigureAwait(false), ct).ConfigureAwait(true);
            return (expression, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // not evaluable (a keyword, a type name…): no tooltip
        }
    }

    // ---- presenting ----

    private void Present(int offset, IReadOnlyList<Diagnostic> diagnostics, Prepared prepared, (string Expression, DebugEvaluateResult Result)? evaluated)
    {
        var p = _v.Palette;
        var style = new MarkdownStyle(_v.Editor.FontFamily, _v.Editor.FontFamily, Math.Max(11, _v.Editor.FontSize - 1), p.WidgetForeground, p.Link, p.CodeBlockBackground, p.DescriptionForeground);
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(10, 6) };

        foreach (var d in diagnostics.Take(5))
        {
            var row = new StackPanel { Spacing = 2 };
            var color = d.Severity switch { DiagnosticSeverity.Error => p.Error, DiagnosticSeverity.Warning => p.Warning, DiagnosticSeverity.Information => p.Info, _ => p.Hint };
            row.Children.Add(new TextBlock { Text = d.Message, TextWrapping = TextWrapping.Wrap, Foreground = p.WidgetForeground, FontFamily = style.Body, FontSize = style.FontSize });
            var meta = string.Join(" ", new[] { d.Source, d.Code is null ? null : $"({d.Code})" }.Where(s => s is not null));
            if (meta.Length > 0) row.Children.Add(new TextBlock { Text = meta, Foreground = p.DescriptionForeground, FontSize = style.FontSize - 1, FontFamily = style.Body });
            var link = new TextBlock { Text = "Quick Fix…", Foreground = p.Link, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand), FontSize = style.FontSize - 1, FontFamily = style.Body };
            link.PointerPressed += (_, e) =>
            {
                Hide();
                _v.Controller.SetSelection(TextRange.At(_v.PositionOf(offset).Line, _v.PositionOf(offset).Character));
                _ = _v.Feature<ICodeActionFeature>()?.ShowQuickFixAsync();
                e.Handled = true;
            };
            row.Children.Add(link);
            stack.Children.Add(new Border { BorderBrush = color.Brush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = row });
        }

        if (evaluated is { } ev)
        {
            var text = new TextBlock { FontFamily = style.Mono, FontSize = style.FontSize, Foreground = p.WidgetForeground, TextWrapping = TextWrapping.Wrap };
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(ev.Expression) { Foreground = p.SuggestHighlight });
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(" = " + ev.Result.Result));
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(text);
            if (ev.Result.Type is { Length: > 0 } type) box.Children.Add(new TextBlock { Text = type, Foreground = p.DescriptionForeground, FontSize = style.FontSize - 1, FontFamily = style.Body });
            stack.Children.Add(box);
        }

        for (var i = 0; i < prepared.Contents.Count; i++)
        {
            if (i > 0 || stack.Children.Count > 0) stack.Children.Add(new Border { Height = 1, Background = p.WidgetBorder, Opacity = 0.7 });
            stack.Children.Add(MarkdownView.Build(prepared.Contents[i], style, prepared.Spans, prepared.Trusted[i] ? OpenTrustedLink : OpenLink));
        }
        if (prepared.Folded is { } foldedBlock) stack.Children.Add(MarkdownView.Build([foldedBlock], style, prepared.Spans, null));

        var scroll = new ScrollViewer { Content = stack, MaxHeight = 280, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _popup.ShowAt(scroll, _v.PositionOf(offset));
    }

    /// <summary>
    /// A link of trusted content (<see cref="MarkupContent.IsTrusted"/>: CsCode's own, such as rust-analyzer's hover actions): a
    /// <c>command:id?&lt;URI-encoded JSON&gt;</c> link runs the command with those arguments (an array spreads into several) and closes the hover,
    /// as in VS Code; any other link opens as usual.
    /// </summary>
    internal void OpenTrustedLink(string url)
    {
        if (!url.StartsWith("command:", StringComparison.Ordinal) || _v.Services.Commands is not { } commands)
        {
            OpenLink(url);
            return;
        }
        var rest = url["command:".Length..];
        var q = rest.IndexOf('?');
        var id = q < 0 ? rest : rest[..q];
        object?[] args = [];
        if (q >= 0 && q + 1 < rest.Length)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(Uri.UnescapeDataString(rest[(q + 1)..]));
                var root = json.RootElement;
                args = root.ValueKind == System.Text.Json.JsonValueKind.Array ? [.. root.EnumerateArray().Select(a => (object?)a.Clone())] : [root.Clone()];
            }
            catch (System.Text.Json.JsonException) { /* not JSON: the command runs without arguments */ }
        }
        _popup.Hide();
        if (!commands.Has(id))
        {
            FeatureSupport.Status(_v, $"The command '{id}' is not available");
            return;
        }
        _ = RunAsync();

        async Task RunAsync()
        {
            try { await commands.ExecuteAsync(id, args).ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { FeatureSupport.Status(_v, $"'{id}' failed: {ex.Message}"); }
        }
    }

    internal void OpenLink(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" or "command") _ = _v.Services.Window?.OpenExternalAsync(uri);
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _dwell?.Cancel();
        _hide?.Cancel();
        _request?.Cancel();
        _d.Dispose();
        _popup.Dispose();
    }
}
