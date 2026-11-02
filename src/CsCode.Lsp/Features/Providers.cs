using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Features;

// One small class per Core feature interface. Each is a thin adapter: build the request on the Language lane, send it through
// LanguageClient (ordering, timeouts, cancellation, stale-drop), map the response to immutable Core records.
// Every public method returns a ValueTask whose synchronous part is just DocRequestAsync's enqueue.

internal abstract class LspProvider(LanguageClient client, ProviderKind kind)
{
    protected LanguageClient Client { get; } = client;
    protected ProviderKind Kind { get; } = kind;
    protected LspSettings Settings => Client.Settings;
    protected int Feature(int sub = 0) => (int)Kind * 16 + sub;
    protected static readonly Uri NoDocument = new("file:///");
}

internal sealed class CompletionProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.Completion), ICompletionProvider
{
    private readonly string[] _triggers = entries.SelectMany(e => e.Opt<P.CompletionOptions>()?.TriggerCharacters ?? []).Distinct().ToArray();
    private readonly bool _resolve = entries.Any(e => e.Opt<P.CompletionOptions>()?.ResolveProvider == true);

    public IReadOnlyList<string> TriggerCharacters => _triggers;

    public ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct) =>
        Client.DocRequestAsync<CompletionList>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.CompletionParams
            {
                TextDocument = dr.Id,
                Position = dr.Mapper.ToLsp(position, dr.Uri),
                Context = new P.CompletionContext { TriggerKind = (P.CompletionTriggerKind)(int)context.TriggerKind, TriggerCharacter = context.TriggerCharacter },
            };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentCompletion, p).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r.Value, dr.Uri, Settings.MaxCompletionItems, out _);
        }, ct);

    public async ValueTask<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken ct)
    {
        if (!_resolve) return item;
        var mapper = Client.Mapper;
        var docUri = mapper.TryGetCompletionOrigin(item, out var origin) ? origin.DocumentUri : NoDocument;
        var lsp = mapper.ToLsp(item, docUri);
        var resolved = await Client.PlainRequestAsync<CompletionItem>(LanePriority.Interactive, default, async ctx =>
        {
            var r = await Client.RequestAsync(ctx, P.LspRequests.CompletionItemResolve, lsp, ctx.Token).ConfigureAwait(false);
            return r is null ? null : mapper.MergeResolved(item, r, docUri);
        }, ct).ConfigureAwait(false);
        return resolved ?? item;
    }
}

internal sealed class HoverProvider(LanguageClient client) : LspProvider(client, ProviderKind.Hover), IHoverProvider
{
    public ValueTask<Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Client.DocRequestAsync<Hover>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.HoverParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri) };
            if (Client.Extension is { Extension.WantsRawHover: true } ext)
            {
                // members the protocol has no slot for (rust-analyzer's hover actions) need the raw answer
                var raw = await Client.SendAsync(P.LspRequests.TextDocumentHover.Method, p, dr.Ctx.Priority, dr.Token, dr.Doc).ConfigureAwait(false);
                if (raw is not { ValueKind: JsonValueKind.Object } answer) return null;
                await dr.Ctx.ResumeOnLane();
                var mapped = dr.Mapper.ToCore(Client.Deserialize<P.Hover>(answer, P.LspRequests.TextDocumentHover.Method), dr.Uri);
                var adjusted = mapped;
                Client.RunExtension("AdjustHover", () => adjusted = ext.Extension.AdjustHover(ext, answer, mapped, dr.Uri));
                return adjusted;
            }
            var h = await dr.SendAsync(P.LspRequests.TextDocumentHover, p).ConfigureAwait(false);
            return dr.Mapper.ToCore(h, dr.Uri);
        }, ct);
}

