using Avalonia;
using Avalonia.Controls;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Media;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// Code actions: the lightbulb by the caret line (never in the breakpoint margin, see <see cref="PositionBulb"/>; shown only after a provider
/// has said there IS something to offer — requested 250 ms after the caret settles, off the UI thread, latest-wins), the Ctrl+. menu (<see cref="IWindowService.ShowQuickPickAsync"/>), and
/// executing the chosen action: its workspace edit through <see cref="INavigationService.ApplyWorkspaceEditAsync"/> and its command through
/// <see cref="ICommandService"/>. <see cref="OrganizeImportsAsync"/> is the <c>source.organizeImports</c> action kind.
/// </summary>
internal sealed class CodeActionFeature : ICodeActionFeature, IThemeAware, IDisposable
{
    private const int MaxActions = 100;
    private static readonly TimeSpan BulbDelay = TimeSpan.FromMilliseconds(250);

    internal sealed record Offered(CodeAction Action, ICodeActionProvider Provider);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly Border _bulb;
    private readonly Shape _bulbShape;
    private CancellationTokenSource? _bulbCts, _debounce;
    private bool _disposed;

    public CodeActionFeature(CodeEditorControl view)
    {
        _v = view;
        _bulbShape = new Shape
        {
            Data = Geometry.Parse("M8,1 C5,1 3,3.2 3,5.8 C3,7.6 4,8.6 5,9.8 L5,11 L11,11 L11,9.8 C12,8.6 13,7.6 13,5.8 C13,3.2 11,1 8,1 Z M5.5,12 L10.5,12 L10.5,13 L5.5,13 Z M6.3,14 L9.7,14 L9.7,15 L6.3,15 Z"),
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
        };
        _bulb = new Border { Child = _bulbShape, IsVisible = false, IsHitTestVisible = true, Cursor = new Cursor(StandardCursorType.Hand), Width = 18, Height = 18, Background = Brushes.Transparent };
        _bulb.PointerPressed += OnBulbPressed;
        ToolTip.SetTip(_bulb, "Show Code Actions (Ctrl+.)");
        Avalonia.Automation.AutomationProperties.SetName(_bulb, "Show Code Actions");
        view.Overlay.Children.Add(_bulb);
        ApplyTheme();
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => ScheduleBulb()));
        _d.Add(view.Document.Changed.Subscribe(_ => HideBulb()));
        view.TextView.ScrollOffsetChanged += OnScrolled;
        _d.Add(Disposable.Create(() =>
        {
            _bulb.PointerPressed -= OnBulbPressed;
            view.TextView.ScrollOffsetChanged -= OnScrolled;
        }));
    }

    internal bool BulbVisible => _bulb.IsVisible;
    /// <summary>Where the lightbulb is, in the editor's overlay coordinates (tests).</summary>
    internal Rect BulbBounds => new(Canvas.GetLeft(_bulb), Canvas.GetTop(_bulb), _bulb.Width, _bulb.Height);

    public void OnThemeChanged() => ApplyTheme();

    private void ApplyTheme() => _bulbShape.Fill = _v.Palette.LightBulb;

    // ---- lightbulb ----

    private void ScheduleBulb()
    {
        if (_disposed) return;
        HideBulb();
        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(BulbDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = UpdateBulbAsync();
            });
        }, TaskScheduler.Default);
    }

    private void HideBulb()
    {
        _bulbCts?.Cancel();
        _bulb.IsVisible = false;
    }

    private async Task UpdateBulbAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        if (_v.Services.Features.GetProviders<ICodeActionProvider>(_v.Document).Count == 0) return;
        _bulbCts?.Cancel();
        _bulbCts?.Dispose();
        var cts = _bulbCts = new CancellationTokenSource();
        var version = _v.Document.Version;
        var caretLine = _v.Controller.Caret.Line;
        var actions = await GatherAsync(null, CodeActionTriggerKind.Automatic, cts.Token).ConfigureAwait(true);
        if (_disposed || cts.IsCancellationRequested || _v.Document.Version != version || _v.Controller.Caret.Line != caretLine) return;
        if (actions.Count == 0 || actions.All(a => a.Action.DisabledReason is not null)) return;
        PositionBulb(caretLine);
    }

    /// <summary>
    /// The lightbulb never sits in the breakpoint margin: a click there must always set a breakpoint (reported from the desktop: the bulb of the
    /// caret line covered the whole 18 px glyph column, so the first click opened the code actions instead). It goes where VS Code draws it, in
    /// the text: into the caret line's indentation when that is wide enough, else into the line just above or below when that one is blank or
    /// indented enough, and otherwise into the folding column right against the text.
    /// </summary>
    private void PositionBulb(int line)
    {
        var (top, bottom) = OverlayPopup.AnchorFor(_v, new TextPosition(line, 0));
        var height = bottom.Y - top.Y;
        var textLeft = top.X;
        var x = textLeft + 1;
        var y = top.Y + Math.Max(0, (height - _bulb.Height) / 2);
        if (!Fits(line))
        {
            if ((Neighbour(line - 1) ?? Neighbour(line + 1)) is { } other) y = other;
            else x = textLeft - _bulb.Width;
        }
        Canvas.SetLeft(_bulb, x);
        Canvas.SetTop(_bulb, y);
        _bulb.IsVisible = top.Y >= -1 && bottom.Y <= _v.Overlay.Bounds.Height + 1;

        // Blank, or indented by at least the bulb's width: the bulb covers no text there.
        bool Fits(int l)
        {
            var text = _v.Document.GetLineText(l);
            var indent = text.Length - text.TrimStart(' ', '\t').Length;
            if (indent == text.Length) return true;
            return indent > 0 && OverlayPopup.AnchorFor(_v, new TextPosition(l, indent)).Top.X - textLeft >= _bulb.Width + 2;
        }

        // A line next to the caret line on screen (not one hidden in a fold, nor a wrapped line's far end) where the bulb fits.
        double? Neighbour(int candidate)
        {
            if (candidate < 0 || candidate >= _v.Document.LineCount || !Fits(candidate)) return null;
            var (t, b) = OverlayPopup.AnchorFor(_v, new TextPosition(candidate, 0));
            if (Math.Abs(Math.Abs(t.Y - top.Y) - height) > 1) return null;
            return t.Y + Math.Max(0, (b.Y - t.Y - _bulb.Height) / 2);
        }
    }

    private void OnScrolled(object? sender, EventArgs e)
    {
        if (_bulb.IsVisible && !_disposed) PositionBulb(_v.Controller.Caret.Line);
    }

    private void OnBulbPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _ = ShowQuickFixAsync();
    }

    // ---- gathering ----

    /// <summary>Actions for the selection/caret with the diagnostics that overlap it, from every provider, preferred first.</summary>
    internal async Task<IReadOnlyList<Offered>> GatherAsync(IReadOnlyList<string>? only, CodeActionTriggerKind trigger, CancellationToken ct)
    {
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<ICodeActionProvider>(doc);
        if (providers.Count == 0) return [];
        var range = FeatureSupport.SelectionOrCaret(_v);
        var diagnostics = _v.Feature<DiagnosticsFeature>() is { } f
            ? f.Overlapping(doc.OffsetAt(new TextPosition(range.Start.Line, 0)), doc.OffsetAt(new TextPosition(range.End.Line, doc.GetLineText(Math.Min(range.End.Line, doc.LineCount - 1)).Length)))
                .Where(d => Touches(d.Range, range)).ToList()
            : [];
        var context = new CodeActionContext(diagnostics, only, trigger);
        var calls = providers.Select(p => (Provider: p, Task: FeatureSupport.CallAsync(_v, c => p.ProvideCodeActionsAsync(doc, range, context, c), ct, TimeSpan.FromSeconds(3)))).ToList();
        var result = new List<Offered>();
        foreach (var (provider, task) in calls)
        {
            var list = await task.ConfigureAwait(true);
            if (list is null) continue;
            foreach (var a in list)
            {
                if (only is { Count: > 0 } && !KindMatches(a.Kind, only)) continue;
                result.Add(new Offered(a, provider));
                if (result.Count >= MaxActions) break;
            }
        }
        return result
            .Select((o, i) => (o, i))
            .OrderBy(x => x.o.Action.DisabledReason is null ? 0 : 1)
            .ThenBy(x => x.o.Action.IsPreferred ? 0 : 1)
            .ThenBy(x => x.i)
            .Select(x => x.o)
            .ToList();
    }

    private static bool Touches(TextRange diagnostic, TextRange selection) =>
        diagnostic.Start <= selection.End && diagnostic.End >= selection.Start;

    /// <summary>LSP kind matching: "refactor" matches "refactor.extract.method"; an action without a kind never matches a kind filter.</summary>
    internal static bool KindMatches(string? kind, IReadOnlyList<string> only) =>
        kind is not null && only.Any(o => kind == o || kind.StartsWith(o + ".", StringComparison.Ordinal));

    // ---- Ctrl+. ----

    public async Task ShowQuickFixAsync(string? onlyKind = null)
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var version = _v.Document.Version;
        var actions = await GatherAsync(onlyKind is null ? null : [onlyKind], CodeActionTriggerKind.Invoked, CancellationToken.None).ConfigureAwait(true);
        if (_disposed || _v.Document.Version != version) return; // the text moved on while the menu was being prepared
        if (actions.Count == 0)
        {
            FeatureSupport.Status(_v, "No code actions available");
            return;
        }
        Offered? chosen;
        var window = _v.Services.Window;
        if (window is null)
        {
            chosen = actions.FirstOrDefault(a => a.Action.DisabledReason is null);
        }
        else
        {
            var items = actions.Select(a => new QuickPickItem(a.Action.Title, a.Action.DisabledReason ?? (a.Action.IsPreferred ? "preferred" : null), a.Action.Kind, Tag: a)).ToList();
            var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Quick Fix", Placeholder: "Select an action to apply")).ConfigureAwait(true);
            chosen = picked?.Tag as Offered;
        }
        if (chosen is null || chosen.Action.DisabledReason is not null) return;
        if (_disposed) return;
        HideBulb();
        await ApplyAsync(chosen, version).ConfigureAwait(true);
    }

    public async Task OrganizeImportsAsync()
    {
        if (_disposed || _v.Document.IsReadOnly) return;
        var version = _v.Document.Version;
        var actions = await GatherAsync(["source.organizeImports"], CodeActionTriggerKind.Invoked, CancellationToken.None).ConfigureAwait(true);
        var action = actions.FirstOrDefault(a => a.Action.DisabledReason is null);
        if (action is null)
        {
            FeatureSupport.Status(_v, "No organize imports action available");
            return;
        }
        await ApplyAsync(action, version).ConfigureAwait(true);
    }

    /// <summary>Resolves (when the provider defers the edit), applies the edit as one undo group, then runs the command.</summary>
    internal async Task ApplyAsync(Offered offered, int requestedVersion)
    {
        var action = offered.Action;
        try
        {
            if (action.Edit is null && (action.Data is not null || action.Command is null))
            {
                var resolved = await FeatureSupport.CallAsync(_v, ct => ResolveAsync(offered, ct), CancellationToken.None, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                if (resolved is not null) action = resolved;
            }
        }
        catch (Exception ex)
        {
            _v.Services.LoggerFactory.CreateLogger<CodeActionFeature>().LogDebug(ex, "Resolving a code action failed");
        }
        if (_disposed) return;
        if (action.Edit is { IsEmpty: false } edit)
        {
            if (_v.Document.Version != requestedVersion && edit.Changes.ContainsKey(_v.Document.Uri))
            {
                FeatureSupport.Status(_v, "The document changed; the code action was not applied");
                return;
            }
            await FeatureSupport.ApplyWorkspaceEditAsync(_v, edit, action.Title).ConfigureAwait(true);
        }
        if (action.Command is { } command && _v.Services.Commands is { } commands)
        {
            try
            {
                var args = command.Arguments is { } a ? a.Cast<object?>().ToArray() : [];
                await commands.ExecuteAsync(command.CommandId, args).AsTask().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _v.Services.LoggerFactory.CreateLogger<CodeActionFeature>().LogWarning(ex, "Code action command {Command} failed", command.CommandId);
            }
        }
    }

    private static async ValueTask<CodeAction?> ResolveAsync(Offered offered, CancellationToken ct) =>
        await offered.Provider.ResolveCodeActionAsync(offered.Action, ct).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _debounce?.Cancel();
        _bulbCts?.Cancel();
        _d.Dispose();
        _v.Overlay.Children.Remove(_bulb);
    }
}
