using Avalonia.Controls;
using Avalonia.Media;
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

    public Control EnsureView() => _view ??= CreateView();

    protected virtual Control CreateView() => ViewFactory?.Invoke() ?? new TextBlock { Text = Title };

    /// <summary>Drops the live view (memory policy / tab close). Safe to call repeatedly; the view is recreated on next presentation.</summary>
    public virtual void DisposeView()
    {
        if (_view is null) return;
        (_view as IDisposable)?.Dispose();
        _view = null;
    }

    public bool IsDisposed { get; private set; }

    public virtual void Dispose()
    {
        IsDisposed = true;
        DisposeView();
    }
}

/// <summary>A file (or untitled buffer) tab. Owns its dirty subscription; the editor view is created lazily and may be dropped by the LRU policy.</summary>
public sealed class EditorTab : ShellDocument
{
    private readonly IEditorViewFactory _editors;
    private readonly DisposableStore _subs = new();
    private bool _isPreview, _isPinned;

    public EditorTab(IEditorDocument document, IEditorViewFactory editors)
    {
        Doc = document;
        _editors = editors;
        Id = "doc:" + document.Uri;
        Title = TitleFor(document.Uri);
        CanFloat = true;
        IsModified = document.IsDirty;
        _subs.Add(document.DirtyChanged.Subscribe(_ =>
        {
            IsModified = Doc.IsDirty;
            if (Doc.IsDirty) IsPreview = false; // editing a preview tab makes it permanent (VS Code behaviour)
        }));
    }

    public IEditorDocument Doc { get; }
    public Uri Uri => Doc.Uri;
    public bool IsDirty => Doc.IsDirty;

    /// <summary>A preview tab (italic) is replaced by the next preview open; editing or double-click promotes it.</summary>
    public bool IsPreview
    {
        get => _isPreview;
        set { if (SetProperty(ref _isPreview, value)) OnPropertyChanged(nameof(PreviewFontStyle)); }
    }

    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }

    /// <summary>Bound by the tab style so preview tabs render in italic.</summary>
    public FontStyle PreviewFontStyle => _isPreview ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>Caret/selection captured when the view was dropped by the LRU policy; restored when the view is recreated.</summary>
    public (TextPosition Caret, IReadOnlyList<TextRange> Selections)? SavedViewState { get; private set; }

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
            if (SavedViewState is { } s)
            {
                controller.SetSelection(new TextRange(s.Caret, s.Caret), reveal: false);
                if (s.Selections.Count > 0) controller.SetSelection(s.Selections[0], reveal: false);
            }
            if (PendingReveal is { } r) controller.SetSelection(r, reveal: true);
        }
        SavedViewState = null;
        PendingReveal = null;
        _viewCreated.Fire(this);
        return view;
    }

    public override void DisposeView()
    {
        if (LiveView is { } v && _editors.GetController(v) is { } c)
            SavedViewState = (c.Caret, c.Selections.ToArray());
        base.DisposeView();
    }

    public override void Dispose()
    {
        base.Dispose();
        _viewCreated.Clear();
        _subs.Dispose();
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

    public void DisposeView()
    {
        if (_view is null) return;
        (_view as IDisposable)?.Dispose();
        (_view.DataContext as IDisposable)?.Dispose();
        _view.DataContext = null;
        _view = null;
    }

    public void Dispose() => DisposeView();
}