internal sealed class SignatureHelpProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.SignatureHelp), ISignatureHelpProvider
{
    private readonly string[] _triggers = entries.SelectMany(e => e.Opt<P.SignatureHelpOptions>()?.TriggerCharacters ?? []).Distinct().ToArray();
    private readonly string[] _retrigger = entries.SelectMany(e => e.Opt<P.SignatureHelpOptions>()?.RetriggerCharacters ?? []).Distinct().ToArray();

    public IReadOnlyList<string> TriggerCharacters => _triggers;
    public IReadOnlyList<string> RetriggerCharacters => _retrigger;

    public ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ITextDocument document, TextPosition position, SignatureHelpContext context, CancellationToken ct) =>
        Client.DocRequestAsync<SignatureHelp>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.SignatureHelpParams
            {
                TextDocument = dr.Id,
                Position = dr.Mapper.ToLsp(position, dr.Uri),
                Context = new P.SignatureHelpContext
                {
                    TriggerKind = (P.SignatureHelpTriggerKind)(int)context.TriggerKind,
                    TriggerCharacter = context.TriggerCharacter,
                    IsRetrigger = context.IsRetrigger,
                    ActiveSignatureHelp = context.ActiveSignatureHelp is null ? null : ActiveToLsp(context.ActiveSignatureHelp),
                },
            };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentSignatureHelp, p).ConfigureAwait(false);
            return dr.Mapper.ToCore(r);
        }, ct);

    private static P.SignatureHelp ActiveToLsp(SignatureHelp h) => new()
    {
        Signatures = h.Signatures.Select(s => new P.SignatureInformation
        {
            Label = s.Label,
            Parameters = s.Parameters?.Select(x => new P.ParameterInformation { Label = x.LabelRange is { } r ? new int[] { r.Start, r.End } : x.Label }).ToArray(),
            ActiveParameter = s.ActiveParameter,
        }).ToArray(),
        ActiveSignature = h.ActiveSignature,
        ActiveParameter = h.ActiveParameter,
    };
}

internal sealed class DefinitionProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.Definition), IDefinitionProvider
{
    private readonly HashSet<DefinitionKind> _kinds = entries.Select(e => e.Method switch
    {
        P.LspMethods.TextDocumentDefinition => (DefinitionKind?)DefinitionKind.Definition,
        P.LspMethods.TextDocumentDeclaration => DefinitionKind.Declaration,
        P.LspMethods.TextDocumentTypeDefinition => DefinitionKind.TypeDefinition,
        P.LspMethods.TextDocumentImplementation => DefinitionKind.Implementation,
        _ => null,
    }).Where(k => k is not null).Select(k => k!.Value).ToHashSet();

    public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct)
    {
        if (!_kinds.Contains(kind)) return default;
        return Client.DocRequestAsync<IReadOnlyList<LocationLink>>(Kind, Feature((int)kind), document, LanePriority.Interactive, async dr =>
        {
            var tdp = (Id: dr.Id, Pos: dr.Mapper.ToLsp(position, dr.Uri));
            var r = kind switch
            {
                DefinitionKind.Declaration => await dr.SendAsync(P.LspRequests.TextDocumentDeclaration, new P.DeclarationParams { TextDocument = tdp.Id, Position = tdp.Pos }).ConfigureAwait(false),
                DefinitionKind.TypeDefinition => await dr.SendAsync(P.LspRequests.TextDocumentTypeDefinition, new P.TypeDefinitionParams { TextDocument = tdp.Id, Position = tdp.Pos }).ConfigureAwait(false),
                DefinitionKind.Implementation => await dr.SendAsync(P.LspRequests.TextDocumentImplementation, new P.ImplementationParams { TextDocument = tdp.Id, Position = tdp.Pos }).ConfigureAwait(false),
                _ => await dr.SendAsync(P.LspRequests.TextDocumentDefinition, new P.DefinitionParams { TextDocument = tdp.Id, Position = tdp.Pos }).ConfigureAwait(false),
            };
            return dr.Mapper.ToLinks(r, dr.Uri, Settings.MaxReferences);
        }, ct);
    }
}

