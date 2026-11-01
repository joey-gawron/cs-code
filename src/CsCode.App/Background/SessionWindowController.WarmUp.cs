using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsCode.Editor;
using CsCode.Editor.View;
using CsCode.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Background;

public sealed partial class SessionWindowController
{
    /// <summary>How long the editor warm-up waits for the sample's colours (the grammar loads and tokenises on the editor worker).</summary>
    internal static TimeSpan EditorWarmUpTokenWait { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>What the warm-up of this process did, for <c>inspect</c> (tests): "window", "editor", "colours". UI thread.</summary>
    private readonly List<string> _warmedUp = [];
    private readonly TaskCompletionSource _spareWarm = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the spare's warm-up ended (finished, skipped by a claim, or failed). Tests.</summary>
    internal Task SpareWarm => _spareWarm.Task;

    /// <summary>What the warm-up did so far (UI thread). Tests.</summary>
    internal IReadOnlyList<string> WarmedUp => _warmedUp;

    /// <summary>Tests: awaited once the warm-up editor is in the window, before its colours are waited for (a claim can then arrive "in the middle").</summary>
    internal static Func<Task>? EditorWarmUpHoldForTests { get; set; }

    /// <summary>
    /// The warm spare's last start-up step: everything the first frame of a claimed session would otherwise do for the first time happens now,
    /// off screen, in small steps at background priority (a claim arriving meanwhile waits for one step at most, then the rest is skipped):
    /// <list type="number">
    /// <item>the window: its templates and styles, the first layout pass, the first Skia render with its glyph and geometry caches, and the JIT
    /// of all of it (the hidden window is laid out at its size and rendered into a bitmap that is thrown away; no native window is shown);</item>
    /// <item>the first editor (<see cref="EditorWarmUp"/>): a throwaway editor view over an unregistered sample document, put in the window's
    /// overlay layer and rendered the same way, then rendered once more after its colours arrived (the grammar, the TextMate engine and its regex
    /// library, the colouriser), then removed and disposed. Opening the claimed session's first file was the biggest part of a claim after the
    /// window's first frame (docs/SESSIONS.md, "Start-up performance").</item>
    /// </list>
    /// Not warmed: the compositor's first frame of the real window (a different path from the bitmap render; it needs a shown window: Avalonia's
    /// <c>Compositor.CreateCompositionVisualSnapshot</c> refuses the visual of a window that was never shown, tried 2026-10-29).
    /// </summary>
    internal async Task WarmUpSpareAsync()
    {
        SessionTrace.Mark("host: spare warm-up");
        var size = new Size(double.IsNaN(_window.Width) ? 1280 : _window.Width, double.IsNaN(_window.Height) ? 800 : _window.Height);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _window.ApplyTemplate();
                _window.Measure(size);
                _window.Arrange(new Rect(size));
            }, DispatcherPriority.Background);
            if (_host.IsUnclaimedSpare)
            {
                await Dispatcher.UIThread.InvokeAsync(() => RenderOffScreen(size), DispatcherPriority.Background);
                _warmedUp.Add("window");
            }
            if (_host.IsUnclaimedSpare) await WarmUpEditorAsync(size).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Only an optimisation: a spare that could not warm up is still a spare.
            SessionTrace.Mark("host: spare warm-up failed: " + ex.Message);
        }
        // Waiting for a claim is idle time: no periodic work (the UI stall watchdog stops like in a hidden session).
        if (_host.IsUnclaimedSpare) _shell.SetHidden(true);
        SessionTrace.Mark("host: spare warm");
        _spareWarm.TrySetResult();
    }

    private void RenderOffScreen(Size size)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
        bitmap.Render(_window);
    }

    /// <summary>Step 2 of <see cref="WarmUpSpareAsync"/>: a throwaway editor, laid out and rendered in the overlay layer, then disposed.</summary>
    private async Task WarmUpEditorAsync(Size size)
    {
        if (_sp.GetService<IEditorViewFactory>() is not { } editors) return;
        EditorWarmUp? warm = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var layer = OverlayLayer.GetOverlayLayer(_window);
                if (layer is null || !_host.IsUnclaimedSpare) return;
                warm = EditorWarmUp.Create(editors, SpareWarmUpSample.LanguageId, SpareWarmUpSample.Text, SpareWarmUpSample.FileName);
                if (warm is null) return;
                var created = warm;
                _endWarmUp = () =>
                {
                    layer.Children.Remove(created.View);
                    created.Dispose();
                };
                warm.View.Width = Math.Min(900, size.Width);
                warm.View.Height = Math.Min(500, size.Height);
                warm.View.IsHitTestVisible = false;
                layer.Children.Add(warm.View);
                _window.Measure(size);
                _window.Arrange(new Rect(size));
                RenderOffScreen(size);
                _warmedUp.Add("editor");
            }, DispatcherPriority.Background);
            if (warm is null) return;
            if (EditorWarmUpHoldForTests is { } hold) await hold().ConfigureAwait(true);
            // The colours: the grammar loads and the visible lines are tokenised on the editor worker; then one more render draws them.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!warm.TokensReady && clock.Elapsed < EditorWarmUpTokenWait && _host.IsUnclaimedSpare) await Task.Delay(20).ConfigureAwait(true);
            if (warm.HasGrammar && warm.TokensReady && _host.IsUnclaimedSpare)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _window.Measure(size);
                    _window.Arrange(new Rect(size));
                    RenderOffScreen(size);
                }, DispatcherPriority.Background);
                _warmedUp.Add("colours");
            }
        }
        finally
        {
            // Always taken out again, claimed or not (a claim takes it out first: EndWarmUp in ActivateAsync).
            await Dispatcher.UIThread.InvokeAsync(EndWarmUp, DispatcherPriority.Send);
        }
    }

    private Action? _endWarmUp;

    /// <summary>Takes the warm-up editor out of the window and disposes it, if one is there (UI thread; a claim calls it before restoring).</summary>
    private void EndWarmUp()
    {
        var end = _endWarmUp;
        _endWarmUp = null;
        end?.Invoke();
    }
}

/// <summary>The sample the warm spare's editor warm-up colours: a few lines of C#, CsCode's own language.</summary>
internal static class SpareWarmUpSample
{
    public const string LanguageId = "csharp";
    public const string FileName = "Sample.cs";
    public const string Text = """
        using System;
        using System.Collections.Generic;

        namespace Sample;

        /// <summary>A sample the warm spare colours once.</summary>
        public sealed class Widget<T> : IDisposable where T : notnull
        {
            private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);

            public int Count => _items.Count;

            public bool TryAdd(string key, T value)
            {
                if (string.IsNullOrEmpty(key)) throw new ArgumentException("empty key", nameof(key));
                return _items.TryAdd(key, value); // 42, 3.14, 'c', $"{key}"
            }

            public void Dispose() => _items.Clear();
        }
        """;
}
