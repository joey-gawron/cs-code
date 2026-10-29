using Avalonia.Controls;
using Avalonia.Controls.Recycling;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Ui;
using Dock.Model.Mvvm.Controls;

namespace CsCode.App.Docking;

/// <summary>A document-well tab that is not necessarily a file (Welcome page, Find Results as a document, ...).</summary>
public class ShellDocument : Document, IDisposable
{
    private Control? _view;

    /// <summary>Creates the content on first presentation (UI thread). Null for tabs that override <see cref="CreateView"/>.</summary>
    public Func<Control>? ViewFactory { get; init; }

    public Control? LiveView => _view;

    public Control EnsureView()
    {
        if (_view is not null) return _view;
        _view = CreateView();
        OnViewCreated(); // after the assignment: listeners read LiveView (and the editor controller behind it)
        return _view;
    }

    /// <summary>Runs once per created view, after <see cref="LiveView"/> holds it.</summary>
    protected virtual void OnViewCreated() { }

    private Border? _host;
    private WeakReference<TopLevel>? _window;
    // Every Dock cache that presented this tab (a floating window has a DockControl, and a cache, of its own). Weak: the caches outlive no window.
    private readonly List<WeakReference<ControlRecycling>> _recyclers = new(1);

    /// <summary>
    /// What Dock presents: a thin host holding the real view, so the view can be destroyed and recreated while Dock keeps presenting "this tab".
    /// See <see cref="DisposeView"/> for how a host that Dock no longer shows is released.
    /// </summary>
    public Control EnsureHost()
    {
        if (_host is null)
        {
            _host = new Border();
            _host.AttachedToVisualTree += OnHostAttached;
        }
        var view = EnsureView();
        if (!ReferenceEquals(_host.Child, view)) _host.Child = view;
        return _host;
    }

    // Remembered while the host is in the window: focus may have to be moved, and Dock's cache entry dropped, after Dock has detached it.
    private void OnHostAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Avalonia.Visual host) return;
        if (TopLevel.GetTopLevel(host) is { } top) _window = new WeakReference<TopLevel>(top);
        for (var v = host; v is not null; v = v.GetVisualParent())
        {
            if (ControlRecyclingDataTemplate.GetControlRecycling(v) is not ControlRecycling recycling) continue;
            if (!_recyclers.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, recycling))) _recyclers.Add(new WeakReference<ControlRecycling>(recycling));
            break;
        }
    }

    protected virtual Control CreateView() => ViewFactory?.Invoke() ?? new TextBlock { Text = Title };

    /// <summary>
    /// Drops the live view (memory policy / tab close). Safe to call repeatedly; the view is recreated on next presentation.
    /// A host that Dock no longer shows is dropped with it: Dock's control-recycling cache keeps the host it presented for every tab, and a host emptied
    /// while out of the window still lists the removed view in its composition visual (that list is only synchronised when the host is rendered), so
    /// the view would stay reachable through Dock. The next presentation builds a fresh host.
    /// </summary>
    public virtual void DisposeView()
    {
        var view = _view;
        if (view is not null) MoveFocusOff(view);
        if (_host is not null)
        {
            _host.Child = null;
            if (_host.GetVisualParent() is null) ForgetHost();
        }
        if (view is null) return;
        _view = null;
        ReleaseView(view);
    }

    /// <summary>Destroys a view this tab created (after it left the tree). Views that are not the editor's are disposed if they can be.</summary>
    protected virtual void ReleaseView(Control view) => (view as IDisposable)?.Dispose();

    private void ForgetHost()
    {
        if (_host is { } host) host.AttachedToVisualTree -= OnHostAttached;
        _host = null;
        foreach (var w in _recyclers)
            if (w.TryGetTarget(out var recycling)) recycling.Remove(this);
        _recyclers.Clear();
    }

    /// <summary>True when keyboard focus was inside this tab's view as it closed (the well then focuses the editor that takes its place).</summary>
    internal bool HadKeyboardFocus { get; private set; }

    /// <summary>Called by the well just before Dock removes the tab, while the view is still in the window: see <see cref="MoveFocusOff"/>.</summary>
    internal void ReleaseFocus()
    {
        if (_view is { } view && MoveFocusOff(view)) HadKeyboardFocus = true;
    }

    /// <summary>
    /// A focused control stays reachable from Avalonia's focus and input-method machinery until focus moves elsewhere (true of a bare AvaloniaEdit editor,
    /// docs/EDITOR.md; detaching the control is not enough), so a view that holds keyboard focus gives it up before it is destroyed. The window is not
    /// focusable, so focus is cleared; the well then focuses the editor that takes the closed tab's place. Focus elsewhere (Explorer, a tool window) is
    /// left alone. Returns whether focus was inside the view.
    /// </summary>
    private bool MoveFocusOff(Control view)
    {
        var top = TopLevel.GetTopLevel(view);
        if (top is null && _window?.TryGetTarget(out var remembered) == true) top = remembered;
        if (top?.FocusManager is not { } focus) return false;
        var focused = focus.GetFocusedElement() as Avalonia.Visual;
        var inView = view.IsKeyboardFocusWithin || (focused is not null && (ReferenceEquals(focused, view) || view.IsVisualAncestorOf(focused)));
        if (!inView) return false;
        if (!top.Focus()) focus.Focus(null);
        return true;
    }

    public bool IsDisposed { get; private set; }

    /// <summary>Recorded just before the tab closes so the well can pick the most-recently-used successor (Dock would pick a neighbour).</summary>
    internal bool WasActiveOnClose { get; set; }

    public virtual void Dispose()
    {
        IsDisposed = true;
        DisposeView();
        ForgetHost(); // also the cache entry of a host that was still in the window: the tab is gone for good
    }
}

