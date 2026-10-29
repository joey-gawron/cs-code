using CsCode.Core.Lifecycle;

namespace CsCode.Core.Debugging;

// Additive companions to IDebugService (new interfaces rather than new members, so existing implementers/fakes are not broken).
// CsCode.Dap's DebugService implements all of them; the shell/editor resolve them from DI when they need them.

/// <summary>The call stack frame the UI selected (Call Stack panel / hover), which scopes evaluate/locals and moves the stop marker.</summary>
public interface IDebugFrameFocus
{
    /// <summary>Selects a frame of a stopped session; updates <see cref="IDebugService.StopLocation"/> (and fires its event).</summary>
    ValueTask SetFocusedFrameAsync(string sessionId, int threadId, int frameId, CancellationToken ct = default);
    /// <summary>Frame currently selected for the active session (null when running/none).</summary>
    int? FocusedFrameId { get; }
}

/// <summary>Bounded Debug Console history for late subscribers (the Debug Console panel opened after output started).</summary>
public interface IDebugOutputHistory
{
    /// <summary>Most recent output batches; total retained text is bounded (≤ 5,000 lines).</summary>
    IReadOnlyList<DebugOutputEventArgs> GetOutputHistory();
    void ClearOutputHistory();
    IEventSource<object?> OutputCleared { get; }
}

/// <summary>Content of sources that only exist inside the adapter (DAP <c>sourceReference</c>), exposed as read-only <c>debug:</c> URIs.</summary>
public interface IDebugSourceContentProvider
{
    ValueTask<string?> GetSourceContentAsync(Uri debugUri, CancellationToken ct = default);
}

/// <summary>Optional health information of a session: set when requests time out (hung adapter).</summary>
public interface IDebugSessionHealth
{
    bool IsUnresponsive { get; }
    IEventSource<bool> UnresponsiveChanged { get; }
}

/// <summary>The shell implements this (optional) so <c>${selectedText}</c> in launch.json can be resolved.</summary>
public interface IActiveSelectionProvider
{
    string? SelectedText { get; }
}

/// <summary><c>debug:/&lt;sessionId&gt;/&lt;sourceReference&gt;/&lt;name&gt;</c> — read-only virtual documents for adapter-side sources.</summary>
public static class DebugSourceUri
{
    public const string Scheme = "debug";

    public static Uri Create(string sessionId, int sourceReference, string? name) =>
        new($"{Scheme}:/{Uri.EscapeDataString(sessionId)}/{sourceReference}/{Uri.EscapeDataString(string.IsNullOrEmpty(name) ? "source" : name)}");

    public static bool TryParse(Uri uri, out string sessionId, out int sourceReference, out string name)
    {
        sessionId = name = string.Empty;
        sourceReference = 0;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[1], out sourceReference)) return false;
        sessionId = Uri.UnescapeDataString(parts[0]);
        name = Uri.UnescapeDataString(parts[2]);
        return true;
    }
}