internal sealed class ReferencesProvider(LanguageClient client) : LspProvider(client, ProviderKind.References), IReferenceProvider
{
    public ValueTask<IReadOnlyList<Location>?> ProvideReferencesAsync(ITextDocument document, TextPosition position, bool includeDeclaration, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<Location>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.ReferenceParams
            {
                TextDocument = dr.Id,
                Position = dr.Mapper.ToLsp(position, dr.Uri),
                Context = new P.ReferenceContext { IncludeDeclaration = includeDeclaration },
            };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentReferences, p).ConfigureAwait(false);
            if (r is null) return null;
            var cap = Settings.MaxReferences;
            var list = new List<Location>(Math.Min(r.Length, cap));
            foreach (var l in r)
            {
                if (list.Count >= cap) break;
                if (dr.Mapper.ToCore(l) is { } loc) list.Add(loc);
            }
            return list;
        }, ct);
}

internal sealed class DocumentHighlightProvider(LanguageClient client) : LspProvider(client, ProviderKind.DocumentHighlight), IDocumentHighlightProvider
{
    public ValueTask<IReadOnlyList<DocumentHighlight>?> ProvideDocumentHighlightsAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<DocumentHighlight>>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var p = new P.DocumentHighlightParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentDocumentHighlight, p).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r, dr.Uri);
        }, ct);
}

internal sealed class DocumentSymbolProvider(LanguageClient client) : LspProvider(client, ProviderKind.DocumentSymbol), IDocumentSymbolProvider
{
    public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<DocumentSymbol>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentDocumentSymbol, new P.DocumentSymbolParams { TextDocument = dr.Id }).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r.Value, dr.Uri, 50_000);
        }, ct);
}

internal sealed class WorkspaceSymbolProvider(LanguageClient client) : LspProvider(client, ProviderKind.WorkspaceSymbol), IWorkspaceSymbolProvider
{
    public ValueTask<IReadOnlyList<WorkspaceSymbol>?> ProvideWorkspaceSymbolsAsync(string query, CancellationToken ct) =>
        Client.PlainRequestAsync<IReadOnlyList<WorkspaceSymbol>>(LanePriority.Interactive, new WorkKey(Client, Feature()), async ctx =>
        {
            var r = await Client.RequestAsync(ctx, P.LspRequests.WorkspaceSymbol, new P.WorkspaceSymbolParams { Query = query }, ctx.Token).ConfigureAwait(false);
            return r is null ? null : Client.Mapper.ToCore(r.Value, Settings.MaxWorkspaceSymbols);
        }, ct);
}

internal sealed class CodeActionProvider(LanguageClient client) : LspProvider(client, ProviderKind.CodeAction), ICodeActionProvider
{
    private readonly bool _resolve = client.Features.EntriesOf(ProviderKind.CodeAction).Any(e => e.Opt<P.CodeActionOptions>()?.ResolveProvider == true);

    public ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<CodeAction>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var m = dr.Mapper;
            var p = new P.CodeActionParams
            {
                TextDocument = dr.Id,
                Range = m.ToLsp(range, dr.Uri),
                Context = new P.CodeActionContext
                {
                    Diagnostics = context.Diagnostics.Select(d => m.ToLsp(d, dr.Uri)).ToArray(),
                    Only = context.Only?.Select(k => new P.CodeActionKind(k)).ToArray(),
                    TriggerKind = (P.CodeActionTriggerKind)(int)context.TriggerKind,
                },
            };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentCodeAction, p).ConfigureAwait(false);
            if (r is null) return null;
            var list = new List<CodeAction>(r.Length);
            foreach (var item in r) list.Add(item.IsT0 ? m.CommandAsAction(item.AsT0) : m.ToCore(item.AsT1, dr.Uri));
            return list;
        }, ct);

    public async ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct)
    {
        if (!_resolve || action.Edit is not null) return action;
        var m = Client.Mapper;
        var lsp = m.ToLsp(action, NoDocument);
        var resolved = await Client.PlainRequestAsync<CodeAction>(LanePriority.Interactive, default, async ctx =>
        {
            var r = await Client.RequestAsync(ctx, P.LspRequests.CodeActionResolve, lsp, ctx.Token).ConfigureAwait(false);
            return r is null ? null : m.ToCore(r, NoDocument);
        }, ct).ConfigureAwait(false);
        return resolved ?? action;
    }
}

