using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Markdown;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Parameter hints: triggered by the providers' trigger characters (typically <c>(</c> and <c>,</c>) or Ctrl+Shift+Space, retriggered on the retrigger
/// characters (<c>,</c> and <c>)</c>), with the active parameter bold and ↑/↓ cycling through overloads. Requests run off the UI thread and are
/// latest-wins: an answer for a document version that has moved on is re-requested (bounded), never shown.
/// </summary>
internal sealed class SignatureFeature : ISignatureFeature, IThemeAware, IDisposable
{
    private const int MaxRetries = 2;

    private readonly CodeEditorControl _v;
    private readonly OverlayPopup _popup;
    private readonly DisposableStore _d = new();
    private CancellationTokenSource? _cts;
    private SignatureHelp? _help;
    private int _active;
    private TextPosition _openedAt;
    private bool _disposed;

    public SignatureFeature(CodeEditorControl view)
    {
        _v = view;
        _popup = new OverlayPopup(view, 640, 220);
        if (view.Typed is { } typed) _d.Add(typed.Subscribe(OnTyped));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => OnCaretChanged()));
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        view.TextArea.LostFocus += OnLostFocus;
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            view.TextArea.LostFocus -= OnLostFocus;
        }));
    }

    public bool IsOpen => _popup.IsOpen;
    internal Control PopupRoot => _popup.Root;
    internal SignatureHelp? Current => _help;
    internal int ActiveSignature => _active;

    public void OnThemeChanged() => _popup.ApplyTheme();

    public void Trigger() => _ = RequestAsync(SignatureHelpTriggerKind.Invoked, null, retrigger: _help is not null, attempt: 0);

    private void OnTyped(string text)
    {
        if (_disposed || text.Length != 1 || _v.Document.IsReadOnly) return;
        var providers = _v.Services.Features.GetProviders<ISignatureHelpProvider>(_v.Document);
        if (providers.Count == 0) return;
        var open = _help is not null && _popup.IsOpen;
        if (open && providers.Any(p => p.RetriggerCharacters.Contains(text) || p.TriggerCharacters.Contains(text)))
            _ = RequestAsync(SignatureHelpTriggerKind.TriggerCharacter, text, retrigger: true, attempt: 0);
        else if (!open && providers.Any(p => p.TriggerCharacters.Contains(text)))
            _ = RequestAsync(SignatureHelpTriggerKind.TriggerCharacter, text, retrigger: false, attempt: 0);
    }

    private void OnCaretChanged()
    {
        if (_disposed || !_popup.IsOpen) return;
        var caret = _v.Controller.Caret;
        // moving off the line of the call (or before where it was opened) leaves the call: close rather than show hints for something else
        if (caret.Line != _openedAt.Line || caret < _openedAt) Close();
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_v.TextArea.IsKeyboardFocusWithin) Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || !_popup.IsOpen || _help is null) return;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Up when _help.Signatures.Count > 1 && e.KeyModifiers == KeyModifiers.None:
                Cycle(-1);
                e.Handled = true;
                break;
            case Key.Down when _help.Signatures.Count > 1 && e.KeyModifiers == KeyModifiers.None:
                Cycle(+1);
                e.Handled = true;
                break;
        }
    }

    private void Cycle(int delta)
    {
        if (_help is null) return;
        var n = _help.Signatures.Count;
        _active = ((_active + delta) % n + n) % n;
        Render();
    }

    private async Task RequestAsync(SignatureHelpTriggerKind kind, string? trigger, bool retrigger, int attempt)
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<ISignatureHelpProvider>(doc);
        if (providers.Count == 0) return;
        var version = doc.Version;
        var position = _v.Controller.Caret;
        var context = new SignatureHelpContext(kind, trigger, retrigger, _help);

        // best provider first; the first non-empty answer in provider order wins (an unanswered/slow provider never blocks a faster better one for long: 3 s deadline)
        var calls = providers.Select(p => FeatureSupport.CallAsync(_v, ct => p.ProvideSignatureHelpAsync(doc, position, context, ct), cts.Token, TimeSpan.FromSeconds(3))).ToList();
        SignatureHelp? result = null;
        foreach (var call in calls)
        {
            var r = await call.ConfigureAwait(true);
            if (r is { Signatures.Count: > 0 })
            {
                result = r;
                break;
            }
        }
        if (_disposed || cts.IsCancellationRequested) return;
        if (doc.Version != version)
        {
            // typed on while we waited: the answer is for older text. Ask again for the current text (bounded), never show the old answer.
            if (attempt < MaxRetries) _ = RequestAsync(kind, trigger, retrigger, attempt + 1);
            return;
        }
        if (result is null)
        {
            Close();
            return;
        }
        _help = result;
        _active = Math.Clamp(result.ActiveSignature, 0, result.Signatures.Count - 1);
        if (!_popup.IsOpen) _openedAt = position;
        Render();
    }

    private void Render()
    {
        if (_help is null) return;
        var p = _v.Palette;
        var sig = _help.Signatures[_active];
        var activeParam = sig.ActiveParameter ?? _help.ActiveParameter ?? 0;
        var mono = _v.Editor.FontFamily;
        var size = Math.Max(11, _v.Editor.FontSize - 1);

        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = mono, FontSize = size, Foreground = p.WidgetForeground };
        AddLabelRuns(label, sig, activeParam, p);

        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(10, 6) };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        if (_help.Signatures.Count > 1)
        {
            header.Children.Add(new TextBlock { Text = $"↑ {_active + 1}/{_help.Signatures.Count} ↓", Foreground = p.DescriptionForeground, FontSize = size - 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top });
        }
        header.Children.Add(label);
        stack.Children.Add(header);

        var style = new MarkdownStyle(_v.Editor.FontFamily, _v.Editor.FontFamily, size, p.WidgetForeground, p.Link, p.CodeBlockBackground, p.DescriptionForeground);
        if (sig.Parameters is { } ps && activeParam >= 0 && activeParam < ps.Count && ps[activeParam].Documentation is { Value.Length: > 0 } paramDoc)
            stack.Children.Add(DocBlock(paramDoc, style));
        if (sig.Documentation is { Value.Length: > 0 } doc)
        {
            stack.Children.Add(new Border { Height = 1, Background = p.WidgetBorder, Opacity = 0.6 });
            stack.Children.Add(DocBlock(doc, style));
        }
        _popup.ShowAt(stack, _v.Controller.Caret);
    }

    private static Control DocBlock(MarkupContent content, MarkdownStyle style)
    {
        var text = content.Value.Length > 2000 ? content.Value[..2000] + "…" : content.Value;
        var blocks = content.IsMarkdown ? MarkdownParser.Parse(text) : [new MdParagraph([new MdText(text)])];
        return MarkdownView.Build(blocks, style, new Dictionary<MdCodeBlock, IReadOnlyList<CodeSpan>>(), null);
    }

    /// <summary>The signature label, with the active parameter bold and in the highlight colour (by the provider's label range, else by finding its text).</summary>
    private static void AddLabelRuns(TextBlock block, SignatureInformation sig, int activeParam, EditorPalette p)
    {
        var label = sig.Label;
        (int Start, int End)? range = null;
        if (sig.Parameters is { } ps && activeParam >= 0 && activeParam < ps.Count)
        {
            var param = ps[activeParam];
            if (param.LabelRange is { } r && r.Start >= 0 && r.End <= label.Length && r.End > r.Start) range = r;
            else if (param.Label.Length > 0)
            {
                var open = label.IndexOf('(');
                var at = label.IndexOf(param.Label, Math.Max(0, open), StringComparison.Ordinal);
                if (at >= 0) range = (at, at + param.Label.Length);
            }
        }
        var inlines = block.Inlines!;
        if (range is not { } active)
        {
            inlines.Add(new Run(label));
            return;
        }
        if (active.Start > 0) inlines.Add(new Run(label[..active.Start]));
        inlines.Add(new Run(label[active.Start..active.End]) { FontWeight = FontWeight.Bold, Foreground = p.SuggestHighlight, TextDecorations = TextDecorations.Underline });
        if (active.End < label.Length) inlines.Add(new Run(label[active.End..]));
    }

    public void Close()
    {
        _cts?.Cancel();
        _help = null;
        _popup.Hide();
    }

    public void Dispose()
    {
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _d.Dispose();
        _popup.Dispose();
    }
}
