using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Threading;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Services;

/// <summary>
/// VS Code's <c>files.autoSave</c> (read per document, so a <c>"[lang]"</c> section can set it, and live):
/// <list type="bullet">
/// <item><c>afterDelay</c>: a dirty document is saved <c>files.autoSaveDelay</c> ms (1000) after its last edit; every edit starts the wait again.
/// The save skips format on save and the carets' lines (<see cref="DocumentSaveReason.AfterDelay"/>, docs/EDITOR.md §7).</item>
/// <item><c>onFocusChange</c>: an editor's document is saved when the editor loses the keyboard (to another editor, a tool window, the palette)
/// or stops being the active editor, and every dirty document when a CsCode window loses activation.</item>
/// <item><c>onWindowChange</c>: every dirty document when a CsCode window loses activation (another application, or another CsCode window).</item>
/// </list>
/// Never an untitled or read-only document, never one whose "changed on disk" question is open (<c>EditorDocument.IsExternalChangePending</c>; a
/// delayed save waits until it is answered). Turning auto save on saves the dirty documents at once, as VS Code does. Saves go through the
/// document well (the will-save participants, then the editor's save, whose encoding and write run on the IO lane), never blocking the UI
/// thread; one document is never saved twice at once. A save that fails is logged and reported once (until one succeeds again).
/// UI-thread object; the delay timers post back to it. Each open document is tracked while a tab shows it, and released when the last one closes.
/// </summary>
public sealed class AutoSave : IDisposable
{
    public const int DefaultDelayMs = 1000;

    /// <summary>The <c>files.autoSave</c> values.</summary>
    public enum Mode { Off, AfterDelay, OnFocusChange, OnWindowChange }

    private readonly DocumentWell _well;
    private readonly IConfigurationService _config;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly IWindowService? _windows;
    private readonly ILogger? _log;
    private readonly Func<bool> _closing;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<IEditorDocument, Tracked> _docs = new(ReferenceEqualityComparer.Instance);
    private readonly Func<EditorTab, DocumentSaveReason?> _saveOnClose;
    private EditorTab? _focused; // the editor tab whose view has the keyboard
    private EditorTab? _active;
    private bool _disposed;

    /// <param name="closing">True while the window closes (its editors close then): every auto-save mode saves instead of asking, as VS Code
    /// saves the documents it auto-saves on shutdown.</param>
    public AutoSave(DocumentWell well, IConfigurationService config, IUiDispatcher ui, TimeProvider? time = null, IWindowService? windows = null,
        ILogger? log = null, Func<bool>? closing = null)
    {
        _well = well;
        _config = config;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _windows = windows;
        _log = log;
        _closing = closing ?? (() => false);
        _subs.Add(config.Changed.Subscribe(e =>
        {
            if (e.Affects("files") || e.AffectedKeys.Any(k => k.StartsWith('['))) _ui.Post(OnConfigurationChanged);
        }));
        _subs.Add(well.TabsChanged.Subscribe(_ => Reconcile()));
        _subs.Add(well.ActiveChanged.Subscribe(OnActiveChanged));
        _saveOnClose = SaveOnClose;
        well.SaveOnClose = _saveOnClose;
        _active = well.ActiveEditor;
        Reconcile();
    }

    /// <summary>The auto-save mode for <paramref name="document"/> (its language's section first).</summary>
    public Mode ModeOf(ITextDocument document) => _config.Get(SettingKeys.FilesAutoSave, document.LanguageId, "off") switch
    {
        "afterDelay" => Mode.AfterDelay,
        "onFocusChange" => Mode.OnFocusChange,
        "onWindowChange" => Mode.OnWindowChange,
        _ => Mode.Off,
    };

    private TimeSpan DelayOf(ITextDocument document) =>
        TimeSpan.FromMilliseconds(Math.Clamp(_config.Get(SettingKeys.FilesAutoSaveDelay, document.LanguageId, DefaultDelayMs), 0, int.MaxValue));

    /// <summary>The documents tracked now (tests: a closed tab's document is released).</summary>
    internal int TrackedCount => _docs.Count;

    // ------------------------------------------------------------------ windows and focus

