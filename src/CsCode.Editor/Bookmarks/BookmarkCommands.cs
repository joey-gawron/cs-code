using System.Text;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.View;

namespace CsCode.Editor.Bookmarks;

/// <summary>
/// The bookmark commands (<see cref="BookmarkCommandIds"/>, palette category "Bookmarks", Visual Studio's wording) and the editor's right-click
/// Bookmarks submenu. Commands that act at the caret use the view that last had the keyboard; Next / Previous / List / Clear All work without
/// one (Next then starts at the first bookmark). Going to a bookmark in the view's own document moves its caret there (scrolling only when the
/// line is off screen); in another document it opens it through <see cref="INavigationService"/>. A file that no longer exists loses its
/// bookmarks when navigation reaches it (the status bar says so) and the next one is taken.
/// </summary>
internal static class BookmarkCommands
{
    public const string Submenu = "editor/context/bookmark";
    /// <summary>The focused view's document can have bookmarks (it is one the document manager knows).</summary>
    public const string CanBookmarkKey = "cscode.editor.canBookmark";
    public const string LineHasBookmarkKey = "cscode.editor.lineHasBookmark";
    public const string LineBookmarkEnabledKey = "cscode.editor.lineBookmarkEnabled";
    public const string DocumentHasBookmarksKey = "cscode.editor.documentHasBookmarks";

    private const string Category = "Bookmarks";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(4);

    public static IDisposable Register(EditorViewFactory factory, ICommandService commands, IBookmarkService service)
    {
        var store = new DisposableStore();
        void Reg(string id, string title, Func<CommandInvocation, Task> run) =>
            store.Add(commands.Register(id, async inv =>
            {
                await OnUiAsync(factory, () => run(inv)).ConfigureAwait(false);
                return null;
            }, new CommandMetadata(id, title, Category)));

        Reg(BookmarkCommandIds.Toggle, "Toggle Bookmark", _ =>
        {
            if (Bookmarkable(factory) is { } view)
                foreach (var line in CaretLines(view)) service.Toggle(view.Document.Uri, line);
            return Task.CompletedTask;
        });
        Reg(BookmarkCommandIds.ToggleEnabled, "Enable or Disable Bookmark", _ =>
        {
            if (Bookmarkable(factory) is not { } view) return Task.CompletedTask;
            var lines = CaretLines(view);
            foreach (var b in service.GetForUri(view.Document.Uri).Where(b => lines.Contains(b.Line)))
                service.Update(b with { Enabled = !b.Enabled });
            return Task.CompletedTask;
        });
        Reg(BookmarkCommandIds.ToggleAllEnabled, "Enable or Disable All Bookmarks", _ =>
        {
            service.SetEnabledAll(!service.All.Any(b => b.Enabled));
            return Task.CompletedTask;
        });
        Reg(BookmarkCommandIds.JumpToNext, "Next Bookmark", _ => JumpAsync(factory, service, forward: true, inDocument: false));
        Reg(BookmarkCommandIds.JumpToPrevious, "Previous Bookmark", _ => JumpAsync(factory, service, forward: false, inDocument: false));
        Reg(BookmarkCommandIds.JumpToNextInDocument, "Next Bookmark in Document", _ => JumpAsync(factory, service, forward: true, inDocument: true));
        Reg(BookmarkCommandIds.JumpToPreviousInDocument, "Previous Bookmark in Document", _ => JumpAsync(factory, service, forward: false, inDocument: true));
        Reg(BookmarkCommandIds.Clear, "Clear Bookmarks in Document", _ =>
        {
            if (Bookmarkable(factory) is { } view) service.Clear(view.Document.Uri);
            return Task.CompletedTask;
        });
        Reg(BookmarkCommandIds.ClearFromAllFiles, "Clear All Bookmarks", _ => ClearAllAsync(factory, service));
        Reg(BookmarkCommandIds.List, "List Bookmarks", _ => ListAsync(factory, service));
        store.Add(commands.Register(BookmarkCommandIds.ShowWindow, async _ =>
        {
            // the window is the shell's (CsCode.App); without it (a composition without the shell) the command does nothing
            if (factory.Services.Provider?.GetService(typeof(IViewActivator)) is IViewActivator views)
                await views.ShowViewAsync(BookmarkCommandIds.WindowId).ConfigureAwait(false);
            return null;
        }, new CommandMetadata(BookmarkCommandIds.ShowWindow, "Bookmark Window", "View")));
        return store;
    }

