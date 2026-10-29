using System.Text.Json;
using StreamJsonRpc;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

/// <summary>
/// The JSON-RPC target for everything a server sends to the client. Runs on StreamJsonRpc's reader loop, so every handler is
/// "enqueue and return": notifications go into bounded latest-wins mailboxes drained on the Language lane; requests are answered
/// asynchronously (the reader never waits for UI interaction or for a slow consumer).
/// </summary>
internal sealed class ServerRequestTarget(LanguageClient client)
{
    // ---- notifications ----

    [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
    public void PublishDiagnostics(JsonElement p) => client.OnPublishDiagnostics(p);

    [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
    public void LogMessage(JsonElement p) => client.OnLogMessage(p);

    [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
    public void ShowMessage(JsonElement p) => client.OnShowMessage(p);

    [JsonRpcMethod("$/logTrace", UseSingleObjectParameterDeserialization = true)]
    public void LogTrace(JsonElement p) => client.OnLogTrace(p);

    [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
    public void Progress(JsonElement p) => client.OnProgress(p);

    [JsonRpcMethod("telemetry/event", UseSingleObjectParameterDeserialization = true)]
    public void Telemetry(JsonElement p) { /* deliberately ignored: CsCode never forwards server telemetry */ }

    // ---- requests ----

    [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> RegisterCapability(JsonElement p) => client.HandleRegisterCapabilityAsync(p);

    [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> UnregisterCapability(JsonElement p) => client.HandleUnregisterCapabilityAsync(p);

    [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> Configuration(JsonElement p) => client.HandleConfigurationAsync(p);

    [JsonRpcMethod("workspace/workspaceFolders")]
    public Task<object?> WorkspaceFolders() => client.HandleWorkspaceFoldersAsync();

    [JsonRpcMethod("window/showMessageRequest", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> ShowMessageRequest(JsonElement p) => client.HandleShowMessageRequestAsync(p);

    [JsonRpcMethod("window/showDocument", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> ShowDocument(JsonElement p) => client.HandleShowDocumentAsync(p);

    [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> CreateProgress(JsonElement p) => Task.FromResult<object?>(null);

    [JsonRpcMethod("workspace/applyEdit", UseSingleObjectParameterDeserialization = true)]
    public Task<object?> ApplyEdit(JsonElement p) => client.HandleApplyEditAsync(p);

    [JsonRpcMethod("workspace/semanticTokens/refresh")]
    public Task<object?> SemanticTokensRefresh() => client.HandleRefreshAsync(RefreshKind.SemanticTokens);

    [JsonRpcMethod("workspace/inlayHint/refresh")]
    public Task<object?> InlayHintRefresh() => client.HandleRefreshAsync(RefreshKind.InlayHints);

    [JsonRpcMethod("workspace/codeLens/refresh")]
    public Task<object?> CodeLensRefresh() => client.HandleRefreshAsync(RefreshKind.CodeLenses);

    [JsonRpcMethod("workspace/diagnostic/refresh")]
    public Task<object?> DiagnosticRefresh() => client.HandleRefreshAsync(RefreshKind.Diagnostics);

    [JsonRpcMethod("workspace/foldingRange/refresh")]
    public Task<object?> FoldingRefresh() => client.HandleRefreshAsync(RefreshKind.Folding);

    [JsonRpcMethod("workspace/inlineValue/refresh")]
    public Task<object?> InlineValueRefresh() => Task.FromResult<object?>(null);
}

internal enum RefreshKind { SemanticTokens, InlayHints, CodeLenses, Diagnostics, Folding }

/// <summary>JSON shape helpers for the cheap, allocation-light parsing done on the reader loop.</summary>
internal static class JsonProps
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int? Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public static string? TokenKey(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("token", out var t)
            ? t.ValueKind switch { JsonValueKind.String => t.GetString(), JsonValueKind.Number => t.GetRawText(), _ => null }
            : null;
}
