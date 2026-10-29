using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Remote.Protocol;

/// <summary>Protocol constants. Bump <see cref="Version"/> on any incompatible change; client and server refuse a mismatch at the handshake.</summary>
public static class RemoteProtocol
{
    public const int Version = 1;

    /// <summary>The server writes this line on stdout before the framed stream starts; the client skips whatever a login shell printed before it.</summary>
    public const string Greeting = "CSCODE-SERVER-READY v1";

    /// <summary>Capability names exchanged in the handshake.</summary>
    public static class Capabilities
    {
        public const string Gzip = "gzip";
        public const string Languages = "languages";
        public const string Search = "search";
        public const string Watch = "watch";
    }
}

/// <summary>JSON-RPC method names (client → server unless marked).</summary>
public static class RemoteMethods
{
    public const string Hello = "hello";
    public const string Ping = "$/ping";
    public const string Shutdown = "server/shutdown";

    public const string FsStat = "fs/stat";
    public const string FsReadDir = "fs/readDir";
    public const string FsRead = "fs/read";
    public const string FsWrite = "fs/write";
    public const string FsWriteBegin = "fs/writeBegin";
    public const string FsWriteChunk = "fs/writeChunk";
    public const string FsWriteCommit = "fs/writeCommit";
    public const string FsWriteAbort = "fs/writeAbort";
    public const string FsMkdir = "fs/mkdir";
    public const string FsDelete = "fs/delete";
    public const string FsRename = "fs/rename";
    public const string FsCopy = "fs/copy";

    public const string WatchStart = "watch/start";
    public const string WatchStop = "watch/stop";
    /// <summary>server → client notification</summary>
    public const string WatchChanges = "watch/changes";

    public const string SearchFiles = "search/files";
    public const string SearchText = "search/text";
    public const string SearchNext = "search/next";
    public const string SearchCancel = "search/cancel";

    public const string WorkspaceSetFolders = "workspace/setFolders";
    public const string SettingsSetUser = "settings/setUser";

    // ---- R2: documents and language features
    public const string DocOpen = "doc/open";
    public const string DocChange = "doc/change";
    public const string DocClose = "doc/close";
    public const string DocSaved = "doc/saved";
    public const string DocLanguage = "doc/language";
    /// <summary>server → client notifications: a language-feature provider appeared / went away on the server.</summary>
    public const string LangRegistered = "lang/registered";
    public const string LangUnregistered = "lang/unregistered";
    public const string LangInvoke = "lang/invoke";
    public const string LangWillSave = "lang/willSave";
    public const string LangFileOperation = "lang/fileOperation";
    /// <summary>server → client notification: diagnostics of a batch of documents.</summary>
    public const string DiagPublish = "diag/publish";
    /// <summary>server → client notification: language server list changed (process isolation mode shows it in the status bar).</summary>
    public const string LspServers = "lsp/servers";
    public const string LspControl = "lsp/control";
    public const string LspAddDefinition = "lsp/addDefinition";
    public const string LspRemoveDefinition = "lsp/removeDefinition";
    /// <summary>server → client notification: commands the language servers contributed on the server (code actions/lenses invoke them).</summary>
    public const string CommandsChanged = "commands/changed";
    public const string CommandsExecute = "commands/execute";

    // ---- R2: server → client UI requests (window/navigation forwarding)
    public const string WindowShowMessage = "window/showMessage";
    public const string WindowQuickPick = "window/quickPick";
    public const string WindowInputBox = "window/inputBox";
    public const string WindowOpenExternal = "window/openExternal";
    public const string WindowClipboard = "window/clipboard";
    public const string WindowStatus = "window/status";
    public const string WindowProgress = "window/progress";
    /// <summary>client → server: the user cancelled a progress notification.</summary>
    public const string WindowProgressCancel = "window/progressCancel";
    public const string NavOpen = "nav/open";
    public const string NavApplyEdit = "nav/applyEdit";
    /// <summary>server → client: text appended to one of the server's output channels (bounded, coalesced).</summary>
    public const string OutputAppend = "output/append";
}

// ------------------------------------------------------------------------------------------------------------------- handshake

public sealed record HelloParams(int ProtocolVersion, string ClientVersion, IReadOnlyList<string> Capabilities, string Mode);

public sealed record HelloResult(
    int ProtocolVersion,
    string ServerVersion,
    string Os,
    string Arch,
    int ProcessId,
    string Home,
    string DataDir,
    IReadOnlyList<string> Capabilities);

// ------------------------------------------------------------------------------------------------------------------- file system

public sealed record PathParams(string Path);

