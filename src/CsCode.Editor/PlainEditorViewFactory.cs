using Avalonia.Controls;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Editor;

/// <summary>
/// STUB: TextBox-based placeholder so the shell can be developed in parallel with the real editor.
/// The Editor module owner replaces this with the AvaloniaEdit/TextMate implementation (see docs/ARCHITECTURE.md).
/// </summary>
internal sealed class PlainEditorViewFactory : IEditorViewFactory
{
    private int _untitled;
    private readonly Dictionary<Control, PlainController> _controllers = new();

    public ValueTask<IEditorDocument> OpenDocumentAsync(Uri uri, CancellationToken ct = default)
    {
        var text = uri.IsFile && File.Exists(uri.LocalPath) ? File.ReadAllText(uri.LocalPath) : string.Empty;
        return ValueTask.FromResult<IEditorDocument>(new PlainDocument(uri, "plaintext", text));
    }

    public IEditorDocument CreateUntitled(string? languageId = null) =>
        new PlainDocument(new Uri($"untitled:Untitled-{++_untitled}"), languageId ?? "plaintext", string.Empty);

    public Control CreateView(IEditorDocument document)
    {
        var box = new TextBox { AcceptsReturn = true, Text = document.GetText(), FontFamily = "monospace" };
        _controllers[box] = new PlainController(document);
        return box;
    }

    public IEditorViewController? GetController(Control view) => _controllers.GetValueOrDefault(view);

    public ValueTask CloseDocumentAsync(IEditorDocument document) => ValueTask.CompletedTask;

    private sealed class PlainDocument : IEditorDocument
    {
        private readonly InMemoryTextDocument _inner;
        private readonly Emitter<IEditorDocument> _saved = new(), _dirty = new();

        public PlainDocument(Uri uri, string lang, string text) => _inner = new InMemoryTextDocument(uri, lang, text);

        public Uri Uri => _inner.Uri;
        public string LanguageId => _inner.LanguageId;
        public int Version => _inner.Version;
        public bool IsDirty => _inner.IsDirty;
        public int Length => _inner.Length;
        public int LineCount => _inner.LineCount;
        public bool IsReadOnly => false;
        public bool IsUntitled => Uri.Scheme == "untitled";
        public string EncodingName => "utf-8";
        public string EndOfLine => "\n";
        public string GetText() => _inner.GetText();
        public string GetText(TextRange range) => _inner.GetText(range);
        public string GetLineText(int line) => _inner.GetLineText(line);
        public int OffsetAt(TextPosition position) => _inner.OffsetAt(position);
        public TextPosition PositionAt(int offset) => _inner.PositionAt(offset);
        public TextPosition Validate(TextPosition position) => _inner.Validate(position);
        public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => _inner.ApplyEdits(edits);
        public IEventSource<TextDocumentChangedEventArgs> Changed => _inner.Changed;
        public IEventSource<ITextDocument> LanguageIdChanged => _inner.LanguageIdChanged;
        public IEventSource<IEditorDocument> Saved => _saved;
        public IEventSource<IEditorDocument> DirtyChanged => _dirty;
        public void SetLanguageId(string languageId) => _inner.SetLanguageId(languageId);
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PlainController(IEditorDocument document) : IEditorViewController
    {
        private readonly Emitter<TextPosition> _caret = new();
        private readonly Emitter<IReadOnlyList<TextRange>> _sel = new();
        public IEditorDocument Document { get; } = document;
        public TextPosition Caret => TextPosition.Zero;
        public IReadOnlyList<TextRange> Selections => [];
        public void SetSelection(TextRange range, bool reveal = true) { }
        public void Reveal(TextRange range, bool center = true) { }
        public void Focus() { }
        public bool CanUndo => false;
        public bool CanRedo => false;
        public void Undo() { }
        public void Redo() { }
        public void ShowFind(bool replace) { }
        public void FormatDocument() { }
        public void SetDebugStopLine(int? line) { }
        public IEventSource<TextPosition> CaretChanged => _caret;
        public IEventSource<IReadOnlyList<TextRange>> SelectionChanged => _sel;
    }
}