internal sealed class CodeLensProvider(LanguageClient client) : LspProvider(client, ProviderKind.CodeLens), ICodeLensProvider
{
    public ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<CodeLens>>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentCodeLens, new P.CodeLensParams { TextDocument = dr.Id }, contentModifiedRetries: 2).ConfigureAwait(false);
            if (r is null) return null;
            IReadOnlyList<CodeLens> lenses = r.Take(5000).Select(l => dr.Mapper.ToCore(l, dr.Uri)).ToArray();
            if (Client.Extension is { } ext) Client.RunExtension("AdjustCodeLenses", () => lenses = ext.Extension.AdjustCodeLenses(ext, lenses, dr.Uri));
            return lenses;
        }, ct);

    public async ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct)
    {
        if (lens.Command is not null) return lens;
        var m = Client.Mapper;
        var lsp = m.ToLsp(lens, NoDocument);
        var resolved = await Client.PlainRequestAsync<CodeLens>(LanePriority.Background, default, async ctx =>
        {
            var r = await Client.RequestAsync(ctx, P.LspRequests.CodeLensResolve, lsp, ctx.Token).ConfigureAwait(false);
            return r is null ? null : m.ToCore(r, NoDocument);
        }, ct).ConfigureAwait(false);
        return resolved ?? lens;
    }
}

internal sealed class DocumentColorProvider(LanguageClient client) : LspProvider(client, ProviderKind.DocumentColor), IDocumentColorProvider
{
    public ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<ColorInformation>>(Kind, Feature(), document, LanePriority.Idle, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentDocumentColor, new P.DocumentColorParams { TextDocument = dr.Id }).ConfigureAwait(false);
            return r is null ? null : r.Take(5000).Select(c => LspMapper.ToCore(c, dr.Mapper, dr.Uri)).ToArray();
        }, ct);
}

internal sealed class FormattingProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.Formatting), IDocumentFormattingProvider
{
    private readonly bool _doc = entries.Any(e => e.Method == P.LspMethods.TextDocumentFormatting);
    private readonly bool _range = entries.Any(e => e.Method == P.LspMethods.TextDocumentRangeFormatting);
    private readonly string[] _onType = entries.Where(e => e.Method == P.LspMethods.TextDocumentOnTypeFormatting)
        .SelectMany(e => e.Opt<P.DocumentOnTypeFormattingOptions>() is { } o ? new[] { o.FirstTriggerCharacter }.Concat(o.MoreTriggerCharacter ?? []) : [])
        .Where(c => !string.IsNullOrEmpty(c)).Distinct().ToArray();

    public IReadOnlyList<string> OnTypeTriggerCharacters => _onType;

    private static P.FormattingOptions ToLsp(FormattingOptions o) => new()
    {
        TabSize = o.TabSize,
        InsertSpaces = o.InsertSpaces,
        TrimTrailingWhitespace = o.TrimTrailingWhitespace ? true : null,
        InsertFinalNewline = o.InsertFinalNewline ? true : null,
    };

