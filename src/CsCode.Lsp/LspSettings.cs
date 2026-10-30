using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Lsp.Runtime;

namespace CsCode.Lsp;

public enum LspTraceMode { Off, Messages, Verbose }

/// <summary>Setting keys owned by CsCode.Lsp (all under <c>cscode.lsp.*</c>, plus the user-overridable <c>cscode.languageServers</c>).</summary>
public static class LspSettingKeys
{
    public const string LanguageServers = "cscode.languageServers";
    public const string MaxDocumentBytes = "cscode.lsp.maxDocumentBytes";
    public const string MaxMessageBytes = "cscode.lsp.maxMessageBytes";
    public const string Trace = "cscode.lsp.trace";
    public const string WorkerThreads = "cscode.lsp.workerThreads";
    public const string ProcessPriority = "cscode.lsp.processPriority";
    public const string CpuQuotaPercent = "cscode.lsp.cpuQuotaPercent";
    public const string MaxCompletionItems = "cscode.lsp.maxCompletionItems";
    public const string MaxReferences = "cscode.lsp.maxReferences";
    public const string MaxWorkspaceSymbols = "cscode.lsp.maxWorkspaceSymbols";
    public const string MaxSemanticTokens = "cscode.lsp.maxSemanticTokens";
    public const string InteractiveTimeoutMs = "cscode.lsp.interactiveTimeoutMs";
    public const string BackgroundTimeoutMs = "cscode.lsp.backgroundTimeoutMs";
    public const string InitializeTimeoutMs = "cscode.lsp.initializeTimeoutMs";
    public const string PositionEncoding = "cscode.lsp.positionEncoding";
    public const string QueueCapacity = "cscode.lsp.queueCapacity";
    public const string BreakerThreshold = "cscode.lsp.breakerThreshold";
    public const string BreakerProbeMs = "cscode.lsp.breakerProbeMs";
    public const string MaxRestarts = "cscode.lsp.maxRestarts";
    public const string RestartWindowSeconds = "cscode.lsp.restartWindowSeconds";
    public const string RestartBackoffMs = "cscode.lsp.restartBackoffMs";
    public const string ShutdownTimeoutMs = "cscode.lsp.shutdownTimeoutMs";
    public const string SearchDirs = "cscode.lsp.searchDirs";
}

/// <summary>Immutable snapshot of every tunable; re-read when configuration changes. Defaults are the documented ones.</summary>
internal sealed record LspSettings
{
    public long MaxDocumentBytes { get; init; } = 10 * 1024 * 1024;
    public long MaxMessageBytes { get; init; } = 64L * 1024 * 1024;
    public LspTraceMode Trace { get; init; } = LspTraceMode.Off;
    public int WorkerThreads { get; init; } = 2;
    public ProcessPriority Priority { get; init; } = ProcessPriority.BelowNormal;
    public int CpuQuotaPercent { get; init; }
    public int MaxCompletionItems { get; init; } = 5_000;
    public int MaxReferences { get; init; } = 10_000;
    public int MaxWorkspaceSymbols { get; init; } = 5_000;
    public int MaxSemanticTokens { get; init; } = 250_000;
    public TimeSpan InteractiveTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan BackgroundTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public bool AutoPositionEncoding { get; init; }
    public int QueueCapacity { get; init; } = 4096;
    public int BreakerThreshold { get; init; } = 5;
    public TimeSpan BreakerProbeInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxRestarts { get; init; } = 5;
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan[] RestartBackoff { get; init; } = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    /// <summary>Expanded install directories searched for server executables after PATH (<see cref="LspSettingKeys.SearchDirs"/>).</summary>
    public IReadOnlyList<string> SearchDirs { get; init; } = ExpandDirs(ToolResolver.DefaultSearchDirs);

