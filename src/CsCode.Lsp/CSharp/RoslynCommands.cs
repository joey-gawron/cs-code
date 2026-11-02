using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.CSharp;

/// <summary>
/// The commands Roslyn puts in its answers for the client to run (VS Code's C# extension implements them in <c>src/lsptoolshost</c>; Roslyn's
/// <c>executeCommandProvider</c> lists none of them, so without a client-side handler they reach the server and fail). Checked at 5.13.0-1.26514.10:
/// <list type="bullet">
/// <item><c>roslyn.client.nestedCodeAction</c> <c>[data]</c>: a code action that is a menu (a refactoring with several variants). The data's
/// <c>NestedCodeActions</c> are offered in a quick pick; the chosen one is resolved (<c>codeAction/resolve</c>) and its edit applied (or, nested
/// again, offered again).</item>
/// <item><c>roslyn.client.fixAllCodeAction</c> <c>[data]</c>: "Fix all occurrences": the scope is picked among the data's <c>FixAllFlavors</c>
/// (Document, Project, Solution, ContainingMember, ContainingType) and <c>codeAction/resolveFixAll</c> <c>{title, data, scope}</c> answers the edit.</item>
/// <item><c>roslyn.client.completionComplexEdit</c> <c>[textDocument, textEdit, isSnippetString, newOffset]</c>: a completion whose real edit
/// is not a plain text edit (override completion, partial methods): the edit is applied and the caret put at <c>newOffset</c> (−1: after the
/// edit). Snippet placeholders in such an edit are inserted as text.</item>
/// </list>
/// <c>roslyn.client.peekReferences</c> (code lens) is mapped by the editor (CodeLensCommands). Each command finds the Roslyn session serving
/// the document its data names (the first running one otherwise). Registered once per manager; the JSON work runs on the language lane.
/// </summary>
internal static class RoslynCommands
{
    public const string NestedCodeAction = "roslyn.client.nestedCodeAction";
    public const string FixAllCodeAction = "roslyn.client.fixAllCodeAction";
    public const string CompletionComplexEdit = "roslyn.client.completionComplexEdit";
    public const string ResolveFixAllMethod = "codeAction/resolveFixAll";
    /// <summary>"C#: Open Solution...": choose again which solution (or the projects) the running Roslyn server loads.</summary>
    public const string OpenSolution = "csharp.openSolution";

    /// <param name="clients">The running Roslyn clients.</param>
    /// <param name="restart">Restarts the Roslyn servers (the new choice is read when the server opens the code again).</param>
    public static IDisposable Register(ICommandService commands, Func<IReadOnlyList<LanguageClient>> clients, Func<ValueTask>? restart = null)
    {
        var store = new DisposableStore();
        store.Add(commands.Register(OpenSolution, async _ =>
        {
            if (clients().FirstOrDefault() is not { } client || client.Roslyn is not { } session || client.Services.Window is not { } window) return null;
            if (await session.ChooseAgainAsync(window).ConfigureAwait(false) && restart is not null) await restart().ConfigureAwait(false);
            return null;
        }, new CommandMetadata(OpenSolution, "Open Solution...", "C#")));
        store.Add(commands.Register(NestedCodeAction, args => RunAsync(args, clients, NestedAsync), new CommandMetadata(NestedCodeAction, "C#: Choose a Code Action", "C#")));
        store.Add(commands.Register(FixAllCodeAction, args => RunAsync(args, clients, FixAllAsync), new CommandMetadata(FixAllCodeAction, "C#: Fix All Occurrences", "C#")));
        store.Add(commands.Register(CompletionComplexEdit, args => RunAsync(args, clients, ComplexEditAsync), new CommandMetadata(CompletionComplexEdit, "C#: Complete", "C#")));
        return store;
    }

    private static async ValueTask<object?> RunAsync(CommandInvocation invocation, Func<IReadOnlyList<LanguageClient>> clients,
        Func<LanguageClient, JsonElement[], Task> body)
    {
        var json = invocation.Args.Select(a => a switch
        {
            JsonElement e => e,
            null => default,
            _ => JsonSerializer.SerializeToElement(a, a.GetType()),
        }).ToArray();
        var running = clients();
        var uri = json.Length > 0 ? DocumentUriOf(json[0]) : null;
        var client = running.FirstOrDefault(c => uri is not null && c.IsAttached(uri)) ?? running.FirstOrDefault();
        if (client is null) return null;
        try { await body(client, json).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Log.Add("[client] Roslyn command failed: " + ex.Message);
            client.Services.Window?.SetStatusBarMessage("C#: " + ex.Message, TimeSpan.FromSeconds(5));
        }
        return null;
    }

    /// <summary>The document a command's first argument names: a code action's data (<c>TextDocument.uri</c>) or a text document identifier.</summary>
    private static Uri? DocumentUriOf(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var doc = e.TryGetProperty("TextDocument", out var td) ? td : e.TryGetProperty("textDocument", out var td2) ? td2 : e;
        return doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? LspMapperUri(u.GetString()) : null;
    }

    private static Uri? LspMapperUri(string? s) => Features.LspMapper.ToUri(s);