    public ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, FormattingOptions options, CancellationToken ct)
    {
        if (!_doc) return default;
        return Client.DocRequestAsync<IReadOnlyList<TextEdit>>(Kind, Feature(0), document, LanePriority.Interactive, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentFormatting, new P.DocumentFormattingParams { TextDocument = dr.Id, Options = ToLsp(options) }).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r, dr.Uri);
        }, ct);
    }

    public ValueTask<IReadOnlyList<TextEdit>?> FormatRangeAsync(ITextDocument document, TextRange range, FormattingOptions options, CancellationToken ct)
    {
        if (!_range) return default;
        return Client.DocRequestAsync<IReadOnlyList<TextEdit>>(Kind, Feature(1), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.DocumentRangeFormattingParams { TextDocument = dr.Id, Range = dr.Mapper.ToLsp(range, dr.Uri), Options = ToLsp(options) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentRangeFormatting, p).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r, dr.Uri);
        }, ct);
    }

    public ValueTask<IReadOnlyList<TextEdit>?> FormatOnTypeAsync(ITextDocument document, TextPosition position, string typedCharacter, FormattingOptions options, CancellationToken ct)
    {
        if (_onType.Length == 0) return default;
        return Client.DocRequestAsync<IReadOnlyList<TextEdit>>(Kind, Feature(2), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.DocumentOnTypeFormattingParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri), Ch = typedCharacter, Options = ToLsp(options) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentOnTypeFormatting, p).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r, dr.Uri);
        }, ct);
    }
}

internal sealed class RenameProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.Rename), IRenameProvider
{
    private readonly bool _prepare = entries.Any(e => e.Opt<P.RenameOptions>()?.PrepareProvider == true);

    public ValueTask<PrepareRenameResult?> PrepareRenameAsync(ITextDocument document, TextPosition position, CancellationToken ct)
    {
        if (!_prepare) return ValueTask.FromResult<PrepareRenameResult?>(null);
        return Client.DocRequestAsync<PrepareRenameResult>(Kind, Feature(1), document, LanePriority.Interactive, async dr =>
        {
            var pos = dr.Mapper.ToLsp(position, dr.Uri);
            var r = await dr.SendAsync(P.LspRequests.TextDocumentPrepareRename, new P.PrepareRenameParams { TextDocument = dr.Id, Position = pos }).ConfigureAwait(false);
            if (r is null) return null;
            return r.Value.Match(
                range => new PrepareRenameResult(dr.Mapper.ToCore(range, dr.Uri), null),
                withPlaceholder => new PrepareRenameResult(dr.Mapper.ToCore(withPlaceholder.Range, dr.Uri), withPlaceholder.Placeholder),
                _ => new PrepareRenameResult(TextRange.At(position.Line, position.Character), null)); // defaultBehavior: the editor picks the word at the caret
        }, ct);
    }

    public ValueTask<WorkspaceEdit?> ProvideRenameAsync(ITextDocument document, TextPosition position, string newName, CancellationToken ct) =>
        Client.DocRequestAsync<WorkspaceEdit>(Kind, Feature(0), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.RenameParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri), NewName = newName };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentRename, p).ConfigureAwait(false);
            return r is null ? null : dr.Mapper.ToCore(r);
        }, ct);
}

internal sealed class FoldingRangeProvider(LanguageClient client) : LspProvider(client, ProviderKind.FoldingRange), IFoldingRangeProvider
{
    public ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<FoldingRange>>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentFoldingRange, new P.FoldingRangeParams { TextDocument = dr.Id }, contentModifiedRetries: 2).ConfigureAwait(false);
            return r is null ? null : LspMapper.ToCore(r, 50_000);
        }, ct);
}

internal sealed class SelectionRangeProvider(LanguageClient client) : LspProvider(client, ProviderKind.SelectionRange), ISelectionRangeProvider
{
    public ValueTask<IReadOnlyList<SelectionRange>?> ProvideSelectionRangesAsync(ITextDocument document, IReadOnlyList<TextPosition> positions, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<SelectionRange>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.SelectionRangeParams { TextDocument = dr.Id, Positions = positions.Select(x => dr.Mapper.ToLsp(x, dr.Uri)).ToArray() };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentSelectionRange, p).ConfigureAwait(false);
            return r is null ? null : r.Select(s => dr.Mapper.ToCore(s, dr.Uri)).ToArray();
        }, ct);
}

