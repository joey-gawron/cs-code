using CsCode.Core.Platform;

namespace CsCode.Editor.Documents;

/// <summary>Setting keys owned by the editor module (VS Code names where they exist; <c>cscode.*</c> otherwise).</summary>
public static class EditorSettingKeys
{
    public const string FilesEncoding = "files.encoding";
    public const string FilesEol = "files.eol";
    public const string FilesInsertFinalNewline = "files.insertFinalNewline";
    public const string FilesTrimFinalNewlines = "files.trimFinalNewlines";
    public const string FilesTrimTrailingWhitespace = "files.trimTrailingWhitespace";
    public const string UndoLimit = "cscode.editor.undoLimit";
    public const string MaxFileSizeBytes = "cscode.editor.maxFileSizeBytes";
    public const string LargeFileThresholdBytes = SettingKeys.EditorLargeFileOptimizationsLimit;
    public const string LargeFileLongLine = "cscode.editor.largeFileLongLineChars";
    public const string FormatOnSave = SettingKeys.EditorFormatOnSave;
    public const string FormatOnType = "editor.formatOnType";
    public const string FormatOnPaste = "editor.formatOnPaste";
}

/// <summary>Document-level settings snapshot (read at load/save; cheap).</summary>
internal sealed record FileSettings(
    FileEncoding? Encoding,
    string Eol,
    bool InsertFinalNewline,
    bool TrimFinalNewlines,
    bool TrimTrailingWhitespace,
    long LargeFileThresholdBytes,
    long MaxFileSizeBytes,
    int LongLineChars,
    int UndoLimit,
    bool FormatOnSave)
{
    public const long DefaultLargeFileThreshold = 5L * 1024 * 1024;
    public const long DefaultMaxFileSize = 200L * 1024 * 1024;
    public const int DefaultLongLineChars = 20_000;

    /// <summary>
    /// Reads the settings; with <paramref name="languageId"/> (known once the document has one: save, reload) the keys VS Code lets a language
    /// override (<c>files.encoding</c>, <c>files.eol</c>, the save participants, <c>editor.formatOnSave</c>) honour its <c>"[lang]"</c> section.
    /// The size limits are the application's, not a language's.
    /// </summary>
    public static FileSettings Read(IConfigurationService cfg, string? languageId = null) => new(
        Encoding: FileEncoding.FromSettingName(cfg.GetOr<string?>(EditorSettingKeys.FilesEncoding, languageId, null)),
        Eol: cfg.GetOr(EditorSettingKeys.FilesEol, languageId, "auto"),
        InsertFinalNewline: cfg.GetOr(EditorSettingKeys.FilesInsertFinalNewline, languageId, false),
        TrimFinalNewlines: cfg.GetOr(EditorSettingKeys.FilesTrimFinalNewlines, languageId, false),
        TrimTrailingWhitespace: cfg.GetOr(EditorSettingKeys.FilesTrimTrailingWhitespace, languageId, false),
        LargeFileThresholdBytes: cfg.GetOr(EditorSettingKeys.LargeFileThresholdBytes, DefaultLargeFileThreshold),
        MaxFileSizeBytes: cfg.GetOr(EditorSettingKeys.MaxFileSizeBytes, DefaultMaxFileSize),
        LongLineChars: cfg.GetOr(EditorSettingKeys.LargeFileLongLine, DefaultLongLineChars),
        UndoLimit: cfg.GetOr(EditorSettingKeys.UndoLimit, 1000),
        FormatOnSave: cfg.GetOr(EditorSettingKeys.FormatOnSave, languageId, false));

    /// <summary>The EOL for new/empty buffers: the setting when explicit, else the platform default.</summary>
    public string DefaultEol => Eol is "\n" or "\r\n" or "\r" ? Eol : OperatingSystem.IsWindows() ? "\r\n" : "\n";
}

/// <summary>Thrown by open when a file exceeds <c>cscode.editor.maxFileSizeBytes</c> (default 200 MB). The message is user-facing.</summary>
public sealed class FileTooLargeException(Uri uri, long size, long limit)
    : IOException($"'{Path.GetFileName(uri.LocalPath)}' is {size / (1024 * 1024)} MB, which is larger than the {limit / (1024 * 1024)} MB limit the editor opens. " +
                  "Use a dedicated tool for files this large (or raise cscode.editor.maxFileSizeBytes at your own risk).")
{
    public Uri Uri { get; } = uri;
    public long Size { get; } = size;
}
