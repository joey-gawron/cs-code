using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Editing;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// Everyday editing commands under VS Code's ids and palette titles (docs/EDITOR.md §7): View: Toggle Word Wrap, Editor Font Zoom In / Out /
/// Reset (the Ctrl+wheel zoom), the Transform to ... Case commands and Duplicate Selection. Each acts on the view that last had focus; the text
/// commands work at every caret (multi-cursor) and make ONE undo step.
/// </summary>
internal static class EditorTextCommands
{
    public const string ToggleWordWrap = "editor.action.toggleWordWrap";
    public const string FontZoomIn = "editor.action.fontZoomIn";
    public const string FontZoomOut = "editor.action.fontZoomOut";
    public const string FontZoomReset = "editor.action.fontZoomReset";
    public const string DuplicateSelection = "editor.action.duplicateSelection";

    /// <summary>The case commands: id, VS Code's palette title, the transform.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, TextTransforms.Case Case)> CaseCommands =
    [
        ("editor.action.transformToUppercase", "Transform to Uppercase", TextTransforms.Case.Upper),
        ("editor.action.transformToLowercase", "Transform to Lowercase", TextTransforms.Case.Lower),
        ("editor.action.transformToTitlecase", "Transform to Title Case", TextTransforms.Case.Title),
        ("editor.action.transformToSnakecase", "Transform to Snake Case", TextTransforms.Case.Snake),
        ("editor.action.transformToCamelcase", "Transform to Camel Case", TextTransforms.Case.Camel),
        ("editor.action.transformToKebabcase", "Transform to Kebab Case", TextTransforms.Case.Kebab),
        ("editor.action.transformToPascalcase", "Transform to Pascal Case", TextTransforms.Case.Pascal),
    ];

    /// <summary>Selections covering more characters than this are transformed on the editor worker, from a snapshot.</summary>
    private const int WorkerThreshold = 150_000;

    public static IDisposable Register(EditorViewFactory factory, ICommandService commands)
    {
        var store = new DisposableStore();

        // Like EditorCommands' (the view that last had focus, on the UI thread), with VS Code's titles: no "Editor:" category in the palette.
        void Cmd(string id, string title, string? category, Func<CodeEditorControl, Task> run) =>
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
                new CommandMetadata(id, title, category)));

        Cmd(ToggleWordWrap, "Toggle Word Wrap", "View", view => { view.ToggleWordWrap(); return Task.CompletedTask; });
        Cmd(FontZoomIn, "Editor Font Zoom In", null, view => { view.ZoomFont(1); return Task.CompletedTask; });
        Cmd(FontZoomOut, "Editor Font Zoom Out", null, view => { view.ZoomFont(-1); return Task.CompletedTask; });
        Cmd(FontZoomReset, "Editor Font Zoom Reset", null, view => { view.ZoomFont(0); return Task.CompletedTask; });

        foreach (var (id, title, kind) in CaseCommands)
            Cmd(id, title, null, view => ApplyAsync(factory, view, reveal: false,
                (doc, selections) => TextTransforms.TransformCase(doc, selections, kind, factory.Services.Config.GetOr("editor.wordSeparators", doc.LanguageId, TextTransforms.DefaultWordSeparators))));
        Cmd(DuplicateSelection, "Duplicate Selection", null, view => ApplyAsync(factory, view, reveal: true,
            (doc, selections) => TextTransforms.Duplicate(doc, selections, view.Document.EndOfLine)));
        return store;
    }

    /// <summary>
    /// Computes the edits for every selection of <paramref name="view"/> (on the editor worker from a snapshot when the selections are large;
    /// dropped if the text changed meanwhile), applies them as ONE undo step and puts the selections where they belong. UI thread.
    /// </summary>
    private static async Task ApplyAsync(EditorViewFactory factory, CodeEditorControl view, bool reveal, Func<Documents.EditorDocument, IReadOnlyList<TextRange>, TextTransforms.Result?> compute)
    {
        var doc = view.Document;
        if (doc.IsReadOnly) return;
        var selections = view.Controller.Selections;
        TextTransforms.Result? result;
        var selected = 0L;
        foreach (var s in selections) selected += doc.OffsetAt(s.End) - doc.OffsetAt(s.Start);
        if (selected < WorkerThreshold) result = compute(doc, selections);
        else
        {
            var version = doc.Version;
            result = await factory.Worker.RunAsync(WorkPriority.Interactive, _ => compute(doc, selections)).ConfigureAwait(true);
            if (view.IsDisposed || doc.Version != version) return;
        }
        if (result is null || !doc.ApplyEdits(result.Edits)) return;
        var after = new List<TextRange>(result.Selections.Count);
        foreach (var (start, end) in result.Selections) after.Add(new TextRange(doc.PositionAt(start), doc.PositionAt(end)));
        view.Controller.SetSelections(after, reveal);
    }
}