/// <summary>A file (or untitled buffer) tab. Owns its dirty subscription; the editor view is created lazily and may be dropped by the LRU policy.</summary>
public sealed class EditorTab : ShellDocument
{
    private readonly IEditorViewFactory _editors;
    private readonly DisposableStore _subs = new();
    private bool _isPreview, _isPinned;

    private IEditorDocument? _doc;

    public EditorTab(IEditorDocument document, IEditorViewFactory editors)
    {
        _doc = document;
        Uri = document.Uri;
        _editors = editors;
        Id = "doc:" + document.Uri;
        Title = TitleFor(document.Uri);
        CanFloat = true;
        IsModified = document.IsDirty;
        _subs.Add(document.DirtyChanged.Subscribe(_ =>
        {
            if (_doc is not { } d) return;
            IsModified = d.IsDirty;
            if (d.IsDirty) IsPreview = false; // editing a preview tab makes it permanent (VS Code behaviour)
            _dirtyStateChanged.Fire(this);
        }));
    }

    private readonly Emitter<EditorTab> _dirtyStateChanged = new();

    /// <summary>Fires after the document's dirty flag changed (window title, status).</summary>
    public IEventSource<EditorTab> DirtyStateChanged => _dirtyStateChanged;

    /// <summary>Disposables owned by whoever manages the tab (the document well); disposed with the tab.</summary>
    public DisposableStore Owned { get; } = new();

    /// <summary>The document behind this tab. Released on dispose: Dock.Avalonia 12.1 keeps closed dockables reachable after their content was
    /// presented (verified in isolation with a bare DockControl), so a closed tab must not pin its editor document or view.</summary>
    public IEditorDocument Doc => _doc ?? throw new ObjectDisposedException(nameof(EditorTab));
    public Uri Uri { get; }
    public bool IsDirty => _doc?.IsDirty ?? false;