internal sealed class SemanticTokensProvider : LspProvider, ISemanticTokensProvider
{
    private readonly bool _delta;
    private readonly bool _fullSupported;

    public SemanticTokensProvider(LanguageClient client, FeatureEntry[] entries) : base(client, ProviderKind.SemanticTokens)
    {
        var options = entries.Select(e => e.Opt<P.SemanticTokensOptions>()).FirstOrDefault(o => o is not null);
        Legend = new SemanticTokensLegend(options?.Legend?.TokenTypes ?? [], options?.Legend?.TokenModifiers ?? []);
        _fullSupported = options?.Full is not null;
        _delta = options?.Full is { } f && f.IsT1 && f.AsT1.Delta == true;
    }

    public SemanticTokensLegend Legend { get; }

    public ValueTask<SemanticTokens?> ProvideSemanticTokensAsync(ITextDocument document, CancellationToken ct)
    {
        if (!_fullSupported) return default;
        return Client.DocRequestAsync<SemanticTokens>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var cache = Client.TokenCache;
            var cap = Settings.MaxSemanticTokens * 5;
            uint[]? data = null;
            string? resultId = null;

            if (_delta && cache.TryGet(dr.Uri, out var prevId, out var prevData))
            {
                var p = new P.SemanticTokensDeltaParams { TextDocument = dr.Id, PreviousResultId = prevId };
                var r = await dr.SendAsync(P.LspRequests.TextDocumentSemanticTokensFullDelta, p, contentModifiedRetries: 2).ConfigureAwait(false);
                if (r is null) return null;
                if (r.Value.IsT0) { data = r.Value.AsT0.Data; resultId = r.Value.AsT0.ResultId; }
                else if (SemanticTokenCache.ApplyEdits(prevData, r.Value.AsT1.Edits, out var patched)) { data = patched; resultId = r.Value.AsT1.ResultId; }
                else cache.Remove(dr.Uri); // inconsistent delta: next request asks for the full tokens
            }
            if (data is null)
            {
                var r = await dr.SendAsync(P.LspRequests.TextDocumentSemanticTokensFull, new P.SemanticTokensParams { TextDocument = dr.Id }, contentModifiedRetries: 2).ConfigureAwait(false);
                if (r is null) return null;
                data = r.Data;
                resultId = r.ResultId;
            }
            if (data.Length > cap)
            {
                cache.Remove(dr.Uri); // a truncated array can't serve as the base of later deltas
                return new SemanticTokens(data.AsSpan(0, cap / 5 * 5).ToArray(), null);
            }
            if (resultId is not null) cache.Put(dr.Uri, resultId, data);
            return new SemanticTokens(data, resultId);
        }, ct);
    }
}

internal sealed class SemanticTokensRangeProvider : LspProvider, ISemanticTokensRangeProvider
{
    public SemanticTokensRangeProvider(LanguageClient client, FeatureEntry[] entries) : base(client, ProviderKind.SemanticTokensRange)
    {
        var options = entries.Select(e => e.Opt<P.SemanticTokensOptions>()).FirstOrDefault(o => o is not null);
        Legend = new SemanticTokensLegend(options?.Legend?.TokenTypes ?? [], options?.Legend?.TokenModifiers ?? []);
    }

    public SemanticTokensLegend Legend { get; }

    public ValueTask<SemanticTokens?> ProvideSemanticTokensRangeAsync(ITextDocument document, TextRange range, CancellationToken ct) =>
        Client.DocRequestAsync<SemanticTokens>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var p = new P.SemanticTokensRangeParams { TextDocument = dr.Id, Range = dr.Mapper.ToLsp(range, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentSemanticTokensRange, p, contentModifiedRetries: 2).ConfigureAwait(false);
            if (r is null) return null;
            var cap = Settings.MaxSemanticTokens * 5;
            return new SemanticTokens(r.Data.Length > cap ? r.Data.AsSpan(0, cap / 5 * 5).ToArray() : r.Data, r.ResultId);
        }, ct);
}

