using System.Text;
using System.Text.Json;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Inline merge conflict resolution, VS Code's built-in merge-conflict support (docs/SOURCE-CONTROL.md, "Merge conflicts"): the settings, the code
/// lens above each block ("Accept Current Change | Accept Incoming Change | Accept Both Changes | Compare Changes", through
/// <see cref="ILanguageFeatureRegistry"/> so the editor's own code lens draws it), the commands under VS Code's ids and the editor's right-click
/// items while the caret is in a block. Works on any document with markers, git or not. Each accept is one undo step; Accept All edits every
/// block in one step. The blocks come from <see cref="MergeConflictTracker"/> (per version, on the editor worker); a command waits for the
/// current version's scan, so it never acts on stale offsets.
/// </summary>
internal static class MergeConflictCommands
{
    public const string AcceptCurrent = "merge-conflict.accept.current";
    public const string AcceptIncoming = "merge-conflict.accept.incoming";
    public const string AcceptBoth = "merge-conflict.accept.both";
    public const string AcceptSelection = "merge-conflict.accept.selection";
    public const string AcceptAllCurrent = "merge-conflict.accept.all-current";
    public const string AcceptAllIncoming = "merge-conflict.accept.all-incoming";
    public const string AcceptAllBoth = "merge-conflict.accept.all-both";
    public const string Next = "merge-conflict.next";
    public const string Previous = "merge-conflict.previous";
    public const string Compare = "merge-conflict.compare";

    public const string CodeLensSetting = "merge-conflict.codeLens.enabled";

    /// <summary>The first argument of a lens's command: the block is named by the next two (document URI, start line), not the caret.</summary>
    public const string KnownConflict = "known-conflict";

    public const string NotInConflict = "Editor cursor is not within a merge conflict";
    public const string NoConflicts = "No merge conflicts found in this file";

    private const string Category = "Merge Conflict";

