using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.TestDoubles;

/// <summary>
/// An <see cref="IEditorDocument"/> over an <see cref="ITextDocument"/>, for <see cref="RecordingNavigationService.ActiveDocument"/> in tests of
/// code that acts on the active editor (rust-analyzer's commands). Saving and reloading do nothing.
/// </summary>
public sealed class EditorDocumentView(ITextDocument inner, bool isReadOnly = false) : IEditorDocument
{
    private readonly Emitter<IEditorDocument> _saved = new();
    private readonly Emitter<IEditorDocument> _dirty = new();

    public ITextDocument Inner => inner;
    public Uri Uri => inner.Uri;
    public string LanguageId => inner.LanguageId;
    public int Version => inner.Version;
    public bool IsDirty => inner.IsDirty;
    public int Length => inner.Length;
    public int LineCount => inner.LineCount;
    public string GetText() => inner.GetText();
    public string GetText(TextRange range) => inner.GetText(range);
    public string GetLineText(int line) => inner.GetLineText(line);
    public int OffsetAt(TextPosition position) => inner.OffsetAt(position);
    public TextPosition PositionAt(int offset) => inner.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => inner.Validate(position);
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => inner.ApplyEdits(edits);
    public IEventSource<TextDocumentChangedEventArgs> Changed => inner.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => inner.LanguageIdChanged;

    public bool IsReadOnly => isReadOnly;
    public bool IsUntitled => false;
    public string EncodingName => "utf-8";
    public string EndOfLine => "\n";
    public void SetLanguageId(string languageId) { }
    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IEventSource<IEditorDocument> Saved => _saved;
    public IEventSource<IEditorDocument> DirtyChanged => _dirty;
}