    // ------------------------------------------------------------------ the right-click menu

    public static IReadOnlyList<SubmenuContribution> Submenus { get; } = [new(Submenu, "B_ookmarks", "bookmark")];

    /// <summary>The editor's right-click Bookmarks submenu (in the navigation group, after Find All References).</summary>
    public static IReadOnlyList<MenuContribution> Items { get; } =
    [
        new(MenuIds.EditorContext, Submenu, "2_navigation@9", CanBookmarkKey) { Submenu = Submenu },
        new(Submenu, BookmarkCommandIds.Toggle, "1@1", "!" + LineHasBookmarkKey) { Title = "_Add Bookmark", Icon = "bookmark" },
        new(Submenu, BookmarkCommandIds.Toggle, "1@2", LineHasBookmarkKey) { Title = "_Remove Bookmark" },
        new(Submenu, BookmarkCommandIds.ToggleEnabled, "1@3", LineBookmarkEnabledKey) { Title = "_Disable Bookmark" },
        new(Submenu, BookmarkCommandIds.ToggleEnabled, "1@4", LineHasBookmarkKey + " && !" + LineBookmarkEnabledKey) { Title = "_Enable Bookmark" },
        new(Submenu, BookmarkCommandIds.JumpToNext, "2@1") { Title = "_Next Bookmark" },
        new(Submenu, BookmarkCommandIds.JumpToPrevious, "2@2") { Title = "_Previous Bookmark" },
        new(Submenu, BookmarkCommandIds.Clear, "3@1", DocumentHasBookmarksKey) { Title = "_Clear Bookmarks in Document" },
        new(Submenu, BookmarkCommandIds.ShowWindow, "4@1") { Title = "Bookmark _Window" },
    ];

    public static IDisposable Contribute(IContributionRegistry contributions)
    {
        var dir = AppContext.BaseDirectory;
        return Disposable.Combine(
            contributions.Contribute(ContributionSources.Builtin, dir, Submenus),
            contributions.Contribute(ContributionSources.Builtin, dir, Items));
    }

    /// <summary>The keys the submenu's <c>when</c> clauses read, for <paramref name="view"/> at its caret (O(1) lookups; called by <see cref="EditorMenus.PrepareContextKeys"/>).</summary>
    public static void PrepareContextKeys(CodeEditorControl view, IContextKeyService keys)
    {
        var feature = view.Feature<BookmarkFeature>();
        var at = feature?.BookmarkAt(view.Controller.Caret.Line);
        keys.Set(CanBookmarkKey, feature is not null);
        keys.Set(LineHasBookmarkKey, at is not null);
        keys.Set(LineBookmarkEnabledKey, at?.Enabled ?? false);
        keys.Set(DocumentHasBookmarksKey, feature?.Bookmarks.Count > 0);
    }

    // ------------------------------------------------------------------ behind the commands

    /// <summary>The view commands at the caret act on, when its document can have bookmarks.</summary>
    private static CodeEditorControl? Bookmarkable(EditorViewFactory factory) =>
        factory.ActiveView is { IsDisposed: false } view && BookmarkFeature.IsBookmarkable(view) ? view : null;

    /// <summary>The line of every caret (one per line), the primary's first: Toggle Bookmark acts on each.</summary>
    internal static IReadOnlyList<int> CaretLines(CodeEditorControl view)
    {
        var primary = view.Controller.Caret.Line;
        if (!view.MultiCursor.IsActive) return [primary];
        var doc = view.Editor.Document;
        var lines = new List<int> { primary };
        foreach (var caret in view.MultiCursor.Current.Carets)
        {
            var line = doc.GetLineByOffset(Math.Clamp(caret.Active, 0, doc.TextLength)).LineNumber - 1;
            if (!lines.Contains(line)) lines.Add(line);
        }
        return lines;
    }