internal sealed class InlayHintsProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.InlayHint), IInlayHintsProvider
{
    // The client advertises resolveSupport for tooltips and label parts' tooltips, locations and commands (ClientCapabilitiesFactory): a server
    // that resolves (rust-analyzer) leaves them out of textDocument/inlayHint and sends them on inlayHint/resolve, asked when the pointer rests on
    // a hint or before a link is followed.
    private readonly bool _resolve = entries.Any(e => e.Opt<P.InlayHintOptions>()?.ResolveProvider == true);

    public ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<InlayHint>>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var p = new P.InlayHintParams { TextDocument = dr.Id, Range = dr.Mapper.ToLsp(range, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentInlayHint, p, contentModifiedRetries: 2).ConfigureAwait(false);
            return r is null ? null : r.Take(5000).Select(h => dr.Mapper.ToCore(h, dr.Uri, _resolve)).ToArray();
        }, ct);

    public async ValueTask<InlayHint> ResolveInlayHintAsync(InlayHint hint, CancellationToken ct)
    {
        var m = Client.Mapper;
        if (!_resolve || !hint.CanResolve || !m.TryGetInlayHintOrigin(hint, out var origin)) return hint;
        var resolved = await Client.PlainRequestAsync<InlayHint>(LanePriority.Interactive, default, async ctx =>
        {
            var r = await Client.RequestAsync(ctx, P.LspRequests.InlayHintResolve, origin.Hint, ctx.Token).ConfigureAwait(false);
            return r is null ? null : m.ToCore(r, origin.DocumentUri) with { Position = hint.Position }; // where it is placed stays the editor's
        }, ct).ConfigureAwait(false);
        return resolved ?? hint;
    }
}

internal sealed class LinkedEditingProvider(LanguageClient client) : LspProvider(client, ProviderKind.LinkedEditing), ILinkedEditingRangeProvider
{
    public ValueTask<LinkedEditingRanges?> ProvideLinkedEditingRangesAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Client.DocRequestAsync<LinkedEditingRanges>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.LinkedEditingRangeParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentLinkedEditingRange, p).ConfigureAwait(false);
            return r is null ? null : new LinkedEditingRanges(r.Ranges.Select(x => dr.Mapper.ToCore(x, dr.Uri)).ToArray(), r.WordPattern);
        }, ct);
}

internal sealed class CallHierarchyProvider(LanguageClient client) : LspProvider(client, ProviderKind.CallHierarchy), ICallHierarchyProvider
{
    private static HierarchyItem Map(P.CallHierarchyItem i, LspMapper m)
    {
        var uri = LspMapper.ToUri(i.Uri) ?? NoDocument;
        return new HierarchyItem(i.Name, (SymbolKind)(int)i.Kind, uri, m.ToCore(i.Range, uri), m.ToCore(i.SelectionRange, uri), i.Detail, i.Data);
    }

    private static P.CallHierarchyItem Back(HierarchyItem i, LspMapper m) => new()
    {
        Name = i.Name, Kind = (P.SymbolKind)(int)i.Kind, Detail = i.Detail, Uri = LspMapper.ToUriString(i.Uri), Range = m.ToLsp(i.Range, i.Uri), SelectionRange = m.ToLsp(i.SelectionRange, i.Uri), Data = i.Data,
    };

