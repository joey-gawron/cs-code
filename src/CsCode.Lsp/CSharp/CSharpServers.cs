using System.Text.Json;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;

namespace CsCode.Lsp.CSharp;

/// <summary>The C# servers' library and generated documents (docs/CSHARP.md, "Go to library code").</summary>
internal static class CSharpServers
{
    public const string CSharpLsId = "csharp";
    public const string RoslynId = "roslyn";

    /// <summary>The Roslyn behaviour for a client of the <c>roslyn</c> definition, else null.</summary>
    public static RoslynSession? CreateSession(LanguageClient client) => client.Definition.Id == RoslynId ? new RoslynSession(client) : null;

    public static void AddExternalSources(ExternalDocuments external)
    {
        external.AddSource(CSharpLsId, new CSharpLsMetadataSource());
        external.AddSource(RoslynId, new RoslynDocumentSource());
    }

    /// <summary>
    /// <c>System.String.cs</c> → <c>String.cs</c>, <c>System.Collections.Generic.List`1.cs</c> → <c>List.cs</c>: a metadata file is named after the
    /// type's full metadata name, a tab after the type.
    /// </summary>
    internal static string TypeFileName(string fileName)
    {
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return fileName;
        var stem = System.Text.RegularExpressions.Regex.Replace(fileName[..^3], @"`\d+", "");
        var generic = stem.IndexOf('<');
        var dot = (generic >= 0 ? stem[..generic] : stem).LastIndexOf('.');
        return (dot >= 0 && dot < stem.Length - 1 ? stem[(dot + 1)..] : stem) + ".cs";
    }

    /// <summary>
    /// A request about a document no editor has open yet (the server's own name for it), sent from the language lane (the caller only enqueues)
    /// and reduced there to the text it carries; null when the server is not running, failed, timed out or answered without text.
    /// </summary>
    internal static ValueTask<string?> RequestTextAsync(LanguageClient client, string method, object @params, Func<JsonElement, string?> text, CancellationToken ct) =>
        client.PlainRequestAsync<string>(LanePriority.Interactive, default, async wc =>
        {
            try
            {
                var raw = await client.SendAsync(method, @params, LanePriority.Interactive, wc.Token, timeout: client.Settings.BackgroundTimeout).ConfigureAwait(false);
                return raw is { ValueKind: JsonValueKind.Object } r ? text(r) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Log.Add($"[client] {method} failed: {ex.Message}");
                return null;
            }
        }, ct);

    internal static string? StringMember(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// csharp-ls started with <c>--features metadata-uris</c> (0.28.0; without it go to definition into a referenced assembly answers nothing)
/// answers with a <c>csharp:/&lt;project file&gt;/decompiled/&lt;full type name&gt;.cs</c> location (observed:
/// <c>csharp:/…/Sample.csproj/decompiled/System.String.cs</c>), whose text, decompiled by ICSharpCode.Decompiler, comes from its
/// <c>csharp/metadata</c> request: <c>{textDocument: {uri}}</c> → <c>{projectName, assemblyName, symbolName, source}</c>.
/// </summary>
internal sealed class CSharpLsMetadataSource : IExternalDocumentSource
{
    public const string Scheme = "csharp";
    public const string MetadataMethod = "csharp/metadata";

    public (string Kind, string FileName)? Classify(string serverUri) =>
        serverUri.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)
            ? (VirtualDocumentKinds.Decompiled, CSharpServers.TypeFileName(ExternalDocuments.LastSegment(serverUri)))
            : null;

    public async ValueTask<string?> FetchAsync(LanguageClient client, string serverUri, CancellationToken ct) =>
        await CSharpServers.RequestTextAsync(client, MetadataMethod, new { textDocument = new { uri = serverUri } },
            r => CSharpServers.StringMember(r, "source"), ct).ConfigureAwait(false);
}

/// <summary>
/// Roslyn's library and generated documents (5.13.0-1.26514.10, observed). Source-generated files have
/// <c>roslyn-source-generated://&lt;project id&gt;/&lt;hint name&gt;?documentId=…&amp;hintName=…</c> URIs whose text comes from LSP 3.18's
/// <c>workspace/textDocumentContent</c> (<c>{uri}</c> → <c>{text}</c>; the older <c>sourceGeneratedDocument/_roslyn_getText</c> is gone from this
/// build). Library code ("metadata as source") is written by the server under <c>$TMPDIR/MetadataAsSource/&lt;session&gt;/&lt;provider&gt;/&lt;id&gt;/</c>
/// and answered as <c>file:</c> locations there: the provider directory says what it is (<c>DecompilationMetadataAsSourceFileProvider</c>:
/// decompiled; <c>PdbSourceDocument…</c>: the original source through SourceLink or an embedded PDB source; otherwise declarations from
/// metadata). The file is read as it is; opened under its own <c>file:</c> URI, hover and go to definition work inside it.
/// </summary>
internal sealed class RoslynDocumentSource : IExternalDocumentSource
{
    public const string SourceGeneratedScheme = "roslyn-source-generated";
    public const string GetTextMethod = "workspace/textDocumentContent";
    /// <summary>The directory (under the temporary directory) Roslyn writes metadata-as-source files into.</summary>
    public const string MetadataDirectory = "MetadataAsSource";

    public (string Kind, string FileName)? Classify(string serverUri)
    {
        if (serverUri.StartsWith(SourceGeneratedScheme + ":", StringComparison.OrdinalIgnoreCase))
            return (VirtualDocumentKinds.Generated, ExternalDocuments.LastSegment(serverUri));
        if (!Uri.TryCreate(serverUri, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        var path = uri.LocalPath;
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, p => string.Equals(p, MetadataDirectory, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return null;
        var provider = parts.Length > at + 2 ? parts[at + 2] : "";
        var kind = provider.StartsWith("Decompilation", StringComparison.OrdinalIgnoreCase) ? VirtualDocumentKinds.Decompiled
            : provider.StartsWith("PdbSource", StringComparison.OrdinalIgnoreCase) ? VirtualDocumentKinds.Source
            : VirtualDocumentKinds.Metadata;
        return (kind, parts[^1]);
    }

    public async ValueTask<string?> FetchAsync(LanguageClient client, string serverUri, CancellationToken ct)
    {
        if (serverUri.StartsWith(SourceGeneratedScheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return await CSharpServers.RequestTextAsync(client, GetTextMethod, new { uri = serverUri },
                r => CSharpServers.StringMember(r, "text"), ct).ConfigureAwait(false);
        }
        try { return await File.ReadAllTextAsync(new Uri(serverUri).LocalPath, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