    private static async Task NestedAsync(LanguageClient client, JsonElement[] args)
    {
        if (args.Length == 0 || !args[0].TryGetProperty("NestedCodeActions", out var nested) || nested.ValueKind != JsonValueKind.Array) return;
        var actions = nested.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a => a.Clone()).ToArray();
        if (actions.Length == 0 || client.Services.Window is not { } window) return;
        var items = actions.Select((a, i) => new QuickPickItem(a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", Tag: i)).ToArray();
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Placeholder: "Choose a code action")).ConfigureAwait(false);
        if (picked?.Tag is not int index) return;
        var action = actions[index];
        var resolved = await RequestAsync(client, P.LspMethods.CodeActionResolve, action).ConfigureAwait(false) ?? action;
        await ApplyActionAsync(client, resolved).ConfigureAwait(false);
    }

    private static async Task FixAllAsync(LanguageClient client, JsonElement[] args)
    {
        if (args.Length == 0 || args[0].ValueKind != JsonValueKind.Object) return;
        var data = args[0].Clone();
        var flavors = data.TryGetProperty("FixAllFlavors", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
            : [];
        if (flavors.Length == 0 || client.Services.Window is not { } window) return;
        var scope = flavors.Length == 1 ? flavors[0]
            : (await window.ShowQuickPickAsync(flavors.Select(x => new QuickPickItem(ScopeLabel(x), Tag: x)).ToArray(),
                new QuickPickOptions(Placeholder: "Fix all occurrences in")).ConfigureAwait(false))?.Tag as string;
        if (scope is null) return;
        var title = data.TryGetProperty("UniqueIdentifier", out var id) ? id.GetString() : "Fix all";
        var resolved = await RequestAsync(client, ResolveFixAllMethod, new { title, data, scope }).ConfigureAwait(false);
        if (resolved is { } r) await ApplyActionAsync(client, r).ConfigureAwait(false);
    }

    private static string ScopeLabel(string flavor) => flavor switch
    {
        "Document" => "Document",
        "Project" => "Project",
        "Solution" => "Solution",
        "ContainingMember" => "Containing member",
        "ContainingType" => "Containing type",
        _ => flavor,
    };

    private static async Task ComplexEditAsync(LanguageClient client, JsonElement[] args)
    {
        if (args.Length < 2 || client.Services.Navigation is not { } navigation) return;
        var uri = DocumentUriOf(args[0]);
        if (uri is null) return;
        var edit = await client.PlainRequestAsync<Core.Languages.WorkspaceEdit>(LanePriority.Interactive, default, ctx =>
        {
            var te = P.LspJson.Deserialize<P.TextEdit>(args[1]);
            if (te is null) return ValueTask.FromResult<Core.Languages.WorkspaceEdit?>(null);
            var mapped = client.Mapper.ToCore(te, uri);
            return ValueTask.FromResult<Core.Languages.WorkspaceEdit?>(new Core.Languages.WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [uri] = [mapped] }));
        }, CancellationToken.None).ConfigureAwait(false);
        if (edit is null || !await navigation.ApplyWorkspaceEditAsync(edit, "Complete").ConfigureAwait(false)) return;
        var offset = args.Length > 3 && args[3].ValueKind == JsonValueKind.Number && args[3].TryGetInt32(out var o) ? o : -1;
        if (offset >= 0 && client.Services.Documents.Get(uri) is { } doc && offset <= doc.Length)
            await navigation.OpenAsync(uri, TextRange.At(doc.PositionAt(offset).Line, doc.PositionAt(offset).Character)).ConfigureAwait(false);
    }

    /// <summary>A Roslyn request from a command (any thread): sent and answered on the lane; null on a failure or a null answer.</summary>
    private static async Task<JsonElement?> RequestAsync(LanguageClient client, string method, object @params)
    {
        var text = await CSharpServers.RequestTextAsync(client, method, @params, r => r.GetRawText(), CancellationToken.None).ConfigureAwait(false);
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Applies a resolved code action: its edit, then its command (a nested menu runs through the command service again).</summary>
    private static async Task ApplyActionAsync(LanguageClient client, JsonElement action)
    {
        if (action.TryGetProperty("edit", out var e) && e.ValueKind == JsonValueKind.Object && client.Services.Navigation is { } navigation)
        {
            var edit = await client.PlainRequestAsync<Core.Languages.WorkspaceEdit>(LanePriority.Interactive, default,
                ctx => ValueTask.FromResult(P.LspJson.Deserialize<P.WorkspaceEdit>(e) is { } we ? client.Mapper.ToCore(we) : null), CancellationToken.None).ConfigureAwait(false);
            var title = action.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (edit is not null) await navigation.ApplyWorkspaceEditAsync(edit, title).ConfigureAwait(false);
        }
        if (action.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("command", out var id)
            && id.GetString() is { } commandId && client.Services.Commands is { } commands)
        {
            var args = c.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => (object?)x.Clone()).ToArray() : [];
            await commands.ExecuteAsync(commandId, args).ConfigureAwait(false);
        }
    }
}
