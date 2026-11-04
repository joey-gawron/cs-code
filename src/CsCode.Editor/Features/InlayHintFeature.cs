using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Core.Platform;

namespace CsCode.Editor.Features;

/// <summary>
/// Inlay hints (parameter names, inferred types) shown inline in the text, dimmed, from <see cref="IInlayHintsProvider"/>. Requested for the visible
/// lines (plus a margin) 400 ms after scrolling or typing settles, off the UI thread, latest-wins; at most <see cref="MaxHints"/> are kept. Each hint
/// is anchored to its text position, so it follows edits until the next answer replaces the set; a hint that cannot be placed (its position is past the
/// end of the document) is dropped. Setting <c>editor.inlayHints.enabled</c> = off removes them.
/// <para>A hint's label parts (lang-2) are separate texts: resting the pointer on a hint shows its tooltip (the part's, else the hint's), asking the
/// provider to resolve it first when it can (<see cref="IInlayHintsProvider.ResolveInlayHintAsync"/>: rust-analyzer sends tooltips and type
/// locations only then); Ctrl+click (⌘+click on a Mac) on a part goes to its location (rust-analyzer: the type's definition) or runs its command.</para>
/// </summary>
internal sealed class InlayHintFeature : ISettingsAware, IThemeAware, IDisposable
{
    public const int MaxHints = 1000;
    private const int Margin = 30;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private sealed class Placed(TextAnchor anchor, InlayHint hint, IInlayHintsProvider provider)
    {
        public TextAnchor Anchor { get; } = anchor;
        /// <summary>The hint; replaced by the resolved one once the provider resolved it (UI thread).</summary>
        public InlayHint Hint { get; set; } = hint;
        public IInlayHintsProvider Provider { get; } = provider;
        public Task<InlayHint>? Resolving;
    }

    private readonly CodeEditorControl _v;
    private readonly Generator _generator;
    private readonly DisposableStore _d = new();
    private CancellationTokenSource? _debounce, _request;
    private Placed[] _hints = [];
    private bool _disposed;

