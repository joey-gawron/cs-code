using System.Runtime.CompilerServices;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Features;

/// <summary>
/// LSP &lt;-&gt; Core model mapping for ONE server session (it owns the negotiated position encoding). Pure functions of their inputs:
/// always executed on the Language lane, never on the UI thread, and every result is an immutable Core record.
/// With UTF-16 (default) position conversion is the identity and never touches line text.
/// </summary>
internal sealed class LspMapper(PositionEncoder encoder, Func<Uri, int, string?> lineSource)
{
    // Original protocol objects behind Core items, so resolve requests can echo exactly what the server sent.
    // Weak: nothing is retained once the consumer drops the Core item (bounded by construction, no explicit cache to trim).
    private readonly ConditionalWeakTable<object, object> _originals = new();

    public PositionEncoder Encoder { get; } = encoder;

    public LspMapper() : this(PositionEncoder.Utf16, static (_, _) => null) { }

    /// <summary>The protocol item behind a mapped completion item, plus the document it was requested for.</summary>
    internal sealed record CompletionOrigin(P.CompletionItem Item, Uri DocumentUri);

    public bool TryGetCompletionOrigin(CompletionItem item, out CompletionOrigin origin)
    {
        if (_originals.TryGetValue(item, out var o) && o is CompletionOrigin co) { origin = co; return true; }
        origin = null!;
        return false;
    }

    // ---- URIs ---------------------------------------------------------------------------------------------------

    public static string ToUriString(Uri uri) => uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;

    public static Uri? ToUri(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
    }

    // ---- positions ----------------------------------------------------------------------------------------------

    public P.Position ToLsp(TextPosition p, Uri uri)
    {
        if (Encoder.IsIdentity) return new P.Position { Line = p.Line, Character = p.Character };
        var line = lineSource(uri, p.Line);
        return new P.Position { Line = p.Line, Character = line is null ? p.Character : Encoder.ToLsp(line, p.Character) };
    }

    public P.Range ToLsp(TextRange r, Uri uri) => new() { Start = ToLsp(r.Start, uri), End = ToLsp(r.End, uri) };

    public TextPosition ToCore(P.Position? p, Uri uri)
    {
        if (p is null) return default; // a sloppy server omitted a required member: degrade, never throw
        if (Encoder.IsIdentity) return new TextPosition(p.Line, p.Character);
        var line = lineSource(uri, p.Line);
        return new TextPosition(p.Line, line is null ? p.Character : Encoder.FromLsp(line, p.Character));
    }

    public TextRange ToCore(P.Range? r, Uri uri) => r is null ? default : new(ToCore(r.Start, uri), ToCore(r.End, uri));

    public TextRange ToCoreOrEmpty(P.Range? r, Uri uri) => r is null ? default : ToCore(r, uri);

    public TextEdit ToCore(P.TextEdit e, Uri uri) => new(ToCore(e.Range, uri), e.NewText ?? string.Empty);

    public IReadOnlyList<TextEdit> ToCore(P.TextEdit[]? edits, Uri uri)
    {
        if (edits is null || edits.Length == 0) return [];
        var result = new TextEdit[edits.Length];
        for (var i = 0; i < edits.Length; i++) result[i] = ToCore(edits[i], uri);
        return result;
    }

    // ---- markup / commands --------------------------------------------------------------------------------------

    public static MarkupContent? ToCore(P.OneOf<string, P.MarkupContent>? doc) => doc is null ? null : ToCore(doc.Value);

    public static MarkupContent ToCore(P.OneOf<string, P.MarkupContent> doc) =>
        doc.Match(s => MarkupContent.Plain(s), m => new MarkupContent(m.Value ?? string.Empty, m.Kind.Value == "markdown"));

    public static Command? ToCore(P.Command? c) =>
        c is null ? null : new Command(c.Title ?? string.Empty, c.CommandValue ?? string.Empty, c.Arguments is { Length: > 0 } a ? a.Select(x => x).ToArray() : null);

    public static P.Command? ToLsp(Command? c) =>
        c is null ? null : new P.Command { Title = c.Title, CommandValue = c.CommandId, Arguments = c.Arguments?.Select(x => x).ToArray() };

