using CsCode.App.Docking;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Editor.Markdown.Preview;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Markdown;

/// <summary>
/// The Markdown preview in the shell (docs/MARKDOWN.md): VS Code's <c>markdown.showPreview</c> (Ctrl+Shift+V / Cmd+Shift+V),
/// <c>markdown.showPreviewToSide</c> (Ctrl+K V / Cmd+K V) and <c>markdown.showSource</c>, one preview tab per source file (opening it again
/// activates it), and the <c>markdownPreviewFocus</c> context key while a preview is the active tab. Each tab's content is a
/// <see cref="MarkdownPreviewHost"/>: the editor module's view plus the scroll coupling with the file's editor, links and the clipboard.
/// </summary>
public sealed class MarkdownPreviewIntegration : IDisposable
{
    public const string ShowPreview = "markdown.showPreview";
    public const string ShowPreviewToSide = "markdown.showPreviewToSide";
    public const string ShowSource = "markdown.showSource";
    /// <summary>Context key: the active document tab is a Markdown preview.</summary>
    public const string PreviewFocusKey = "markdownPreviewFocus";
    /// <summary>Prefix of a preview tab's view id (the source URI follows).</summary>
    public const string ViewIdPrefix = "markdown.preview:";

    private readonly IServiceProvider _sp;
    private readonly MarkdownPreviewFactory _factory;
    private readonly DocumentWell _well;
    private readonly DisposableStore _subs = new();

    private MarkdownPreviewIntegration(IServiceProvider sp, MarkdownPreviewFactory factory, DocumentWell well)
    {
        _sp = sp;
        _factory = factory;
        _well = well;
    }

    /// <summary>The integration, or null when the composition has no Markdown preview (no editor module).</summary>
    public static MarkdownPreviewIntegration? TryCreate(IServiceProvider sp) =>
        sp.GetService<MarkdownPreviewFactory>() is { } factory ? new MarkdownPreviewIntegration(sp, factory, sp.GetRequiredService<DocumentWell>()) : null;

    public void Start()
    {
        if (_sp.GetService<ICommandService>() is { } commands)
        {
            // Optional argument: the file's URI (macros, bound args, the Explorer); otherwise the active editor's file.
            void Reg(string id, string title, Func<Uri?, Task> run) => _subs.Add(commands.Register(id, async inv =>
            {
                await run(UriArg(inv)).ConfigureAwait(true);
                return null;
            }, new CommandMetadata(id, title, "Markdown")));
            Reg(ShowPreview, "Open Preview", uri => { Open(uri, toSide: false); return Task.CompletedTask; });
            Reg(ShowPreviewToSide, "Open Preview to the Side", uri => { Open(uri, toSide: true); return Task.CompletedTask; });
            Reg(ShowSource, "Show Source", _ => ShowSourceAsync());
        }
        if (_sp.GetService<IContextKeyService>() is { } keys)
        {
            _subs.Add(_well.ActiveChanged.Subscribe(active => keys.Set(PreviewFocusKey, IsPreviewTab(active))));
            keys.Set(PreviewFocusKey, IsPreviewTab(_well.Active));
        }
    }

    private static Uri? UriArg(CommandInvocation inv) =>
        inv.Args is [{ } arg, ..] ? arg as Uri ?? (Uri.TryCreate(arg.ToString(), UriKind.Absolute, out var parsed) ? parsed : null) : null;

    public static bool IsPreviewTab(ShellDocument? tab) => tab?.Id?.StartsWith("view:" + ViewIdPrefix, StringComparison.Ordinal) == true;

    /// <summary>The source file of a preview tab (null for any other tab).</summary>
    public static Uri? SourceOf(ShellDocument? tab) =>
        IsPreviewTab(tab) && Uri.TryCreate(tab!.Id!["view:".Length..][ViewIdPrefix.Length..], UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>
    /// Opens (or activates) the preview of <paramref name="source"/>, default the active editor's file (or, from a preview, its file). Nothing
    /// happens for a file that is not Markdown. To the side: the preview goes to the group right of the editor (a new one if there is none) and
    /// the keyboard stays in the editor, as in VS Code. Returns the preview tab. UI thread.
    /// </summary>
    public ShellDocument? Open(Uri? source = null, bool toSide = false)
    {
        var editorTab = source is null ? _well.ActiveEditor : _well.Find(source);
        source ??= _well.ActiveEditor?.Uri ?? SourceOf(_well.Active);
        if (source is null) return null;
        var language = (editorTab?.Doc.LanguageId) ?? _sp.GetService<Core.Text.IDocumentManager>()?.Get(source)?.LanguageId;
        if (language is not null ? !MarkdownPreviewFactory.IsMarkdown(language) : !LooksLikeMarkdown(source)) return null;

        var id = ViewIdPrefix + source.AbsoluteUri;
        var existed = _well.Tabs.Any(t => t.Id == "view:" + id);
        var title = "Preview " + EditorTab.TitleFor(source);
        var target = source;
        var tab = _well.OpenView(id, title, () => new MarkdownPreviewHost(_factory.Create(target), _sp, _well), "open-preview");
        if (toSide && !existed)
        {
            // The new tab opened in the active group (the editor's): into the next group, or a new one on the right when there is only one.
            if (!_well.MoveToNextGroup(tab)) _well.SplitRight(tab);
            if (editorTab is not null && !editorTab.IsDisposed) _well.FocusEditor(editorTab);
        }
        else FocusWhenShown(tab);
        return tab;
    }

    /// <summary>
    /// The keyboard moves into a preview opened in place (as in VS Code), so the editor hidden behind it no longer has it (its keys, Cmd+Shift+V
    /// included, would still act on the editor). Dock presents the tab after this turn: wait (bounded) for its view.
    /// </summary>
    private void FocusWhenShown(ShellDocument tab, int attempt = 0)
    {
        if (tab.IsDisposed || !ReferenceEquals(_well.Active, tab)) return;
        if (tab.LiveView is MarkdownPreviewHost { View.Scroller: var scroller } && Avalonia.Controls.TopLevel.GetTopLevel(scroller) is not null && scroller.IsEffectivelyVisible)
        {
            scroller.Focus();
            return;
        }
        if (attempt < 20) Avalonia.Threading.DispatcherTimer.RunOnce(() => FocusWhenShown(tab, attempt + 1), TimeSpan.FromMilliseconds(25));
    }

    private static bool LooksLikeMarkdown(Uri uri)
    {
        var ext = Path.GetExtension(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdown", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mkd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>From a preview: shows its file in the editor (opening it if needed).</summary>
    private async Task ShowSourceAsync()
    {
        if (SourceOf(_well.Active) is not { } source) return;
        if (_well.Find(source) is { } open)
        {
            _well.FocusEditor(open);
            return;
        }
        if (_sp.GetService<INavigationService>() is { } nav) await nav.OpenAsync(source).ConfigureAwait(true);
    }

    public void Dispose() => _subs.Dispose();
}