    /// <summary>
    /// Follows <paramref name="window"/>'s keyboard focus (an editor losing it) and activation (the window losing it). Every shell window gets this
    /// (<see cref="ShellController.AttachWindow"/>): keyboard focus is global, but focus events are routed through the window that holds the control.
    /// </summary>
    public IDisposable AttachWindow(TopLevel window)
    {
        window.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        if (window is WindowBase w) w.Deactivated += OnWindowDeactivated;
        return Disposable.Create(() =>
        {
            window.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            if (window is WindowBase wb) wb.Deactivated -= OnWindowDeactivated;
        });
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        if (_disposed || e.Source is not Visual visual) return;
        // A menu or popup opened over the editor (its right-click menu, a completion list) does not leave it.
        if (TopLevel.GetTopLevel(visual) is PopupRoot || visual.FindAncestorOfType<OverlayPopupHost>(includeSelf: true) is not null) return;
        FocusMoved(TabOf(visual));
    }

    /// <summary>The keyboard moved into <paramref name="now"/>'s editor, or out of every editor (null). Internal for tests.</summary>
    internal void FocusMoved(EditorTab? now)
    {
        var previous = _focused;
        _focused = now;
        if (previous is not null && !ReferenceEquals(previous, now)) SaveOnFocusChange(previous);
    }

