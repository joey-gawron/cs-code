using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// Go to definition (F12; Ctrl+Click with the symbol underlined while Ctrl is held), find references (Shift+F12) and rename (F2).
/// Every request goes to the providers off the UI thread with a deadline; a result for text that has changed since is never applied.
/// Several locations are offered through <see cref="IWindowService.ShowQuickPickAsync"/>; for references the command
/// <c>cscode.references.show</c> (args: <c>IReadOnlyList&lt;Location&gt;</c>, title) is the extension point for the shell's Find Results panel —
/// when the shell registers it, it presents the list instead of the quick pick.
/// </summary>
internal sealed class NavigationFeature : INavigationFeature, IThemeAware, IDisposable
{
    public const string ShowReferencesCommand = "cscode.references.show";
    private static readonly TimeSpan LinkDelay = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly LinkUnderline _underline;
    private CancellationTokenSource? _linkCts;
    private (int Start, int End) _linkWord = (-1, -1);
    private IReadOnlyList<LocationLink>? _linkTargets;
    private Uri? _linkUrl;
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(@"https?://[^\s<>""'`\]\[{}|\\^]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(20));
    private bool _disposed;

    public NavigationFeature(CodeEditorControl view)
    {
        _v = view;
        _underline = new LinkUnderline(this);
        view.TextView.LineTransformers.Add(_underline);
        view.Editor.PointerMoved += OnPointerMoved;
        view.Editor.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        view.TextArea.KeyUp += OnKeyUp;
        view.TextArea.LostFocus += OnLostFocus;
        // Without a shell there is no keybinding service: the editor binds the standard keys itself. With one, the shell routes them to the commands.
        if (view.Services.Commands is null) view.TextArea.AddHandler(InputElement.KeyDownEvent, OnStandaloneKey, RoutingStrategies.Bubble);
        _d.Add(Disposable.Create(() =>
        {
            view.Editor.PointerMoved -= OnPointerMoved;
            view.Editor.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            view.TextArea.KeyUp -= OnKeyUp;
            view.TextArea.LostFocus -= OnLostFocus;
            view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnStandaloneKey);
            view.TextView.LineTransformers.Remove(_underline);
        }));
        _d.Add(view.Document.Changed.Subscribe(_ => ClearLink()));
    }

    public void OnThemeChanged() => _v.TextView.Redraw();

    // ---- definition ----

