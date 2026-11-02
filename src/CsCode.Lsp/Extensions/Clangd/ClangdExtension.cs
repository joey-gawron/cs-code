using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Extensions.RustAnalyzer;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Extensions.Clangd;

/// <summary>
/// clangd's LSP extensions, as its VS Code extension (vscode-clangd) uses them, each checked against the installed clangd 21 (docs/C-CPP.md §3a):
/// <list type="bullet">
/// <item><c>textDocument/switchSourceHeader</c> (its params are a bare <c>TextDocumentIdentifier</c>): Switch Between Source/Header, under
/// vscode-clangd's id and key (Alt+O, ⌥⌘O on macOS);</item>
/// <item><c>textDocument/inactiveRegions</c>, which clangd sends after each parse once the client offers
/// <c>textDocument.inactiveRegionsCapabilities.inactiveRegions</c> (and then stops colouring skipped code as comments): served to the editor as
/// an <see cref="IInactiveRegionsProvider"/>, which draws it faded;</item>
/// <item><c>textDocument/ast</c>: Show AST of the node at the caret, opened as a read-only document;</item>
/// <item><c>textDocument/clangd.fileStatus</c>, sent when <c>initializationOptions.clangdFileStatus</c> is true: the active file's state
/// ("parsing includes", "building AST", "idle") in the status bar.</item>
/// </list>
/// </summary>
public sealed class ClangdExtension : LanguageServerExtension
{
    public const string ServerId = "clangd";
    public const string SwitchHeaderSourceCommand = "clangd.switchheadersource";
    public const string ShowAstCommand = "clangd.ast";
    private const string Category = "clangd";
    /// <summary>The languages clangd serves (its built-in selector), as a when clause.</summary>
    internal const string WhenClangd = "editorLangId =~ /^(c|cpp|objective-c|objective-cpp|cuda-cpp)$/";

    private ServerExtensionHost? _host;
    private FileStatusDisplay? _fileStatus;

    public override string Name => ServerId;

    /// <summary>The host it registered with (tests reach the running sessions through it).</summary>
    internal ServerExtensionHost? Host => _host;

    public override bool AppliesTo(LanguageServerDefinition definition) => definition.Id == ServerId;

    public override void AddClientCapabilities(JsonObject capabilities)
    {
        if (capabilities["textDocument"] is not JsonObject textDocument) capabilities["textDocument"] = textDocument = [];
        textDocument["inactiveRegionsCapabilities"] = new JsonObject { ["inactiveRegions"] = true };
    }

    public override void AddInitializationOptions(JsonObject options) => options["clangdFileStatus"] = true;

    public override void Register(ServerExtensionHost host)
    {
        _host = host;
        _fileStatus = new FileStatusDisplay(host);
        host.Disposables.Add(_fileStatus);
        if (host.Services.Commands is not { } commands) return;
        host.Disposables.Add(commands.Register(SwitchHeaderSourceCommand, async _ => { await SwitchHeaderSourceAsync().ConfigureAwait(false); return null; },
            new CommandMetadata(SwitchHeaderSourceCommand, "Switch Between Source/Header", Category, Enablement: WhenClangd)));
        host.Disposables.Add(commands.Register(ShowAstCommand, async _ => { await ShowAstAsync().ConfigureAwait(false); return null; },
            new CommandMetadata(ShowAstCommand, "Show AST", Category, Enablement: WhenClangd)));
    }

    public override void Attach(ServerExtensionSession session)
    {
        var regions = new InactiveRegionsStore();
        session.State = regions;
        session.Disposables.Add(session.Services.Registry.Register<IInactiveRegionsProvider>(session.Definition.Selector, regions, session.Definition.Id));
        session.Disposables.Add(regions); // after the registration: disposed first, it tells the editors their regions are gone
        session.OnNotification("textDocument/inactiveRegions", p => OnInactiveRegions(session, regions, p));
        session.OnNotification("textDocument/clangd.fileStatus", p => OnFileStatus(session, p));
    }

    public override void OnStopped(ServerExtensionSession session) => _fileStatus?.Remove(session);

    // ------------------------------------------------------------------ notifications

    private static void OnInactiveRegions(ServerExtensionSession session, InactiveRegionsStore store, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("textDocument", out var td) || LspMapper.ToUri(td.Str("uri")) is not { } uri) return;
        if (!p.TryGetProperty("regions", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var ranges = P.LspJson.Deserialize<P.Range[]>(list) ?? [];
        var regions = new List<TextRange>(Math.Min(ranges.Length, InactiveRegionsStore.MaxRegions));
        foreach (var r in ranges.Take(InactiveRegionsStore.MaxRegions)) regions.Add(session.Mapper.ToCore(r, uri));
        store.Set(uri, regions);
    }

    private void OnFileStatus(ServerExtensionSession session, JsonElement p)
    {
        if (LspMapper.ToUri(p.Str("uri")) is { } uri && p.Str("state") is { } state) _fileStatus?.Set(session, uri, state);
    }

    // ------------------------------------------------------------------ commands

    private LspServices Services => _host!.Services;

    private void Status(string text) => Services.Window?.SetStatusBarMessage(text, TimeSpan.FromSeconds(5));

    /// <summary>The active C-family editor and the clangd session serving it; null (said in the status bar) when there is none.</summary>
    private (ServerExtensionSession Session, ITextDocument Document)? Active(string what)
    {
        if (Services.Navigation?.ActiveDocument is not { } doc)
        {
            Status($"{what}: open a C or C++ file first");
            return null;
        }
        if (_host!.SessionFor(this, doc.Uri) is not { } session)
        {
            Status($"{what}: clangd is not running for {Path.GetFileName(doc.Uri.LocalPath)}");
            return null;
        }
        return (session, doc);
    }

    private async ValueTask SwitchHeaderSourceAsync()
    {
        if (Active("Switch Between Source/Header") is not { } a) return;
        var answer = await a.Session.RequestAsync("textDocument/switchSourceHeader", a.Document, (id, _) => id, CancellationToken.None).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.String } s || LspMapper.ToUri(s.GetString()) is not { } target)
        {
            Status($"No matching header or source file for {Path.GetFileName(a.Document.Uri.LocalPath)}");
            return;
        }
        if (Services.Navigation is { } nav) await nav.OpenAsync(target).ConfigureAwait(false);
    }

    private async ValueTask ShowAstAsync()
    {
        if (Active("Show AST") is not { } a) return;
        if (Services.Navigation?.ActiveCaret is not { } caret) return;
        var answer = await a.Session.RequestAsync("textDocument/ast", a.Document,
            (id, m) => new { textDocument = id, range = m.ToLsp(new TextRange(caret, caret), a.Document.Uri) }, CancellationToken.None,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (answer is not { ValueKind: JsonValueKind.Object } node)
        {
            Status("No AST node at the caret");
            return;
        }
        if (Services.ReadOnlyDocuments is not { } store)
        {
            Status("Read-only documents are not available");
            return;
        }
        var name = Path.GetFileNameWithoutExtension(a.Document.Uri.IsFile ? a.Document.Uri.LocalPath : a.Document.Uri.AbsolutePath);
        var uri = store.Put(ServerId, "AST", name + ".ast", AstText.Render(node));
        if (Services.Navigation is { } nav) await nav.OpenAsync(uri).ConfigureAwait(false);
    }
}
