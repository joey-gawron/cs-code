using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Extensions;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.CSharp;

/// <summary>
/// The source actions Roslyn does not have, built from the ones it does. Roslyn's code actions are only <c>quickfix</c> and <c>refactor</c>
/// (5.13.0-1.26514.10; it ignores <c>context.only</c>), so <c>editor.codeActionsOnSave</c>'s <c>source.organizeImports</c> and
/// <c>source.fixAll</c>, and Organize Imports (Shift+Alt+O), would find nothing in its answers. This provider (one per manager, for the documents
/// a Roslyn session serves) offers, only when a source kind is asked for:
/// <list type="bullet">
/// <item><see cref="OrganizeKind"/> "Organize Usings": Roslyn's "Remove unnecessary usings" (the IDE0005 fix, custom tag
/// <c>RemoveUnnecessaryImports</c>), then its "Sort Usings" (the <c>OrganizeImports</c> refactoring), both asked over the using directives.</item>
/// <item><see cref="FixAllKind"/> "Fix All in Document": for each analyzer diagnostic of warning or error severity in the document (IDE…, CA…,
/// a NuGet analyzer's; never the compiler's CS…, as <c>dotnet format</c> leaves them), one id after another, the "Fix All:" of the fix Roslyn
/// offers for it, in the Document scope (<c>codeAction/resolveFixAll</c>). The diagnostics are pulled from Roslyn for the current text before
/// each id, and each fix all is resolved at its diagnostic's own range: over a range that also holds "Sort Usings" (whose fix all has no
/// Document scope) Roslyn's resolve fails.</item>
/// </list>
/// Each step is a round trip on the text the step before left, so each action is a command (<see cref="OrganizeUsingsCommand"/>,
/// <see cref="FixAllCommand"/>) that the will-save participant runs. On save it runs under the participant's deadline (the token the
/// participant asked with, kept with the action): every edit is applied on the calling thread right after the deadline is checked, so none
/// lands after the save went ahead. Each step's edit is applied only to the text it was computed for.
/// </summary>
internal sealed partial class RoslynSourceActions(ServerExtensionHost host, RoslynExtension extension) : ICodeActionProvider
{
    public const string OrganizeKind = "source.organizeImports.roslyn";
    public const string FixAllKind = "source.fixAll.roslyn";
    public const string OrganizeUsingsCommand = "csharp.organizeUsings";
    public const string FixAllCommand = "csharp.fixAllInDocument";
    internal const string RemoveUnnecessaryImportsTag = "RemoveUnnecessaryImports";
    internal const string OrganizeImportsTag = "OrganizeImports";
    internal const int MaxFixAllIds = 16;
    private const string DocumentScope = "Document";

    // on-save runs whose command has not run yet: the document and the participant's deadline, by id; gone when the deadline passes, or a
    // minute later at most (a participant that skipped the action disposes its deadline without cancelling it)
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private const long PendingForMs = 60_000;

    private sealed record Pending(ITextDocument Document, CancellationToken Deadline, long Created)
    {
        public CancellationTokenRegistration Expiry { get; set; }
    }

