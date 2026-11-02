using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Extensions.RustAnalyzer;

/// <summary>
/// rust-analyzer's commands, under the ids of its VS Code extension (palette category "Rust"). Each asks the server that serves the active
/// editor (<see cref="ServerExtensionHost.ActiveSession"/>); the editor commands use its caret (CsCode's navigation service exposes the caret,
/// not the selection: Join Lines joins the caret's line with the next, as VS Code does for an empty selection).
/// </summary>
public sealed partial class RustAnalyzerExtension
{
    public const string ExpandMacroCommand = "rust-analyzer.expandMacro";
    public const string JoinLinesCommand = "rust-analyzer.joinLines";
    public const string MatchingBraceCommand = "rust-analyzer.matchingBrace";
    public const string ParentModuleCommand = "rust-analyzer.parentModule";
    public const string OpenCargoTomlCommand = "rust-analyzer.openCargoToml";
    public const string ViewSyntaxTreeCommand = "rust-analyzer.viewSyntaxTree";
    public const string ViewHirCommand = "rust-analyzer.viewHir";
    public const string ViewMirCommand = "rust-analyzer.viewMir";
    public const string SsrCommand = "rust-analyzer.ssr";
    public const string OpenDocsCommand = "rust-analyzer.openDocs";
    public const string ReloadWorkspaceCommand = "rust-analyzer.reloadWorkspace";
    public const string RebuildProcMacrosCommand = "rust-analyzer.rebuildProcMacros";
    public const string RunCommand = "rust-analyzer.run";
    public const string DebugCommand = "rust-analyzer.debug";
    public const string RunSingleCommand = "rust-analyzer.runSingle";
    public const string DebugSingleCommand = "rust-analyzer.debugSingle";
    public const string ShowReferencesCommand = "rust-analyzer.showReferences";
    public const string GotoLocationCommand = "rust-analyzer.gotoLocation";
    public const string StatusMenuCommand = "rust-analyzer.statusMenu";
    private const string Category = "Rust";
    private const string WhenRust = "editorLangId == rust";

    private ServerExtensionHost? _host;
    private ServerStatusDisplay? _status;