/// <summary><see cref="FileStat"/> plus an etag ("mtimeTicks-size") for conflict detection.</summary>
public sealed record WireStat(FileType Type, long Size, DateTimeOffset ModifiedUtc, bool IsReadOnly, string Etag)
{
    public FileStat ToFileStat() => new(Type, Size, ModifiedUtc, IsReadOnly);
    public static string EtagOf(DateTimeOffset modifiedUtc, long size) => modifiedUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ReadDirResult(IReadOnlyList<DirectoryEntry> Entries);

/// <summary>Reads at most <see cref="Payload.ChunkSize"/> bytes at <paramref name="Offset"/>.</summary>
public sealed record ReadParams(string Path, long Offset, int Length);

/// <summary>A range read: the chunk, the file's size and etag at the time (a client re-reads when chunks disagree: the file changed mid-read).</summary>
public sealed record ReadResult(Payload Chunk, long Size, string Etag);

/// <summary>Small write in one message (≤ one chunk). <paramref name="ExpectedEtag"/>: fail with FileModified unless the file still has it ("" = must not exist).</summary>
public sealed record WriteParams(string Path, Payload Content, bool CreateParents, string? ExpectedEtag = null);

public sealed record WriteBeginParams(string Path, long Size, bool CreateParents, string? ExpectedEtag = null);
public sealed record WriteBeginResult(string UploadId);
public sealed record WriteChunkParams(string UploadId, long Offset, Payload Chunk);
public sealed record UploadParams(string UploadId);

public sealed record DeleteParams(string Path, bool Recursive, bool UseTrash);
public sealed record MoveParams(string Source, string Target, bool Overwrite);

// ------------------------------------------------------------------------------------------------------------------- watch / search

public sealed record WatchStartParams(string WatchId, string Root, bool Recursive, IReadOnlyList<string>? Excludes);
public sealed record WatchStopParams(string WatchId);
public sealed record WireFileChange(string Path, FileChangeKind Kind);
public sealed record WatchChangesParams(string WatchId, IReadOnlyList<WireFileChange> Changes);

public sealed record SearchFilesParams(string Folder, string? IncludeGlob, IReadOnlyList<string> ExcludeGlobs, bool UseIgnoreFiles, bool FollowSymlinks, int MaxResults);

/// <param name="Pattern">Literal text or a .NET regular expression.</param>
public sealed record SearchTextParams(
    IReadOnlyList<string> Folders,
    string Pattern,
    bool IsRegex = false,
    bool MatchCase = false,
    bool WholeWord = false,
    string? IncludeGlob = null,
    IReadOnlyList<string>? ExcludeGlobs = null,
    bool UseIgnoreFiles = true,
    int MaxResults = 20_000,
    long MaxFileBytes = 50L * 1024 * 1024);

public sealed record SearchSession(string SessionId);
public sealed record SearchNextParams(string SessionId, int Max = 1000);

/// <summary>One text match: 0-based line/column (UTF-16) and the line text (truncated to a preview).</summary>
public sealed record TextSearchMatch(string Path, int Line, int Column, int Length, string Preview);

public sealed record SearchBatch(IReadOnlyList<string>? Paths, IReadOnlyList<TextSearchMatch>? Matches, bool Done, string? Error = null, int FilesSearched = 0);

public sealed record SetFoldersParams(IReadOnlyList<string> Folders);

/// <summary>The client's user settings.json text (JSONC); the server writes it as its own user settings file, which its configuration service reloads.</summary>
public sealed record SetUserSettingsParams(string Text);

// ------------------------------------------------------------------------------------------------------------------- documents (R2)

public sealed record DocOpenParams(Uri Uri, string LanguageId, int Version, string Text);
public sealed record DocChangeParams(Uri Uri, int Version, IReadOnlyList<TextContentChange> Changes);
public sealed record DocUriParams(Uri Uri);
public sealed record DocLanguageParams(Uri Uri, string LanguageId);

// ------------------------------------------------------------------------------------------------------------------- language features (R2)

/// <summary>A provider registered in the server's feature registry, mirrored by a bridge provider on the client.</summary>
/// <param name="Feature">The provider interface name (<c>IHoverProvider</c>…).</param>
/// <param name="Info">Static provider data the client needs without a round trip: trigger characters, semantic-token legends.</param>
public sealed record LangRegistration(long Id, string Feature, DocumentSelector Selector, string? Owner, ProviderInfo? Info);

public sealed record ProviderInfo(
    IReadOnlyList<string>? TriggerCharacters = null,
    IReadOnlyList<string>? RetriggerCharacters = null,
    IReadOnlyList<string>? OnTypeTriggerCharacters = null,
    SemanticTokensLegend? Legend = null);

public sealed record LangUnregistration(long Id);

/// <summary>
/// One provider call. <paramref name="Op"/> names the method (<see cref="LangOps"/>); <paramref name="Uri"/>/<paramref name="Version"/> identify the
/// document (absent for workspace-wide and resolve calls); <paramref name="Args"/> carries the method's other arguments.
/// </summary>
public sealed record LangInvokeParams(long Registration, string Op, Uri? Uri, int Version, LangArgs Args);

/// <summary>Arguments of a provider method; each op uses the few it needs.</summary>
public sealed record LangArgs
{
    public TextPosition? Position { get; init; }
    public TextRange? Range { get; init; }
    public IReadOnlyList<TextPosition>? Positions { get; init; }
    /// <summary>New name (rename), query (workspace symbols), typed character (on-type formatting).</summary>
    public string? Text { get; init; }
    public bool Flag { get; init; }
    public DefinitionKind DefinitionKind { get; init; }
    public CompletionContext? CompletionContext { get; init; }
    public SignatureHelpContext? SignatureHelpContext { get; init; }
    public CodeActionContext? CodeActionContext { get; init; }
    public FormattingOptions? Formatting { get; init; }
    public CompletionItem? CompletionItem { get; init; }
    public CodeAction? CodeAction { get; init; }
    public CodeLens? CodeLens { get; init; }
    public HierarchyItem? HierarchyItem { get; init; }
    /// <summary>Which earlier result an item to resolve came from (the server resolves its own original instance; see <see cref="OriginToken"/>).</summary>
    public OriginToken? Origin { get; init; }
}

/// <summary>Names a provider result item on the server: language clients resolve completion items, code actions and lenses by their original instance.</summary>
public sealed record OriginToken(string ResultId, int Index);

/// <summary>A list result whose items can be resolved later; <paramref name="ResultId"/> keys the server's bounded originals cache.</summary>
public sealed record ResolvableResult<T>(string? ResultId, T? Value);

/// <summary>Provider method names of <see cref="LangInvokeParams.Op"/>.</summary>
public static class LangOps
{
    public const string Completion = "completion", ResolveCompletion = "resolveCompletion", Hover = "hover", Definition = "definition",
        References = "references", Highlights = "highlights", PrepareRename = "prepareRename", Rename = "rename", Format = "format",
        FormatRange = "formatRange", FormatOnType = "formatOnType", SignatureHelp = "signatureHelp", CodeActions = "codeActions",
        ResolveCodeAction = "resolveCodeAction", DocumentSymbols = "documentSymbols", WorkspaceSymbols = "workspaceSymbols",
        FoldingRanges = "foldingRanges", SelectionRanges = "selectionRanges", SemanticTokens = "semanticTokens",
        SemanticTokensRange = "semanticTokensRange", InlayHints = "inlayHints", CodeLens = "codeLens", ResolveCodeLens = "resolveCodeLens",
        DocumentColors = "documentColors", LinkedEditing = "linkedEditing", PrepareCallHierarchy = "prepareCallHierarchy",
        IncomingCalls = "incomingCalls", OutgoingCalls = "outgoingCalls", PrepareTypeHierarchy = "prepareTypeHierarchy",
        Supertypes = "supertypes", Subtypes = "subtypes";
}

public sealed record WillSaveParams(Uri Uri, DocumentSaveReason Reason);

public sealed record FileOperationParams(string Kind, IReadOnlyList<(Uri OldUri, Uri NewUri)>? Renames = null, IReadOnlyList<Uri>? Files = null);

public sealed record DiagnosticsEntry(Uri Uri, IReadOnlyList<Diagnostic> Diagnostics, int? Version = null);
public sealed record DiagPublishParams(IReadOnlyList<DiagnosticsEntry> Entries);

public sealed record LspServersParams(IReadOnlyList<LanguageServerInfo> Servers, IReadOnlyList<LanguageServerDefinition> Definitions);
public sealed record LspControlParams(string Action, string? Id = null);
public sealed record LspDefinitionResult(long Token);
public sealed record LspRemoveDefinitionParams(long Token);

public sealed record CommandsChangedParams(IReadOnlyList<string> Commands);
public sealed record ExecuteCommandParams(string Command, IReadOnlyList<JsonElement>? Arguments);
public sealed record ExecuteCommandResult(JsonElement? Value);

// ------------------------------------------------------------------------------------------------------------------- window / navigation (R2)

public sealed record ShowMessageParams(MessageSeverity Severity, string Message, IReadOnlyList<MessageAction>? Actions);
public sealed record ShowMessageResult(string? Action);

public sealed record WireQuickPickItem(string Label, string? Description = null, string? Detail = null, bool Picked = false, bool IsSeparator = false);
public sealed record QuickPickParams(IReadOnlyList<WireQuickPickItem> Items, QuickPickOptions? Options, bool Many);
/// <summary>Indexes into the request's items; null = dismissed.</summary>
public sealed record QuickPickResult(IReadOnlyList<int>? Selected);

public sealed record WireInputBoxOptions(string? Title, string? Prompt, string? Placeholder, string? Value, bool Password);
public sealed record InputBoxResult(string? Value);

public sealed record OpenExternalParams(Uri Uri);
public sealed record BoolResult(bool Value);
public sealed record TextParams(string Text);

public sealed record StatusParams(string Id, string? Text, int TimeoutMs);

/// <param name="Kind">begin | report | end</param>
public sealed record ProgressParams(string Id, string Kind, string? Title = null, bool Cancellable = false, string? Message = null, double? Percent = null);
public sealed record ProgressCancelParams(string Id);

public sealed record NavOpenParams(Uri Uri, TextRange? Selection, bool Preview, bool PreserveFocus);
public sealed record NavApplyEditParams(WorkspaceEdit Edit, string? Label);

public sealed record OutputAppendParams(string Channel, string Text);

/// <summary>Empty params/result.</summary>
public sealed record Empty
{
    public static readonly Empty Value = new();
}
