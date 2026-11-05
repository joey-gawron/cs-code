using System.Text;
using AvaloniaEdit.Document;
using CsCode.Core.Lifecycle;

namespace CsCode.Editor.Documents;

/// <summary>
/// Indentation chosen for one document (the status bar's Indent Using Spaces / Tabs, Change Tab Display Size, Detect Indentation from Content):
/// VS Code's per-model options. <see cref="TabSize"/> is the tab width and the indentation unit (CsCode has no separate indent size).
/// </summary>
public readonly record struct IndentationOptions(int TabSize, bool InsertSpaces);

// Per-document options the status bar shows and changes: indentation, line endings, encoding (docs/EDITOR.md §7).
public sealed partial class EditorDocument
{
    private IndentationOptions? _indentation;
    private readonly Emitter<EditorDocument> _optionsChanged = new();

    /// <summary>
    /// The indentation chosen for this document, over the settings and its <c>.editorconfig</c>, for as long as it is open (VS Code's model
    /// options); null: they decide. Every view of the document follows it, and so does format on save. Set on the UI thread.
    /// </summary>
    public IndentationOptions? IndentationOverride
    {
        get => _indentation;
        set
        {
            var clamped = value is { } v ? v with { TabSize = Math.Clamp(v.TabSize, 1, 16) } : (IndentationOptions?)null;
            if (_indentation == clamped) return;
            _indentation = clamped;
            FireOptionsChanged();
        }
    }

    /// <summary>The encoding, the line ending or <see cref="IndentationOverride"/> changed (UI thread): the status bar and the views follow.</summary>
    public IEventSource<EditorDocument> OptionsChanged => _optionsChanged;

    private void FireOptionsChanged()
    {
        if (!_disposed) _optionsChanged.Fire(this);
    }

    /// <summary>
    /// VS Code's Change End of Line Sequence: rewrites every line ending as ONE undo step (undo also gives back the old <see cref="EndOfLine"/>);
    /// the document becomes dirty when a line ending changed. The new text is computed from a snapshot on the IO lane and applied as one change
    /// whose offset map moves each caret, selection and fold by its own line ending only, so nothing jumps. Applied on the UI thread.
    /// </summary>
    public async Task ChangeEndOfLineAsync(string eol, CancellationToken ct = default)
    {
        if (eol is not ("\n" or "\r\n" or "\r")) throw new ArgumentException("eol must be \\n, \\r\\n or \\r", nameof(eol));
        for (var attempt = 0; attempt < MaxSaveParticipantRetries; attempt++)
        {
            var snap = Snapshot();
            var plan = _svc is { } svc
                ? await svc.Io.Run(() => EolPlan.Compute(snap.GetText(), eol), ct).ConfigureAwait(false)
                : EolPlan.Compute(snap.GetText(), eol);
            if (await OnUi(() => Version == snap.Version && ApplyEndOfLine(eol, plan)).ConfigureAwait(false)) return;
        }
        await OnUi(() => ApplyEndOfLine(eol, EolPlan.Compute(_text.Text, eol))).ConfigureAwait(false); // the user is typing fast: do it here
    }

    /// <summary>Changes the file's line-ending style: rewrites the buffer's terminators as ONE undoable edit (the file becomes dirty).</summary>
    public void SetEndOfLine(string eol)
    {
        if (eol is not ("\n" or "\r\n" or "\r")) throw new ArgumentException("eol must be \\n, \\r\\n or \\r", nameof(eol));
        _ui.Invoke(() => ApplyEndOfLine(eol, EolPlan.Compute(_text.Text, eol)));
    }

    private bool ApplyEndOfLine(string eol, EolPlan? plan)
    {
        var before = _eol;
        if (plan is { } p)
        {
            BeginUndoGroup(continueLast: false);
            try
            {
                AddUndoMarker(redo =>
                {
                    _eol = redo ? eol : before;
                    FireOptionsChanged();
                });
                _text.Replace(0, _text.TextLength, p.Text, p.Map);
            }
            finally
            {
                EndUndoGroup();
            }
        }
        _eol = eol;
        if (before != eol || plan is not null) FireOptionsChanged();
        return true;
    }

    /// <summary>A whole-text rewrite of the line endings with the offset map that keeps every anchor next to its own text.</summary>
    private sealed record EolPlan(string Text, OffsetChangeMap Map)
    {
        /// <summary>Null when every line ending already is <paramref name="eol"/>.</summary>
        public static EolPlan? Compute(string text, string eol)
        {
            OffsetChangeMap? map = null;
            var sb = new StringBuilder(text.Length + 16);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c is not ('\r' or '\n'))
                {
                    sb.Append(c);
                    continue;
                }
                var length = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                if (!text.AsSpan(i, length).SequenceEqual(eol))
                    (map ??= new OffsetChangeMap()).Add(new OffsetChangeMapEntry(sb.Length, length, eol.Length)); // offsets after the entries before it
                sb.Append(eol);
                i += length - 1;
            }
            if (map is null) return null;
            map.Freeze();
            return new EolPlan(sb.ToString(), map);
        }
    }

    /// <summary>"Save with Encoding": the encoding the next save writes. Does not touch the buffer.</summary>
    internal void SetEncoding(FileEncoding encoding)
    {
        if (ReferenceEquals(_encoding, encoding)) return;
        _encoding = encoding;
        FireOptionsChanged();
    }
}
