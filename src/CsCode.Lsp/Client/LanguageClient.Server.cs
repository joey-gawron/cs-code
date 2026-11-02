using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Lsp.Client;

// Server -> client: diagnostics (push + pull), progress, messages, configuration, workspace edits, dynamic registration.
internal sealed partial class LanguageClient
{
    private const int DiagnosticsPullFeature = 1000;
    private const int MaxDiagnosticsPerFile = 2000;

    // (document, diagnostics owner) pairs this session published: the session's owner, and one per pull identifier
    private readonly ConcurrentDictionary<(Uri Uri, string Owner), byte> _publishedUris = new();
    private readonly LatestPerKeyMailbox<string, JsonElement> _diagMailbox = new(512);
    private int _diagScheduled;
    private bool _pullDiagnostics;
    /// <summary>
    /// The pull diagnostic providers the server registered. Roslyn registers several (syntax, compiler semantics, analyzers, …), each answering
    /// for its own identifier only: every one is pulled, and each publishes under an owner of its own so one's report never replaces another's.
    /// </summary>
    private volatile PullProvider[] _pullProviders = [];

    private sealed record PullProvider(string? Identifier, DocumentSelector? Selector, string Owner)
    {
        public string Key => Identifier ?? "";
    }
    private readonly HashSet<string> _requestedSections = [];
    private int _messageBudget = 5;
    private long _messageBudgetResetTicks;

    // =================================================================================================================
    // Messages / logs (reader-thread entry points: parse a couple of fields, enqueue, return)
    // =================================================================================================================

    internal void OnLogMessage(JsonElement p)
    {
        var type = p.Int("type") ?? 4;
        _log.Add($"[{type switch { 1 => "error", 2 => "warn", 3 => "info", _ => "log" }}] {p.Str("message")}");
    }

    internal void OnLogTrace(JsonElement p)
    {
        if (_ctx.Settings().Trace == LspTraceMode.Off) return;
        _log.Add("[trace] " + p.Str("message"));
    }