    public static LspSettings Read(IConfigurationService? cfg)
    {
        if (cfg is null) return new LspSettings();
        var d = new LspSettings();
        return d with
        {
            MaxDocumentBytes = Math.Max(1024, Raw(cfg, LspSettingKeys.MaxDocumentBytes, d.MaxDocumentBytes)),
            MaxMessageBytes = Math.Max(64 * 1024, Raw(cfg, LspSettingKeys.MaxMessageBytes, d.MaxMessageBytes)),
            Trace = ParseTrace(cfg.Get<string>(LspSettingKeys.Trace)),
            WorkerThreads = Math.Clamp(Raw(cfg, LspSettingKeys.WorkerThreads, d.WorkerThreads), 1, 8),
            Priority = ParsePriority(cfg.Get<string>(LspSettingKeys.ProcessPriority)),
            CpuQuotaPercent = Math.Clamp(Raw(cfg, LspSettingKeys.CpuQuotaPercent, 0), 0, 10_000),
            MaxCompletionItems = Math.Max(10, Raw(cfg, LspSettingKeys.MaxCompletionItems, d.MaxCompletionItems)),
            MaxReferences = Math.Max(10, Raw(cfg, LspSettingKeys.MaxReferences, d.MaxReferences)),
            MaxWorkspaceSymbols = Math.Max(10, Raw(cfg, LspSettingKeys.MaxWorkspaceSymbols, d.MaxWorkspaceSymbols)),
            MaxSemanticTokens = Math.Max(100, Raw(cfg, LspSettingKeys.MaxSemanticTokens, d.MaxSemanticTokens)),
            InteractiveTimeout = TimeSpan.FromMilliseconds(Math.Max(10, Raw(cfg, LspSettingKeys.InteractiveTimeoutMs, (int)d.InteractiveTimeout.TotalMilliseconds))),
            BackgroundTimeout = TimeSpan.FromMilliseconds(Math.Max(10, Raw(cfg, LspSettingKeys.BackgroundTimeoutMs, (int)d.BackgroundTimeout.TotalMilliseconds))),
            InitializeTimeout = TimeSpan.FromMilliseconds(Math.Max(100, Raw(cfg, LspSettingKeys.InitializeTimeoutMs, (int)d.InitializeTimeout.TotalMilliseconds))),
            ShutdownTimeout = TimeSpan.FromMilliseconds(Math.Max(10, Raw(cfg, LspSettingKeys.ShutdownTimeoutMs, (int)d.ShutdownTimeout.TotalMilliseconds))),
            AutoPositionEncoding = string.Equals(cfg.Get<string>(LspSettingKeys.PositionEncoding), "auto", StringComparison.OrdinalIgnoreCase),
            QueueCapacity = Math.Clamp(Raw(cfg, LspSettingKeys.QueueCapacity, d.QueueCapacity), 16, 100_000),
            BreakerThreshold = Math.Max(1, Raw(cfg, LspSettingKeys.BreakerThreshold, d.BreakerThreshold)),
            BreakerProbeInterval = TimeSpan.FromMilliseconds(Math.Max(10, Raw(cfg, LspSettingKeys.BreakerProbeMs, (int)d.BreakerProbeInterval.TotalMilliseconds))),
            MaxRestarts = Math.Max(0, Raw(cfg, LspSettingKeys.MaxRestarts, d.MaxRestarts)),
            RestartWindow = TimeSpan.FromSeconds(Math.Max(1, Raw(cfg, LspSettingKeys.RestartWindowSeconds, (int)d.RestartWindow.TotalSeconds))),
            RestartBackoff = Raw<int[]?>(cfg, LspSettingKeys.RestartBackoffMs, null) is { Length: > 0 } ms
                ? ms.Select(m => TimeSpan.FromMilliseconds(Math.Max(0, m))).ToArray()
                : d.RestartBackoff,
            SearchDirs = Raw<string[]?>(cfg, LspSettingKeys.SearchDirs, null) is { } dirs ? ExpandDirs(dirs) : d.SearchDirs,
        };
    }

