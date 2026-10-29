using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

public enum IndentStyle { Space, Tab }

public enum EndOfLineStyle { Lf, CrLf, Cr }

/// <summary>
/// The EditorConfig properties that apply to one file (https://editorconfig.org). A null member means "not specified": the editor falls back to
/// its own settings (<c>editor.tabSize</c>, detected EOL, …). Resolution rules already applied: <c>indent_size = tab</c> uses <c>tab_width</c>;
/// <c>tab_width</c> defaults to <c>indent_size</c>; <c>indent_style = tab</c> with no <c>indent_size</c> uses <c>tab_width</c>.
/// </summary>
/// <param name="Charset">Lower-case: latin1, utf-8, utf-8-bom, utf-16be, utf-16le.</param>
public sealed record EditorConfigSettings(
    IndentStyle? IndentStyle = null,
    int? IndentSize = null,
    int? TabWidth = null,
    EndOfLineStyle? EndOfLine = null,
    string? Charset = null,
    bool? TrimTrailingWhitespace = null,
    bool? InsertFinalNewline = null)
{
    public static readonly EditorConfigSettings Empty = new();

    public bool IsEmpty => this == Empty;
}

/// <summary>
/// Resolves <c>.editorconfig</c> files for documents. Implemented by Platform; the editor consumes it when present
/// (indentation, EOL, charset, trim-trailing-whitespace and final-newline policies on open/save).
/// </summary>
public interface IEditorConfigService
{
    /// <summary>Resolves the settings for <paramref name="file"/>. Does its file reading off the calling thread; cached results complete synchronously.</summary>
    ValueTask<EditorConfigSettings> GetAsync(Uri file, CancellationToken ct = default);

    /// <summary>The cached result for <paramref name="file"/>, or null if it has not been resolved yet (never touches the disk; safe on a UI thread).</summary>
    EditorConfigSettings? TryGetCached(Uri file);

    /// <summary>Raised when an <c>.editorconfig</c> file changed: results obtained earlier may be stale, ask again.</summary>
    IEventSource<object?> Changed { get; }
}