    internal void OnShowMessage(JsonElement p)
    {
        var type = p.Int("type") ?? 3;
        var text = p.Str("message");
        if (string.IsNullOrEmpty(text)) return;
        _log.Add($"[{type switch { 1 => "error", 2 => "warn", _ => "info" }}] {text}");
        var severity = type switch { 1 => MessageSeverity.Error, 2 => MessageSeverity.Warning, _ => MessageSeverity.Info };
        if (Extension is { } extension)
        {
            var handled = false;
            RunExtension("HandleShowMessage", () => handled = extension.Extension.HandleShowMessage(extension, severity, text), extension.Extension);
            if (handled) return;
        }
        var window = _ctx.Services.Window;
        if (window is null || !TakeMessageBudget()) return;
        _ = window.ShowMessageAsync(severity, $"{Definition.DisplayName}: {text}").AsTask().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>At most 5 interrupting messages per 10 s per server: a chatty server must not become a notification storm.</summary>
    private bool TakeMessageBudget()
    {
        var now = Environment.TickCount64;
        lock (_requestedSections)
        {
            if (now - _messageBudgetResetTicks > 10_000) { _messageBudgetResetTicks = now; _messageBudget = 5; }
            if (_messageBudget <= 0) return false;
            _messageBudget--;
            return true;
        }
    }

    // =================================================================================================================
    // Diagnostics: push
    // =================================================================================================================

    internal void OnPublishDiagnostics(JsonElement p)
    {
        var uri = p.Str("uri");
        if (uri is null) return;
        _diagMailbox.Put(uri, p); // latest wins per document; bounded; drops the oldest key when flooded
        ScheduleDiagnosticsDrain();
    }

    private void ScheduleDiagnosticsDrain()
    {
        if (Interlocked.Exchange(ref _diagScheduled, 1) != 0) return;
        if (!_ctx.Queue.Post(LanePriority.Background, DrainDiagnostics, exempt: true)) Volatile.Write(ref _diagScheduled, 0);
    }

    // Single consumer: _diagScheduled stays set while a drain runs, so two lane threads can never apply batches out of order
    // (an older value overwriting a newer one). Producers that arrive meanwhile are picked up by the re-check at the end.
    private void DrainDiagnostics()
    {
        try
        {
            foreach (var (_, json) in _diagMailbox.Drain(64))
            {
                if (Volatile.Read(ref _tornDown) != 0) return;
                try { ProcessPublish(json); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException) { _log.Add("[client] bad publishDiagnostics: " + ex.Message); }
            }
        }
        finally { Volatile.Write(ref _diagScheduled, 0); }
        if (_diagMailbox.HasItems) ScheduleDiagnosticsDrain();
    }

    private void ProcessPublish(JsonElement json)
    {
        var pp = P.LspJson.Deserialize<P.PublishDiagnosticsParams>(json);
        if (pp is null || LspMapper.ToUri(pp.Uri) is not { } uri) return;
        var doc = _ctx.Services.Documents.Get(uri);
        if (doc is not null && pp.Version is { } v && v < doc.Version) return; // computed for text that has since changed
        SetDiagnostics(uri, pp.Diagnostics, pp.Version);
    }

    private void SetDiagnostics(Uri uri, P.Diagnostic[]? diagnostics, int? version, string? owner = null)
    {
        owner ??= _owner;
        var count = Math.Min(diagnostics?.Length ?? 0, MaxDiagnosticsPerFile);
        var list = new List<Diagnostic>(count);
        for (var i = 0; i < count; i++) list.Add(Mapper.ToCore(diagnostics![i], uri));
        if (list.Count > 0) _publishedUris[(uri, owner)] = 0; else _publishedUris.TryRemove((uri, owner), out _);
        _ctx.Services.Diagnostics.Set(uri, owner, list, version);
    }

    private void ClearAllDiagnostics()
    {
        foreach (var (uri, owner) in _publishedUris.Keys.ToArray()) _ctx.Services.Diagnostics.Clear(uri, owner);
        _publishedUris.Clear();
    }

    /// <summary>Clears what this session published for one document (every owner).</summary>
    private void ClearDiagnostics(Uri uri)
    {
        foreach (var key in _publishedUris.Keys.Where(k => k.Uri == uri).ToArray())
        {
            _publishedUris.TryRemove(key, out _);
            _ctx.Services.Diagnostics.Clear(uri, key.Owner);
        }
        _ctx.Services.Diagnostics.Clear(uri, _owner);
    }

    // =================================================================================================================
    // Diagnostics: pull
    // =================================================================================================================

    internal void EnablePullDiagnostics(string? identifier, DocumentSelector? selector, bool workspace)
    {
        var provider = new PullProvider(identifier, selector, identifier is null ? _owner : _owner + "#" + identifier);
        lock (_diagGate)
        {
            // a provider registered again (same identifier) replaces the earlier registration
            _pullProviders = [.. _pullProviders.Where(p => p.Key != provider.Key), provider];
            _pullDiagnostics = true;
        }
        if (workspace)
            _ctx.Queue.Post(LanePriority.Idle, () => _ = _ctx.Queue.RunAsync<object>(LanePriority.Idle, new WorkKey(this, DiagnosticsPullFeature + 1 + _pullProviders.Length),
                ctx => PullWorkspaceAsync(ctx, provider)));
    }

    private readonly object _diagGate = new();

    private void SchedulePullAll()
    {
        if (!_pullDiagnostics) return;
        foreach (var sd in _docs.Values) ScheduleDiagnosticsPull(sd);
    }

    internal void ScheduleDiagnosticsPull(SyncedDocument sd)
    {
        if (!_pullDiagnostics || sd.Detached || !_pullProviders.Any(p => p.Selector is null || p.Selector.Score(sd.Document) > 0)) return;
        var queue = _ctx.Queue;
        if (queue.ShouldShed(LanePriority.Background)) return;
        // Keyed latest-wins: a newer change supersedes (cancels) the older pull while it is still in its debounce delay.
        _ = queue.RunAsync<object>(LanePriority.Background, new WorkKey(sd, DiagnosticsPullFeature), async ctx =>
        {
            await Task.Delay(250, ctx.Token).ConfigureAwait(false);
            await PullDocumentAsync(ctx, sd).ConfigureAwait(false);
            return null;
        }).AsTask().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private async Task PullDocumentAsync(WorkContext ctx, SyncedDocument sd)
    {
        foreach (var provider in _pullProviders)
        {
            if (provider.Selector is not null && provider.Selector.Score(sd.Document) <= 0) continue;
            if (!await PullDocumentAsync(ctx, sd, provider).ConfigureAwait(false)) return;
        }
    }

    /// <summary>One provider's report for one document; false when it is stale (the document changed: a newer pull follows).</summary>
    private async Task<bool> PullDocumentAsync(WorkContext ctx, SyncedDocument sd, PullProvider provider)
    {
        var version = sd.Document.Version;
        var p = new P.DocumentDiagnosticParams
        {
            TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString },
            Identifier = provider.Identifier,
            PreviousResultId = sd.PullResultIds?.GetValueOrDefault(provider.Key),
        };
        var report = await RequestAsync(ctx, P.LspRequests.TextDocumentDiagnostic, p, ctx.Token, sd, contentModifiedRetries: 2).ConfigureAwait(false);
        if (sd.Document.Version != version) return false;
        if (report.Index < 0) return true; // no result
        (sd.PullResultIds ??= new Dictionary<string, string?>(StringComparer.Ordinal))[provider.Key] = report.IsT0 ? report.AsT0.ResultId : report.AsT1.ResultId;
        if (report.IsT0)
        {
            var full = report.AsT0;
            SetDiagnostics(sd.Uri, full.Items, version, provider.Owner);
            if (full.RelatedDocuments is { } related) ApplyRelated(related, provider.Owner);
        }
        else if (report.AsT1.RelatedDocuments is { } related) ApplyRelated(related, provider.Owner);
        return true;
    }

    private void ApplyRelated(Dictionary<string, P.OneOf<P.FullDocumentDiagnosticReport, P.UnchangedDocumentDiagnosticReport>> related, string owner)
    {
        var n = 0;
        foreach (var (uriString, report) in related)
        {
            if (++n > 500) break;
            if (report.IsT0 && LspMapper.ToUri(uriString) is { } uri) SetDiagnostics(uri, report.AsT0.Items, null, owner);
        }
    }

    private async ValueTask<object?> PullWorkspaceAsync(WorkContext ctx, PullProvider provider)
    {
        var p = new P.WorkspaceDiagnosticParams { Identifier = provider.Identifier, PreviousResultIds = [] };
        var report = await RequestAsync(ctx, P.LspRequests.WorkspaceDiagnostic, p, ctx.Token, null, 0, _ctx.Settings().BackgroundTimeout * 2).ConfigureAwait(false);
        if (report?.Items is not { } items) return null;
        var n = 0;
        foreach (var item in items)
        {
            if (++n > 5000) break;
            if (!item.IsT0 || LspMapper.ToUri(item.AsT0.Uri) is not { } uri) continue;
            if (_ctx.Services.Documents.Get(uri) is not null) continue; // open documents are served by the per-document pull
            SetDiagnostics(uri, item.AsT0.Items, item.AsT0.Version, provider.Owner);
        }
        return null;
    }

    // =================================================================================================================
    // Progress (workDoneProgress) -> IWindowService.WithProgressAsync, coalesced
    // =================================================================================================================

    private sealed class ProgressSession(string title, bool cancellable)
    {
        public readonly string Title = title;
        public readonly bool Cancellable = cancellable;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile IProgressReporter? Reporter;
    }

    private readonly Dictionary<string, ProgressSession> _progress = [];
    private readonly BoundedFifo<(string Token, JsonElement Value)> _progressQueue = new(1024);
    private int _progressScheduled;

    internal void OnProgress(JsonElement p)
    {
        if (_ctx.Services.Window is null) return;
        var token = p.TokenKey();
        if (token is null || !p.TryGetProperty("value", out var value)) return;
        _progressQueue.Enqueue((token, value));
        if (Interlocked.Exchange(ref _progressScheduled, 1) == 0 && !_ctx.Queue.Post(LanePriority.Idle, DrainProgress, exempt: true))
            Volatile.Write(ref _progressScheduled, 0);
    }

    private void DrainProgress()
    {
        try { DrainProgressCore(); }
        finally { Volatile.Write(ref _progressScheduled, 0); } // single consumer: begin/report/end must never be applied out of order
        if (_progressQueue.HasItems && Interlocked.Exchange(ref _progressScheduled, 1) == 0 && !_ctx.Queue.Post(LanePriority.Idle, DrainProgress, exempt: true))
            Volatile.Write(ref _progressScheduled, 0);
    }

    private void DrainProgressCore()
    {
        var batch = _progressQueue.Drain(512);
        var lastReport = new Dictionary<string, int>();
        for (var i = 0; i < batch.Count; i++)
            if (batch[i].Value.Str("kind") == "report") lastReport[batch[i].Token] = i; // coalesce: only the latest report per token matters
        for (var i = 0; i < batch.Count; i++)
        {
            var (token, value) = batch[i];
            switch (value.Str("kind"))
            {
                case "begin": BeginProgress(token, value); break;
                case "report":
                    if (lastReport[token] == i) lock (_progress) { if (_progress.TryGetValue(token, out var s)) s.Reporter?.Report(value.Str("message"), value.Int("percentage")); }
                    break;
                case "end":
                    lock (_progress) { if (_progress.Remove(token, out var s)) s.Done.TrySetResult(); }
                    break;
            }
        }
    }

    private void BeginProgress(string token, JsonElement value)
    {
        var window = _ctx.Services.Window;
        if (window is null) return;
        ProgressSession session;
        lock (_progress)
        {
            if (_progress.Count >= 32 || _progress.ContainsKey(token)) return;
            session = new ProgressSession(value.Str("title") ?? Definition.DisplayName, value.TryGetProperty("cancellable", out var c) && c.ValueKind == JsonValueKind.True);
            _progress[token] = session;
        }
        var initial = value.Str("message");
        _ = window.WithProgressAsync($"{Definition.DisplayName}: {session.Title}", session.Cancellable, async (reporter, ct) =>
        {
            session.Reporter = reporter;
            reporter.Report(initial, value.Int("percentage"));
            using var reg = ct.Register(() => NotifyRaw(P.LspMethods.WindowWorkDoneProgressCancel, new P.WorkDoneProgressCancelParams { Token = token }));
            await session.Done.Task.ConfigureAwait(false);
            return 0;
        }).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void EndAllProgress()
    {
        lock (_progress)
        {
            foreach (var s in _progress.Values) s.Done.TrySetResult();
            _progress.Clear();
        }
    }

    // =================================================================================================================
    // Requests from the server
    // =================================================================================================================

    private Task<object?> OnLane(Func<WorkContext, ValueTask<object?>> work) =>
        _ctx.Queue.RunAsync<object>(LanePriority.Interactive, default, work).AsTask();

    internal Task<object?> HandleRegisterCapabilityAsync(JsonElement p) => OnLane(_ =>
    {
        var rp = P.LspJson.Deserialize<P.RegistrationParams>(p);
        if (rp?.Registrations is { } regs)
            foreach (var r in regs)
            {
                try { RegisterDynamic(r); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    _log.Add($"[client] ignoring bad registration for {r.Method}: {ex.Message}");
                }
            }
        return ValueTask.FromResult<object?>(null);
    });

    internal Task<object?> HandleUnregisterCapabilityAsync(JsonElement p) => OnLane(_ =>
    {
        var up = P.LspJson.Deserialize<P.UnregistrationParams>(p);
        if (up?.Unregisterations is { } regs)
            foreach (var r in regs) UnregisterDynamic(r.Id, r.Method);
        return ValueTask.FromResult<object?>(null);
    });

    internal Task<object?> HandleConfigurationAsync(JsonElement p) => OnLane(_ =>
    {
        var cp = P.LspJson.Deserialize<P.ConfigurationParams>(p);
        var results = new List<JsonElement?>(cp?.Items?.Length ?? 0);
        foreach (var item in cp?.Items ?? [])
        {
            if (!string.IsNullOrEmpty(item.Section))
                lock (_requestedSections) { if (_requestedSections.Count < 256) _requestedSections.Add(item.Section!); }
            results.Add(ResolveSetting(item.Section, LspMapper.ToUri(item.ScopeUri)));
        }
        // Array of values aligned with the request; JsonElement? null serialises as JSON null.
        return ValueTask.FromResult<object?>(results.ToArray());
    });

    private JsonElement? ResolveSetting(string? section, Uri? scope = null) => Contribute(ResolveBaseSetting(section), section ?? "", scope);

    /// <summary>
    /// Merges what <see cref="LspServices.SettingsContributors"/> add for this server into <paramref name="value"/> (an object, or nothing yet): the
    /// selected Python interpreter becomes <c>python.pythonPath</c>. A contributor that fails is left out.
    /// </summary>
    private JsonElement? Contribute(JsonElement? value, string section, Uri? scope)
    {
        if (_ctx.Services.SettingsContributors is not { Count: > 0 } contributors) return value;
        if (value is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) }) return value;
        System.Text.Json.Nodes.JsonObject? merged = null;
        foreach (var contributor in contributors)
        {
            IReadOnlyDictionary<string, JsonElement>? members;
            try { members = contributor.Contribute(Definition.Id, section, scope); }
            catch (Exception ex)
            {
                _ctx.Logger.LogWarning(ex, "A settings contributor failed for {Server}/{Section}", Definition.Id, section);
                continue;
            }
            if (members is not { Count: > 0 }) continue;
            merged ??= value is { ValueKind: JsonValueKind.Object } o ? System.Text.Json.Nodes.JsonObject.Create(o.Clone()) ?? new() : new();
            foreach (var (name, member) in members) merged[name] = System.Text.Json.Nodes.JsonNode.Parse(member.GetRawText());
        }
        return merged is null ? value : JsonSerializer.SerializeToElement(merged);
    }