    // ---- diagnostics --------------------------------------------------------------------------------------------

    public Diagnostic ToCore(P.Diagnostic d, Uri docUri)
    {
        var tags = d.Tags is { Length: > 0 } t ? t.Select(x => (DiagnosticTag)(int)x).Where(x => x is DiagnosticTag.Unnecessary or DiagnosticTag.Deprecated).ToArray() : null;
        IReadOnlyList<DiagnosticRelatedInformation>? related = null;
        if (d.RelatedInformation is { Length: > 0 } ri)
        {
            var list = new List<DiagnosticRelatedInformation>(ri.Length);
            foreach (var r in ri)
                if (ToCore(r.Location) is { } loc) list.Add(new DiagnosticRelatedInformation(loc, r.Message));
            related = list;
        }
        var result = new Diagnostic(
            ToCore(d.Range, docUri),
            d.Message ?? string.Empty,
            d.Severity is { } sev and >= P.DiagnosticSeverity.Error and <= P.DiagnosticSeverity.Hint ? (DiagnosticSeverity)(int)sev : DiagnosticSeverity.Error,
            d.Code?.Match(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), s => s),
            d.Source,
            tags,
            related,
            ToUri(d.CodeDescription?.Href));
        _originals.AddOrUpdate(result, d);
        return result;
    }

    public P.Diagnostic ToLsp(Diagnostic d, Uri docUri)
    {
        if (_originals.TryGetValue(d, out var o) && o is P.Diagnostic original) return original;
        return new P.Diagnostic
        {
            Range = ToLsp(d.Range, docUri),
            Message = d.Message,
            Severity = (P.DiagnosticSeverity)(int)d.Severity,
            Code = d.Code is { } code ? new P.OneOf<int, string>?(code) : null,
            Source = d.Source,
            Tags = d.Tags?.Select(t => (P.DiagnosticTag)(int)t).ToArray(),
        };
    }

    // ---- locations ----------------------------------------------------------------------------------------------

    public Location? ToCore(P.Location? l)
    {
        if (l is null || ToUri(l.Uri) is not { } uri) return null;
        return new Location(uri, ToCore(l.Range, uri));
    }

    public LocationLink? ToCore(P.LocationLink l)
    {
        if (ToUri(l.TargetUri) is not { } uri) return null;
        return new LocationLink(uri, ToCore(l.TargetRange, uri), ToCore(l.TargetSelectionRange, uri), null);
    }

    /// <summary>Definition | Declaration | TypeDefinition | Implementation result -> links, capped.</summary>
    public IReadOnlyList<LocationLink>? ToLinks(P.OneOf<P.OneOf<P.Location, P.Location[]>, P.LocationLink[]>? result, Uri originUri, int cap)
    {
        if (result is null) return null;
        var list = new List<LocationLink>();
        var v = result.Value;
        if (v.IsT1)
        {
            foreach (var l in v.AsT1)
            {
                if (list.Count >= cap) break;
                if (ToCore(l) is { } link) list.Add(link);
            }
        }
        else
        {
            var inner = v.AsT0;
            if (inner.IsT0)
            {
                if (ToCore(inner.AsT0) is { } loc) list.Add(new LocationLink(loc.Uri, loc.Range, loc.Range));
            }
            else
                foreach (var l in inner.AsT1)
                {
                    if (list.Count >= cap) break;
                    if (ToCore(l) is { } loc) list.Add(new LocationLink(loc.Uri, loc.Range, loc.Range));
                }
        }
        return list;
    }

    // ---- hover / signature --------------------------------------------------------------------------------------

    public Hover? ToCore(P.Hover? h, Uri uri)
    {
        if (h is null) return null;
        var contents = new List<MarkupContent>();
        var c = h.Contents;
        if (c.IsT0) contents.Add(new MarkupContent(c.AsT0.Value ?? string.Empty, c.AsT0.Kind.Value == "markdown"));
        else if (c.IsT1) contents.Add(MarkedString(c.AsT1));
        else foreach (var m in c.AsT2) contents.Add(MarkedString(m));
        contents.RemoveAll(m => m.Value.Length == 0);
        if (contents.Count == 0) return null;
        return new Hover(contents, h.Range is null ? null : ToCore(h.Range, uri));
    }

    private static MarkupContent MarkedString(P.OneOf<string, P.MarkedStringOption2> m) =>
        m.Match(s => MarkupContent.Markdown(s), lit => MarkupContent.Markdown($"```{lit.Language}\n{lit.Value}\n```"));

    public SignatureHelp? ToCore(P.SignatureHelp? h)
    {
        if (h is null || h.Signatures is not { Length: > 0 }) return null;
        var sigs = new List<SignatureInformation>(h.Signatures.Length);
        foreach (var s in h.Signatures)
        {
            var label = s.Label ?? string.Empty;
            List<ParameterInformation>? ps = null;
            if (s.Parameters is { Length: > 0 })
            {
                ps = new List<ParameterInformation>(s.Parameters.Length);
                foreach (var p in s.Parameters)
                {
                    if (p.Label.IsT1 && p.Label.AsT1 is { Length: 2 } off)
                    {
                        var start = Math.Clamp(Encoder.IsIdentity ? off[0] : Encoder.FromLsp(label, off[0]), 0, label.Length);
                        var end = Math.Clamp(Encoder.IsIdentity ? off[1] : Encoder.FromLsp(label, off[1]), start, label.Length);
                        ps.Add(new ParameterInformation(label[start..end], ToCore(p.Documentation), (start, end)));
                    }
                    else ps.Add(new ParameterInformation(p.Label.IsT0 ? p.Label.AsT0 : string.Empty, ToCore(p.Documentation)));
                }
            }
            sigs.Add(new SignatureInformation(label, ToCore(s.Documentation), ps, s.ActiveParameter));
        }
        var active = h.ActiveSignature is { } a && a >= 0 && a < sigs.Count ? a : 0;
        return new SignatureHelp(sigs, active, h.ActiveParameter ?? sigs[active].ActiveParameter);
    }

    // ---- completion ---------------------------------------------------------------------------------------------

    public CompletionList ToCore(P.OneOf<P.CompletionItem[], P.CompletionList> result, Uri docUri, int cap, out int totalReceived)
    {
        P.CompletionItem[] items;
        bool incomplete = false;
        P.CompletionListItemDefaults? defaults = null;
        if (result.IsT0) items = result.AsT0;
        else
        {
            items = result.AsT1.Items ?? [];
            incomplete = result.AsT1.IsIncomplete;
            defaults = result.AsT1.ItemDefaults;
        }
        totalReceived = items.Length;
        var take = Math.Min(items.Length, cap);
        if (items.Length > cap) incomplete = true; // never ship 50k items: have the consumer re-query as the user types
        var mapped = new List<CompletionItem>(take);
        for (var i = 0; i < take; i++) mapped.Add(ToCore(items[i], defaults, docUri));
        return new CompletionList(mapped, incomplete);
    }

    public CompletionItem ToCore(P.CompletionItem item, P.CompletionListItemDefaults? defaults, Uri docUri)
    {
        TextEdit? edit = null;
        if (item.TextEdit is { } te)
            edit = te.Match(t => ToCore(t, docUri), ir => new TextEdit(ToCore(ir.Insert, docUri), ir.NewText ?? string.Empty));
        else if (defaults?.EditRange is { } er)
        {
            var range = er.Match(r => r, o => o.Insert);
            edit = new TextEdit(ToCore(range, docUri), item.TextEditText ?? item.Label);
        }

        var format = item.InsertTextFormat ?? defaults?.InsertTextFormat ?? P.InsertTextFormat.PlainText;
        var commit = item.CommitCharacters ?? defaults?.CommitCharacters;
        var data = item.Data ?? defaults?.Data;
        var deprecated = item.Deprecated == true || (item.Tags?.Contains(P.CompletionItemTag.Deprecated) ?? false);

        var result = new CompletionItem
        {
            Label = item.Label ?? string.Empty,
            LabelDetail = item.LabelDetails?.Detail,
            LabelDescription = item.LabelDetails?.Description,
            Kind = item.Kind is { } k and >= P.CompletionItemKind.Text and <= P.CompletionItemKind.TypeParameter ? (CompletionItemKind)(int)k : CompletionItemKind.Text,
            Detail = item.Detail,
            Documentation = ToCore(item.Documentation),
            SortText = item.SortText,
            FilterText = item.FilterText,
            InsertText = item.InsertText ?? item.TextEditText,
            InsertTextFormat = format == P.InsertTextFormat.Snippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
            TextEdit = edit,
            AdditionalTextEdits = item.AdditionalTextEdits is { Length: > 0 } ate ? ToCore(ate, docUri) : null,
            CommitCharacters = commit,
            Preselect = item.Preselect == true,
            Deprecated = deprecated,
            Command = ToCore(item.Command),
            Data = data,
        };
        _originals.AddOrUpdate(result, new CompletionOrigin(WithDefaults(item, defaults), docUri));
        return result;
    }

    // Echoing the item for resolve: the server must see the defaults it applied (data in particular).
    private static P.CompletionItem WithDefaults(P.CompletionItem item, P.CompletionListItemDefaults? d) =>
        d is null ? item : item with { Data = item.Data ?? d.Data, CommitCharacters = item.CommitCharacters ?? d.CommitCharacters, InsertTextFormat = item.InsertTextFormat ?? d.InsertTextFormat };

    public P.CompletionItem ToLsp(CompletionItem item, Uri uri)
    {
        if (_originals.TryGetValue(item, out var o) && o is CompletionOrigin origin) return origin.Item;
        return new P.CompletionItem
        {
            Label = item.Label,
            Kind = (P.CompletionItemKind)(int)item.Kind,
            Detail = item.Detail,
            SortText = item.SortText,
            FilterText = item.FilterText,
            InsertText = item.InsertText,
            InsertTextFormat = item.InsertTextFormat == InsertTextFormat.Snippet ? P.InsertTextFormat.Snippet : P.InsertTextFormat.PlainText,
            TextEdit = item.TextEdit is { } ie ? new P.OneOf<P.TextEdit, P.InsertReplaceEdit>?(new P.TextEdit { Range = ToLsp(ie.Range, uri), NewText = ie.NewText }) : null,
            Data = item.Data,
        };
    }

    /// <summary>Merges a resolved LSP item into the Core item (resolve only fills documentation/detail/edits lazily).</summary>
    public CompletionItem MergeResolved(CompletionItem original, P.CompletionItem resolved, Uri docUri)
    {
        var mapped = ToCore(resolved, null, docUri);
        var merged = original with
        {
            Detail = mapped.Detail ?? original.Detail,
            Documentation = mapped.Documentation ?? original.Documentation,
            AdditionalTextEdits = mapped.AdditionalTextEdits ?? original.AdditionalTextEdits,
            TextEdit = original.TextEdit ?? mapped.TextEdit,
            Command = mapped.Command ?? original.Command,
            Data = mapped.Data ?? original.Data,
        };
        _originals.AddOrUpdate(merged, new CompletionOrigin(resolved, docUri));
        return merged;
    }

    // ---- symbols ------------------------------------------------------------------------------------------------

    public IReadOnlyList<DocumentSymbol> ToCore(P.OneOf<P.SymbolInformation[], P.DocumentSymbol[]> result, Uri uri, int cap)
    {
        var budget = cap;
        if (result.IsT1)
        {
            var list = new List<DocumentSymbol>(result.AsT1.Length);
            foreach (var s in result.AsT1)
                if (budget > 0) list.Add(ToCore(s, uri, ref budget));
            return list;
        }
        var flat = new List<DocumentSymbol>(result.AsT0.Length);
        foreach (var s in result.AsT0)
        {
            if (flat.Count >= cap) break;
            var range = ToCore(s.Location.Range, ToUri(s.Location.Uri) ?? uri);
            flat.Add(new DocumentSymbol(s.Name ?? string.Empty, SymbolKindOf(s.Kind), range, range, s.ContainerName, null, s.Deprecated == true || (s.Tags?.Contains(P.SymbolTag.Deprecated) ?? false)));
        }
        return flat;
    }

    private DocumentSymbol ToCore(P.DocumentSymbol s, Uri uri, ref int budget)
    {
        budget--;
        List<DocumentSymbol>? children = null;
        if (s.Children is { Length: > 0 })
        {
            children = new List<DocumentSymbol>(s.Children.Length);
            foreach (var c in s.Children)
                if (budget > 0) children.Add(ToCore(c, uri, ref budget));
        }
        return new DocumentSymbol(s.Name ?? string.Empty, SymbolKindOf(s.Kind), ToCore(s.Range, uri), ToCore(s.SelectionRange, uri), s.Detail, children,
            s.Deprecated == true || (s.Tags?.Contains(P.SymbolTag.Deprecated) ?? false));
    }

    private static SymbolKind SymbolKindOf(P.SymbolKind k) =>
        k is >= P.SymbolKind.File and <= P.SymbolKind.TypeParameter ? (SymbolKind)(int)k : SymbolKind.Variable;

    public IReadOnlyList<WorkspaceSymbol> ToCore(P.OneOf<P.SymbolInformation[], P.WorkspaceSymbol[]> result, int cap)
    {
        var list = new List<WorkspaceSymbol>();
        if (result.IsT0)
        {
            foreach (var s in result.AsT0)
            {
                if (list.Count >= cap) break;
                if (ToCore(s.Location) is { } loc) list.Add(new WorkspaceSymbol(s.Name ?? string.Empty, SymbolKindOf(s.Kind), loc, s.ContainerName));
            }
        }
        else
        {
            foreach (var s in result.AsT1)
            {
                if (list.Count >= cap) break;
                Location? loc = s.Location.IsT0
                    ? ToCore(s.Location.AsT0)
                    : (ToUri(s.Location.AsT1.Uri) is { } u ? new Location(u, default) : null); // range-less: resolved lazily by the shell on open
                if (loc is not null) list.Add(new WorkspaceSymbol(s.Name ?? string.Empty, SymbolKindOf(s.Kind), loc, s.ContainerName));
            }
        }
        return list;
    }

    public IReadOnlyList<DocumentHighlight> ToCore(P.DocumentHighlight[] hs, Uri uri)
    {
        var list = new List<DocumentHighlight>(hs.Length);
        foreach (var h in hs)
            list.Add(new DocumentHighlight(ToCore(h.Range, uri), h.Kind is { } k and >= P.DocumentHighlightKind.Text and <= P.DocumentHighlightKind.Write ? (DocumentHighlightKind)(int)k : DocumentHighlightKind.Text));
        return list;
    }

    public static IReadOnlyList<FoldingRange> ToCore(P.FoldingRange[] rs, int cap)
    {
        var list = new List<FoldingRange>(Math.Min(rs.Length, cap));
        foreach (var r in rs)
        {
            if (list.Count >= cap) break;
            FoldingRangeKind? kind = r.Kind?.Value switch { "comment" => FoldingRangeKind.Comment, "imports" => FoldingRangeKind.Imports, "region" => FoldingRangeKind.Region, _ => null };
            list.Add(new FoldingRange(r.StartLine, r.EndLine, kind, r.CollapsedText));
        }
        return list;
    }

    public SelectionRange ToCore(P.SelectionRange r, Uri uri) =>
        new(ToCore(r.Range, uri), r.Parent is null ? null : ToCore(r.Parent, uri));

    // ---- code actions / lenses / hints / colours ----------------------------------------------------------------

    public CodeAction ToCore(P.CodeAction a, Uri uri)
    {
        var result = new CodeAction(
            a.Title ?? string.Empty,
            a.Kind?.Value,
            a.Diagnostics?.Select(d => ToCore(d, uri)).ToArray(),
            a.IsPreferred == true,
            a.Disabled?.Reason,
            a.Edit is null ? null : ToCore(a.Edit),
            ToCore(a.Command),
            a.Data);
        _originals.AddOrUpdate(result, a);
        return result;
    }

    public CodeAction CommandAsAction(P.Command c) => new(c.Title ?? string.Empty, null, null, false, null, null, ToCore(c), null);

    public P.CodeAction ToLsp(CodeAction a, Uri uri)
    {
        if (_originals.TryGetValue(a, out var o) && o is P.CodeAction original) return original;
        return new P.CodeAction
        {
            Title = a.Title,
            Kind = a.Kind is null ? null : new P.CodeActionKind(a.Kind),
            Diagnostics = a.Diagnostics?.Select(d => ToLsp(d, uri)).ToArray(),
            IsPreferred = a.IsPreferred ? true : null,
            Command = ToLsp(a.Command),
            Data = a.Data,
        };
    }

    public CodeLens ToCore(P.CodeLens l, Uri uri)
    {
        var result = new CodeLens(ToCore(l.Range, uri), ToCore(l.Command), l.Data);
        _originals.AddOrUpdate(result, l);
        return result;
    }

    public P.CodeLens ToLsp(CodeLens l, Uri uri)
    {
        if (_originals.TryGetValue(l, out var o) && o is P.CodeLens original) return original;
        return new P.CodeLens { Range = ToLsp(l.Range, uri), Command = ToLsp(l.Command), Data = l.Data };
    }

    public InlayHint ToCore(P.InlayHint h, Uri uri)
    {
        var label = h.Label.Match(s => s, parts => string.Concat(parts.Select(p => p.Value)));
        var tooltip = h.Tooltip is null ? null : ToCore(h.Tooltip.Value);
        return new InlayHint(ToCore(h.Position, uri), label, h.Kind is { } k and (P.InlayHintKind.Type or P.InlayHintKind.Parameter) ? (InlayHintKind)(int)k : null,
            h.PaddingLeft == true, h.PaddingRight == true, tooltip);
    }

    public static ColorInformation ToCore(P.ColorInformation c, LspMapper m, Uri uri) =>
        new(m.ToCore(c.Range, uri), c.Color.Red, c.Color.Green, c.Color.Blue, c.Color.Alpha);

    // ---- workspace edits ----------------------------------------------------------------------------------------

    public WorkspaceEdit ToCore(P.WorkspaceEdit we)
    {
        var changes = new Dictionary<Uri, IReadOnlyList<TextEdit>>();
        List<ResourceOperation>? ops = null;

        void AddEdits(Uri uri, IReadOnlyList<TextEdit> edits)
        {
            if (changes.TryGetValue(uri, out var existing)) changes[uri] = [.. existing, .. edits];
            else changes[uri] = edits;
        }

        if (we.DocumentChanges is { Length: > 0 } dc)
        {
            foreach (var change in dc)
            {
                switch (change.Index)
                {
                    case 0:
                        var tde = change.AsT0;
                        if (ToUri(tde.TextDocument.Uri) is { } u)
                            AddEdits(u, tde.Edits.Select(e => e.Match(t => ToCore(t, u), a => new TextEdit(ToCore(a.Range, u), a.NewText ?? string.Empty))).ToArray());
                        break;
                    case 1:
                        if (ToUri(change.AsT1.Uri) is { } cu)
                            (ops ??= []).Add(new CreateFileOperation(cu, change.AsT1.Options?.Overwrite == true, change.AsT1.Options?.IgnoreIfExists == true));
                        break;
                    case 2:
                        if (ToUri(change.AsT2.OldUri) is { } ou && ToUri(change.AsT2.NewUri) is { } nu)
                            (ops ??= []).Add(new RenameFileOperation(ou, nu, change.AsT2.Options?.Overwrite == true, change.AsT2.Options?.IgnoreIfExists == true));
                        break;
                    case 3:
                        if (ToUri(change.AsT3.Uri) is { } du)
                            (ops ??= []).Add(new DeleteFileOperation(du, change.AsT3.Options?.Recursive == true, change.AsT3.Options?.IgnoreIfNotExists == true));
                        break;
                }
            }
        }
        else if (we.Changes is { Count: > 0 } map)
        {
            foreach (var (uriString, edits) in map)
                if (ToUri(uriString) is { } u) AddEdits(u, ToCore(edits, u));
        }
        return new WorkspaceEdit(changes, ops);
    }

    // ---- misc ---------------------------------------------------------------------------------------------------

    public static JsonElement? ToJson<T>(T value) => P.LspJson.ToElement(value);
}
