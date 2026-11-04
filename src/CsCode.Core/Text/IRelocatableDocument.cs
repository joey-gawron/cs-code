namespace CsCode.Core.Text;

/// <summary>
/// An open document that can follow its file to a new path without being closed: the Explorer's rename and move (drag and drop, cut and
/// paste). The buffer, its unsaved changes, its undo history and its views stay; the document manager sees the old identity closed and the
/// new one opened (language servers get didClose / didOpen), as after Save As. Optional: a document without it is closed and reopened.
/// </summary>
public interface IRelocatableDocument
{
    /// <summary>
    /// Makes <paramref name="target"/> the document's identity. Nothing is written: call it after the file was moved there. Callable from any
    /// thread.
    /// </summary>
    Task RelocateAsync(Uri target, CancellationToken ct = default);
}