    private JsonElement? ResolveBaseSetting(string? section)
    {
        if (Definition.Settings is { ValueKind: JsonValueKind.Object } s)
        {
            if (string.IsNullOrEmpty(section)) return s;
            if (s.TryGetProperty(section, out var flat)) return flat;
            var cur = s;
            var found = true;
            foreach (var part in section.Split('.'))
            {
                if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var next)) cur = next;
                else { found = false; break; }
            }
            if (found) return cur;
        }
        return string.IsNullOrEmpty(section) ? null : _ctx.Services.Configuration?.GetRaw(section);
    }

    internal Task<object?> HandleWorkspaceFoldersAsync() => OnLane(_ =>
        ValueTask.FromResult<object?>(CurrentFolders().Select(f => new P.WorkspaceFolder { Uri = LspMapper.ToUriString(f), Name = FolderName(f) }).ToArray()));

    internal Task<object?> HandleShowMessageRequestAsync(JsonElement p)
    {
        var type = p.Int("type") ?? 3;
        var text = p.Str("message") ?? "";
        var titles = new List<string>();
        if (p.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            foreach (var a in actions.EnumerateArray())
                if (a.Str("title") is { } t) titles.Add(t);
        return ShowMessageRequestCoreAsync(type, text, titles);
    }

    private async Task<object?> ShowMessageRequestCoreAsync(int type, string text, List<string> titles)
    {
        _log.Add($"[{(type == 1 ? "error" : type == 2 ? "warn" : "info")}] {text}");
        var window = _ctx.Services.Window;
        if (window is null) return null;
        var severity = type switch { 1 => MessageSeverity.Error, 2 => MessageSeverity.Warning, _ => MessageSeverity.Info };
        var chosen = await window.ShowMessageAsync(severity, $"{Definition.DisplayName}: {text}", titles.Select(t => new MessageAction(t)).ToArray(), _lifetime.Token).ConfigureAwait(false);
        return chosen is null ? null : new P.MessageActionItem { Title = chosen };
    }

    internal async Task<object?> HandleShowDocumentAsync(JsonElement p)
    {
        var sp = P.LspJson.Deserialize<P.ShowDocumentParams>(p);
        if (sp is null || LspMapper.ToUri(sp.Uri) is not { } uri) return new P.ShowDocumentResult { Success = false };
        try
        {
            if (sp.External == true)
                return new P.ShowDocumentResult { Success = _ctx.Services.Window is { } w && await w.OpenExternalAsync(uri).ConfigureAwait(false) };
            if (_ctx.Services.Navigation is not { } nav) return new P.ShowDocumentResult { Success = false };
            TextRange? selection = sp.Selection is { } r ? Mapper.ToCore(r, uri) : null;
            var opened = await nav.OpenAsync(uri, selection, preview: false, preserveFocus: sp.TakeFocus == false, ct: _lifetime.Token).ConfigureAwait(false);
            return new P.ShowDocumentResult { Success = opened is not null };
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            return new P.ShowDocumentResult { Success = false };
        }
    }

    internal async Task<object?> HandleApplyEditAsync(JsonElement p)
    {
        var ap = P.LspJson.Deserialize<P.ApplyWorkspaceEditParams>(p);
        if (ap is null) return new P.ApplyWorkspaceEditResult { Applied = false, FailureReason = "invalid parameters" };
        if (_ctx.Services.Navigation is not { } nav) return new P.ApplyWorkspaceEditResult { Applied = false, FailureReason = "no editor available" };
        try
        {
            var edit = await _ctx.Queue.RunAsync(LanePriority.Interactive, default, ctx => ValueTask.FromResult<WorkspaceEdit?>(Mapper.ToCore(ap.Edit)), _lifetime.Token).ConfigureAwait(false);
            var applied = edit is not null && await nav.ApplyWorkspaceEditAsync(edit, ap.Label, _lifetime.Token).ConfigureAwait(false);
            return new P.ApplyWorkspaceEditResult { Applied = applied };
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            return new P.ApplyWorkspaceEditResult { Applied = false, FailureReason = ex.Message };
        }
    }

    internal Task<object?> HandleRefreshAsync(RefreshKind kind)
    {
        // Core has no "invalidate" event, so a refresh re-registers the provider: ProvidersChanged makes consumers re-query.
        _ctx.Queue.Post(LanePriority.Background, () =>
        {
            if (kind == RefreshKind.Diagnostics) { foreach (var sd in _docs.Values) sd.PullResultIds = null; SchedulePullAll(); }
            else _features.Refresh(kind);
        });
        return Task.FromResult<object?>(null);
    }

    // =================================================================================================================
    // Environment: configuration changes, workspace folders, commands
    // =================================================================================================================

    private List<Uri> _folders = [];
    internal IReadOnlyList<Uri> CurrentFolders() { lock (_requestedSections) return _folders.Count > 0 ? [.. _folders] : [.. WorkspaceFolders]; }

    private bool FollowsWorkspaceFolders => Definition.RootMarkers is not { Count: > 0 };

    private void SubscribeEnvironment()
    {
        lock (_requestedSections) _folders = [.. WorkspaceFolders];
        if (_ctx.Services.Configuration is { } cfg)
            _store.Add(cfg.Changed.Subscribe(e =>
            {
                string[] sections;
                lock (_requestedSections) sections = [.. _requestedSections];
                if (e.Affects(LspSettingKeys.LanguageServers + "." + Definition.Id) || sections.Any(e.Affects))
                    _ctx.Queue.Post(LanePriority.Background, PushConfiguration);
            }));
        foreach (var contributor in _ctx.Services.SettingsContributors)
            _store.Add(contributor.Changed.Subscribe(id =>
            {
                if (id is null || id == Definition.Id) _ctx.Queue.Post(LanePriority.Background, PushConfiguration);
            }));
        if (_ctx.Services.Workspace is { } ws && FollowsWorkspaceFolders)
            _store.Add(ws.FoldersChanged.Subscribe(e => _ctx.Queue.Post(LanePriority.Interactive, () => OnFoldersChanged(e), exempt: true)));
    }

    private void OnFoldersChanged(WorkspaceFoldersChangedEventArgs e)
    {
        lock (_requestedSections)
        {
            _folders.RemoveAll(f => e.Removed.Contains(f));
            foreach (var a in e.Added) if (!_folders.Contains(a)) _folders.Add(a);
        }
        if (ServerCapabilities?.Workspace?.WorkspaceFolders?.ChangeNotifications is null) return;
        NotifyRaw(P.LspMethods.WorkspaceDidChangeWorkspaceFolders, new P.DidChangeWorkspaceFoldersParams
        {
            Event = new P.WorkspaceFoldersChangeEvent
            {
                Added = e.Added.Select(f => new P.WorkspaceFolder { Uri = LspMapper.ToUriString(f), Name = FolderName(f) }).ToArray(),
                Removed = e.Removed.Select(f => new P.WorkspaceFolder { Uri = LspMapper.ToUriString(f), Name = FolderName(f) }).ToArray(),
            },
        });
    }

    internal void PushConfiguration()
    {
        if (!IsOperational) return;
        var settings = Definition.Settings is { ValueKind: JsonValueKind.Object } s ? s : JsonDocument.Parse("{}").RootElement;
        NotifyRaw(P.LspMethods.WorkspaceDidChangeConfiguration, new P.DidChangeConfigurationParams { Settings = settings });
    }

    /// <summary>Bridges the server's executeCommandProvider.commands into ICommandService (when one exists).</summary>
    internal void RegisterServerCommands(IEnumerable<string> commands, string registrationId)
    {
        if (_ctx.Services.Commands is not { } commandService) return;
        foreach (var id in commands)
        {
            if (commandService.Has(id)) continue; // another session (root) of the same server owns it
            var command = id;
            _store.Add(commandService.Register(command, async inv =>
            {
                var args = inv.Args.Select(a => a is JsonElement je ? je : JsonSerializer.SerializeToElement(a)).ToArray();
                return await ExecuteServerCommandAsync(command, args, inv.Cancellation).ConfigureAwait(false);
            }));
        }
    }

    internal async ValueTask<object?> ExecuteServerCommandAsync(string command, JsonElement[] arguments, CancellationToken ct)
    {
        return await _ctx.Queue.RunAsync<object>(LanePriority.Interactive, default, async wc =>
        {
            var p = new P.ExecuteCommandParams { Command = command, Arguments = arguments };
            var r = await RequestAsync(wc, P.LspRequests.WorkspaceExecuteCommand, p, wc.Token, null, 0, _ctx.Settings().BackgroundTimeout).ConfigureAwait(false);
            return r is { } v ? (object)v : null;
        }, ct).ConfigureAwait(false);
    }
}