    public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<HierarchyItem>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.CallHierarchyPrepareParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentPrepareCallHierarchy, p).ConfigureAwait(false);
            return r is null ? null : r.Select(i => Map(i, dr.Mapper)).ToArray();
        }, ct);

    public ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct) =>
        Client.PlainRequestAsync<IReadOnlyList<CallHierarchyIncomingCall>>(LanePriority.Interactive, default, async ctx =>
        {
            var m = Client.Mapper;
            var r = await Client.RequestAsync(ctx, P.LspRequests.CallHierarchyIncomingCalls, new P.CallHierarchyIncomingCallsParams { Item = Back(item, m) }, ctx.Token).ConfigureAwait(false);
            return r is null ? null : r.Select(c => new CallHierarchyIncomingCall(Map(c.From, m), c.FromRanges.Select(x => m.ToCore(x, LspMapper.ToUri(c.From.Uri) ?? NoDocument)).ToArray())).ToArray();
        }, ct);

    public ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct) =>
        Client.PlainRequestAsync<IReadOnlyList<CallHierarchyOutgoingCall>>(LanePriority.Interactive, default, async ctx =>
        {
            var m = Client.Mapper;
            var r = await Client.RequestAsync(ctx, P.LspRequests.CallHierarchyOutgoingCalls, new P.CallHierarchyOutgoingCallsParams { Item = Back(item, m) }, ctx.Token).ConfigureAwait(false);
            return r is null ? null : r.Select(c => new CallHierarchyOutgoingCall(Map(c.To, m), c.FromRanges.Select(x => m.ToCore(x, item.Uri)).ToArray())).ToArray();
        }, ct);
}

internal sealed class TypeHierarchyProvider(LanguageClient client) : LspProvider(client, ProviderKind.TypeHierarchy), ITypeHierarchyProvider
{
    private static HierarchyItem Map(P.TypeHierarchyItem i, LspMapper m)
    {
        var uri = LspMapper.ToUri(i.Uri) ?? NoDocument;
        return new HierarchyItem(i.Name, (SymbolKind)(int)i.Kind, uri, m.ToCore(i.Range, uri), m.ToCore(i.SelectionRange, uri), i.Detail, i.Data);
    }

    private static P.TypeHierarchyItem Back(HierarchyItem i, LspMapper m) => new()
    {
        Name = i.Name, Kind = (P.SymbolKind)(int)i.Kind, Detail = i.Detail, Uri = LspMapper.ToUriString(i.Uri), Range = m.ToLsp(i.Range, i.Uri), SelectionRange = m.ToLsp(i.SelectionRange, i.Uri), Data = i.Data,
    };

    public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareTypeHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<HierarchyItem>>(Kind, Feature(), document, LanePriority.Interactive, async dr =>
        {
            var p = new P.TypeHierarchyPrepareParams { TextDocument = dr.Id, Position = dr.Mapper.ToLsp(position, dr.Uri) };
            var r = await dr.SendAsync(P.LspRequests.TextDocumentPrepareTypeHierarchy, p).ConfigureAwait(false);
            return r is null ? null : r.Select(i => Map(i, dr.Mapper)).ToArray();
        }, ct);

    public ValueTask<IReadOnlyList<HierarchyItem>?> SupertypesAsync(HierarchyItem item, CancellationToken ct) =>
        Client.PlainRequestAsync<IReadOnlyList<HierarchyItem>>(LanePriority.Interactive, default, async ctx =>
        {
            var m = Client.Mapper;
            var r = await Client.RequestAsync(ctx, P.LspRequests.TypeHierarchySupertypes, new P.TypeHierarchySupertypesParams { Item = Back(item, m) }, ctx.Token).ConfigureAwait(false);
            return r is null ? null : r.Select(i => Map(i, m)).ToArray();
        }, ct);

    public ValueTask<IReadOnlyList<HierarchyItem>?> SubtypesAsync(HierarchyItem item, CancellationToken ct) =>
        Client.PlainRequestAsync<IReadOnlyList<HierarchyItem>>(LanePriority.Interactive, default, async ctx =>
        {
            var m = Client.Mapper;
            var r = await Client.RequestAsync(ctx, P.LspRequests.TypeHierarchySubtypes, new P.TypeHierarchySubtypesParams { Item = Back(item, m) }, ctx.Token).ConfigureAwait(false);
            return r is null ? null : r.Select(i => Map(i, m)).ToArray();
        }, ct);
}
