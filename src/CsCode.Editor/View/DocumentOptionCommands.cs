using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// What the status bar's encoding, line-ending and indentation items open (docs/EDITOR.md §7), under VS Code's ids: Change File Encoding (Reopen
/// with Encoding / Save with Encoding, then the encoding), Change End of Line Sequence (LF / CRLF), and the indentation picker
/// (<c>changeEditorIndentation</c>: Indent Using Spaces / Tabs, Change Tab Display Size, Detect Indentation from Content, Convert Indentation to
/// Spaces / Tabs, each also a command of its own). The indentation choices are the document's own (<see cref="EditorDocument.IndentationOverride"/>),
/// over the settings and its <c>.editorconfig</c>, as VS Code's model options. Each acts on the document of the view that last had focus.
/// </summary>
internal static class DocumentOptionCommands
{
    public const string ChangeEncoding = "workbench.action.editor.changeEncoding";
    public const string ChangeEol = "workbench.action.editor.changeEOL";
    public const string ChangeIndentation = "changeEditorIndentation";
    public const string IndentUsingSpaces = "editor.action.indentUsingSpaces";
    public const string IndentUsingTabs = "editor.action.indentUsingTabs";
    public const string ChangeTabDisplaySize = "editor.action.changeTabDisplaySize";
    public const string DetectIndentation = "editor.action.detectIndentation";
    public const string IndentationToSpaces = "editor.action.indentationToSpaces";
    public const string IndentationToTabs = "editor.action.indentationToTabs";

    private const string Reopen = "Reopen with Encoding";
    private const string SaveWith = "Save with Encoding";

    /// <summary>Documents above this many characters are converted / examined on the editor worker, from a snapshot.</summary>
    private const int WorkerThreshold = 150_000;

    /// <summary>The indentation picker's entries: VS Code's titles, in its order.</summary>
    internal static readonly IReadOnlyList<(string Id, string Title, string Detail)> IndentationActions =
    [
        (IndentUsingSpaces, "Indent Using Spaces", "Change View"),
        (IndentUsingTabs, "Indent Using Tabs", "Change View"),
        (ChangeTabDisplaySize, "Change Tab Display Size", "Change View"),
        (DetectIndentation, "Detect Indentation from Content", "Change View"),
        (IndentationToSpaces, "Convert Indentation to Spaces", "Convert File"),
        (IndentationToTabs, "Convert Indentation to Tabs", "Convert File"),
    ];

    public static IDisposable Register(EditorViewFactory factory, ICommandService commands)
    {
        var store = new DisposableStore();

        void Cmd(string id, string? title, Func<CodeEditorControl, Task> run) =>
            store.Add(commands.Register(
                id,
                async _ =>
                {
                    var view = factory.ActiveView;
                    if (view is null || view.IsDisposed) return null;
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    factory.Services.Ui.Post(async () =>
                    {
                        try
                        {
                            if (!view.IsDisposed) await run(view);
                            tcs.SetResult();
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });
                    await tcs.Task.ConfigureAwait(false);
                    return null;
                },
                new CommandMetadata(id, title)));

        Cmd(ChangeEncoding, "Change File Encoding", view => ChangeEncodingAsync(factory, commands, view));
        Cmd(ChangeEol, "Change End of Line Sequence", view => ChangeEolAsync(factory, view));
        Cmd(ChangeIndentation, null, view => PickIndentationActionAsync(factory, commands, view)); // the status bar item's (VS Code: not in the palette)
        Cmd(IndentUsingSpaces, "Indent Using Spaces", view => PickTabSizeAsync(factory, view, insertSpaces: true));
        Cmd(IndentUsingTabs, "Indent Using Tabs", view => PickTabSizeAsync(factory, view, insertSpaces: false));
        Cmd(ChangeTabDisplaySize, "Change Tab Display Size", view => PickTabSizeAsync(factory, view, insertSpaces: null));
        Cmd(DetectIndentation, "Detect Indentation from Content", view => DetectAsync(factory, view));
        Cmd(IndentationToSpaces, "Convert Indentation to Spaces", view => ConvertAsync(factory, view, toSpaces: true));
        Cmd(IndentationToTabs, "Convert Indentation to Tabs", view => ConvertAsync(factory, view, toSpaces: false));
        return store;
    }

    // ---- encoding ----

    private static async Task ChangeEncodingAsync(EditorViewFactory factory, ICommandService commands, CodeEditorControl view)
    {
        if (factory.Services.Window is not { } window) return;
        var doc = view.Document;
        // an untitled document has no file to reopen: Save with Encoding only (VS Code)
        var action = doc.IsUntitled ? SaveWith : (await window.ShowQuickPickAsync(
            [new QuickPickItem(Reopen, Detail: "Reopen"), new QuickPickItem(SaveWith, Detail: "Save")],
            new QuickPickOptions(Placeholder: "Select Action")).ConfigureAwait(true))?.Label;
        if (action is null || view.IsDisposed) return;

        var reopen = action == Reopen;
        var current = doc.EncodingName;
        var items = FileEncoding.Choices.Select(c => new QuickPickItem(c.Label, Description: c.Id == current ? c.Id + " (current)" : c.Id, Picked: c.Id == current, Tag: c.Id)).ToList();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Placeholder: reopen ? "Select File Encoding to Reopen File" : "Select File Encoding to Save with")).ConfigureAwait(true);
        if (picked?.Tag is not string id || FileEncoding.FromSettingName(id) is not { } encoding || view.IsDisposed) return;

