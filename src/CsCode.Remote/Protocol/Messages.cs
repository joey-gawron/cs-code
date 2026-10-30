using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Core.Terminal;
using CsCode.Core.Text;

namespace CsCode.Remote.Protocol;

/// <summary>Protocol constants. Bump <see cref="Version"/> on any incompatible change; client and server refuse a mismatch at the handshake.</summary>
public static class RemoteProtocol
{
    /// <summary>v2: byte channels (terminals, debug adapters), tasks, persistent servers (R3/R4).</summary>
    public const int Version = 2;

    /// <summary>The server writes this line on stdout before the framed stream starts; the client skips whatever a login shell printed before it.</summary>
    public const string Greeting = "CSCODE-SERVER-READY v1";

    /// <summary>Capability names exchanged in the handshake.</summary>
    public static class Capabilities
    {
        public const string Gzip = "gzip";
        public const string Languages = "languages";
        public const string Search = "search";
        public const string Watch = "watch";
        /// <summary>Remote terminals (a PTY on the server), R3.</summary>
        public const string Terminals = "terminals";
        /// <summary>Debug adapters run on the server, their DAP stream tunnelled over the connection, R3.</summary>
        public const string Debug = "debug";
        /// <summary>tasks.json tasks run on the server, R3.</summary>
        public const string Tasks = "tasks";
        /// <summary>The server outlives a dropped connection (grace period) and the client reattaches to it, R4.</summary>
        public const string Persistent = "persistent";
        /// <summary>
        /// Client capability (end pass, T49): the client shows a task's process in a terminal (VS Code's task presentation), so the server's task
        /// runner asks it for one (<c>task/terminal</c>) and the process runs on a server PTY; without it tasks are piped into "Remote: Tasks".
        /// </summary>
        public const string TaskTerminals = "taskTerminals";
    }
}

/// <summary>JSON-RPC method names (client → server unless marked).</summary>
public static class RemoteMethods
{
    public const string Hello = "hello";
    public const string Ping = "$/ping";
    public const string Shutdown = "server/shutdown";
    /// <summary>What the server holds for this connection (diagnostics, leak checks).</summary>
    public const string Status = "server/status";

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
    /// <summary>client → server after a (re)connect: the watches the client still has; the server stops every other one (stopped while the
    /// client was away, or left by a previous client process of the session).</summary>
    public const string WatchKeep = "watch/keep";
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

    // ---- R3/R4: byte channels — terminals and debug adapters — resumable across reconnects (offsets) with flow control (acks)
    /// <summary>Both ways (notification): bytes of a channel at an absolute offset.</summary>
    public const string ChanData = "chan/data";
    /// <summary>client → server (notification): the client consumed the channel's output up to an offset (flow control).</summary>
    public const string ChanAck = "chan/ack";
    /// <summary>server → client (notification): the channel's source ended after its last byte (exit code / reason).</summary>
    public const string ChanClosed = "chan/closed";
    /// <summary>client → server: end the channel's source (kill the process, SIGHUP then SIGKILL after the grace period).</summary>
    public const string ChanClose = "chan/close";
    /// <summary>client → server after a reconnect: resume a channel from the client's received offset (the server replays what was missed).</summary>
    public const string ChanAttach = "chan/attach";
    /// <summary>client → server after a reconnect: the channels the client still has; the server ends every other one.</summary>
    public const string ChanKeep = "chan/keep";

    public const string TermCreate = "term/create";
    /// <summary>client → server (notification): the terminal's window size.</summary>
    public const string TermResize = "term/resize";
    /// <summary>The terminal's foreground process (read on the server).</summary>
    public const string TermInfo = "term/info";

    /// <summary>The server runs its own debug configuration providers for a type (they see the server's file system).</summary>
    public const string DapResolve = "dap/resolve";
    /// <summary>Starts the debug adapter for a configuration on the server; its DAP stream becomes a channel.</summary>
    public const string DapStart = "dap/start";
    /// <summary>A second connection to an adapter server started by <see cref="DapStart"/> (js-debug's child sessions).</summary>
    public const string DapConnect = "dap/connect";

    public const string TaskList = "task/list";
    public const string TaskStart = "task/start";
    /// <summary>Long poll: completes with the task's result (re-issued after a reconnect; the task keeps running on the server).</summary>
    public const string TaskWait = "task/wait";
    public const string TaskCancel = "task/cancel";
    /// <summary>client → server after a (re)connect: the task runs the client still waits for; the server cancels and forgets every other one
    /// (cancelled while the client was away, or started by a previous client process of the session).</summary>
    public const string TaskKeep = "task/keep";
    /// <summary>
    /// server → client: show a task's process in a terminal named after the task (the client's task presentation: reuse, reveal, echo…); the
    /// terminal runs it on this server (<c>term/create</c> carrying the request's tag). Answers once the process was started.
    /// </summary>
    public const string TaskTerminal = "task/terminal";
    /// <summary>server → client (notification): bring a task's terminal forward (<c>reveal: silent</c> after a failure).</summary>
    public const string TaskReveal = "task/reveal";

    /// <summary>client → server after a reconnect: the documents the client still has open; the server closes the others.</summary>
    public const string DocKeep = "doc/keep";
}

// ------------------------------------------------------------------------------------------------------------------- handshake

