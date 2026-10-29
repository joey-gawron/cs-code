using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Document / selection formatting (Shift+Alt+F, Ctrl+K Ctrl+F), on-type formatting after a trigger character and on-paste formatting (both
/// setting-gated: <c>editor.formatOnType</c>, <c>editor.formatOnPaste</c>). Format on save is part of the document's save pipeline
/// (<c>editor.formatOnSave</c>). The provider runs off the UI thread; edits are applied as ONE undo unit and only when the text is still the
/// version they were computed for — a stale format result is dropped (and the user told), never mapped onto changed text.
/// </summary>
internal sealed class FormattingFeature : IFormattingFeature, IDisposable
{
    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private (int Start, int Length) _lastInsert;
    private bool _disposed;

    public FormattingFeature(CodeEditorControl view)
    {
        _v = view;
        view.Controller.FormatHandler = () => _ = FormatDocumentAsync();
        if (view.Typed is { } typed) _d.Add(typed.Subscribe(OnTyped));
        _d.Add(view.Document.Changed.Subscribe(e =>
        {
            if (e.Changes.Count == 1) _lastInsert = (e.Changes[0].RangeOffset, e.Changes[0].Text.Length);
        }));
        view.TextArea.TextPasted += OnPasted;
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.TextPasted -= OnPasted;
            view.Controller.FormatHandler = null;
        }));
    }

    internal FormattingOptions Options => new(_v.Settings.TabSize, _v.Settings.InsertSpaces);

    public async Task FormatDocumentAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc);
        if (providers.Count == 0)
        {
            FeatureSupport.Status(_v, $"There is no formatter for '{doc.LanguageId}' files installed");
            return;
        }
        var version = doc.Version;
        var options = Options;
        var edits = await FirstResultAsync(providers, (p, ct) => p.FormatDocumentAsync(doc, options, ct), TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        await ApplyAsync(edits, version).ConfigureAwait(true);
    }

    public async Task FormatSelectionAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var selection = FeatureSupport.SelectionOrCaret(_v);
        if (selection.IsEmpty)
        {
            await FormatDocumentAsync().ConfigureAwait(true);
            return;
        }
        await FormatRangeAsync(selection, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
    }

    private async Task FormatRangeAsync(TextRange range, TimeSpan timeout)
    {
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc);
        if (providers.Count == 0) return;
        var version = doc.Version;
        var options = Options;
        var edits = await FirstResultAsync(providers, (p, ct) => p.FormatRangeAsync(doc, range, options, ct), timeout).ConfigureAwait(true);
        await ApplyAsync(edits, version).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<TextEdit>?> FirstResultAsync(IReadOnlyList<IDocumentFormattingProvider> providers,
        Func<IDocumentFormattingProvider, CancellationToken, ValueTask<IReadOnlyList<TextEdit>?>> call, TimeSpan timeout)
    {
        var calls = providers.Select(p => FeatureSupport.CallAsync(_v, ct => call(p, ct), CancellationToken.None, timeout)).ToList();
        foreach (var c in calls)
            if (await c.ConfigureAwait(true) is { Count: > 0 } edits) return edits;
        return null;
    }

    private Task ApplyAsync(IReadOnlyList<TextEdit>? edits, int version)
    {
        if (_disposed || edits is not { Count: > 0 }) return Task.CompletedTask;
        if (_v.Document.Version != version)
        {
            FeatureSupport.Status(_v, "The document changed while formatting; the result was discarded");
            return Task.CompletedTask;
        }
        // ApplyEdits clamps every position to the text and applies the lot as ONE undo unit; the caret (an anchor in the buffer) follows the edits
        _v.Document.ApplyEdits(edits.ToList());
        return Task.CompletedTask;
    }

    // ---- on type / on paste ----

    private void OnTyped(string text)
    {
        if (_disposed || text.Length != 1 || !_v.Settings.FormatOnType || _v.Document.IsReadOnly) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc).Where(p => p.OnTypeTriggerCharacters.Contains(text)).ToList();
        if (providers.Count == 0) return;
        var version = doc.Version;
        var position = _v.Controller.Caret;
        var options = Options;
        _ = RunOnTypeAsync(providers, text, position, options, version);
    }

    private async Task RunOnTypeAsync(IReadOnlyList<IDocumentFormattingProvider> providers, string text, TextPosition position, FormattingOptions options, int version)
    {
        var doc = _v.Document;
        var edits = await FirstResultAsync(providers, (p, ct) => p.FormatOnTypeAsync(doc, position, text, options, ct), TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        if (_disposed || edits is null || _v.Document.Version != version) return; // typed on meanwhile: on-type results are stale by definition
        await ApplyAsync(edits, version).ConfigureAwait(true);
    }

    private void OnPasted(object? sender, AvaloniaEdit.Editing.TextEventArgs e)
    {
        if (_disposed || !_v.Settings.FormatOnPaste || _v.Document.IsReadOnly || _lastInsert.Length == 0) return;
        var start = Math.Min(_lastInsert.Start, _v.Document.Length);
        var end = Math.Min(start + _lastInsert.Length, _v.Document.Length);
        _ = FormatRangeAsync(new TextRange(_v.Document.PositionAt(start), _v.Document.PositionAt(end)), TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _disposed = true;
        _d.Dispose();
    }
}
