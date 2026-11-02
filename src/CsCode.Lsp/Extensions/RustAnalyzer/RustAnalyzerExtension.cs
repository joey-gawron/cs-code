using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Core.Platform;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// rust-analyzer's LSP extensions, as its VS Code extension uses them (method names from rust-analyzer's
/// <c>docs/book/src/contributing/lsp-extensions.md</c>, each checked against the installed 1.96.1: docs/RUST.md):
/// <list type="bullet">
/// <item>experimental client capabilities CsCode honours: <c>hoverActions</c>, <c>serverStatusNotification</c>, <c>codeActionGroup</c>,
/// <c>snippetTextEdit</c> (applied as plain text: <see cref="Snippets"/>) and <c>commands</c> (the client-side commands registered here);</item>
/// <item><c>experimental/serverStatus</c> in the status bar (<see cref="ServerStatus"/>);</item>
/// <item>hover actions (Run, Debug, Go to Implementations, Go to Type) as links under the hover;</item>
/// <item>Run | Debug lenses on <c>fn main</c> and examples (test lenses are left to the Test Explorer's own);</item>
/// <item>the commands of <see cref="RustAnalyzerCommands"/> (Expand Macro, Join Lines, … Run…, Debug…).</item>
/// </list>
/// </summary>
public sealed partial class RustAnalyzerExtension : LanguageServerExtension
{
    public const string ServerId = "rust-analyzer";

    /// <summary>The client-side commands of rust-analyzer's VS Code extension that CsCode registers (the <c>commands</c> capability).</summary>
    internal static readonly string[] ClientCommands =
        [RunSingleCommand, DebugSingleCommand, ShowReferencesCommand, GotoLocationCommand];

    public override string Name => ServerId;

    public override bool AppliesTo(LanguageServerDefinition definition) => definition.Id == ServerId;

    public override void AddExperimentalCapabilities(JsonObject experimental)
    {
        experimental["hoverActions"] = true;
        experimental["serverStatusNotification"] = true;
        experimental["codeActionGroup"] = true;
        experimental["snippetTextEdit"] = true;
        experimental["commands"] = new JsonObject { ["commands"] = new JsonArray([.. ClientCommands.Select(c => (JsonNode)JsonValue.Create(c))]) };
    }

    public override void Attach(ServerExtensionSession session)
    {
        session.State = new SessionState();
        session.OnNotification("experimental/serverStatus", p => OnServerStatus(session, p), latestWins: true);
    }

    public override void OnStopped(ServerExtensionSession session) => _status?.Remove(session);

    // ------------------------------------------------------------------ hover actions

    public override bool WantsRawHover => true;

    /// <summary>
    /// rust-analyzer's <c>actions</c> (groups of command links) become one more, trusted, Markdown block under the hover: its links run the
    /// commands through <see cref="ICommandService"/> (the editor runs <c>command:</c> links of trusted content only).
    /// </summary>
    public override Hover? AdjustHover(ServerExtensionSession session, JsonElement raw, Hover? hover, Uri document)
    {
        if (hover is null || !raw.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return hover;
        var links = new List<string>();
        foreach (var group in actions.EnumerateArray())
        {
            if (!group.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in commands.EnumerateArray())
            {
                if (c.Str("command") is not { } id || c.Str("title") is not { } title) continue;
                var args = c.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetRawText() : "[]";
                links.Add($"[{EscapeLinkText(title.Trim())}](command:{id}?{Uri.EscapeDataString(args)})");
            }
        }
        if (links.Count == 0) return hover;
        return hover with { Contents = [.. hover.Contents, new MarkupContent(string.Join(" · ", links), IsMarkdown: true) { IsTrusted = true }] };
    }

    private static string EscapeLinkText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '[' or ']' or '\\' or '*' or '_' or '`') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ snippet text edits

    public override bool RewritesAnswerOf(string method) => Snippets.Methods.Contains(method);

    public override JsonElement RewriteAnswer(ServerExtensionSession session, string method, JsonElement raw) => Snippets.MakePlain(raw);

    // ------------------------------------------------------------------ lenses

    /// <summary>
    /// The server's Run / Debug lenses: kept on binaries and examples (<c>cargo run</c>), dropped on tests, benches and doctests, whose lenses
    /// the Test Explorer draws itself ("Run Test | Debug Test": they would show twice).
    /// </summary>
    public override IReadOnlyList<CodeLens> AdjustCodeLenses(ServerExtensionSession session, IReadOnlyList<CodeLens> lenses, Uri document)
    {
        List<CodeLens>? kept = null;
        for (var i = 0; i < lenses.Count; i++)
        {
            var lens = lenses[i];
            var drop = lens.Command is { CommandId: RunSingleCommand or DebugSingleCommand } c && !(Runnable.From(c.Arguments) is { IsRun: true });
            if (drop) kept ??= [.. lenses.Take(i)];
            else kept?.Add(lens);
        }
        return kept ?? lenses;
    }

    /// <summary>Per-server state: its last status.</summary>
    internal sealed class SessionState
    {
        public ServerStatus? Status;
    }
}

/// <summary><c>experimental/serverStatus</c>: <c>health</c> ok / warning / error, <c>quiescent</c> (done loading and indexing), <c>message</c>.</summary>
internal sealed record ServerStatus(string Health, bool Quiescent, string? Message)
{
    public static ServerStatus? From(JsonElement p) =>
        p.ValueKind != JsonValueKind.Object ? null
        : new ServerStatus(p.Str("health") ?? "ok", p.TryGetProperty("quiescent", out var q) && q.ValueKind == JsonValueKind.True, p.Str("message"));
}

internal static class JsonExtensions
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