    public override void Register(ServerExtensionHost host)
    {
        _host = host;
        _status = new ServerStatusDisplay(host, this);
        host.Disposables.Add(_status);
        if (host.Services.Commands is not { } commands) return;

        void Add(string id, string title, Func<CommandInvocation, ValueTask> run, string? when = WhenRust) =>
            host.Disposables.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(false); return null; }, new CommandMetadata(id, title, Category, Enablement: when)));

        Add(ExpandMacroCommand, "Expand Macro Recursively at Caret", _ => ExpandMacroAsync());
        Add(JoinLinesCommand, "Join Lines", _ => JoinLinesAsync());
        Add(MatchingBraceCommand, "Find Matching Brace", _ => MatchingBraceAsync());
        Add(ParentModuleCommand, "Locate Parent Module", _ => ParentModuleAsync());
        Add(OpenCargoTomlCommand, "Open Cargo.toml", _ => OpenCargoTomlAsync());
        Add(ViewSyntaxTreeCommand, "Show Syntax Tree", _ => ViewSyntaxTreeAsync());
        Add(ViewHirCommand, "View Hir", _ => ViewTextAsync("rust-analyzer/viewHir", "hir", "HIR"));
        Add(ViewMirCommand, "View Mir", _ => ViewTextAsync("rust-analyzer/viewMir", "mir", "MIR"));
        Add(SsrCommand, "Structural Search Replace", _ => SsrAsync());
        Add(OpenDocsCommand, "Open External Docs", _ => OpenDocsAsync());
        Add(ReloadWorkspaceCommand, "Reload Workspace", _ => WorkspaceRequestAsync("rust-analyzer/reloadWorkspace", "Reloading the workspace"), when: null);
        Add(RebuildProcMacrosCommand, "Rebuild Build Dependencies and Proc Macros", _ => WorkspaceRequestAsync("rust-analyzer/rebuildProcMacros", "Rebuilding build scripts and proc macros"), when: null);
        Add(RunCommand, "Run...", _ => PickRunnableAsync(debug: false));
        Add(DebugCommand, "Debug...", _ => PickRunnableAsync(debug: true));
        Add(StatusMenuCommand, "Server Status", _ => StatusMenuAsync(), when: null);
        // client-side commands of rust-analyzer's VS Code extension, named in lenses and hover actions (the "commands" capability)
        Add(RunSingleCommand, "Run Runnable", inv => RunSingleAsync(inv, debug: false), when: null);
        Add(DebugSingleCommand, "Debug Runnable", inv => RunSingleAsync(inv, debug: true), when: null);
        Add(ShowReferencesCommand, "Show References", ShowReferencesAsync, when: null);
        Add(GotoLocationCommand, "Go to Location", GotoLocationAsync, when: null);
    }

    // ------------------------------------------------------------------ context

    private LspServices Services => _host!.Services;

    /// <summary>The active Rust editor, its caret and the session serving it; null (said in the status bar) when there is none.</summary>
    private (ServerExtensionSession Session, ITextDocument Document, TextPosition Caret)? Active(string what)
    {
        var nav = Services.Navigation;
        if (nav?.ActiveDocument is not { } doc || nav.ActiveCaret is not { } caret)
        {
            Status($"{what}: open a Rust file first");
            return null;
        }
        if (_host!.SessionFor(this, doc.Uri) is not { } session)
        {
            Status($"{what}: rust-analyzer is not running for {Path.GetFileName(doc.Uri.LocalPath)}");
            return null;
        }
        return (session, doc, caret);
    }

    private void Status(string text) => Services.Window?.SetStatusBarMessage(text, TimeSpan.FromSeconds(5));

    private object PositionParams(P.TextDocumentIdentifier id, LspMapper m, Uri uri, TextPosition caret) =>
        new { textDocument = id, position = m.ToLsp(caret, uri) };

    private ValueTask<JsonElement?> AtCaretAsync(string method, string what, out (ServerExtensionSession Session, ITextDocument Document, TextPosition Caret)? active)
    {
        active = Active(what);
        if (active is not { } a) return ValueTask.FromResult<JsonElement?>(null);
        return a.Session.RequestAsync(method, a.Document, (id, m) => PositionParams(id, m, a.Document.Uri, a.Caret), CancellationToken.None);
    }

    private async ValueTask OpenAsync(Uri uri, TextRange? range = null)
    {
        if (Services.Navigation is { } nav) await nav.OpenAsync(uri, range).ConfigureAwait(false);
    }

    /// <summary>Shows server-computed text as a read-only document (<see cref="ReadOnlyDocuments"/>).</summary>
    private async ValueTask ShowReadOnlyAsync(string kind, string fileName, string text)
    {
        if (Services.ReadOnlyDocuments is not { } store)
        {
            Status("Read-only documents are not available");
            return;
        }
        await OpenAsync(store.Put(ServerId, kind, fileName, text)).ConfigureAwait(false);
    }

    private static string StemOf(Uri uri) => Path.GetFileNameWithoutExtension(uri.IsFile ? uri.LocalPath : uri.AbsolutePath);

    // ------------------------------------------------------------------ editor commands

    private async ValueTask ExpandMacroAsync()
    {
        var answer = await AtCaretAsync("rust-analyzer/expandMacro", "Expand Macro", out var a).ConfigureAwait(false);
        if (a is null) return;
        if (answer is not { ValueKind: JsonValueKind.Object } r || r.Str("expansion") is not { } expansion)
        {
            Status("No macro call at the caret");
            return;
        }
        var name = r.Str("name") ?? "macro";
        await ShowReadOnlyAsync("expand-macro", name + ".rs", $"// Recursive expansion of {name} macro\n// {new string('=', 32 + name.Length)}\n\n{expansion}").ConfigureAwait(false);
    }

    private async ValueTask JoinLinesAsync()
    {
        if (Active("Join Lines") is not { } a) return;
        var answer = await a.Session.RequestAsync("experimental/joinLines", a.Document,
            (id, m) => new { textDocument = id, ranges = new[] { m.ToLsp(new TextRange(a.Caret, a.Caret), a.Document.Uri) } }, CancellationToken.None).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Array } edits || edits.GetArrayLength() == 0) return;
        var mapped = a.Session.Mapper.ToCore(P.LspJson.Deserialize<P.TextEdit[]>(edits), a.Document.Uri);
        if (Services.Navigation is { } nav)
            await nav.ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<Uri, IReadOnlyList<TextEdit>> { [a.Document.Uri] = mapped }), "Join Lines").ConfigureAwait(false);
    }

    private async ValueTask MatchingBraceAsync()
    {
        if (Active("Find Matching Brace") is not { } a) return;
        var answer = await a.Session.RequestAsync("experimental/matchingBrace", a.Document,
            (id, m) => new { textDocument = id, positions = new[] { m.ToLsp(a.Caret, a.Document.Uri) } }, CancellationToken.None).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Array } positions || positions.GetArrayLength() == 0) return;
        var target = a.Session.Mapper.ToCore(P.LspJson.Deserialize<P.Position>(positions[0]), a.Document.Uri);
        if (target != a.Caret) await OpenAsync(a.Document.Uri, new TextRange(target, target)).ConfigureAwait(false);
    }

    private async ValueTask ParentModuleAsync()
    {
        var answer = await AtCaretAsync("experimental/parentModule", "Locate Parent Module", out var a).ConfigureAwait(false);
        if (a is null) return;
        var locations = Locations(a.Value.Session, answer);
        if (locations.Count == 0) { Status("This is the crate root: it has no parent module"); return; }
        await OpenLocationsAsync(locations, "Parent modules").ConfigureAwait(false);
    }

    private async ValueTask OpenCargoTomlAsync()
    {
        if (Active("Open Cargo.toml") is not { } a) return;
        var answer = await a.Session.RequestAsync("experimental/openCargoToml", a.Document, (id, _) => new { textDocument = id }, CancellationToken.None).ConfigureAwait(false);
        var locations = Locations(a.Session, answer);
        if (locations.Count == 0) { Status("No Cargo.toml for this file"); return; }
        await OpenAsync(locations[0].Uri, locations[0].Range).ConfigureAwait(false);
    }

    private async ValueTask ViewSyntaxTreeAsync()
    {
        if (Active("Show Syntax Tree") is not { } a) return;
        var answer = await a.Session.RequestAsync("rust-analyzer/viewSyntaxTree", a.Document, (id, _) => new { textDocument = id }, CancellationToken.None,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.String } s) { Status("rust-analyzer returned no syntax tree"); return; }
        await ShowReadOnlyAsync("syntax-tree", StemOf(a.Document.Uri) + ".rast", SyntaxTreeText.Render(s.GetString()!)).ConfigureAwait(false);
    }

    private async ValueTask ViewTextAsync(string method, string kind, string what)
    {
        var answer = await AtCaretAsync(method, "View " + what, out var a).ConfigureAwait(false);
        if (a is null) return;
        if (answer is not { ValueKind: JsonValueKind.String } s || s.GetString() is not { Length: > 0 } text) { Status($"No {what} for the function at the caret"); return; }
        await ShowReadOnlyAsync(kind, StemOf(a.Value.Document.Uri) + "." + kind, text).ConfigureAwait(false);
    }

    private async ValueTask SsrAsync()
    {
        if (Active("Structural Search Replace") is not { } a || Services.Window is not { } window) return;
        var query = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "Structural Search Replace", Prompt: "Enter request, for example 'Foo($a) ==>> Foo::new($a)'",
            Placeholder: "pattern ==>> replacement")).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query)) return;
        var answer = await a.Session.RequestAsync("experimental/ssr", a.Document, (id, m) => new
        {
            query,
            parseOnly = false,
            textDocument = id,
            position = m.ToLsp(a.Caret, a.Document.Uri),
            selections = Array.Empty<object>(),
        }, CancellationToken.None, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Object } edit)
        {
            await window.ShowMessageAsync(MessageSeverity.Error, $"Structural search replace failed: rust-analyzer could not run '{query}' (see its output).").ConfigureAwait(false);
            return;
        }
        var mapped = a.Session.Mapper.ToCore(P.LspJson.Deserialize<P.WorkspaceEdit>(edit)!);
        if (mapped.IsEmpty) { Status($"No matches for '{query}'"); return; }
        if (Services.Navigation is { } nav) await nav.ApplyWorkspaceEditAsync(mapped, "Structural Search Replace").ConfigureAwait(false);
    }

    private async ValueTask OpenDocsAsync()
    {
        var answer = await AtCaretAsync("experimental/externalDocs", "Open External Docs", out var a).ConfigureAwait(false);
        if (a is null) return;
        // a string (the web URL), or {web, local} when the client advertises localDocs (CsCode does not)
        var url = answer switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Object } o => o.Str("web") ?? o.Str("local"),
            _ => null,
        };
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) { Status("No documentation for the symbol at the caret"); return; }
        if (Services.Window is { } window) await window.OpenExternalAsync(uri).ConfigureAwait(false);
    }

    private async ValueTask WorkspaceRequestAsync(string method, string what)
    {
        var sessions = _host!.ActiveSession(this) is { } active ? [active] : _host.Sessions(this);
        if (sessions.Count == 0) { Status("rust-analyzer is not running"); return; }
        Status(what + "…");
        foreach (var s in sessions) await s.RequestAsync(method, null, CancellationToken.None, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
    }

    private async ValueTask StatusMenuAsync()
    {
        if (Services.Window is not { } window) return;
        var status = _status?.Current;
        var items = new List<QuickPickItem>
        {
            new("Reload Workspace", Tag: ReloadWorkspaceCommand),
            new("Rebuild Build Dependencies and Proc Macros", Tag: RebuildProcMacrosCommand),
            new("Restart Server", Tag: "restart"),
        };
        var picked = await window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "rust-analyzer", Placeholder: status?.Message?.Split('\n')[0] ?? ServerStatusDisplay.Describe(status))).ConfigureAwait(false);
        switch (picked?.Tag)
        {
            case "restart":
                if (_host!.Manager is { } manager) await manager.RestartAsync(ServerId).ConfigureAwait(false);
                break;
            case string id when Services.Commands is { } commands:
                await commands.ExecuteAsync(id).ConfigureAwait(false);
                break;
        }
    }

    // ------------------------------------------------------------------ locations

    /// <summary>Location | Location[] | LocationLink[] (rust-analyzer answers any of them) as Core locations.</summary>
    private static List<Location> Locations(ServerExtensionSession session, JsonElement? answer)
    {
        var result = new List<Location>();
        void One(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("targetUri", out _))
            {
                if (P.LspJson.Deserialize<P.LocationLink>(e) is { } link && session.Mapper.ToCore(link) is { } l) result.Add(new Location(l.TargetUri, l.TargetSelectionRange));
            }
            else if (session.Mapper.ToCore(P.LspJson.Deserialize<P.Location>(e)) is { } loc) result.Add(loc);
        }
        switch (answer)
        {
            case { ValueKind: JsonValueKind.Array } arr: foreach (var e in arr.EnumerateArray()) One(e); break;
            case { ValueKind: JsonValueKind.Object } o: One(o); break;
        }
        return result;
    }

    private async ValueTask OpenLocationsAsync(IReadOnlyList<Location> locations, string title)
    {
        if (locations.Count == 1 || Services.Window is not { } window)
        {
            await OpenAsync(locations[0].Uri, locations[0].Range).ConfigureAwait(false);
            return;
        }
        var picked = await window.ShowQuickPickAsync(
            [.. locations.Select(l => new QuickPickItem(Path.GetFileName(l.Uri.LocalPath) + ":" + (l.Range.Start.Line + 1), l.Uri.IsFile ? l.Uri.LocalPath : l.Uri.ToString(), Tag: l))],
            new QuickPickOptions(Title: title)).ConfigureAwait(false);
        if (picked?.Tag is Location chosen) await OpenAsync(chosen.Uri, chosen.Range).ConfigureAwait(false);
    }

    /// <summary><c>rust-analyzer.showReferences(uri, position, locations)</c> (hover's "Go to N implementations"): the shell's references list.</summary>
    private async ValueTask ShowReferencesAsync(CommandInvocation inv)
    {
        var locations = inv.Args.Count > 2 && inv.Args[2] is JsonElement l && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(e => P.LspJson.Deserialize<P.Location>(e)).Select(LocationOf).OfType<Location>().ToList()
            : [];
        if (locations.Count == 0) { Status("No locations"); return; }
        if (locations.Count > 1 && Services.Commands is { } commands && commands.Has("cscode.references.show"))
        {
            await commands.ExecuteAsync("cscode.references.show", locations, "Implementations").ConfigureAwait(false);
            return;
        }
        await OpenLocationsAsync(locations, "Locations").ConfigureAwait(false);
    }

    /// <summary><c>rust-analyzer.gotoLocation(location)</c> (hover's "Go to Type").</summary>
    private async ValueTask GotoLocationAsync(CommandInvocation inv)
    {
        if (inv.Args.Count > 0 && inv.Args[0] is JsonElement e && e.ValueKind == JsonValueKind.Object && LocationOf(P.LspJson.Deserialize<P.Location>(e)) is { } location)
            await OpenAsync(location.Uri, location.Range).ConfigureAwait(false);
    }

    /// <summary>A location from a command argument: no session's encoding applies (UTF-16 as sent; columns only place the caret).</summary>
    private static Location? LocationOf(P.Location? l) =>
        l is null || LspMapper.ToUri(l.Uri) is not { } uri ? null
        : new Location(uri, new TextRange(new TextPosition(l.Range.Start.Line, l.Range.Start.Character), new TextPosition(l.Range.End.Line, l.Range.End.Character)));
}
