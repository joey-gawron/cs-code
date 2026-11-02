using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Tests.Harness;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary><c>inlayHint/resolve</c> and label parts (lang-2) against the fake server.</summary>
public class InlayHintResolveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Hints = """
        [{"position":{"line":0,"character":5},"label":[{"value":": "},{"value":"Vec"},{"value":"<i32>"}],"kind":1,"data":{"id":7}}]
        """;

    [Fact]
    public async Task The_client_offers_to_resolve_tooltips_and_label_part_locations_and_commands()
    {
        await using var env = new LspTestEnv();
        var (server, _) = await env.StartAsync();
        var props = server.Of(P.LspMethods.Initialize).Single().Params.GetProperty("capabilities").GetProperty("textDocument").GetProperty("inlayHint")
            .GetProperty("resolveSupport").GetProperty("properties").EnumerateArray().Select(p => p.GetString());
        Assert.Equal(["tooltip", "label.tooltip", "label.location", "label.command"], props);
    }

    [Fact]
    public async Task Label_parts_are_mapped_and_resolve_echoes_the_hint_and_maps_the_answer()
    {
        await using var env = new LspTestEnv();
        JsonElement? echoed = null;
        var (_, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { InlayHintProvider = new P.InlayHintOptions { ResolveProvider = true } };
            s.Requests[P.LspMethods.TextDocumentInlayHint] = _ => Task.FromResult<object?>(Json(Hints));
            s.Requests[P.LspMethods.InlayHintResolve] = p =>
            {
                echoed = p.Clone();
                return Task.FromResult<object?>(Json("""
                    {"position":{"line":0,"character":5},"label":[{"value":": "},
                      {"value":"Vec","tooltip":{"kind":"markdown","value":"A growable array"},"location":{"uri":"file:///lib/vec.rs","range":{"start":{"line":437,"character":11},"end":{"line":437,"character":14}}}},
                      {"value":"<i32>"}],"kind":1}
                    """));
            };
        }, text: "let v = vec![1];\n");
        var provider = env.Provider<IInlayHintsProvider>(doc);
        var hint = Assert.Single((await provider.ProvideInlayHintsAsync(doc, TextRange.FromLines(0, 0, 1, 0), Ct).Within())!);
        Assert.Equal(": Vec<i32>", hint.Label);
        Assert.Equal([": ", "Vec", "<i32>"], hint.LabelParts!.Select(p => p.Value));
        Assert.True(hint.CanResolve);
        Assert.Null(hint.LabelParts![1].Location);

        var resolved = await provider.ResolveInlayHintAsync(hint, Ct).AsTask().Within();
        Assert.Equal(7, echoed!.Value.GetProperty("data").GetProperty("id").GetInt32()); // the server's own hint, data included
        var vec = resolved.LabelParts![1];
        Assert.Equal(("file:///lib/vec.rs", 437, 11), (vec.Location!.Uri.ToString(), vec.Location.Range.Start.Line, vec.Location.Range.Start.Character));
        Assert.Equal("A growable array", vec.Tooltip!.Value);
        Assert.Equal(hint.Position, resolved.Position);
    }

    [Fact]
    public async Task Without_resolveProvider_hints_are_not_resolvable_and_resolve_sends_nothing()
    {
        await using var env = new LspTestEnv();
        var (server, doc) = await env.StartAsync(s =>
        {
            s.Capabilities = new P.ServerCapabilities { InlayHintProvider = true };
            s.Requests[P.LspMethods.TextDocumentInlayHint] = _ => Task.FromResult<object?>(Json(Hints));
        }, text: "let v = vec![1];\n");
        var provider = env.Provider<IInlayHintsProvider>(doc);
        var hint = Assert.Single((await provider.ProvideInlayHintsAsync(doc, TextRange.FromLines(0, 0, 1, 0), Ct).Within())!);
        Assert.False(hint.CanResolve);
        Assert.Same(hint, await provider.ResolveInlayHintAsync(hint, Ct).AsTask().Within());
        Assert.Equal(0, server.CountOf(P.LspMethods.InlayHintResolve));
    }
}
