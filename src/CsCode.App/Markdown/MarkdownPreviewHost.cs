using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Editor.Markdown.Preview;
using CsCode.Ui.Menus;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Markdown;

/// <summary>
/// A preview tab's content: the <see cref="MarkdownPreviewView"/>, coupled to the editor of the same file.
/// <list type="bullet">
/// <item>Scroll sync by source line, both ways (<c>markdown.preview.scrollPreviewWithEditor</c>, <c>markdown.preview.scrollEditorWithPreview</c>):
/// the editor's top line scrolls the preview to the block of that line, the preview's scrolls the editor. Each side ignores the echo of a scroll
/// it caused. The editor is found by the file's URI and followed as its view is destroyed and recreated by the well's memory policy.</item>
/// <item>Links: <c>#anchors</c> scroll the preview (the view does that); relative paths and <c>file:</c> URIs open in the editor (<c>#L12</c> goes
/// to line 12); http(s) and mailto open in the system browser or mail program; any other scheme asks first.</item>
/// <item>Double-click: the editor at that line (<c>markdown.preview.doubleClickToSwitchToEditor</c>).</item>
/// <item>Edit > Copy and Cmd/Ctrl+C copy the selected preview text (<see cref="IClipboardTarget"/>).</item>
/// </list>
/// Disposed by its tab (with the view and every subscription).
/// </summary>
public sealed class MarkdownPreviewHost : Border, IClipboardTarget, IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly DocumentWell _well;
    private readonly IConfigurationService? _config;
    private readonly DisposableStore _subs = new();
    private EditorTab? _editorTab;
    private IDisposable? _tabSub;
    private IDisposable? _editorSub;
    private IEditorViewController? _editor;
    private double _editorExpected = double.NaN;
    private bool _initialSyncDone;
    private bool _disposed;

    public MarkdownPreviewHost(MarkdownPreviewView view, IServiceProvider sp, DocumentWell well)
    {
        View = view;
        _sp = sp;
        _well = well;
        _config = sp.GetService<IConfigurationService>();
        Child = view;
        _subs.Add(view.Scrolled.Subscribe(OnPreviewScrolled));
        _subs.Add(view.LinkClicked.Subscribe(url => _ = OpenLinkAsync(url)));
        _subs.Add(view.DoubleClickedLine.Subscribe(line => _ = SwitchToEditorAsync(line)));
        _subs.Add(view.Rendered.Subscribe(_ => OnRendered()));
        _subs.Add(well.TabsChanged.Subscribe(_ => FindEditor()));
        FindEditor();
        AddHandler(GotFocusEvent, OnGotFocus, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // Avalonia remembers the last focused element of a container whose tab navigation is "Once" (KeyboardNavigation.TabOnceActiveElement, set
    // on a Dock items control above the tab): a preview that had the keyboard would stay reachable from there after its tab closed. The
    // containers above a focused element are noted (weakly) and the entries that point into this preview are cleared when it is disposed.
    private readonly List<WeakReference<Avalonia.Input.InputElement>> _focusContainers = new();

    private void OnGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (var v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(this); v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is Avalonia.Input.InputElement element && !_focusContainers.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, element)))
                _focusContainers.Add(new WeakReference<Avalonia.Input.InputElement>(element));
        }
    }

    private void ForgetFocusMemory()
    {
        foreach (var w in _focusContainers)
        {
            if (!w.TryGetTarget(out var container)) continue;
            if (Avalonia.Input.KeyboardNavigation.GetTabOnceActiveElement(container) is Avalonia.Visual remembered
                && (ReferenceEquals(remembered, this) || Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, remembered)))
                Avalonia.Input.KeyboardNavigation.SetTabOnceActiveElement(container, null);
        }
        _focusContainers.Clear();
    }

    public MarkdownPreviewView View { get; }

    /// <summary>The editor the preview is coupled with now (null: the file has no editor view).</summary>
    public IEditorViewController? Editor => _editor;

    // ---------------------------------------------------------------- the editor of the file

    private void FindEditor()
    {
        if (_disposed) return;
        var tab = _well.Find(View.Source);
        if (!ReferenceEquals(tab, _editorTab))
        {
            _tabSub?.Dispose();
            _editorTab = tab;
            _tabSub = tab?.ViewCreated.Subscribe(_ => BindEditor(tab.Controller));
        }
        BindEditor(tab?.Controller);
    }

    private void BindEditor(IEditorViewController? editor)
    {
        if (_disposed || ReferenceEquals(editor, _editor)) return;
        _editorSub?.Dispose();
        _editor = editor;
        _editorExpected = double.NaN;
        _editorSub = editor?.Scrolled.Subscribe(OnEditorScrolled);
    }

    private void OnRendered()
    {
        // The first render starts where the editor is (VS Code opens the preview at the editor's position).
        if (_initialSyncDone) return;
        _initialSyncDone = true;
        if (_editor is { } editor && editor.TopLine > 0.5 && Setting(MarkdownPreviewSettings.ScrollPreviewWithEditor)) View.ScrollToLine(editor.TopLine);
    }

    private void OnEditorScrolled(double line)
    {
        if (_disposed) return;
        if (!double.IsNaN(_editorExpected))
        {
            var echo = Math.Abs(line - _editorExpected) < 0.5;
            _editorExpected = double.NaN;
            if (echo) return; // the preview scrolled the editor here
        }
        if (Setting(MarkdownPreviewSettings.ScrollPreviewWithEditor)) View.ScrollToLine(line);
    }

    private void OnPreviewScrolled(double line)
    {
        if (_disposed || _editor is not { } editor || !Setting(MarkdownPreviewSettings.ScrollEditorWithPreview)) return;
        if (Math.Abs(editor.TopLine - line) < 0.25) return;
        _editorExpected = line;
        editor.ScrollToLine(line);
    }

    private bool Setting(string key) => _config?.Get(key, true) ?? true;

    // ---------------------------------------------------------------- links and double-click

    /// <summary>Opens a link of the preview (see the class summary for where each kind goes).</summary>
    public async Task OpenLinkAsync(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url)) return;
        var window = _sp.GetService<IWindowService>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !absolute.IsFile && absolute.Scheme != View.Source.Scheme)
        {
            if (window is null) return;
            if (absolute.Scheme is not ("http" or "https" or "mailto"))
            {
                var answer = await window.ShowMessageAsync(MessageSeverity.Info, $"Open '{absolute}' with the program your system uses for {absolute.Scheme} links?",
                    [new MessageAction("Open"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
                if (answer != "Open") return;
            }
            await window.OpenExternalAsync(absolute).ConfigureAwait(true);
            return;
        }
        if (ResolveLocal(View.Source, url) is not { } local) return;
        var (target, fragment) = local;
        if (target == View.Source && fragment is { Length: > 0 } && !IsLineFragment(fragment, out _))
        {
            View.RevealAnchor(fragment);
            return;
        }
        TextRange? at = fragment is not null && IsLineFragment(fragment, out var line) ? TextRange.At(line, 0) : null;
        if (_sp.GetService<INavigationService>() is { } nav) await nav.OpenAsync(target, at).ConfigureAwait(true);
    }

    /// <summary>A relative or absolute local link against the preview's file: the target file and its fragment (without '#').</summary>
    public static (Uri Target, string? Fragment)? ResolveLocal(Uri source, string url)
    {
        string? fragment = null;
        var hash = url.IndexOf('#');
        var path = url;
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(url[(hash + 1)..]);
            path = url[..hash];
        }
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        if (path.Length == 0) return (source, fragment);
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && (absolute.IsFile || absolute.Scheme == source.Scheme)) return (absolute, fragment);
        return Uri.TryCreate(source, path.Replace(" ", "%20", StringComparison.Ordinal), out var relative) ? (relative, fragment) : null;
    }

    /// <summary>GitHub's line fragments: <c>L12</c> (1-based) → line 11.</summary>
    private static bool IsLineFragment(string fragment, out int line)
    {
        line = 0;
        if (fragment.Length < 2 || fragment[0] is not ('L' or 'l')) return false;
        var digits = fragment.AsSpan(1);
        var dash = digits.IndexOf('-');
        if (dash > 0) digits = digits[..dash];
        if (!int.TryParse(digits, out var n) || n < 1) return false;
        line = n - 1;
        return true;
    }

    private async Task SwitchToEditorAsync(double line)
    {
        if (_disposed || !Setting(MarkdownPreviewSettings.DoubleClickToSwitchToEditor)) return;
        if (_sp.GetService<INavigationService>() is { } nav) await nav.OpenAsync(View.Source, TextRange.At((int)Math.Max(0, line), 0)).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- clipboard

    public bool CanCopy => View.CanCopy;

    /// <summary>Through the app's clipboard service (as every other copy in the shell), or the text block's own copy without one.</summary>
    public async Task CopyAsync()
    {
        if (View.SelectedText is not { } text) return;
        if (_sp.GetService<IClipboardService>() is { } clipboard) await clipboard.WriteAsync(ClipboardText.Plain(text)).ConfigureAwait(true);
        else View.Copy();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveHandler(GotFocusEvent, OnGotFocus);
        ForgetFocusMemory();
        _subs.Dispose();
        _tabSub?.Dispose();
        _editorSub?.Dispose();
        _tabSub = _editorSub = null;
        _editor = null;
        _editorTab = null;
        Child = null;
        View.Dispose();
    }
}