    private EditorTab? TabOf(Visual visual)
    {
        foreach (var tab in _well.EditorTabs)
            if (tab.LiveView is { } view && (ReferenceEquals(view, visual) || view.IsVisualAncestorOf(visual))) return tab;
        return null;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => WindowDeactivated();

    /// <summary>A CsCode window lost activation: both focus modes save every dirty document. Internal for tests.</summary>
    internal void WindowDeactivated()
    {
        if (_disposed) return;
        foreach (var t in _docs.Values.ToArray())
            if (t.Mode is Mode.OnFocusChange or Mode.OnWindowChange) _ = SaveAsync(t, DocumentSaveReason.FocusOut);
    }

    private void OnActiveChanged(ShellDocument? tab)
    {
        // VS Code treats a change of the active editor as a focus change of the editor that was active.
        var previous = _active;
        _active = tab as EditorTab;
        if (previous is not null && !ReferenceEquals(previous, tab)) SaveOnFocusChange(previous);
    }

    private void SaveOnFocusChange(EditorTab tab)
    {
        if (_disposed || tab.IsDisposed || !_docs.TryGetValue(tab.Doc, out var t) || t.Mode != Mode.OnFocusChange) return;
        _ = SaveAsync(t, DocumentSaveReason.FocusOut);
    }

    private DocumentSaveReason? SaveOnClose(EditorTab tab)
    {
        if (_disposed || tab.IsDisposed) return null;
        var doc = tab.Doc;
        if (doc.IsUntitled || doc.IsReadOnly) return null;
        var mode = ModeOf(doc);
        if (mode == Mode.Off) return null;
        if (_closing()) return mode == Mode.AfterDelay ? DocumentSaveReason.AfterDelay : DocumentSaveReason.FocusOut;
        // the question would take the keyboard, which saves the editor anyway; VS Code treats a dirty editor with a short delay as being saved
        if (mode == Mode.OnFocusChange) return DocumentSaveReason.FocusOut;
        if (mode == Mode.AfterDelay && DelayOf(doc) <= TimeSpan.FromMilliseconds(DefaultDelayMs)) return DocumentSaveReason.AfterDelay;
        return null;
    }

    // ------------------------------------------------------------------ documents

    private void Reconcile()
    {
        if (_disposed) return;
        var tabs = _well.EditorTabs;
        var open = new HashSet<IEditorDocument>(ReferenceEqualityComparer.Instance);
        foreach (var tab in tabs) open.Add(tab.Doc);
        foreach (var doc in open)
            if (!_docs.ContainsKey(doc)) Track(doc);
        if (_docs.Count > open.Count)
            foreach (var doc in _docs.Keys.Where(d => !open.Contains(d)).ToArray()) Untrack(doc);
        // closed tabs are not kept (nor their views and documents) until the next focus change
        if (_focused is { IsDisposed: true }) _focused = null;
        if (_active is { IsDisposed: true }) _active = null;
    }

    private void Track(IEditorDocument doc)
    {
        var t = new Tracked(doc);
        Refresh(t);
        t.Changes = doc.Changed.Subscribe(_ => OnChanged(t));
        t.Language = doc.LanguageIdChanged.Subscribe(_ => _ui.Post(() => { if (!t.Untracked) Refresh(t); }));
        _docs[doc] = t;
    }

    private void Untrack(IEditorDocument doc)
    {
        if (!_docs.Remove(doc, out var t)) return;
        t.Untracked = true;
        t.Changes?.Dispose();
        t.Language?.Dispose();
        t.Timer?.Dispose();
        t.Timer = null;
    }

    private void Refresh(Tracked t)
    {
        t.Mode = ModeOf(t.Doc);
        t.Delay = DelayOf(t.Doc);
        if (t.Mode != Mode.AfterDelay) t.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The typing path (UI thread): one field check, and for <c>afterDelay</c> the timer restarted (no allocation after the first edit).</summary>
    private void OnChanged(Tracked t)
    {
        if (t.Mode != Mode.AfterDelay || t.Untracked || _disposed) return;
        Arm(t);
    }

    private void Arm(Tracked t)
    {
        t.Timer ??= _time.CreateTimer(_ => _ui.Post(() => OnDelayElapsed(t)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        t.Timer.Change(t.Delay, Timeout.InfiniteTimeSpan);
    }

    private void OnDelayElapsed(Tracked t)
    {
        if (t.Untracked || _disposed || t.Mode != Mode.AfterDelay) return;
        _ = SaveAsync(t, DocumentSaveReason.AfterDelay);
    }

    private void OnConfigurationChanged()
    {
        if (_disposed) return;
        foreach (var t in _docs.Values.ToArray())
        {
            var before = t.Mode;
            Refresh(t);
            // VS Code saves the dirty editors when auto save is turned on (or changes mode)
            if (t.Mode != before && t.Mode != Mode.Off) _ = SaveAsync(t, t.Mode == Mode.AfterDelay ? DocumentSaveReason.AfterDelay : DocumentSaveReason.FocusOut);
        }
    }

    private async Task SaveAsync(Tracked t, DocumentSaveReason reason)
    {
        var doc = t.Doc;
        if (t.Untracked || _disposed || !doc.IsDirty || doc.IsUntitled || doc.IsReadOnly) return;
        if (doc is CsCode.Editor.Documents.EditorDocument { IsExternalChangePending: true })
        {
            // the user is being asked whether to reload: after a delay, try again once they answered (a save now would wait for the answer)
            if (reason == DocumentSaveReason.AfterDelay) Arm(t);
            return;
        }
        if (t.Saving)
        {
            t.Again = true; // edits made while this one saved are saved once it is done
            return;
        }
        if (_well.EditorTabs.FirstOrDefault(x => ReferenceEquals(x.Doc, doc)) is not { } tab) return;
        t.Saving = true;
        try
        {
            await _well.SaveAsync(tab, reason).ConfigureAwait(true);
            t.FailureShown = false;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Auto save of {Uri} failed", doc.Uri);
            if (!t.FailureShown && _windows is not null)
            {
                t.FailureShown = true; // once until a save succeeds: a delayed save retries on every edit
                _ = _windows.ShowMessageAsync(MessageSeverity.Error, $"Auto save could not save '{EditorTab.FileNameFor(doc.Uri)}': {ex.Message}").AsTask();
            }
        }
        finally
        {
            t.Saving = false;
        }
        if (t.Again && !t.Untracked && !_disposed)
        {
            t.Again = false;
            if (doc.IsDirty && t.Mode == Mode.AfterDelay) Arm(t);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        if (_well.SaveOnClose == _saveOnClose) _well.SaveOnClose = null;
        foreach (var doc in _docs.Keys.ToArray()) Untrack(doc);
        _focused = null;
        _active = null;
    }

    private sealed class Tracked(IEditorDocument doc)
    {
        public IEditorDocument Doc { get; } = doc;
        public IDisposable? Changes, Language;
        public ITimer? Timer;
        public Mode Mode;
        public TimeSpan Delay;
        public bool Saving, Again, FailureShown, Untracked;
    }
}