        if (!reopen)
        {
            doc.SetEncoding(encoding);
            // the shell's save (its will-save participants) when this document is the one it saves; else the editor's own
            if (factory.Services.Navigation?.ActiveDocument == doc && commands.Has(KnownCommands.Save)) await commands.ExecuteAsync(KnownCommands.Save).ConfigureAwait(true);
            else await doc.SaveAsync().ConfigureAwait(true);
            return;
        }
        if (doc.IsDirty)
        {
            var name = Path.GetFileName(doc.Uri.IsFile ? doc.Uri.LocalPath : doc.Uri.AbsolutePath);
            var answer = await window.ShowMessageAsync(MessageSeverity.Warning,
                $"'{name}' has unsaved changes. Reopening it with another encoding discards them.",
                [new MessageAction("Discard and Reopen"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(true);
            if (answer != "Discard and Reopen" || view.IsDisposed) return;
        }
        await doc.ReloadAsync(encoding, CancellationToken.None).ConfigureAwait(true);
    }

    // ---- line endings ----

    private static async Task ChangeEolAsync(EditorViewFactory factory, CodeEditorControl view)
    {
        if (factory.Services.Window is not { } window || view.Document.IsReadOnly) return;
        var current = view.Document.EndOfLine;
        var picked = await window.ShowQuickPickAsync(
            [new QuickPickItem("LF", Picked: current == "\n", Tag: "\n"), new QuickPickItem("CRLF", Picked: current == "\r\n", Tag: "\r\n")],
            new QuickPickOptions(Placeholder: "Select End of Line Sequence")).ConfigureAwait(true);
        if (picked?.Tag is not string eol || view.IsDisposed) return;
        await view.Document.ChangeEndOfLineAsync(eol).ConfigureAwait(true);
    }

    // ---- indentation ----

    private static async Task PickIndentationActionAsync(EditorViewFactory factory, ICommandService commands, CodeEditorControl view)
    {
        if (factory.Services.Window is not { } window) return;
        var readOnly = view.Document.IsReadOnly;
        var items = IndentationActions.Where(a => !readOnly || a.Detail != "Convert File")
            .Select(a => new QuickPickItem(a.Title, Description: a.Detail, Tag: a.Id)).ToList();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Placeholder: "Select Action")).ConfigureAwait(true);
        if (picked?.Tag is string id && !view.IsDisposed) await commands.ExecuteAsync(id).ConfigureAwait(true);
    }

    /// <summary>VS Code's tab size picker (1 to 8, the current one first-picked); <paramref name="insertSpaces"/> null keeps spaces or tabs as they are.</summary>
    private static async Task PickTabSizeAsync(EditorViewFactory factory, CodeEditorControl view, bool? insertSpaces)
    {
        if (factory.Services.Window is not { } window) return;
        var doc = view.Document;
        var (current, spaces) = Current(view);
        var configured = Math.Clamp(factory.Services.Config.GetOr(SettingKeys.EditorTabSize, doc.LanguageId, 4), 1, 16);
        var items = Enumerable.Range(1, 8).Select(n => new QuickPickItem(n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Description: n == configured && n == current ? "Configured Tab Size" : n == configured ? "Default Tab Size" : n == current ? "Current Tab Size" : null,
            Picked: n == current, Tag: n)).ToList();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Placeholder: "Select Tab Size for the Current File")).ConfigureAwait(true);
        if (picked?.Tag is not int size || view.IsDisposed) return;
        doc.IndentationOverride = new IndentationOptions(size, insertSpaces ?? spaces);
    }

    /// <summary>The indentation the document has now: its own choice, else what its views read from the settings and .editorconfig.</summary>
    private static (int TabSize, bool InsertSpaces) Current(CodeEditorControl view) =>
        view.Document.IndentationOverride is { } chosen ? (chosen.TabSize, chosen.InsertSpaces) : (view.Settings.TabSize, view.Settings.InsertSpaces);

    private static async Task DetectAsync(EditorViewFactory factory, CodeEditorControl view)
    {
        var doc = view.Document;
        // what the settings and .editorconfig say is the guess's default (VS Code: the creation options)
        var defaults = EditorSettings.Read(factory.Services.Config, doc.LanguageId, doc.EditorConfigProperties);
        var guess = doc.Length < WorkerThreshold
            ? IndentationOperations.Guess(doc, defaults.TabSize, defaults.InsertSpaces)
            : await factory.Worker.RunAsync(WorkPriority.Interactive, _ => IndentationOperations.Guess(doc, defaults.TabSize, defaults.InsertSpaces)).ConfigureAwait(true);
        if (!view.IsDisposed) doc.IndentationOverride = new IndentationOptions(guess.TabSize, guess.InsertSpaces);
    }

    private static async Task ConvertAsync(EditorViewFactory factory, CodeEditorControl view, bool toSpaces)
    {
        var doc = view.Document;
        if (doc.IsReadOnly) return;
        var tabSize = Current(view).TabSize;
        var version = doc.Version;
        var edits = doc.Length < WorkerThreshold
            ? IndentationOperations.Convert(doc, tabSize, toSpaces)
            : await factory.Worker.RunAsync(WorkPriority.Interactive, _ => IndentationOperations.Convert(doc, tabSize, toSpaces)).ConfigureAwait(true);
        if (view.IsDisposed || doc.Version != version) return;
        if (edits.Count > 0 && !doc.ApplyEdits(edits)) return; // ONE undo step
        doc.IndentationOverride = new IndentationOptions(tabSize, toSpaces); // and the document indents that way from now on (VS Code)
    }
}
