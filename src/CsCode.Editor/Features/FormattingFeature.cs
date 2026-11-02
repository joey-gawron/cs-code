using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Document / selection formatting (Shift+Alt+F, Ctrl+K Ctrl+F), on-type formatting after a trigger character and on-paste formatting (both
/// setting-gated: <c>editor.formatOnType</c>, <c>editor.formatOnPaste</c>). Format on save is part of the document's save pipeline
/// (<c>editor.formatOnSave</c>). The provider runs off the UI thread; edits are applied as ONE undo unit and only when the text is still the
/// version they were computed for — a stale format result is dropped (and the user told), never mapped onto changed text.
/// Every path uses only the formatter <c>editor.defaultFormatter</c> chooses (<see cref="FormatterChoice"/>); Format Document With… picks one for
/// a single run.
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
        var all = _v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc);
        if (all.Count == 0)
        {
            FeatureSupport.Status(_v, $"There is no formatter for '{doc.LanguageId}' files installed");
            return;
        }
        if (Chosen(all, tell: true) is not { Count: > 0 } providers) return;
        await FormatDocumentWithAsync(providers).ConfigureAwait(true);
    }

    /// <summary>
    /// Format Document With…: a quick pick of the servers that can format this document (the default one marked), then one run with the chosen
    /// one. <c>editor.defaultFormatter</c> is not changed.
    /// </summary>
    public async Task FormatDocumentWithAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var doc = _v.Document;
        var all = _v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc);
        if (all.Count == 0)
        {
            FeatureSupport.Status(_v, $"There is no formatter for '{doc.LanguageId}' files installed");
            return;
        }
        if (_v.Services.Window is not { } window)
        {
            await FormatDocumentAsync().ConfigureAwait(true);
            return;
        }
        var items = PickItems(all, FormatterChoice.Configured(_v.Services.Config, doc.LanguageId));
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Format Document With…", Placeholder: "Select a formatter")).ConfigureAwait(true);
        if (_disposed || picked?.Tag is not IDocumentFormattingProvider provider) return;
        await FormatDocumentWithAsync([provider]).ConfigureAwait(true);
    }

    /// <summary>Format Document With…'s items: each formatter by its server's name (id as detail), the <c>editor.defaultFormatter</c> one marked.</summary>
    internal static IReadOnlyList<QuickPickItem> PickItems(IReadOnlyList<IDocumentFormattingProvider> all, string? configured) =>
        all.Select((p, i) =>
        {
            var id = LanguageProviderSource.IdOf(p);
            var isDefault = configured is not null && FormatterChoice.Names(configured, id);
            return new QuickPickItem(LanguageProviderSource.NameOf(p, $"Formatter {i + 1}"), isDefault ? "default" : null, id, Tag: p);
        }).ToList();

    private async Task FormatDocumentWithAsync(IReadOnlyList<IDocumentFormattingProvider> providers)
    {
        var doc = _v.Document;
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
        await FormatRangeAsync(selection, TimeSpan.FromSeconds(15), tell: true).ConfigureAwait(true);
    }

    /// <summary>Formats a line range (the modal surface's = and gq); no-op without a formatter.</summary>
    internal Task FormatLinesAsync(TextRange range) => _disposed || _v.Document.IsReadOnly ? Task.CompletedTask : FormatRangeAsync(range, TimeSpan.FromSeconds(15), tell: true);

    /// <summary>The providers <c>editor.defaultFormatter</c> allows (all of them when it is unset); with <paramref name="tell"/> a missing choice is reported.</summary>
    private IReadOnlyList<IDocumentFormattingProvider> Chosen(IReadOnlyList<IDocumentFormattingProvider> all, bool tell)
    {
        var (providers, missing) = FormatterChoice.Select(all, FormatterChoice.Configured(_v.Services.Config, _v.Document.LanguageId));
        if (missing is not null && tell) FeatureSupport.Status(_v, FormatterChoice.MissingMessage(missing, _v.Document.LanguageId));
        return providers;
    }

    private async Task FormatRangeAsync(TextRange range, TimeSpan timeout, bool tell)
    {
        var doc = _v.Document;
        var providers = Chosen(_v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc), tell);
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
        var providers = Chosen(_v.Services.Features.GetProviders<IDocumentFormattingProvider>(doc), tell: false).Where(p => p.OnTypeTriggerCharacters.Contains(text)).ToList();
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
        _ = FormatRangeAsync(new TextRange(_v.Document.PositionAt(start), _v.Document.PositionAt(end)), TimeSpan.FromSeconds(5), tell: false);
    }

    /// <summary>The editor's own paste (EditorClipboard: AvaloniaEdit's TextPasted is not raised for it): range formatting of the pasted text, as its own undo step.</summary>
    public void OnPasted(TextRange range)
    {
        if (_disposed || !_v.Settings.FormatOnPaste || _v.Document.IsReadOnly || range.IsEmpty) return;
        _ = FormatRangeAsync(range, TimeSpan.FromSeconds(5), tell: false);
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _d.Dispose();
    }
}
