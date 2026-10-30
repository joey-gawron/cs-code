using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Terminal;
using CsCode.Dap.Client;
using CsCode.Dap.Transport;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Session;

public sealed record DebugSessionSettings
{
    /// <summary>Advertised to the adapter in <c>initialize</c>; every position crossing the boundary is converted accordingly.</summary>
    public bool LinesStartAt1 { get; init; } = true;
    public bool ColumnsStartAt1 { get; init; } = true;
    /// <summary>Default per-request timeout; a hung adapter makes requests fail (and the session report Unresponsive), never block.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>launch / attach / configurationDone may legitimately take long (build output, process start).</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long to wait for the adapter's <c>initialized</c> event after sending launch/attach.</summary>
    public TimeSpan InitializedTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Graceful stop: how long to wait for <c>terminated</c> after <c>terminate</c>/<c>disconnect</c> before killing.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Output batching window (≥ 16 ms: one UI update per frame at most).</summary>
    public TimeSpan OutputWindow { get; init; } = TimeSpan.FromMilliseconds(16);
    public TimeSpan BreakpointDebounce { get; init; } = TimeSpan.FromMilliseconds(50);
    public int MaxVariablesPerRequest { get; init; } = 10_000;
    public int MaxStackFrames { get; init; } = 1_000;
    /// <summary>Frames above this are skipped by the transport (memory bound against a hostile/buggy adapter).</summary>
    public int MaxFrameBytes { get; init; } = 16 * 1024 * 1024;
    public string ClientId { get; init; } = "cscode";
    public string ClientName { get; init; } = "CsCode";
}

/// <summary>Asks the host (DebugService) to start a child session for the adapter's <c>startDebugging</c> reverse request.</summary>
public delegate Task<IDebugSession?> StartChildSession(DebugSession parent, string request, JsonObject configuration, CancellationToken ct);

public sealed record DebugSessionOptions(
    string Id,
    DebugConfiguration Configuration,
    Func<CancellationToken, ValueTask<IDapTransport>> TransportFactory,
    IBreakpointManager Breakpoints,
    DebugLane Lane)
{
    public IDebugSession? Parent { get; init; }
    public DebugSessionSettings Settings { get; init; } = new();
    public StartChildSession? StartChild { get; init; }
    public ILogger? Logger { get; init; }
    public IMemoryMonitor? Memory { get; init; }
    /// <summary>What the adapter of this session's debug type does differently (GDB's native DAP); <see cref="DebugAdapterQuirks.None"/> by default.</summary>
    public DebugAdapterQuirks Quirks { get; init; } = DebugAdapterQuirks.None;
    /// <summary>The integrated terminal for <c>runInTerminal</c> (null, or null returned: the debuggee runs piped into the Debug Console).</summary>
    public Func<ITerminalService?>? Terminals { get; init; }
    /// <summary>
    /// URIs ⇄ adapter paths for a session of a workspace that is not on this machine (a remote workspace: the adapter runs there and speaks its
    /// paths); null or unhandled URIs keep the <c>file:</c> rules.
    /// </summary>
    public IDebugPathMapper? PathMapper { get; init; }
}

/// <summary>A <c>variablesReference</c>/frame id was used after the debuggee resumed (handles only live while stopped).</summary>
public sealed class DebugHandleInvalidatedException(string message) : InvalidOperationException(message);

/// <summary>The session could not be started (adapter missing, handshake failed, launch rejected).</summary>
public sealed class DebugSessionStartException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record DebugExceptionInfo(string ExceptionId, string? Description, string BreakMode, string? Message, string? TypeName, string? StackTrace);

public sealed record DebugProgressEventArgs(string Kind, string ProgressId, string? Title, string? Message, double? Percentage);

public sealed record DebugLoadedSourceEventArgs(string Reason, DebugSource Source);

public sealed record DebugModuleInfo(string Id, string Name, string? Path, string? Version, bool? IsUserCode, string? SymbolStatus);