    private static ConfigurationProperty P(string key, object value, string description) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse("""{"type":"boolean"}""").RootElement.Clone());

    /// <summary>VS Code's merge-conflict settings (names, defaults and descriptions).</summary>
    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        P(CodeLensSetting, true, "Create a CodeLens for merge conflict blocks within editor."),
        P(MergeConflictFeature.DecoratorsSetting, true, "Create decorators for merge conflict blocks within editor."),
    ];

    /// <summary>The editor's right-click items, shown while the caret is in a block (<c>mergeConflictAtCursor</c>), above everything else.</summary>
    public static IReadOnlyList<MenuContribution> Menus { get; } =
    [
        new(MenuIds.EditorContext, AcceptCurrent, "0_mergeConflict@1", MergeConflictFeature.AtCursorKey + " && !editorReadonly") { Title = "Accept _Current Change" },
        new(MenuIds.EditorContext, AcceptIncoming, "0_mergeConflict@2", MergeConflictFeature.AtCursorKey + " && !editorReadonly") { Title = "Accept _Incoming Change" },
        new(MenuIds.EditorContext, AcceptBoth, "0_mergeConflict@3", MergeConflictFeature.AtCursorKey + " && !editorReadonly") { Title = "Accept _Both Changes" },
        new(MenuIds.EditorContext, Compare, "0_mergeConflict@4", MergeConflictFeature.AtCursorKey) { Title = "Compare C_hanges", Icon = "diff" },
    ];

    /// <summary>Registers the settings, the lens provider, the commands and the menu items; dispose to withdraw them.</summary>
    public static IDisposable Register(EditorViewFactory factory)
    {
        var services = factory.Services;
        var store = new DisposableStore();
        store.Add(services.Config.RegisterProperties(Properties));
        var lenses = new LensProvider(factory);
        store.Add(services.Features.Register<ICodeLensProvider>(DocumentSelector.All, lenses, "cscode.mergeConflicts"));
        store.Add(services.Config.Changed.Subscribe(e => { if (e.Affects(CodeLensSetting)) lenses.FireChanged(); }));
        store.Add(Disposable.Create(lenses.Dispose));
        if (services.Commands is not { } commands) return store;

        void Cmd(string id, string title, Func<CommandInvocation, Task> run)
        {
            if (commands.Has(id)) return;
            store.Add(commands.Register(id, async inv =>
            {
                await services.Ui.InvokeAsync(() => { }).ConfigureAwait(false); // hop to the UI thread (inline when already there)
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                services.Ui.Post(async () =>
                {
                    try { await run(inv); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                await tcs.Task.ConfigureAwait(false);
                return null;
            }, new CommandMetadata(id, title, Category)));
        }

        Cmd(AcceptCurrent, "Accept Current", inv => AcceptAsync(factory, inv, MergeConflictChoice.Current));
        Cmd(AcceptIncoming, "Accept Incoming", inv => AcceptAsync(factory, inv, MergeConflictChoice.Incoming));
        Cmd(AcceptBoth, "Accept Both", inv => AcceptAsync(factory, inv, MergeConflictChoice.Both));
        Cmd(AcceptSelection, "Accept Selection", inv => AcceptAsync(factory, inv, choice: null));
        Cmd(AcceptAllCurrent, "Accept All Current", inv => AcceptAllAsync(factory, inv, MergeConflictChoice.Current));
        Cmd(AcceptAllIncoming, "Accept All Incoming", inv => AcceptAllAsync(factory, inv, MergeConflictChoice.Incoming));
        Cmd(AcceptAllBoth, "Accept All Both", inv => AcceptAllAsync(factory, inv, MergeConflictChoice.Both));
        Cmd(Next, "Next Conflict", _ => NavigateAsync(factory, forward: true));
        Cmd(Previous, "Previous Conflict", _ => NavigateAsync(factory, forward: false));
        Cmd(Compare, "Compare Current Conflict", inv => CompareAsync(factory, inv));
        store.Add(services.Contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, Menus));
        return store;
    }

    // ------------------------------------------------------------------------------------- targets

    /// <summary>What a command acts on: a document, the view it was run in (if any) and the line naming the block (a lens's, else the caret's).</summary>
    private sealed record Target(EditorDocument Document, CodeEditorControl? View, int Line, int Column, bool FromLens);

    private static Target? TargetOf(EditorViewFactory factory, CommandInvocation inv)
    {
        if (ArgString(inv, 0) == KnownConflict && ArgString(inv, 1) is { } s && Uri.TryCreate(s, UriKind.Absolute, out var uri) && inv.Arg<int?>(2) is { } line)
        {
            var doc = factory.TryGetOpenDocument(uri);
            if (doc is null) return null;
            var view = factory.ActiveView is { IsDisposed: false } a && ReferenceEquals(a.Document, doc) ? a : factory.Views.FirstOrDefault(v => ReferenceEquals(v.Document, doc) && !v.IsDisposed);
            return new Target(doc, view, line, 0, FromLens: true);
        }
        if (factory.ActiveView is not { IsDisposed: false } active) return null;
        var caret = active.TextArea.Caret;
        return new Target(active.Document, active, caret.Line - 1, caret.Column - 1, FromLens: false);
    }

    private static string? ArgString(CommandInvocation inv, int index) => index < inv.Args.Count ? inv.Args[index] switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } j => j.GetString(),
        _ => null,
    } : null;

    /// <summary>The blocks of the document's current version (waits for the scan; UI thread before and after).</summary>
    internal static async Task<MergeConflictSet?> CurrentSetAsync(EditorViewFactory factory, EditorDocument doc)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var set = await factory.MergeConflicts.GetAsync(doc).ConfigureAwait(true);
            if (set.Version == doc.Version) return set;
        }
        return null; // the text keeps changing under us (a macro typing): do nothing rather than act on old offsets
    }

    private static void Warn(EditorViewFactory factory, string message)
    {
        if (factory.Services.Window is { } window) _ = window.ShowMessageAsync(MessageSeverity.Warning, message).AsTask();
    }

    // ------------------------------------------------------------------------------------- commands

    /// <summary>Accept Current / Incoming / Both of the block named by a lens or holding the caret; <paramref name="choice"/> null: the side the caret is in.</summary>
    internal static async Task AcceptAsync(EditorViewFactory factory, CommandInvocation inv, MergeConflictChoice? choice)
    {
        if (TargetOf(factory, inv) is not { } t || t.Document.IsReadOnly) return;
        if (await CurrentSetAsync(factory, t.Document).ConfigureAwait(true) is not { } set) return;
        var conflict = set.At(t.Line);
        if (conflict is null || t.FromLens && conflict.StartLine != t.Line)
        {
            Warn(factory, t.FromLens ? "The merge conflict is no longer there" : NotInConflict);
            return;
        }
        if (choice is null)
        {
            switch (conflict.PartAt(t.Line))
            {
                case MergeConflictPart.CurrentHeader or MergeConflictPart.Current: choice = MergeConflictChoice.Current; break;
                case MergeConflictPart.Incoming or MergeConflictPart.IncomingFooter: choice = MergeConflictChoice.Incoming; break;
                case MergeConflictPart.CommonAncestors:
                    Warn(factory, "Editor cursor is within the common ancestors block, please move it to either the \"current\" or \"incoming\" block");
                    return;
                default:
                    Warn(factory, "Editor cursor is within the merge conflict splitter, please move it to either the \"current\" or \"incoming\" block");
                    return;
            }
        }
        // ONE edit: one undo step
        t.Document.ApplyEdits([new TextEdit(conflict.Range, conflict.Resolve(set.Text, choice.Value))]);
    }

    /// <summary>
    /// Accept All Current / Incoming / Both: every block of the active document in one undo step, or, with file URIs as arguments (the Source
    /// Control window's merge rows), every block of those files: open documents are edited in their editor, closed files on disk.
    /// </summary>
    internal static async Task AcceptAllAsync(EditorViewFactory factory, CommandInvocation inv, MergeConflictChoice choice)
    {
        var uris = inv.Args.Select(a => a switch
        {
            Uri u => u,
            string s when s != KnownConflict && Uri.TryCreate(s, UriKind.Absolute, out var p) => p,
            JsonElement { ValueKind: JsonValueKind.String } j when Uri.TryCreate(j.GetString(), UriKind.Absolute, out var p) => p,
            _ => null,
        }).OfType<Uri>().Distinct().ToList();
        if (uris.Count == 0)
        {
            if (factory.ActiveView is not { IsDisposed: false } view || view.Document.IsReadOnly) return;
            if (await CurrentSetAsync(factory, view.Document).ConfigureAwait(true) is not { } set) return;
            if (set.Conflicts.Count == 0) { Warn(factory, NoConflicts); return; }
            view.Document.ApplyEdits(EditsFor(set.Conflicts, set.Text, choice));
            return;
        }
        var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>>();
        foreach (var uri in uris)
        {
            if (factory.TryGetOpenDocument(uri) is { } doc)
            {
                if (doc.IsReadOnly || await CurrentSetAsync(factory, doc).ConfigureAwait(true) is not { Conflicts.Count: > 0 } set) continue;
                if (factory.Services.Navigation is null) doc.ApplyEdits(EditsFor(set.Conflicts, set.Text, choice));
                else changes[uri] = EditsFor(set.Conflicts, set.Text, choice);
                continue;
            }
            // a file that is not open: read it, scan it on the worker, and edit it on disk
            byte[] bytes;
            try { bytes = await factory.Services.FileSystem.ReadFileAsync(uri).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var conflicts = await factory.Worker.RunAsync(WorkPriority.Interactive, ct => MergeConflictParser.Parse(text, ct)).ConfigureAwait(true);
            if (conflicts.Count == 0) continue;
            if (factory.Services.Navigation is not null) { changes[uri] = EditsFor(conflicts, text, choice); continue; }
            var resolved = MergeConflictParser.ResolveAll(text, conflicts, choice);
            var encoded = Encoding.UTF8.GetBytes(resolved);
            await factory.Services.FileSystem.WriteFileAsync(uri, bom ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded).ConfigureAwait(true);
        }
        if (changes.Count > 0 && factory.Services.Navigation is { } navigation)
            await navigation.ApplyWorkspaceEditAsync(new WorkspaceEdit(changes), "Accept All").ConfigureAwait(true);
    }

    private static List<TextEdit> EditsFor(IReadOnlyList<MergeConflict> conflicts, string text, MergeConflictChoice choice) =>
        [.. conflicts.Select(c => new TextEdit(c.Range, c.Resolve(text, choice)))];

    /// <summary>Next / Previous Conflict: the caret goes to the start of the next block after it (before it), wrapping around.</summary>
    internal static async Task NavigateAsync(EditorViewFactory factory, bool forward)
    {
        if (factory.ActiveView is not { IsDisposed: false } view) return;
        if (await CurrentSetAsync(factory, view.Document).ConfigureAwait(true) is not { } set || view.IsDisposed) return;
        var list = set.Conflicts;
        if (list.Count == 0) { Warn(factory, NoConflicts); return; }
        var caret = view.Controller.Caret;
        MergeConflict target;
        if (forward) target = list.FirstOrDefault(c => c.StartLine > caret.Line) ?? list[0];
        else target = list.LastOrDefault(c => c.StartLine < caret.Line || c.StartLine == caret.Line && caret.Character > 0) ?? list[^1];
        view.Controller.SetSelection(TextRange.At(target.StartLine, 0), reveal: true);
    }

    /// <summary>
    /// Compare Current Conflict (the lens's Compare Changes): the whole file with every block taken as its current side next to the whole file
    /// with every block taken as its incoming side, in the diff editor, at the block (VS Code's comparison).
    /// </summary>
    internal static async Task CompareAsync(EditorViewFactory factory, CommandInvocation inv)
    {
        if (TargetOf(factory, inv) is not { } t) return;
        if (await CurrentSetAsync(factory, t.Document).ConfigureAwait(true) is not { } set) return;
        var conflict = set.At(t.Line);
        if (conflict is null) { Warn(factory, NotInConflict); return; }
        if (factory.Services.Provider?.GetService(typeof(IDiffEditorService)) is not IDiffEditorService diffs)
        {
            if (t.View is { } v) FeatureSupport.Status(v, "The diff editor is not available");
            return;
        }
        var text = set.Text;
        var (left, right) = await factory.Worker.RunAsync(WorkPriority.Interactive, _ =>
            (MergeConflictParser.ResolveAll(text, set.Conflicts, MergeConflictChoice.Current), MergeConflictParser.ResolveAll(text, set.Conflicts, MergeConflictChoice.Incoming))).ConfigureAwait(true);
        // where the block starts in the incoming text: the earlier blocks lost their markers and their current sides
        var line = conflict.StartLine;
        foreach (var c in set.Conflicts)
        {
            if (ReferenceEquals(c, conflict)) break;
            line -= c.EndLine - c.StartLine + 1 - (c.Incoming.ContentEndLine - c.Incoming.ContentStartLine);
        }
        var uri = t.Document.Uri;
        var name = Path.GetFileName(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);
        await diffs.OpenDiffAsync(new DiffEditorRequest(new DiffSide($"{name}: Current Changes", Text: left), new DiffSide($"{name}: Incoming Changes", Text: right),
            $"{name}: Current Changes ↔ Incoming Changes", uri.ToString(), Math.Max(0, line))).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------- code lens

    /// <summary>
    /// The lens line above each block (VS Code's titles and order). A provider in <see cref="ILanguageFeatureRegistry"/> for every document, so
    /// the editor's code lens (<see cref="CodeLensFeature"/>: zones, hover, click, the stable layout) draws it like any server's lenses; it answers
    /// from the tracker's scan of the version asked for and nothing for read-only documents. <c>merge-conflict.codeLens.enabled</c> false (or
    /// <c>editor.codeLens</c> false) shows none.
    /// </summary>
    internal sealed class LensProvider(EditorViewFactory factory) : ICodeLensProvider, IDisposable
    {
        private readonly Emitter<Uri?> _changed = new();

        public IEventSource<Uri?> CodeLensesChanged => _changed;

        internal void FireChanged() => _changed.Fire(null);

        public async ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct)
        {
            if (document is not EditorDocument doc || doc.IsReadOnly || doc.IsLargeFile || !MergeConflictFeature.AppliesTo(doc)) return null;
            if (factory.Services.Config.Get<bool?>(CodeLensSetting) == false) return null;
            var set = await factory.MergeConflicts.GetAsync(doc, ct).ConfigureAwait(false);
            if (set.Conflicts.Count == 0) return [];
            var uri = JsonSerializer.SerializeToElement(doc.Uri.ToString());
            var known = JsonSerializer.SerializeToElement(KnownConflict);
            var lenses = new List<CodeLens>(set.Conflicts.Count * 4);
            foreach (var c in set.Conflicts)
            {
                IReadOnlyList<JsonElement> args = [known, uri, JsonSerializer.SerializeToElement(c.StartLine)];
                var at = TextRange.At(c.StartLine, 0);
                lenses.Add(new CodeLens(at, new Command("Accept Current Change", AcceptCurrent, args)));
                lenses.Add(new CodeLens(at, new Command("Accept Incoming Change", AcceptIncoming, args)));
                lenses.Add(new CodeLens(at, new Command("Accept Both Changes", AcceptBoth, args)));
                lenses.Add(new CodeLens(at, new Command("Compare Changes", Compare, args)));
            }
            return lenses;
        }

        public void Dispose() => _changed.Clear();
    }
}
