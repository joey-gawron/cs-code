using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Tests;

/// <summary>An <see cref="IEditorDocument"/> over <see cref="InMemoryTextDocument"/> with a controllable dirty flag and EOL.</summary>
public sealed class TestEditorDocument : IEditorDocument
{
    private readonly InMemoryTextDocument _doc;
    private readonly Emitter<IEditorDocument> _saved = new(), _dirty = new();
    private bool _isDirty;

    public TestEditorDocument(Uri uri, string text, bool dirty = false, string endOfLine = "\n")
    {
        _doc = new InMemoryTextDocument(uri, "plaintext", text);
        _isDirty = dirty;
        EndOfLine = endOfLine;
        _doc.Changed.Subscribe(_ => { _isDirty = true; _dirty.Fire(this); });
    }

    public int ApplyEditsCalls { get; private set; }
    public Uri Uri => _doc.Uri;
    public string LanguageId => _doc.LanguageId;
    public int Version => _doc.Version;
    public bool IsDirty => _isDirty;
    public int Length => _doc.Length;
    public int LineCount => _doc.LineCount;
    public string GetText() => _doc.GetText();
    public string GetText(TextRange range) => _doc.GetText(range);
    public string GetLineText(int line) => _doc.GetLineText(line);
    public int OffsetAt(TextPosition position) => _doc.OffsetAt(position);
    public TextPosition PositionAt(int offset) => _doc.PositionAt(offset);
    public TextPosition Validate(TextPosition position) => _doc.Validate(position);
    public bool ApplyEdits(IReadOnlyList<TextEdit> edits) { ApplyEditsCalls++; return _doc.ApplyEdits(edits); }
    public IEventSource<TextDocumentChangedEventArgs> Changed => _doc.Changed;
    public IEventSource<ITextDocument> LanguageIdChanged => _doc.LanguageIdChanged;
    public bool IsReadOnly => false;
    public bool IsUntitled => Uri.Scheme == "untitled";
    public string EncodingName => "utf-8";
    public string EndOfLine { get; }
    public void SetLanguageId(string languageId) => _doc.SetLanguageId(languageId);
    public Task SaveAsync(CancellationToken ct = default) { _isDirty = false; _saved.Fire(this); return Task.CompletedTask; }
    public Task SaveAsAsync(Uri target, CancellationToken ct = default) => SaveAsync(ct);
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IEventSource<IEditorDocument> Saved => _saved;
    public IEventSource<IEditorDocument> DirtyChanged => _dirty;
}
