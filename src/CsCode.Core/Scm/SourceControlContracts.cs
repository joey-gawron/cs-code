using CsCode.Core.Lifecycle;

namespace CsCode.Core.Scm;

// Additive Core contracts added by the source-control work (docs/SOURCE-CONTROL.md). Implemented by CsCode.Panels.Git (decorations, quick diff,
// blame) and the shell (the diff editor tab); consumed by the shell's Explorer and by the editor (gutter change markers, inline blame). All
// optional: without a provider the consumers show nothing.

/// <summary>
/// How the Explorer shows one file or folder: a short badge after the name (<c>M</c>, <c>U</c>, <c>A</c>, <c>D</c>, <c>!</c>, <c>●</c> for a folder
/// with changes inside) and the colour of the name, as a theme colour id (<c>gitDecoration.modifiedResourceForeground</c>...).
/// </summary>
public sealed record FileDecoration(string Badge, string ColorKey, string? Tooltip = null)
{
    /// <summary>The name is drawn struck through (a deleted file still listed).</summary>
    public bool Strikethrough { get; init; }

    /// <summary>The row is dimmed (an ignored file).</summary>
    public bool Faded { get; init; }
}

/// <summary>Supplies Explorer decorations. Thread-safe and cheap: the Explorer asks it for every row it shows, on the UI thread.</summary>
public interface IFileDecorationProvider
{
    /// <summary>The decoration of <paramref name="uri"/>, or null. Must not block (an in-memory lookup).</summary>
    FileDecoration? GetDecoration(Uri uri, bool isDirectory);

    /// <summary>Fires (any thread) when decorations changed: the URIs affected, or null when any may have.</summary>
    IEventSource<IReadOnlyList<Uri>?> DecorationsChanged { get; }
}

/// <summary>The text the editor's gutter compares a document with, and what it is (<c>Index</c>, <c>HEAD</c>).</summary>
public sealed record QuickDiffOriginal(string Text, string Label);

/// <summary>
/// The baseline of the editor's change markers (VS Code's quick diff): for a file in a git repository, its version in the index. Called off the UI
/// thread; implementations run their work on their own lane and cache.
/// </summary>
public interface IQuickDiffProvider
{
    /// <summary>The original of <paramref name="uri"/>; null when there is none (not under source control, untracked, a binary file).</summary>
    ValueTask<QuickDiffOriginal?> GetOriginalAsync(Uri uri, CancellationToken ct = default);

    /// <summary>Fires (any thread) when the original of a file may have changed (staged, committed, checked out): its URI, or null for all files.</summary>
    IEventSource<Uri?> OriginalChanged { get; }

    /// <summary>Stages the lines <paramref name="startLine"/>..<paramref name="endLine"/> (0-based, inclusive, of the document) of the changes in <paramref name="uri"/>. Optional.</summary>
    ValueTask<bool> StageRangeAsync(Uri uri, int startLine, int endLine, CancellationToken ct = default) => ValueTask.FromResult(false);
}

/// <summary>One line's last change, for the inline blame annotation (<c>Jane Doe, 3 weeks ago • Fix the parser</c>).</summary>
public sealed record LineBlame(string Text, string? Tooltip = null);

/// <summary>Annotates the line under the caret with who changed it last. Called off the UI thread.</summary>
public interface ILineBlameProvider
{
    /// <summary>Whether the annotation is on (a setting, <c>git.blame.editorDecoration.enabled</c>).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// The blame of line <paramref name="line"/> (0-based) of <paramref name="uri"/>. <paramref name="documentText"/> is the editor's current text
    /// when it differs from the disk (blame then runs against it, so unsaved lines say "not committed yet"), else null.
    /// </summary>
    ValueTask<LineBlame?> GetLineBlameAsync(Uri uri, int line, string? documentText, CancellationToken ct = default);

    /// <summary>Fires (any thread) when the annotation was turned on or off, or the history moved (a commit, a checkout).</summary>
    IEventSource<object?> Changed { get; }
}

/// <summary>
/// One side of a diff: a fixed text (a revision from git, the clipboard), or a file whose current content is shown and followed (the open
/// document when there is one, else the disk). <see cref="Label"/> names it in the tab and the header (<c>Program.cs (Index)</c>).
/// </summary>
public sealed record DiffSide(string Label, Uri? Uri = null, string? Text = null);

/// <summary>
/// What the diff editor shows. <see cref="Key"/> identifies the tab (opening the same comparison again activates it); <see cref="LanguageHint"/>
/// is a file name or URI whose language colours both sides. <see cref="RevealLine"/> (0-based, modified side) is scrolled to.
/// </summary>
public sealed record DiffEditorRequest(DiffSide Original, DiffSide Modified, string Title, string? LanguageHint = null, int? RevealLine = null)
{
    public string Key => $"{Original.Uri?.ToString() ?? "text:" + Original.Label}|{Modified.Uri?.ToString() ?? "text:" + Modified.Label}";
}

/// <summary>Opens a diff editor tab. Implemented by the shell; callable from any thread.</summary>
public interface IDiffEditorService
{
    ValueTask OpenDiffAsync(DiffEditorRequest request, CancellationToken ct = default);
}

/// <summary>Context keys of the diff editor and source control (when clauses of the keymaps and menus).</summary>
public static class ScmContextKeys
{
    /// <summary>True while the active document is a diff editor (VS Code's <c>textCompareEditorVisible</c>).</summary>
    public const string TextCompareEditorVisible = "textCompareEditorVisible";

    /// <summary>True while the keyboard is in the Source Control commit message box.</summary>
    public const string ScmInputFocus = "scmInputFocus";

    /// <summary>True when a file was picked with Select for Compare (VS Code's <c>resourceSelectedForCompare</c>).</summary>
    public const string ResourceSelectedForCompare = "resourceSelectedForCompare";

    /// <summary>True when the workspace has at least one git repository (VS Code's <c>scmProvider == git</c>, simplified).</summary>
    public const string GitOpenRepositoryCount = "gitOpenRepositoryCount";
}
