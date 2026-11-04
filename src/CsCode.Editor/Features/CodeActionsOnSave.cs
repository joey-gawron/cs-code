using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.Features;

/// <summary>
/// <c>editor.codeActionsOnSave</c>: source actions run right before a document is saved, ahead of format on save (VS Code's order). The setting
/// comes in VS Code's three shapes, globally or per language: the object form <c>{"source.organizeImports": "explicit", "source.fixAll": "always"}</c>,
/// its legacy booleans (<c>true</c> = explicit, <c>false</c> = never) and the array form <c>["source.fixAll", "source.organizeImports"]</c> (each entry
/// explicit, run in the listed order). <c>explicit</c> runs on a save the user asked for, <c>always</c> also on the auto saves of a focus or window
/// change (not on one after a delay), as in VS Code. In the object form the <c>source.fixAll</c> kinds run first, then the rest in the order written (VS Code sorts the same way).
/// <para>
/// Kinds match hierarchically: <c>source.fixAll</c> runs <c>source.fixAll.ruff</c>. A kind listed together with a more specific one leaves that
/// one's actions to it (<c>source.fixAll</c> next to <c>source.fixAll.eslint</c> does not run ESLint's twice).
/// </para>
/// <para>
/// Each kind asks the document's code-action providers one after another, so the next one sees the text the previous one left; per provider the
/// first matching action with an edit is resolved (when the server defers its edit) and applied, and command-only actions (Pyright's organize
/// imports is a server command that answers with <c>workspace/applyEdit</c>) are executed. Everything one save applies is ONE undo unit (an undo
/// group held over the whole run). The run has a deadline, <c>editor.codeActionsOnSaveTimeout</c> (750 ms by default): when it passes, what was
/// applied stays and the save goes ahead without the rest. A provider that fails is skipped. Edits computed for text that has since changed are
/// dropped, never applied at stale positions.
/// </para>
/// Unlike most will-save participants it applies its edits itself (several rounds, each against the text the last one left) and returns none.
/// Called on the UI thread by the shell's save path; the provider calls only enqueue on the language lane.
/// </summary>
public sealed class CodeActionsOnSaveParticipant : IWillSaveParticipant
{
    public const string SettingKey = "editor.codeActionsOnSave";
    public const string TimeoutKey = "editor.codeActionsOnSaveTimeout";
    public const int DefaultTimeoutMs = 750;
    private const string FixAll = "source.fixAll";

    public enum RunMode { Never, Explicit, Always }

    private readonly EditorServices _svc;
    private readonly ILogger _log;

