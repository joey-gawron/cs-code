using CsCode.App.Docking;
using CsCode.App.QuickInput;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Input;

/// <summary>Which part of the shell holds keyboard focus; drives the focus context keys.</summary>
public enum FocusArea { None, Editor, Explorer, List, Panel, Input, Terminal }

/// <summary>
/// Keeps the shell-owned context keys in <see cref="IContextKeyService"/> in sync with reality so menus, keybindings and the palette can
/// gate on `when` clauses: editorTextFocus, editorHasSelection, editorHasMultipleSelections, editorReadonly, editorLangId, resourceExtname, resourceFilename,
/// resourceScheme, explorerViewletFocus, listFocus, panelFocus, inputFocus, terminalFocus, inDebugMode, debugState, isMac/isLinux/isWindows,
/// workspaceFolderCount, hasOpenEditors, activeEditorIsDirty, quickInputVisible. (`config.*` keys belong to Platform.)
/// All handlers run on the UI thread; debug-service events are marshalled onto it.
/// </summary>
public sealed class ContextKeyTracker : IDisposable
{
    private readonly IContextKeyService _keys;
    private readonly DocumentWell _well;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _subs = new();
    private IDisposable? _selectionSub, _viewSub;
    private FocusArea _focus;

    public ContextKeyTracker(IContextKeyService keys, DocumentWell well, IUiDispatcher ui, QuickInputViewModel? quick = null,
        IWorkspaceService? workspace = null, IDebugService? debug = null)
    {
        _keys = keys;
        _well = well;
        _ui = ui;

        keys.Set("isMac", OperatingSystem.IsMacOS());
        keys.Set("isLinux", OperatingSystem.IsLinux());
        keys.Set("isWindows", OperatingSystem.IsWindows());
        keys.Set("workspaceFolderCount", workspace?.Folders.Count ?? 0);
        keys.Set("quickInputVisible", false);
        keys.Set("inDebugMode", false);
        keys.Set("debugState", "inactive");
        keys.Set("hasOpenEditors", false);
        SetFocus(FocusArea.None);
        OnActive(null);

        _subs.Add(well.ActiveChanged.Subscribe(OnActive));
        _subs.Add(well.TabsChanged.Subscribe(_ => keys.Set("hasOpenEditors", well.Tabs.Count > 0)));
        if (quick is not null)
            quick.PropertyChanged += OnQuickChanged;
        _quick = quick;
        if (workspace is not null)
            _subs.Add(workspace.FoldersChanged.Subscribe(_ => _ui.Post(() => keys.Set("workspaceFolderCount", workspace.Folders.Count))));
        if (debug is not null)
        {
            Apply(debug.State);
            _subs.Add(debug.StateChanged.Subscribe(s => _ui.Post(() => Apply(s))));
        }
    }

    private readonly QuickInputViewModel? _quick;

    private void OnQuickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickInputViewModel.IsVisible)) _keys.Set("quickInputVisible", _quick!.IsVisible);
    }

    private void Apply(DebugState state)
    {
        _keys.Set("debugState", state switch { DebugState.Initializing => "initializing", DebugState.Running => "running", DebugState.Stopped => "stopped", _ => "inactive" });
        _keys.Set("inDebugMode", state != DebugState.Inactive);
    }

    /// <summary>
    /// Called by the main window whenever keyboard focus moves (also within a region: a control may have changed a key itself meanwhile, so every
    /// key is written again; the context key service publishes only values that changed).
    /// </summary>
    public void SetFocus(FocusArea area)
    {
        _focus = area;
        _keys.Set("editorTextFocus", area == FocusArea.Editor);
        _keys.Set("explorerViewletFocus", area == FocusArea.Explorer);
        _keys.Set("listFocus", area is FocusArea.Explorer or FocusArea.List);
        _keys.Set("panelFocus", area is FocusArea.Panel or FocusArea.Terminal);
        _keys.Set("inputFocus", area == FocusArea.Input);
        _keys.Set("terminalFocus", area == FocusArea.Terminal);
    }

    public FocusArea Focus => _focus;

    private void OnActive(ShellDocument? tab)
    {
        _selectionSub?.Dispose();
        _viewSub?.Dispose();
        _selectionSub = _viewSub = null;
        var editor = tab as EditorTab;
        var doc = editor?.Doc;
        _keys.Set("activeEditorIsDirty", doc?.IsDirty ?? false);
        _keys.Set("editorReadonly", doc?.IsReadOnly ?? false);
        _keys.Set("editorLangId", doc?.LanguageId ?? "");
        _keys.Set("resourceScheme", doc?.Uri.Scheme ?? "");
        _keys.Set("resourceFilename", editor is null ? "" : EditorTab.TitleFor(doc!.Uri));
        _keys.Set("resourceExtname", editor is null ? "" : Path.GetExtension(EditorTab.TitleFor(doc!.Uri)));
        _keys.Set("editorHasSelection", false);
        _keys.Set("editorHasMultipleSelections", false);
        if (editor is null) return;

        _selectionSub = doc!.DirtyChanged.Subscribe(_ => _keys.Set("activeEditorIsDirty", doc.IsDirty));
        void Attach(EditorTab t)
        {
            if (t.Controller is not { } c) return;
            _keys.Set("editorHasSelection", c.Selections.Any(s => !s.IsEmpty));
            _keys.Set("editorHasMultipleSelections", c.Selections.Count > 1);
            _viewSub?.Dispose();
            _viewSub = c.SelectionChanged.Subscribe(sel =>
            {
                _keys.Set("editorHasSelection", sel.Any(s => !s.IsEmpty));
                _keys.Set("editorHasMultipleSelections", sel.Count > 1); // multi-cursor (VS Code's key)
            });
        }
        Attach(editor);
        if (editor.Controller is null)
        {
            var created = editor.ViewCreated.Subscribe(Attach);
            _selectionSub = Disposable.Combine(_selectionSub, created);
        }
    }

    public void Dispose()
    {
        _selectionSub?.Dispose();
        _viewSub?.Dispose();
        if (_quick is not null) _quick.PropertyChanged -= OnQuickChanged;
        _subs.Dispose();
    }
}
