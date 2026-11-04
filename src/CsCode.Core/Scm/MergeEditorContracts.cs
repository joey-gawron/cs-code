namespace CsCode.Core.Scm;

// Additive Core contracts added by the merge-conflict work (docs/SOURCE-CONTROL.md, "Merge editor"): the git module asks for a 3-way merge
// editor, the shell opens it as a document tab over the editor's MergeEditorControl. Optional: without a service the Source Control window
// offers no "Resolve in Merge Editor".

/// <summary>
/// What the 3-way merge editor shows: the three versions of a conflicted file (<see cref="Base"/> = git's stage 1, <see cref="Current"/> = stage 2,
/// ours, <see cref="Incoming"/> = stage 3, theirs) and the file itself (<see cref="Result"/>), whose open document is the editable result.
/// <see cref="CurrentLabel"/> / <see cref="IncomingLabel"/> name the sides (<c>HEAD</c>, a branch). <see cref="Complete"/> runs after Complete
/// Merge saved the result (git: stage the file, which marks it resolved).
/// </summary>
public sealed record MergeEditorRequest(Uri Result, string Base, string Current, string Incoming, string Title)
{
    public string? CurrentLabel { get; init; }
    public string? IncomingLabel { get; init; }
    public Func<CancellationToken, Task>? Complete { get; init; }
}

/// <summary>Opens a merge editor tab (one per file: opening it again activates it). Implemented by the shell; callable from any thread.</summary>
public interface IMergeEditorService
{
    ValueTask OpenMergeEditorAsync(MergeEditorRequest request, CancellationToken ct = default);
}
