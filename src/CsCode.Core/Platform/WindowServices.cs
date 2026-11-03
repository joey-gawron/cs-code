using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Platform;

public enum MessageSeverity { Info, Warning, Error }

public sealed record MessageAction(string Title, bool IsCloseAffordance = false);

public sealed record QuickPickItem(string Label, string? Description = null, string? Detail = null, bool Picked = false, object? Tag = null, bool IsSeparator = false);

public sealed record QuickPickOptions(string? Title = null, string? Placeholder = null, bool CanPickMany = false, bool MatchOnDescription = true, bool MatchOnDetail = false);

public sealed record InputBoxOptions(string? Title = null, string? Prompt = null, string? Placeholder = null, string? Value = null, bool Password = false, Func<string, string?>? Validate = null);

public sealed record OpenDialogOptions(bool CanSelectFiles = true, bool CanSelectFolders = false, bool CanSelectMany = false, string? Title = null, IReadOnlyList<(string Name, string[] Extensions)>? Filters = null, Uri? DefaultUri = null);

public sealed record SaveDialogOptions(string? Title = null, Uri? DefaultUri = null, IReadOnlyList<(string Name, string[] Extensions)>? Filters = null);

public interface IProgressReporter
{
    void Report(string? message = null, double? percent = null);
}

/// <summary>
/// UI interactions needed by non-UI modules (LSP window/showMessage, DAP prompts…).
/// Implemented by CsCode.App. All calls are thread-safe and marshal to the UI thread.
/// </summary>
public interface IWindowService
{
    ValueTask<string?> ShowMessageAsync(MessageSeverity severity, string message, IReadOnlyList<MessageAction>? actions = null, CancellationToken ct = default);
    ValueTask<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<QuickPickItem>?> ShowQuickPickManyAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions? options = null, CancellationToken ct = default);
    ValueTask<string?> ShowInputBoxAsync(InputBoxOptions? options = null, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Uri>?> ShowOpenDialogAsync(OpenDialogOptions options, CancellationToken ct = default);
    ValueTask<Uri?> ShowSaveDialogAsync(SaveDialogOptions options, CancellationToken ct = default);
    /// <summary>Runs work with a notification-area progress indicator (e.g. LSP workDoneProgress).</summary>
    Task<T> WithProgressAsync<T>(string title, bool cancellable, Func<IProgressReporter, CancellationToken, Task<T>> work);
    /// <summary>Transient status-bar text; dispose to clear early.</summary>
    IDisposable SetStatusBarMessage(string text, TimeSpan? timeout = null);
    ValueTask<bool> OpenExternalAsync(Uri uri);
    ValueTask CopyToClipboardAsync(string text);
}

public enum StatusBarAlignment { Left = 1, Right = 2 }

public sealed record StatusBarItemModel(string Id, string Text, string? Tooltip = null, string? CommandId = null, StatusBarAlignment Alignment = StatusBarAlignment.Left, int Priority = 0, bool IsVisible = true, string? ColorKey = null)
{
    /// <summary>
    /// A theme colour id for the item's own background (VS Code's <c>statusBarItem.remoteBackground</c> for the remote indicator; additive, Remote
    /// R4/wave 3). Null: the status bar's background shows through. Pair it with a matching <see cref="ColorKey"/> for the text.
    /// </summary>
    public string? BackgroundColorKey { get; init; }
}

/// <summary>Contributed status-bar entries (language selector, diagnostics counts, debug state, memory, EOL, caret position…).</summary>
public interface IStatusBarService
{
    /// <summary>Adds or updates (by Id). Dispose to remove.</summary>
    IDisposable Set(StatusBarItemModel item);
    IReadOnlyList<StatusBarItemModel> Items { get; }
    IEventSource<IReadOnlyList<StatusBarItemModel>> Changed { get; }
}

public interface IOutputChannel : IDisposable
{
    string Name { get; }
    void Append(string text);
    void AppendLine(string line);
    void Clear();
    /// <summary>Bounded ring: implementations MUST cap retained text (default ≤ 2 MB per channel) so logs can't grow memory.</summary>
    string GetText();
    IEventSource<string> Appended { get; }
}

public interface IOutputService
{
    IOutputChannel GetOrCreate(string name);
    IReadOnlyList<string> ChannelNames { get; }
    IEventSource<string> ChannelsChanged { get; }
}

/// <summary>
/// Open/reveal editors from non-UI code (go-to-definition, debugger stop location, Error List click, Find References).
/// Implemented by the shell's document well.
/// </summary>
public interface INavigationService
{
    ValueTask<IEditorDocument?> OpenAsync(Uri uri, TextRange? selection = null, bool preview = false, bool preserveFocus = false, CancellationToken ct = default);
    /// <summary>Currently focused editor document, if any.</summary>
    IEditorDocument? ActiveDocument { get; }
    TextPosition? ActiveCaret { get; }
    IEventSource<IEditorDocument?> ActiveDocumentChanged { get; }
    /// <summary>
    /// The active editor's caret moved (or another editor became active: its caret; null when none is). Additive (org mode: context keys that
    /// follow the caret); the default never fires.
    /// </summary>
    IEventSource<TextPosition?> ActiveCaretChanged => EventSource.Never<TextPosition?>();
    IReadOnlyList<IEditorDocument> OpenDocuments { get; }
    /// <summary>Apply a workspace edit (open docs edited in-editor as one undo group; closed files edited on disk).</summary>
    ValueTask<bool> ApplyWorkspaceEditAsync(Languages.WorkspaceEdit edit, string? label = null, CancellationToken ct = default);
}
