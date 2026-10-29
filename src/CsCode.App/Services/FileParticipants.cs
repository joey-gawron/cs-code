using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Services;

/// <summary>
/// Runs the language features that take part in saving and in file operations (docs/LSP.md): <see cref="IWillSaveParticipant"/> right before a
/// document is saved (each participant's edits are applied as one undo unit; all of them together may delay the save by at most
/// <see cref="WillSaveBudget"/>, so a hung server never holds a save), and <see cref="IFileOperationParticipant"/> around file renames (will: its edit
/// is applied before the rename), creates and deletes (did: afterwards). A participant that fails or times out is skipped and logged: it never fails
/// the save or the file operation. Participants are resolved on first use: the language client, which is the participant, itself depends on the
/// navigation service that uses this class.
/// </summary>
public sealed class FileParticipants
{
    /// <summary>The most a save waits for will-save participants (docs/LSP.md: a hung server may delay a save by at most 1.5 s).</summary>
    public static readonly TimeSpan WillSaveBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>The most a rename waits for will-rename participants (each language client bounds its own request at 2 s).</summary>
    public static readonly TimeSpan WillRenameBudget = TimeSpan.FromSeconds(3);

    /// <summary>Did* notifications only enqueue; bounded anyway so a misbehaving participant cannot stall a file operation.</summary>
    private static readonly TimeSpan NotifyBudget = TimeSpan.FromSeconds(2);

    private readonly Lazy<IWillSaveParticipant[]> _willSave;
    private readonly Lazy<IFileOperationParticipant[]> _fileOps;
    private readonly ILogger? _log;

    public FileParticipants(Func<IEnumerable<IWillSaveParticipant>> willSave, Func<IEnumerable<IFileOperationParticipant>> fileOperations, ILogger? log = null)
    {
        _willSave = new(() => willSave().ToArray());
        _fileOps = new(() => fileOperations().ToArray());
        _log = log;
    }

    /// <summary>
    /// Asks every will-save participant for edits and applies them to <paramref name="document"/> (call on the UI thread: it edits the buffer).
    /// Edits computed for a version the user has since changed are dropped (as VS Code does), not applied at stale positions.
    /// </summary>
    public async Task WillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct = default)
    {
        var participants = _willSave.Value;
        if (participants.Length == 0) return;
        var clock = Stopwatch.StartNew();
        foreach (var participant in participants)
        {
            var remaining = WillSaveBudget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) { _log?.LogWarning("Save participants of {Uri} exceeded {Budget}; saving without the rest", document.Uri, WillSaveBudget); return; }
            var version = document.Version;
            IReadOnlyList<TextEdit>? edits;
            try
            {
                edits = await participant.OnWillSaveAsync(document, reason, ct).AsTask().WaitAsync(remaining, ct).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                _log?.LogWarning("A save participant of {Uri} did not answer within {Budget}; saving without its edits", document.Uri, WillSaveBudget);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "A save participant of {Uri} failed; saving without its edits", document.Uri);
                continue;
            }
            if (edits is not { Count: > 0 }) continue;
            if (document.Version != version) { _log?.LogInformation("{Uri} changed while save participants ran: their edits were dropped", document.Uri); continue; }
            if (!document.ApplyEdits(edits)) _log?.LogWarning("Save participant edits could not be applied to {Uri}", document.Uri);
        }
    }

    /// <summary>The edits will-rename participants want applied before <paramref name="files"/> are renamed (merged), or null.</summary>
    public async Task<WorkspaceEdit?> WillRenameAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct = default)
    {
        var participants = _fileOps.Value;
        if (participants.Length == 0 || files.Count == 0) return null;
        var clock = Stopwatch.StartNew();
        var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>>();
        List<ResourceOperation>? operations = null;
        foreach (var participant in participants)
        {
            var remaining = WillRenameBudget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            WorkspaceEdit? edit;
            try { edit = await participant.WillRenameFilesAsync(files, ct).AsTask().WaitAsync(remaining, ct).ConfigureAwait(false); }
            catch (TimeoutException) { _log?.LogWarning("Rename participants did not answer within {Budget}; renaming without their edits", WillRenameBudget); break; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log?.LogWarning(ex, "A rename participant failed; renaming without its edits"); continue; }
            if (edit is null || edit.IsEmpty) continue;
            foreach (var (uri, list) in edit.Changes) changes[uri] = changes.TryGetValue(uri, out var existing) ? [.. existing, .. list] : list;
            if (edit.ResourceOperations is { Count: > 0 } ops) (operations ??= []).AddRange(ops);
        }
        return changes.Count == 0 && operations is null ? null : new WorkspaceEdit(changes, operations);
    }

    public Task DidRenameAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files) => NotifyAsync("rename", files.Count, (p, ct) => p.DidRenameFilesAsync(files, ct));
    public Task DidCreateAsync(IReadOnlyList<Uri> files) => NotifyAsync("create", files.Count, (p, ct) => p.DidCreateFilesAsync(files, ct));
    public Task DidDeleteAsync(IReadOnlyList<Uri> files) => NotifyAsync("delete", files.Count, (p, ct) => p.DidDeleteFilesAsync(files, ct));

    private async Task NotifyAsync(string operation, int count, Func<IFileOperationParticipant, CancellationToken, ValueTask> notify)
    {
        if (count == 0) return;
        foreach (var participant in _fileOps.Value)
        {
            try { await notify(participant, CancellationToken.None).AsTask().WaitAsync(NotifyBudget).ConfigureAwait(false); }
            catch (Exception ex) { _log?.LogWarning(ex, "A file {Operation} participant failed", operation); }
        }
    }
}