public sealed record HelloParams(int ProtocolVersion, string ClientVersion, IReadOnlyList<string> Capabilities, string Mode);

/// <param name="InstanceId">Random per server process: a reconnect that finds the same id resumed the same server (its terminals, debug adapters,
/// tasks and language servers kept running).</param>
public sealed record HelloResult(
    int ProtocolVersion,
    string ServerVersion,
    string Os,
    string Arch,
    int ProcessId,
    string Home,
    string DataDir,
    IReadOnlyList<string> Capabilities,
    string? InstanceId = null);

public sealed record OpenDocumentInfo(Uri Uri, int Version, int Length);

public sealed record ServerStatus(IReadOnlyList<OpenDocumentInfo> Documents, int Watches, int Searches, int Uploads, int Registrations, long ManagedBytes, int Threads)
{
    public int Terminals { get; init; }
    public int DebugAdapters { get; init; }
    public int Channels { get; init; }
    public int RunningTasks { get; init; }
    /// <summary>Client connections this server process has served (more than one: a persistent server that was reconnected to).</summary>
    public int Connections { get; init; }
    public string? InstanceId { get; init; }
}

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
public sealed record WatchKeepParams(IReadOnlyList<string> WatchIds);
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

/// <summary>One text match: 0-based line/column (UTF-16) and the line text (truncated to a preview that starts at <paramref name="PreviewStart"/> of the line).</summary>
public sealed record TextSearchMatch(string Path, int Line, int Column, int Length, string Preview, int PreviewStart = 0);

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

// ------------------------------------------------------------------------------------------------------------------- channels (R3/R4)

public sealed record ChanDataParams(long Id, long Offset, Payload Data);
public sealed record ChanAckParams(long Id, long Offset);
/// <param name="FinalOffset">Total bytes the channel produced (the client has everything once it received up to here).</param>
public sealed record ChanClosedParams(long Id, long FinalOffset, int? ExitCode, string? Reason);
public sealed record ChanCloseParams(long Id, int GraceMs);
public sealed record ChanIdParams(long Id);
public sealed record ChanAttachParams(long Id, long ReceivedOffset);
/// <param name="Exists">False: the server no longer has the channel (it was resumed by a different server, or ended long ago).</param>
/// <param name="ServerReceived">Bytes of client input the server has accepted (the client resends from there).</param>
/// <param name="LostBytes">Output that fell out of the server's replay buffer while nobody was connected.</param>
public sealed record ChanAttachResult(bool Exists, long ServerReceived, long LostBytes);
public sealed record ChanKeepParams(IReadOnlyList<long> Ids);

/// <param name="Folder">The client's workspace folder: a new terminal starts there even when the server has not applied the client's latest
/// <c>workspace/setFolders</c> yet (requests run concurrently on the server).</param>
public sealed record TermCreateParams(TerminalCreateOptions Options, int Columns, int Rows, Uri? Folder = null);
/// <param name="ShellIntegration">The shell-integration variables the server started the shell with (<c>CSCODE_NONCE</c>, <c>CSCODE_INJECTION</c>;
/// the server's own scripts, shipped in its bundle), so the client's terminal trusts the command lines the shell reports; null: not injected.</param>
public sealed record TermCreateResult(long Channel, int ProcessId, string Program, string WorkingDirectory, IReadOnlyDictionary<string, string>? ShellIntegration = null);
public sealed record TermResizeParams(long Id, int Columns, int Rows);
public sealed record TermInfoResult(string? ForegroundProcess);

public sealed record DapResolveParams(Uri? Folder, DebugConfiguration Configuration);
/// <summary>The configuration as the server's providers left it; null = a provider aborted the launch.</summary>
public sealed record DapResolveResult(DebugConfiguration? Configuration);
public sealed record DapStartParams(DebugConfiguration Configuration);
/// <param name="Shareable">The adapter is a server a child session can connect to again (<see cref="RemoteMethods.DapConnect"/>).</param>
public sealed record DapStartResult(long Channel, int? ProcessId, string Description, bool Shareable);
public sealed record DapConnectParams(long Parent);

public sealed record TaskListParams(Uri? Folder);
public sealed record TaskStartParams(string Label, Uri? Folder);
public sealed record TaskRunParams(string RunId);
public sealed record TaskResultWire(TaskResult Result);
public sealed record TaskKeepParams(IReadOnlyList<string> RunIds);

/// <summary>A task process to show in a terminal (<see cref="RemoteMethods.TaskTerminal"/>); paths are the server's.</summary>
/// <param name="Tag">Names this start: the terminal's <c>term/create</c> carries it (<see cref="TaskTerminalTag.Variable"/>) so the server
/// connects that PTY to the waiting task (its output to the problem matchers, its exit to the result).</param>
public sealed record TaskTerminalParams(string Tag, string Label, string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment, TaskPresentation Presentation, string? CommandLine);

public sealed record TaskTagParams(string Tag);

/// <summary>How a task terminal's <c>term/create</c> names the task start it belongs to (removed from the program's environment on the server).</summary>
public static class TaskTerminalTag
{
    public const string Variable = "CSCODE_SERVER_TASK";
}

public sealed record DocKeepParams(IReadOnlyList<Uri> Uris);

/// <summary>Empty params/result.</summary>
public sealed record Empty
{
    public static readonly Empty Value = new();
}