    public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct)
    {
        // never in the light bulb: only for a source kind (the save participant, Organize Imports, the source action menu)
        if (context.Only is not { Count: > 0 } only || host.SessionFor(extension, document.Uri) is null) return new((IReadOnlyList<CodeAction>?)null);
        List<CodeAction>? actions = null;
        if (Asks(only, OrganizeKind) && UsingsRange(document.GetText()) is not null)
            (actions ??= []).Add(Action("Organize Usings", OrganizeKind, OrganizeUsingsCommand, document, context, ct));
        if (Asks(only, FixAllKind) && host.Services.Diagnostics.Get(document.Uri).Any(d => IsTarget(d.Severity, d.Code)))
            (actions ??= []).Add(Action("Fix All in Document", FixAllKind, FixAllCommand, document, context, ct));
        return new(actions);
    }

    /// <summary>Whether a requested kind (<c>source</c>, <c>source.fixAll</c>, <c>source.fixAll.roslyn</c>) covers <paramref name="kind"/>.</summary>
    internal static bool Asks(IReadOnlyList<string> only, string kind) =>
        only.Any(o => kind == o || kind.StartsWith(o + ".", StringComparison.Ordinal));

    private CodeAction Action(string title, string kind, string command, ITextDocument document, CodeActionContext context, CancellationToken deadline)
    {
        var args = new Dictionary<string, string> { ["uri"] = document.Uri.ToString() };
        if (context.TriggerKind == CodeActionTriggerKind.Automatic && deadline.CanBeCanceled)
        {
            var now = Environment.TickCount64;
            foreach (var (key, old) in _pending)
                if (now - old.Created > PendingForMs && _pending.TryRemove(key, out _)) old.Expiry.Dispose();
            var id = Guid.NewGuid().ToString("N");
            var pending = new Pending(document, deadline, now);
            _pending[id] = pending;
            pending.Expiry = deadline.Register(() => _pending.TryRemove(id, out _));
            args["op"] = id;
        }
        return new CodeAction(title, kind, Command: new Command(title, command, [JsonSerializer.SerializeToElement(args)]));
    }

    /// <summary>
    /// The document and deadline a command's argument names: an on-save run's (null when its deadline passed: the save went ahead without it),
    /// else the open document at its <c>uri</c>, else the active editor's.
    /// </summary>
    private (ITextDocument Document, CancellationToken Deadline)? Target(JsonElement[] args)
    {
        var arg = args.Length > 0 && args[0].ValueKind == JsonValueKind.Object ? args[0] : default;
        if (arg.ValueKind == JsonValueKind.Object && arg.Str("op") is { } op)
        {
            if (!_pending.TryRemove(op, out var pending)) return null;
            pending.Expiry.Dispose();
            return (pending.Document, pending.Deadline);
        }
        var uri = arg.ValueKind == JsonValueKind.Object ? Features.LspMapper.ToUri(arg.Str("uri")) : null;
        ITextDocument? document = uri is null ? host.Services.Navigation?.ActiveDocument : host.Services.Documents.Get(uri);
        return document is null ? null : (document, CancellationToken.None);
    }

    // ------------------------------------------------------------------ Organize Usings

    public async Task OrganizeUsingsAsync(ServerExtensionSession session, JsonElement[] args)
    {
        if (Target(args) is not var (document, deadline)) return;
        foreach (var tag in new[] { RemoveUnnecessaryImportsTag, OrganizeImportsTag })
        {
            if (deadline.IsCancellationRequested || UsingsRange(document.GetText()) is not { } range) return;
            var version = document.Version;
            var offered = await session.RequestAsync(P.LspMethods.TextDocumentCodeAction, document,
                (id, m) => new { textDocument = id, range = m.ToLsp(range, document.Uri), context = new { diagnostics = Array.Empty<object>(), triggerKind = 2 } },
                deadline).ConfigureAwait(true);
            if (offered is not { ValueKind: JsonValueKind.Array } list) return;
            var action = list.EnumerateArray().FirstOrDefault(a => HasTag(a, tag) && !IsFixAll(a) && !IsNested(a));
            if (action.ValueKind != JsonValueKind.Object) continue;
            var resolved = await session.RequestAsync(P.LspMethods.CodeActionResolve, document, (_, _) => action, deadline).ConfigureAwait(true);
            if (resolved is { } r) Apply(session, document, r, version, deadline);
        }
    }

    /// <summary>
    /// From the first using directive's line to the end of the last one's (file-scoped and block namespaces' too), or null when there is none.
    /// Using statements and declarations (<c>using var x = …;</c>, <c>using (…)</c>, <c>using Stream s = …;</c>) do not count.
    /// </summary>
    internal static TextRange? UsingsRange(string text)
    {
        int first = -1, last = -1, lastLength = 0, line = 0;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            if (UsingDirective().IsMatch(raw))
            {
                if (first < 0) first = line;
                last = line;
                lastLength = raw.Length;
            }
            line++;
        }
        return first < 0 ? null : new TextRange(new TextPosition(first, 0), new TextPosition(last, lastLength));
    }

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:(?:unsafe\s+)?@?[A-Za-z_]\w*\s*=\s*[^;]+|@?[A-Za-z_][\w.]*(?:::[\w.]+)?)\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex UsingDirective();

    // ------------------------------------------------------------------ Fix All in Document

    public async Task FixAllAsync(ServerExtensionSession session, JsonElement[] args)
    {
        if (Target(args) is not var (document, deadline)) return;
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (done.Count < MaxFixAllIds && !deadline.IsCancellationRequested)
        {
            var version = document.Version;
            var pulled = await session.RequestAsync(P.LspMethods.TextDocumentDiagnostic, document, (id, _) => new { textDocument = id }, deadline).ConfigureAwait(true);
            if (NextTarget(pulled, done) is not { } target) return;
            done.Add(target.Code);
            var offered = await session.RequestAsync(P.LspMethods.TextDocumentCodeAction, document,
                (id, _) => new { textDocument = id, range = target.Diagnostic.GetProperty("range"), context = new { diagnostics = new[] { target.Diagnostic }, triggerKind = 2 } },
                deadline).ConfigureAwait(true);
            if (offered is not { ValueKind: JsonValueKind.Array } list) continue;
            var fixAll = list.EnumerateArray().FirstOrDefault(a => IsFixAll(a) && Fixes(a, target.Code) && Flavors(a).Contains(DocumentScope));
            if (fixAll.ValueKind != JsonValueKind.Object || !fixAll.TryGetProperty("data", out var data)) continue;
            if (document.Version != version) return;
            var resolved = await session.RequestAsync(RoslynCommands.ResolveFixAllMethod, document,
                (_, _) => new { title = data.Str("UniqueIdentifier") ?? fixAll.Str("title"), data, scope = DocumentScope }, deadline).ConfigureAwait(true);
            if (resolved is { } r && !Apply(session, document, r, version, deadline)) return;
        }
    }

    /// <summary>The first diagnostic (in document order) of a pulled report that <see cref="IsTarget"/> takes and whose id is not done yet.</summary>
    internal static (string Code, JsonElement Diagnostic)? NextTarget(JsonElement? report, IReadOnlySet<string> done)
    {
        if (report is not { ValueKind: JsonValueKind.Object } r || !r.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        (string Code, JsonElement Diagnostic, int Line, int Character)? best = null;
        foreach (var d in items.EnumerateArray())
        {
            var code = d.TryGetProperty("code", out var c) ? c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString() : null;
            var severity = d.TryGetProperty("severity", out var s) && s.TryGetInt32(out var v) ? (DiagnosticSeverity)v : DiagnosticSeverity.Error;
            if (code is null || done.Contains(code) || !IsTarget(severity, code) || !d.TryGetProperty("range", out var range)) continue;
            var start = range.GetProperty("start");
            var (line, character) = (start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32());
            if (best is null || line < best.Value.Line || (line == best.Value.Line && character < best.Value.Character)) best = (code, d.Clone(), line, character);
        }
        return best is { } b ? (b.Code, b.Diagnostic) : null;
    }

    /// <summary>An analyzer's warning or error (not the C# or VB compiler's own: <c>CS0168</c>, <c>BC42024</c>).</summary>
    internal static bool IsTarget(DiagnosticSeverity severity, string? code) =>
        severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning && code is { Length: > 0 } && !CompilerCode().IsMatch(code);

    [GeneratedRegex(@"^(?:CS|BC)\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerCode();

    // ------------------------------------------------------------------ Roslyn's code actions (its CodeActionResolveData is PascalCase)

    private static bool HasTag(JsonElement action, string tag) =>
        action.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("CustomTags", out var tags)
        && tags.ValueKind == JsonValueKind.Array && tags.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == tag);

    private static bool IsFixAll(JsonElement action) => CommandOf(action) == RoslynCommands.FixAllCodeAction;

    private static bool IsNested(JsonElement action) => CommandOf(action) == RoslynCommands.NestedCodeAction;

    private static string? CommandOf(JsonElement action) =>
        action.ValueKind == JsonValueKind.Object && action.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.Object ? c.Str("command") : null;

    /// <summary>Whether Roslyn says the action fixes <paramref name="code"/> (its <c>diagnostics</c> are the request's of the ids it fixes).</summary>
    private static bool Fixes(JsonElement action, string code) =>
        action.TryGetProperty("diagnostics", out var ds) && ds.ValueKind == JsonValueKind.Array
        && ds.EnumerateArray().Any(d => d.TryGetProperty("code", out var c) && (c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString()) == code);

    private static IReadOnlyList<string> Flavors(JsonElement action) =>
        action.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("FixAllFlavors", out var f) && f.ValueKind == JsonValueKind.Array
            ? [.. f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    /// <summary>
    /// Applies a resolved action's edit to <paramref name="document"/> (edits to other files are left out) when the deadline has not passed and
    /// the text is still the one it was computed for: checked and applied in one go on the calling thread. False when nothing was applied.
    /// </summary>
    private static bool Apply(ServerExtensionSession session, ITextDocument document, JsonElement resolved, int version, CancellationToken deadline)
    {
        if (!resolved.TryGetProperty("edit", out var e) || e.ValueKind != JsonValueKind.Object || P.LspJson.Deserialize<P.WorkspaceEdit>(e) is not { } lsp) return false;
        var edits = session.Mapper.ToCore(lsp).Changes.Where(kv => SameFile(kv.Key, document.Uri)).SelectMany(kv => kv.Value).ToList();
        if (edits.Count == 0 || deadline.IsCancellationRequested || document.Version != version) return false;
        return document.ApplyEdits(edits);
    }

    private static bool SameFile(Uri a, Uri b) => a == b || (a.IsFile && b.IsFile && a.LocalPath == b.LocalPath);
}