    public CodeActionsOnSaveParticipant(EditorServices services)
    {
        _svc = services;
        _log = services.LoggerFactory.CreateLogger<CodeActionsOnSaveParticipant>();
    }

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(SettingKey, JsonDocument.Parse("{}").RootElement.Clone(),
            "Code action kinds run before saving, e.g. { \"source.fixAll\": \"explicit\", \"source.organizeImports\": \"explicit\" }: \"explicit\" on a save you ask for, " +
            "\"always\" also on the auto saves of a focus or window change, \"never\" not at all (true and false mean explicit and never). An array of kinds runs each one explicitly, in order.",
            JsonDocument.Parse("""{"type":["object","array"],"additionalProperties":{"enum":["explicit","always","never",true,false]},"items":{"type":"string"}}""").RootElement.Clone()),
        new(TimeoutKey, JsonSerializer.SerializeToElement(DefaultTimeoutMs),
            "Milliseconds the code actions run on save may take; the save goes ahead without the rest after that.",
            JsonDocument.Parse("""{"type":"integer","minimum":0}""").RootElement.Clone()),
    ];

    /// <summary>The kinds to run for a save with <paramref name="reason"/>, in order, from the raw setting (any of its shapes; anything else = none).</summary>
    public static IReadOnlyList<string> KindsToRun(JsonElement? setting, DocumentSaveReason reason)
    {
        var entries = Parse(setting);
        // VS Code: "always" also runs on the auto saves of a focus or window change, never on one after a delay (it would rewrite the code while
        // the user types: an import just added and not used yet would go)
        return entries.Where(e => (e.Mode == RunMode.Always && reason != DocumentSaveReason.AfterDelay) || (e.Mode == RunMode.Explicit && reason == DocumentSaveReason.Manual))
            .Select(e => e.Kind).ToList();
    }

    internal static IReadOnlyList<(string Kind, RunMode Mode)> Parse(JsonElement? setting)
    {
        var result = new List<(string Kind, RunMode Mode)>();
        switch (setting)
        {
            case { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } kind && result.All(r => r.Kind != kind))
                        result.Add((kind, RunMode.Explicit));
                return result;
            case { ValueKind: JsonValueKind.Object } obj:
                foreach (var p in obj.EnumerateObject())
                {
                    var mode = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => RunMode.Explicit,
                        JsonValueKind.String => p.Value.GetString() switch { "explicit" => RunMode.Explicit, "always" => RunMode.Always, _ => RunMode.Never },
                        _ => RunMode.Never,
                    };
                    if (p.Name.Length == 0) continue;
                    result.RemoveAll(r => r.Kind == p.Name); // a later duplicate (a language section merged over the global one) wins
                    if (mode != RunMode.Never) result.Add((p.Name, mode));
                }
                // VS Code's order for the object form: the fix-all kinds first, the rest as written (a stable sort).
                return result.Select((e, i) => (e, i)).OrderBy(x => IsKindOrChild(x.e.Kind, FixAll) ? 0 : 1).ThenBy(x => x.i).Select(x => x.e).ToList();
            default:
                return result;
        }
    }

    /// <summary>Whether <paramref name="kind"/> is <paramref name="parent"/> or below it (<c>source.fixAll.ruff</c> is below <c>source.fixAll</c>).</summary>
    internal static bool IsKindOrChild(string kind, string parent) =>
        kind == parent || kind.StartsWith(parent + ".", StringComparison.Ordinal);

    public async ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct)
    {
        if (document is IEditorDocument { IsReadOnly: true }) return null;
        var kinds = KindsToRun(_svc.Config.GetRawFor(SettingKey, document.LanguageId), reason);
        if (kinds.Count == 0) return null;
        if (_svc.Features.GetProviders<ICodeActionProvider>(document) is not { Count: > 0 } providers) return null;
        var timeoutMs = Math.Max(0, _svc.Config.GetOr(TimeoutKey, document.LanguageId, DefaultTimeoutMs));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        // One undo unit for everything this save applies, however many rounds and server round-trips it takes.
        using var group = document is EditorDocument editor && _svc.Ui.CheckAccess() ? editor.OpenUndoGroup() : null;
        foreach (var kind in kinds)
        {
            var leaveToOthers = kinds.Where(k => k != kind && IsKindOrChild(k, kind)).ToList();
            try
            {
                foreach (var provider in providers)
                    await RunAsync(document, provider, kind, leaveToOthers, deadline.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogInformation("Code actions on save of {Uri} stopped at {Kind}: {Timeout} ms ({Key}) passed", document.Uri, kind, timeoutMs, TimeoutKey);
                break;
            }
        }
        return null;
    }

    private async Task RunAsync(ITextDocument document, ICodeActionProvider provider, string kind, IReadOnlyList<string> leaveToOthers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var version = document.Version;
        var whole = new TextRange(new TextPosition(0, 0), document.PositionAt(document.Length));
        var context = new CodeActionContext(_svc.Diagnostics.Get(document.Uri), [kind], CodeActionTriggerKind.Automatic);
        IReadOnlyList<CodeAction>? offered;
        try
        {
            offered = await provider.ProvideCodeActionsAsync(document, whole, context, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A code action provider failed for {Kind} on save of {Uri}", kind, document.Uri);
            return;
        }
        if (offered is null) return;
        var applied = false;
        foreach (var candidate in offered)
        {
            if (candidate.DisabledReason is not null || !CodeActionFeature.KindMatches(candidate.Kind, [kind])) continue;
            if (leaveToOthers.Count > 0 && CodeActionFeature.KindMatches(candidate.Kind, leaveToOthers)) continue;
            var action = candidate;
            if (action.Edit is null && (action.Data is not null || action.Command is null))
            {
                try
                {
                    action = await provider.ResolveCodeActionAsync(action, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Resolving the code action '{Title}' failed on save of {Uri}", action.Title, document.Uri);
                    continue;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (action.Edit is { IsEmpty: false } edit)
            {
                // One action with an edit per provider and kind: the next one was computed for the text before this one changed it.
                if (applied || document.Version != version)
                {
                    _log.LogDebug("Skipped the code action '{Title}' on save of {Uri}: the text changed since it was computed", action.Title, document.Uri);
                    continue;
                }
                if (!await ApplyAsync(document, edit, action.Title).ConfigureAwait(true)) continue;
                applied = true;
            }
            if (action.Command is { } command) await ExecuteAsync(command, ct).ConfigureAwait(true);
        }
    }

    private async Task<bool> ApplyAsync(ITextDocument document, WorkspaceEdit edit, string title)
    {
        var onlyThis = edit.ResourceOperations is not { Count: > 0 } && edit.Changes.Keys.All(u => FeatureSupport.SameFile(u, document.Uri));
        if (onlyThis || _svc.Navigation is not { } navigation)
        {
            var edits = edit.Changes.Where(kv => FeatureSupport.SameFile(kv.Key, document.Uri)).SelectMany(kv => kv.Value).ToList();
            if (edits.Count == 0) return false;
            if (document.ApplyEdits(edits)) return true;
            _log.LogWarning("The code action '{Title}' could not be applied on save of {Uri}", title, document.Uri);
            return false;
        }
        return await navigation.ApplyWorkspaceEditAsync(edit, title, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task ExecuteAsync(Command command, CancellationToken ct)
    {
        if (_svc.Commands is not { } commands) return;
        try
        {
            var args = command.Arguments is { } a ? a.Cast<object?>().ToArray() : [];
            await commands.ExecuteAsync(command.CommandId, args).AsTask().WaitAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The code action command {Command} failed on save", command.CommandId);
        }
    }
}