    /// <summary>A preview tab (italic) is replaced by the next preview open; editing or double-click promotes it.</summary>
    public bool IsPreview
    {
        get => _isPreview;
        set { if (SetProperty(ref _isPreview, value)) OnPropertyChanged(nameof(PreviewFontStyle)); }
    }

    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }

    /// <summary>Bound by the tab style so preview tabs render in italic.</summary>
    public FontStyle PreviewFontStyle => _isPreview ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>The editor's view state (caret, selection, scroll position, folds) captured when the view was dropped by the LRU policy; restored when the view is recreated.</summary>
    public EditorViewState? SavedViewState { get; private set; }

    /// <summary>Set while a user-confirmed close is in flight so the Dock closing hook lets it through.</summary>
    public bool CloseConfirmed { get; set; }

    public IEditorViewController? Controller => LiveView is { } v ? _editors.GetController(v) : null;

    private readonly Emitter<EditorTab> _viewCreated = new();

    /// <summary>Raised on the UI thread after the editor view was created (the controller exists from then on).</summary>
    public IEventSource<EditorTab> ViewCreated => _viewCreated;

    /// <summary>A selection to reveal as soon as the view exists (navigation requested before the tab was ever presented).</summary>
    public TextRange? PendingReveal { get; set; }

    protected override Control CreateView()
    {
        var view = _editors.CreateView(Doc);
        var controller = _editors.GetController(view);
        if (controller is not null)
        {
            if (SavedViewState is { } s) controller.RestoreViewState(s); // clamped by the editor if the document changed meanwhile
            if (PendingReveal is { } r) controller.SetSelection(r, reveal: true);
        }
        SavedViewState = null;
        PendingReveal = null;
        return view;
    }

    // Not from CreateView: there LiveView is still null, so a listener asking for Controller (caret, selection) would get nothing.
    protected override void OnViewCreated() => _viewCreated.Fire(this);

    public override void DisposeView()
    {
        // GetViewState -> DisposeView now, CreateView + RestoreViewState later (docs/EDITOR.md): the tab comes back exactly as it was.
        if (LiveView is { } v && _editors.GetController(v) is { } c) SavedViewState = c.GetViewState();
        base.DisposeView();
    }

    /// <summary>The editor owns its views' teardown (handlers, renderers, margins, undo-history references): hand the view back to it.</summary>
    protected override void ReleaseView(Control view) => _editors.DisposeView(view);

    public override void Dispose()
    {
        base.Dispose();
        _viewCreated.Clear();
        _dirtyStateChanged.Clear();
        Owned.Dispose();
        _subs.Dispose();
        _doc = null;
        SavedViewState = null;
    }

    public static string TitleFor(Uri uri)
    {
        if (uri.Scheme == "untitled") return uri.AbsolutePath.Length > 0 ? uri.AbsolutePath : uri.ToString();
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        return Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path;
    }
}

/// <summary>A dockable tool window instance (Explorer, Output, ...). The view is created when first shown and disposed when hidden.</summary>
public sealed class ToolTab : Tool, IDisposable
{
    private Control? _view;
    private readonly IServiceProvider _services;

    public ToolTab(IToolWindow spec, IServiceProvider services)
    {
        Spec = spec;
        _services = services;
        Id = "tool:" + spec.Id;
        Title = spec.Title;
        CanFloat = true;
        CanPin = true;
    }

    public IToolWindow Spec { get; }
    public string? IconKey => Spec.IconKey;
    public Control? LiveView => _view;

    public Control EnsureView() => _view ??= Spec.CreateView(_services);

    private Border? _host;

    /// <summary>The thin host Dock presents (see <see cref="ShellDocument.EnsureHost"/>).</summary>
    public Control EnsureHost()
    {
        _host ??= new Border();
        var view = EnsureView();
        if (!ReferenceEquals(_host.Child, view)) _host.Child = view;
        return _host;
    }

    public void DisposeView()
    {
        if (_host is not null) _host.Child = null;
        if (_view is null) return;
        (_view as IDisposable)?.Dispose();
        (_view.DataContext as IDisposable)?.Dispose();
        _view.DataContext = null;
        _view = null;
    }

    public void Dispose()
    {
        DisposeView();
        _host = null;
    }
}