    private static string[] ExpandDirs(IEnumerable<string> dirs) =>
        dirs.Select(ToolResolver.ExpandDirectory).Where(d => d is not null).Select(d => d!).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Reads one setting through GetRaw so an UNSET value-typed key yields the documented default (not default(T)) whatever the
    /// configuration service does for Get&lt;T&gt;(key, fallback).
    /// </summary>
    private static T Raw<T>(IConfigurationService cfg, string key, T fallback)
    {
        if (cfg.GetRaw(key) is not { } raw || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        try { return raw.Deserialize<T>(CoreJson.Options) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    private static LspTraceMode ParseTrace(string? s) => s?.ToLowerInvariant() switch
    {
        "messages" => LspTraceMode.Messages,
        "verbose" => LspTraceMode.Verbose,
        _ => LspTraceMode.Off,
    };

    private static ProcessPriority ParsePriority(string? s) => s?.ToLowerInvariant() switch
    {
        "normal" => ProcessPriority.Normal,
        "idle" => ProcessPriority.Idle,
        _ => ProcessPriority.BelowNormal,
    };

    /// <summary>Configuration properties (defaults + descriptions) registered so the settings UI can show them.</summary>
    public static IEnumerable<ConfigurationProperty> Properties()
    {
        static JsonElement J<T>(T v) => JsonSerializer.SerializeToElement(v);
        var d = new LspSettings();
        yield return new(LspSettingKeys.MaxDocumentBytes, J(d.MaxDocumentBytes), "Documents larger than this many bytes are not sent to language servers.");
        yield return new(LspSettingKeys.MaxMessageBytes, J(d.MaxMessageBytes), "A language server that announces a single message larger than this many bytes has its connection dropped (the server is then restarted): parsing needs ~9x the message size in memory.");
        yield return new(LspSettingKeys.Trace, J("off"), "Language-server protocol tracing into the server's Output channel: off | messages | verbose.");
        yield return new(LspSettingKeys.WorkerThreads, J(d.WorkerThreads), "Dedicated (below-normal priority) threads of the language lane. Applies to servers started after the change.");
        yield return new(LspSettingKeys.ProcessPriority, J("belowNormal"), "OS priority of language-server child processes: belowNormal | normal | idle.");
        yield return new(LspSettingKeys.CpuQuotaPercent, J(0), "Linux: cap each language server (and its children) to this percentage of one CPU using a transient systemd user scope (100 = one core, 0 = no cap).");
        yield return new(LspSettingKeys.MaxCompletionItems, J(d.MaxCompletionItems), "Completion lists are capped to this many items (the list is then marked incomplete).");
        yield return new(LspSettingKeys.MaxReferences, J(d.MaxReferences), "Maximum references returned by Find References.");
        yield return new(LspSettingKeys.MaxWorkspaceSymbols, J(d.MaxWorkspaceSymbols), "Maximum workspace symbols returned per query.");
        yield return new(LspSettingKeys.MaxSemanticTokens, J(d.MaxSemanticTokens), "Maximum semantic tokens delivered per document (the rest is dropped).");
        yield return new(LspSettingKeys.InteractiveTimeoutMs, J(3000), "Timeout for interactive requests (completion, hover, definition...).");
        yield return new(LspSettingKeys.BackgroundTimeoutMs, J(30000), "Timeout for background requests (semantic tokens, inlay hints, pull diagnostics...).");
        yield return new(LspSettingKeys.InitializeTimeoutMs, J(60000), "Timeout for the initialize handshake.");
        yield return new(LspSettingKeys.PositionEncoding, J("utf-16"), "utf-16 (default, zero-cost) or auto (negotiate utf-8/utf-32 when the server supports it).");
        // Machine scope (lsp-core-1): where executables are searched for, and what a server runs, come from user settings only; a workspace's
        // .vscode/.cscode settings.json may still tune a server (selector, settings, initializationOptions, rootMarkers, enabled, autoStart).
        yield return new(LspSettingKeys.SearchDirs, J(ToolResolver.DefaultSearchDirs),
            "Directories searched for language-server executables after PATH and before the managed tools directory (~ = home directory). An editor started from a desktop launcher often lacks these in its PATH. User settings only.")
        { Scope = ConfigurationScope.Machine };
        yield return new(LspSettingKeys.LanguageServers, null,
            "Language server definitions keyed by id: { command, args, env, workingDirectory, selector, initializationOptions, settings, enabled, autoStart, rootMarkers, restartAboveBytes }. "
            + "command, args, env and workingDirectory are taken from user settings only.")
        { MachineScopedMembers = MachineScopedServerMembers };
    }

    /// <summary>The members of a <c>cscode.languageServers</c> entry that decide what is started: user settings only.</summary>
    public static IReadOnlyList<string> MachineScopedServerMembers { get; } = ["*.command", "*.args", "*.env", "*.workingDirectory"];
}