    private static async Task JumpAsync(EditorViewFactory factory, IBookmarkService service, bool forward, bool inDocument)
    {
        var view = factory.ActiveView is { IsDisposed: false } v ? v : null;
        if (inDocument && view is null) return;
        var from = view?.Document.Uri;
        var line = view?.Controller.Caret.Line ?? 0;
        // a file that no longer exists drops its bookmarks and the next one is tried: at most once per file
        for (var attempt = 0; attempt < 64; attempt++)
        {
            if (BookmarkNavigation.Next(service.All, from, line, forward, inDocument) is not { } target)
            {
                Status(factory, service.All.Count == 0 ? "There are no bookmarks." : inDocument && !service.All.Any(b => b.Uri == from) ? "There are no bookmarks in this document." : "There are no enabled bookmarks.");
                return;
            }
            if (await GoToAsync(factory, service, target).ConfigureAwait(true)) return;
        }
    }

    /// <summary>
    /// Moves to <paramref name="bookmark"/>: the caret to the start of its line in the visible view of its document, else the document opened
    /// there. False (and its bookmarks removed) when its file no longer exists.
    /// </summary>
    internal static async Task<bool> GoToAsync(EditorViewFactory factory, IBookmarkService service, Bookmark bookmark)
    {
        var view = factory.ActiveView;
        if (view is { IsDisposed: false, IsEffectivelyVisible: true } && view.Document.Uri == bookmark.Uri)
        {
            var line = Math.Clamp(bookmark.Line, 0, Math.Max(0, view.Document.LineCount - 1));
            var at = TextRange.At(line, 0);
            view.Controller.SetSelection(at, reveal: false);
            if (!OnScreen(view, line)) view.Controller.Reveal(at, center: true);
            view.Controller.Focus();
            return true;
        }
        if (factory.Services.Navigation is not { } navigation) return true; // nothing can open it (standalone editor): not a missing file
        if (factory.Services.Documents.Get(bookmark.Uri) is null && !await ExistsAsync(factory, bookmark.Uri).ConfigureAwait(true))
        {
            if (service is BookmarkService concrete) concrete.RemoveUnder(bookmark.Uri);
            else service.Clear(bookmark.Uri);
            Status(factory, $"Removed the bookmarks of {NameOf(bookmark.Uri)}: the file no longer exists.");
            return false;
        }
        try
        {
            await navigation.OpenAsync(bookmark.Uri, TextRange.At(bookmark.Line, 0)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status(factory, $"Could not open {NameOf(bookmark.Uri)}: {ex.Message}");
        }
        return true;
    }

    private static async Task<bool> ExistsAsync(EditorViewFactory factory, Uri uri)
    {
        try { return await factory.Services.FileSystem.StatAsync(uri).ConfigureAwait(true) is { Type: not Core.Platform.FileType.Directory }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return true; } // unknown: let the open say why
    }

    /// <summary>The line is wholly inside the viewport (then going there does not scroll).</summary>
    private static bool OnScreen(CodeEditorControl view, int line)
    {
        var textView = view.TextView;
        if (!textView.VisualLinesValid || textView.GetVisualLine(line + 1) is not { } vl) return false;
        var top = vl.VisualTop - textView.VerticalOffset;
        return top >= 0 && top + vl.Height <= textView.Bounds.Height;
    }

    /// <summary>Visual Studio asks before Clear All Bookmarks deletes them (Ctrl+K Ctrl+L).</summary>
    private static async Task ClearAllAsync(EditorViewFactory factory, IBookmarkService service)
    {
        var count = service.All.Count;
        if (count == 0) return;
        if (factory.Services.Window is { } window)
        {
            var answer = await window.ShowMessageAsync(MessageSeverity.Warning,
                count == 1 ? "Are you sure you want to delete the bookmark?" : $"Are you sure you want to delete all {count} bookmarks?",
                [new MessageAction("Delete"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Delete") return;
        }
        service.ClearAll();
    }

    /// <summary>A quick pick of every bookmark: its label or line text, file and line, folder; picking one goes there.</summary>
    private static async Task ListAsync(EditorViewFactory factory, IBookmarkService service)
    {
        if (factory.Services.Window is not { } window) return;
        var all = service.All;
        if (all.Count == 0)
        {
            Status(factory, "There are no bookmarks.");
            return;
        }
        var texts = await LineTextsAsync(factory, all).ConfigureAwait(true);
        var items = all.Select(b =>
        {
            var text = texts.TryGetValue(b, out var t) ? t.Trim() : "";
            var label = b.Label ?? (text.Length > 0 ? text : $"Line {b.Line + 1}");
            var detail = b.Label is not null && text.Length > 0 ? text : FolderOf(b.Uri);
            return new QuickPickItem((b.Enabled ? "" : "(disabled) ") + label, $"{NameOf(b.Uri)}:{b.Line + 1}", detail, Tag: b);
        }).ToArray();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Bookmarks", Placeholder: "Go to a bookmark (type to filter by text or file)", MatchOnDescription: true, MatchOnDetail: true)).ConfigureAwait(true);
        if (picked?.Tag is Bookmark chosen) await GoToAsync(factory, service, chosen).ConfigureAwait(true);
    }

    /// <summary>
    /// The text of each bookmarked line: from the open document, else read from its file off the UI thread (at most 64 files; the rest show the
    /// line number), so the list never waits on more than a bounded read.
    /// </summary>
    internal static async Task<Dictionary<Bookmark, string>> LineTextsAsync(EditorViewFactory factory, IReadOnlyList<Bookmark> bookmarks)
    {
        const int MaxFilesRead = 64;
        var result = new Dictionary<Bookmark, string>();
        var closed = new List<IGrouping<Uri, Bookmark>>();
        foreach (var group in bookmarks.GroupBy(b => b.Uri))
        {
            if (factory.Services.Documents.Get(group.Key) is { } doc)
                foreach (var b in group) result[b] = b.Line < doc.LineCount ? doc.GetLineText(b.Line) : "";
            else if (closed.Count < MaxFilesRead) closed.Add(group);
        }
        var fs = factory.Services.FileSystem;
        var read = await Task.Run(async () =>
        {
            var lines = new List<(Bookmark, string)>();
            foreach (var group in closed)
            {
                string[] text;
                try { text = SplitLines(await fs.ReadFileAsync(group.Key).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { continue; }
                foreach (var b in group) lines.Add((b, b.Line < text.Length ? text[b.Line] : ""));
            }
            return lines;
        }).ConfigureAwait(true);
        foreach (var (b, text) in read) result[b] = text;
        return result;
    }

    private static string[] SplitLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        return text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    internal static string NameOf(Uri uri) => Path.GetFileName(uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath)) is { Length: > 0 } name ? name : uri.ToString();

    private static string FolderOf(Uri uri) => (uri.IsFile ? Path.GetDirectoryName(uri.LocalPath) : Path.GetDirectoryName(Uri.UnescapeDataString(uri.AbsolutePath))) ?? "";

    private static void Status(EditorViewFactory factory, string text) => factory.Services.Window?.SetStatusBarMessage(text, StatusTimeout);

    /// <summary>Runs on the UI thread (inline when already there) and completes when <paramref name="run"/> has.</summary>
    private static async Task OnUiAsync(EditorViewFactory factory, Func<Task> run)
    {
        var ui = factory.Services.Ui;
        if (ui.CheckAccess())
        {
            await run().ConfigureAwait(true);
            return;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async () =>
        {
            try
            {
                await run();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        await tcs.Task.ConfigureAwait(false);
    }
}