    public InlayHintFeature(CodeEditorControl view)
    {
        _v = view;
        _generator = new Generator(this);
        // Before AvaloniaEdit's own generators (folding, whitespace markers): at one offset AvaloniaEdit keeps constructing elements while they
        // are zero-length and stops at the first that covers text, so a hint after them would be lost where a whitespace marker, a control
        // character box or a folded region starts. After a code lens zone, which opens its line.
        var generators = view.TextView.ElementGenerators;
        var at = 0;
        while (at < generators.Count && LineZones.IsZoneGenerator(generators[at])) at++;
        generators.Insert(at, _generator);
        view.TextView.ScrollOffsetChanged += OnScrolled;
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule()));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IInlayHintsProvider)) _v.Post(Schedule);
        }));
        _d.Add(Disposable.Create(() => view.TextView.ScrollOffsetChanged -= OnScrolled));
        Schedule();
    }

    /// <summary>Hints currently placed: (offset, label) in document order.</summary>
    internal IReadOnlyList<(int Offset, string Label)> Placements => _hints.Where(p => !p.Anchor.IsDeleted).Select(p => (p.Anchor.Offset, p.Hint.Label)).OrderBy(x => x.Offset).ToList();

    /// <summary>The placed hints themselves (tests: resolving and following them).</summary>
    internal IReadOnlyList<InlayHint> PlacedHints => [.. _hints.Select(p => p.Hint)];

    /// <summary>Resolves the placed hint at <paramref name="index"/> as resting the pointer on it does; returns the hint now placed (tests).</summary>
    internal Task<InlayHint> ResolveForTestAsync(int index) => ResolveAsync(_hints[index]);

    /// <summary>Follows a part as Ctrl/⌘+click on it does (tests).</summary>
    internal async Task FollowForTestAsync(int index, int part) { var hint = await ResolveAsync(_hints[index]).ConfigureAwait(true); if (hint.LabelParts is { } parts) await FollowAsync(parts[part]).ConfigureAwait(true); }

    public void OnThemeChanged() => _v.TextView.Redraw();

    public void OnSettingsChanged()
    {
        if (!_v.Settings.InlayHints) Clear();
        else Schedule();
    }

    private void OnScrolled(object? sender, EventArgs e) => Schedule();

    private void Clear()
    {
        if (_hints.Length == 0) return;
        _hints = [];
        _v.TextView.Redraw();
    }

    private void Schedule()
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        if (!_v.Settings.InlayHints) return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(Delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = RefreshAsync();
            });
        }, TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IInlayHintsProvider>(doc);
        if (providers.Count == 0 || !_v.TextView.VisualLinesValid || _v.TextView.VisualLines.Count == 0)
        {
            Clear();
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var first = Math.Max(0, _v.TextView.VisualLines[0].FirstDocumentLine.LineNumber - 1 - Margin);
        var last = Math.Min(doc.LineCount - 1, _v.TextView.VisualLines[^1].LastDocumentLine.LineNumber - 1 + Margin);
        var range = new TextRange(new TextPosition(first, 0), new TextPosition(last, doc.GetLineText(last).Length));

        var all = new List<(InlayHint Hint, IInlayHintsProvider Provider)>();
        foreach (var (provider, call) in providers.Select(p => (p, FeatureSupport.CallAsync(_v, c => p.ProvideInlayHintsAsync(doc, range, c), cts.Token, TimeSpan.FromSeconds(3)))).ToList())
            if (await call.ConfigureAwait(true) is { } list) all.AddRange(list.Select(h => (h, provider)));
        if (_disposed || cts.IsCancellationRequested || doc.Version != version) return; // text moved on: a newer request is already scheduled

        var placed = new List<Placed>();
        foreach (var (hint, provider) in all.Take(MaxHints))
        {
            var p = doc.Validate(hint.Position);
            if (p != hint.Position) continue;
            var anchor = doc.Buffer.CreateAnchor(doc.OffsetAt(p));
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            placed.Add(new Placed(anchor, hint, provider));
        }
        _hints = placed.OrderBy(h => h.Anchor.Offset).ToArray();
        _v.TextView.Redraw();
    }

    private sealed class Generator(InlayHintFeature owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var hints = owner._hints;
            if (hints.Length == 0) return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            foreach (var h in hints)
            {
                if (h.Anchor.IsDeleted) continue;
                var offset = h.Anchor.Offset;
                if (offset >= startOffset) return offset <= end ? offset : -1;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var placed = owner._hints.FirstOrDefault(h => !h.Anchor.IsDeleted && h.Anchor.Offset == offset);
            if (placed is null) return null;
            var hint = placed.Hint;
            var p = owner._v.Palette;
            TextBlock Text(string value, int part) => new()
            {
                Text = value,
                FontSize = Math.Max(9, owner._v.Editor.FontSize - 2),
                FontFamily = owner._v.Editor.FontFamily,
                Foreground = p.InlayForeground,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Tag = part,
            };
            Control content;
            if (hint.LabelParts is { Count: > 0 } parts)
            {
                var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                for (var i = 0; i < parts.Count; i++) panel.Children.Add(Text(parts[i].Value, i));
                content = panel;
            }
            else content = Text(hint.Label, -1);
            var border = new Border
            {
                Child = content,
                Background = p.InlayBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(hint.PaddingLeft ? 4 : 0, 0, hint.PaddingRight ? 4 : 0, 0),
                // only a hint with something to show or follow takes the pointer; a plain one lets clicks reach the text
                IsHitTestVisible = hint.CanResolve || hint.Tooltip is not null || hint.LabelParts?.Any(x => x.Tooltip is not null || x.Location is not null || x.Command is not null) == true,
            };
            if (border.IsHitTestVisible) owner.Hook(border, placed);
            return new InlineObjectElement(0, border);
        }
    }

    // ------------------------------------------------------------------ tooltips and links (lang-2)

    private void Hook(Border border, Placed placed)
    {
        border.PointerEntered += async (_, e) =>
        {
            var hint = await ResolveAsync(placed).ConfigureAwait(true);
            if (_disposed) return;
            var part = PartAt(e.Source, hint);
            ToolTip.SetTip(border, TooltipOf(hint, part));
            border.Cursor = hint.LabelParts?.Any(x => x.Location is not null || x.Command is not null) == true ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null;
        };
        border.PointerMoved += (_, e) =>
        {
            if (placed.Resolving is { IsCompletedSuccessfully: true }) ToolTip.SetTip(border, TooltipOf(placed.Hint, PartAt(e.Source, placed.Hint)));
        };
        border.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed || !EditorKeys.HasLinkModifier(e.KeyModifiers, _v.Settings.MultiCursorModifier)) return;
            e.Handled = true; // never moves the caret
            var index = (e.Source as Control)?.Tag is int i ? i : -1;
            var hint = await ResolveAsync(placed).ConfigureAwait(true);
            if (_disposed || hint.LabelParts is not { } parts) return;
            if (index < 0 || index >= parts.Count) index = parts.ToList().FindIndex(x => x.Location is not null);
            if (index < 0) return;
            await FollowAsync(parts[index]).ConfigureAwait(true);
        };
    }

    /// <summary>The resolved hint (once per placed hint), or the hint as it is when its provider does not resolve or fails (UI thread).</summary>
    private Task<InlayHint> ResolveAsync(Placed placed)
    {
        if (!placed.Hint.CanResolve) return Task.FromResult(placed.Hint);
        return placed.Resolving ??= Resolve();

        async Task<InlayHint> Resolve()
        {
            var hint = placed.Hint;
            var resolved = await FeatureSupport.CallAsync<InlayHint>(_v, async c => await placed.Provider.ResolveInlayHintAsync(hint, c).ConfigureAwait(false), CancellationToken.None, TimeSpan.FromSeconds(3))
                .ConfigureAwait(true);
            if (resolved is not null) placed.Hint = resolved with { CanResolve = false };
            return placed.Hint;
        }
    }

    private static InlayHintLabelPart? PartAt(object? source, InlayHint hint) =>
        source is Control { Tag: int index } && hint.LabelParts is { } parts && index >= 0 && index < parts.Count ? parts[index] : null;

    private string? TooltipOf(InlayHint hint, InlayHintLabelPart? part)
    {
        var text = (part?.Tooltip ?? hint.Tooltip)?.Value;
        var link = part?.Location is { } l ? $"{EditorKeys.LinkModifierLabel(_v.Settings.MultiCursorModifier)}+click to go to {Path.GetFileName(l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.AbsolutePath)}:{l.Range.Start.Line + 1}" : null;
        return text is null ? link : link is null ? text : text + "\n\n" + link;
    }

    private async Task FollowAsync(InlayHintLabelPart part)
    {
        try
        {
            if (part.Location is { } location) await FeatureSupport.OpenLocationAsync(_v, location.Uri, location.Range).ConfigureAwait(true);
            else if (part.Command is { } command && _v.Services.Commands is { } commands && commands.Has(command.CommandId))
                await commands.ExecuteAsync(command.CommandId, [.. (command.Arguments ?? []).Select(a => (object?)a)]).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureSupport.Status(_v, $"Cannot follow the hint: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Cancel();
        _request?.Cancel();
        _d.Dispose();
        _v.TextView.ElementGenerators.Remove(_generator);
        _hints = [];
    }
}