    public async Task GoToDefinitionAsync(DefinitionKind kind = DefinitionKind.Definition)
    {
        if (_disposed) return;
        var caret = _v.Editor.CaretOffset;
        var targets = await FindDefinitionsAsync(caret, kind, CancellationToken.None).ConfigureAwait(true);
        if (_disposed) return;
        if (targets.Count == 0)
        {
            var word = FeatureSupport.WordAt(_v.Editor.Document, caret);
            FeatureSupport.Status(_v, word.End > word.Start ? $"No definition found for '{_v.Editor.Document.GetText(word.Start, word.End - word.Start)}'" : "No definition found");
            return;
        }
        await PickAndOpenAsync(targets.Select(t => new Location(t.TargetUri, t.TargetSelectionRange)).ToList(), "Go to Definition").ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<LocationLink>> FindDefinitionsAsync(int offset, DefinitionKind kind, CancellationToken ct)
    {
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IDefinitionProvider>(doc);
        if (providers.Count == 0) return [];
        var position = doc.PositionAt(offset);
        var calls = providers.Select(p => FeatureSupport.CallAsync(_v, c => p.ProvideDefinitionAsync(doc, position, kind, c), ct, TimeSpan.FromSeconds(3))).ToList();
        var all = new List<LocationLink>();
        foreach (var call in calls)
            if (await call.ConfigureAwait(true) is { } list) all.AddRange(list);
        return all.DistinctBy(l => (l.TargetUri, l.TargetSelectionRange)).Take(200).ToList();
    }

    private async Task PickAndOpenAsync(IReadOnlyList<Location> locations, string title)
    {
        if (locations.Count == 1)
        {
            await FeatureSupport.OpenLocationAsync(_v, locations[0].Uri, locations[0].Range).ConfigureAwait(true);
            return;
        }
        var window = _v.Services.Window;
        if (window is null)
        {
            await FeatureSupport.OpenLocationAsync(_v, locations[0].Uri, locations[0].Range).ConfigureAwait(true);
            return;
        }
        var items = locations.Select(l => new QuickPickItem(
            Path.GetFileName(l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.AbsolutePath) + ":" + (l.Range.Start.Line + 1),
            l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.ToString(),
            null,
            Tag: l)).ToList();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: title)).ConfigureAwait(true);
        if (picked?.Tag is Location location) await FeatureSupport.OpenLocationAsync(_v, location.Uri, location.Range).ConfigureAwait(true);
    }

    // ---- references ----

    public async Task FindReferencesAsync()
    {
        if (_disposed) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IReferenceProvider>(doc);
        if (providers.Count == 0) return;
        var offset = _v.Editor.CaretOffset;
        var position = doc.PositionAt(offset);
        var calls = providers.Select(p => FeatureSupport.CallAsync(_v, c => p.ProvideReferencesAsync(doc, position, true, c), CancellationToken.None, TimeSpan.FromSeconds(8))).ToList();
        var all = new List<Location>();
        foreach (var call in calls)
            if (await call.ConfigureAwait(true) is { } list) all.AddRange(list);
        if (_disposed) return;
        var references = all.Distinct().Take(5000).ToList();
        var word = FeatureSupport.WordAt(_v.Editor.Document, offset);
        var symbol = word.End > word.Start ? _v.Editor.Document.GetText(word.Start, word.End - word.Start) : "symbol";
        if (references.Count == 0)
        {
            FeatureSupport.Status(_v, $"No references found for '{symbol}'");
            return;
        }
        if (references.Count > 1 && _v.Services.Commands is { } commands && commands.Has(ShowReferencesCommand))
        {
            try
            {
                await commands.ExecuteAsync(ShowReferencesCommand, references, $"References to '{symbol}'").AsTask().ConfigureAwait(true);
                return;
            }
            catch (Exception ex)
            {
                _v.Services.LoggerFactory.CreateLogger<NavigationFeature>().LogDebug(ex, "{Command} failed; falling back to the quick pick", ShowReferencesCommand);
            }
        }
        await PickAndOpenAsync(references, $"References to '{symbol}'").ConfigureAwait(true);
    }

    // ---- rename ----

    public async Task RenameAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IRenameProvider>(doc);
        if (providers.Count == 0) return;
        var provider = providers[0];
        var offset = _v.Editor.CaretOffset;
        var position = doc.PositionAt(offset);
        var version = doc.Version;

        var prepared = await FeatureSupport.CallAsync(_v, c => provider.PrepareRenameAsync(doc, position, c), CancellationToken.None, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        if (_disposed) return;
        var word = FeatureSupport.WordAt(_v.Editor.Document, offset);
        string placeholder;
        if (prepared is not null && !prepared.Range.IsEmpty) placeholder = prepared.Placeholder ?? doc.GetText(prepared.Range);
        else if (word.End > word.Start) placeholder = _v.Editor.Document.GetText(word.Start, word.End - word.Start);
        else
        {
            FeatureSupport.Status(_v, "The element can't be renamed");
            return;
        }

        var window = _v.Services.Window;
        if (window is null) return; // nothing to ask the name with
        var name = await window.ShowInputBoxAsync(new InputBoxOptions(
            Title: "Rename Symbol",
            Prompt: "Enter new name, press Enter to confirm",
            Value: placeholder,
            Validate: v => string.IsNullOrWhiteSpace(v) ? "The new name must not be empty" : null)).ConfigureAwait(true);
        if (_disposed || string.IsNullOrWhiteSpace(name) || name == placeholder) return;
        if (doc.Version != version)
        {
            FeatureSupport.Status(_v, "The document changed; rename cancelled");
            return;
        }

        var edit = await FeatureSupport.CallAsync(_v, c => provider.ProvideRenameAsync(doc, position, name, c), CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
        if (_disposed) return;
        if (edit is null || edit.IsEmpty)
        {
            FeatureSupport.Status(_v, "No result");
            return;
        }
        if (doc.Version != version && edit.Changes.ContainsKey(doc.Uri))
        {
            FeatureSupport.Status(_v, "The document changed while renaming; the rename was not applied");
            return;
        }
        await FeatureSupport.ApplyWorkspaceEditAsync(_v, edit, "Rename").ConfigureAwait(true);
    }

    // ---- Ctrl+Click ----

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_disposed) return;
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            ClearLink();
            return;
        }
        var pos = _v.Editor.GetPositionFromPoint(e.GetPosition(_v.Editor));
        if (pos is not { } p || p.IsAtEndOfLine)
        {
            ClearLink();
            return;
        }
        var offset = _v.Document.OffsetAt(new TextPosition(p.Line - 1, p.Column - 1));
        if (UrlAt(offset) is { } url)
        {
            if (_linkUrl == url.Uri) return;
            ClearLink();
            _linkUrl = url.Uri;
            _underline.Range = (url.Start, url.End);
            _v.Editor.Cursor = new Cursor(StandardCursorType.Hand);
            _v.TextView.Redraw();
            return;
        }
        var word = FeatureSupport.WordAt(_v.Editor.Document, offset);
        if (word.End <= word.Start)
        {
            ClearLink();
            return;
        }
        if (word == _linkWord) return;
        ClearLink();
        _linkWord = word;
        _linkCts?.Cancel();
        _linkCts?.Dispose();
        var cts = _linkCts = new CancellationTokenSource();
        var version = _v.Document.Version;
        _ = Task.Delay(LinkDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _v.Post(async () =>
            {
                if (cts.IsCancellationRequested) return;
                var targets = await FindDefinitionsAsync(offset, DefinitionKind.Definition, cts.Token).ConfigureAwait(true);
                if (_disposed || cts.IsCancellationRequested || _v.Document.Version != version || _linkWord != word || targets.Count == 0) return;
                _linkTargets = targets;
                _underline.Range = targets[0].OriginSelectionRange is { } origin ? (_v.Document.OffsetAt(origin.Start), _v.Document.OffsetAt(origin.End)) : word;
                _v.Editor.Cursor = new Cursor(StandardCursorType.Hand);
                _v.TextView.Redraw();
            });
        }, TaskScheduler.Default);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed || (e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (!e.GetCurrentPoint(_v.Editor).Properties.IsLeftButtonPressed) return;
        if (_linkUrl is { } url)
        {
            e.Handled = true;
            ClearLink();
            _ = _v.Services.Window?.OpenExternalAsync(url);
            return;
        }
        if (_linkTargets is not { Count: > 0 } targets) return;
        e.Handled = true;
        ClearLink();
        _ = PickAndOpenAsync(targets.Select(t => new Location(t.TargetUri, t.TargetSelectionRange)).ToList(), "Go to Definition");
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) ClearLink();
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e) => ClearLink();

    private void ClearLink()
    {
        _linkCts?.Cancel();
        _linkWord = (-1, -1);
        if (_linkTargets is null && _linkUrl is null && _underline.Range is null) return;
        _linkTargets = null;
        _linkUrl = null;
        _underline.Range = null;
        _v.Editor.Cursor = null;
        if (!_disposed) _v.TextView.Redraw();
    }

    private void OnStandaloneKey(object? sender, KeyEventArgs e)
    {
        if (_disposed) return;
        switch (e.Key, e.KeyModifiers)
        {
            case (Key.F12, KeyModifiers.None):
                _ = GoToDefinitionAsync();
                e.Handled = true;
                break;
            case (Key.F12, KeyModifiers.Shift):
                _ = FindReferencesAsync();
                e.Handled = true;
                break;
            case (Key.F2, KeyModifiers.None):
                _ = RenameAsync();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The http(s) URL at <paramref name="offset"/> (bounded: only the line it is on, at most 2000 characters of it), or null.</summary>
    internal (Uri Uri, int Start, int End)? UrlAt(int offset)
    {
        var doc = _v.Editor.Document;
        if (offset < 0 || offset > doc.TextLength) return null;
        var line = doc.GetLineByOffset(offset);
        if (line.Length is 0 or > 2000) return null;
        var text = doc.GetText(line.Offset, line.Length);
        try
        {
            foreach (System.Text.RegularExpressions.Match m in UrlPattern.Matches(text))
            {
                var value = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                var start = line.Offset + m.Index;
                var end = start + value.Length;
                if (offset < start || offset > end) continue;
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? (uri, start, end) : null;
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // a pathological line: no link
        }
        return null;
    }

    // ---- underline of the symbol under Ctrl ----

    private sealed class LinkUnderline(NavigationFeature owner) : DocumentColorizingTransformer
    {
        public (int Start, int End)? Range { get; set; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (Range is not { } r || r.End <= line.Offset || r.Start >= line.EndOffset) return;
            var from = Math.Max(r.Start, line.Offset);
            var to = Math.Min(r.End, line.EndOffset);
            if (to <= from) return;
            ChangeLinePart(from, to, e =>
            {
                e.TextRunProperties.SetForegroundBrush(owner._v.Palette.Link);
                e.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _linkCts?.Cancel();
        _linkCts?.Dispose();
        _d.Dispose();
    }
}
