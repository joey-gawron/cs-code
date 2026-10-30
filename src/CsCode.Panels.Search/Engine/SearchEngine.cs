using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

/// <summary>
/// Workspace text search that never touches the UI thread or the shared thread pool.
/// <list type="bullet">
/// <item><see cref="Start"/> returns after enqueueing one item (microseconds); the pattern is compiled, settings are read and files are
/// enumerated on a dedicated coordinator thread (a single-thread <see cref="TaskScheduler"/>, so async enumeration resumes on it).</item>
/// <item>Scanning runs on <see cref="SearchLimits.Parallelism"/> dedicated worker threads per session (below-normal priority) fed through a
/// bounded queue, so enumeration cannot run ahead of scanning and memory stays flat however many files there are.</item>
/// <item>Results flow to subscribers as small immutable batches about every 16 ms (see <see cref="SearchSession"/>), capped at
/// <c>search.maxResults</c>; cancellation is a token checked per line.</item>
/// </list>
/// </summary>
public sealed class SearchEngine : IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly IDocumentManager? _documents;
    private readonly IConfigurationService? _config;
    private readonly ISearchFileSource _source;
    private readonly SearchLimits? _limitsOverride;
    private readonly IReadOnlyList<ITextSearchProvider> _textSearch;
    private readonly ConcurrentDictionary<int, SearchSession> _sessions = new();
    private readonly object _schedulerGate = new();
    private DedicatedThreadScheduler? _scheduler;
    private int _nextId, _runningWorkers;
    private volatile bool _disposed;

    /// <param name="textSearch">Providers that search folders of their scheme where the files are (a remote workspace's server): such folders are not
    /// read here, only their matches arrive.</param>
    public SearchEngine(IWorkspaceService workspace, IDocumentManager? documents = null, IConfigurationService? configuration = null,
        ISearchFileSource? fileSource = null, SearchLimits? limits = null, IReadOnlyList<ITextSearchProvider>? textSearch = null)
    {
        _workspace = workspace;
        _documents = documents;
        _config = configuration;
        _source = fileSource ?? new DefaultFileSource(workspace);
        _limitsOverride = limits;
        _textSearch = textSearch ?? [];
    }

    /// <summary>Worker threads currently alive across all sessions (tests assert this returns to 0 after cancel/dispose).</summary>
    public int RunningWorkers => Volatile.Read(ref _runningWorkers);

    public int ActiveSessions => _sessions.Count;

    /// <summary>Starts a search and returns immediately. Dispose or <see cref="SearchSession.Cancel"/> the session to stop it.</summary>
    /// <param name="attach">
    /// Subscribes to the session's events BEFORE it runs. Subscribe here rather than after <see cref="Start"/> returns: the coordinator may
    /// finish a session before then (an invalid pattern completes at once), and <see cref="SearchSession.Completed"/> never fires for a late
    /// subscriber.
    /// </param>
    public SearchSession Start(SearchQuery query, Action<SearchSession>? attach = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = new SearchSession(Interlocked.Increment(ref _nextId), query, _limitsOverride ?? new SearchLimits());
        attach?.Invoke(session);
        _sessions[session.Id] = session;
        _ = session.Result.ContinueWith(static (_task, s) =>
        {
            var (engine, id) = ((SearchEngine, int))s!;
            engine._sessions.TryRemove(id, out _);
        }, (this, session.Id), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = Task.Factory.StartNew(() => RunAsync(session), CancellationToken.None, TaskCreationOptions.DenyChildAttach, Scheduler()).Unwrap();
        return session;
    }

    private DedicatedThreadScheduler Scheduler()
    {
        lock (_schedulerGate) return _scheduler ??= new DedicatedThreadScheduler("cscode-search-coordinator");
    }

    // ------------------------------------------------------------------------------------------ coordinator

    private async Task RunAsync(SearchSession session)
    {
        var flusher = Task.CompletedTask;
        try
        {
            var query = session.Query;
            session.Limits = _limitsOverride ?? SearchLimits.FromConfiguration(_config);
            var limits = session.Limits;

            TextMatcher matcher;
            try { matcher = TextMatcher.Create(query, limits); }
            catch (SearchPatternException ex)
            {
                session.StopForFailure(ex.Message);
                session.Complete(ex.Message);
                return;
            }

            flusher = FlushLoopAsync(session);

            var queue = new WorkQueue(limits.QueueCapacity);
            var workers = Math.Max(1, limits.Parallelism);
            session.SetWorkerCount(workers);
            for (var i = 0; i < workers; i++) StartWorker(session, matcher, queue, i);

            try
            {
                await ProduceAsync(session, queue).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
            {
                // stopped (user cancel, result cap or failure): fall through to completion
            }
            finally
            {
                session.MarkEnumerationDone();
                queue.Complete(workers);
            }

            // Wait for the workers to finish, but a stop (user cancel, result cap, failure) ends the wait whenever it comes, also after the
            // enumeration is over: a worker stuck in a slow regex (up to search.regexTimeoutMs) or a blocking open must not keep the session,
            // its flush timer and the panel's "Searching…" alive.
            await Task.WhenAny(session.WorkersStopped, session.StopRequested).ConfigureAwait(true);
            if (session.CapReached)
            {
                // Truncated: let the workers hand in the file they were scanning so the delivered count equals the reported one,
                // but never wait long on a worker that is stuck in a slow regex.
                await Task.WhenAny(session.WorkersStopped, Task.Delay(250)).ConfigureAwait(true);
            }
            // A user cancel completes at once: the workers wind down on their own (see SearchSession.WorkersStopped).
            session.Complete();
        }
        catch (OperationCanceledException)
        {
            session.Complete();
        }
        catch (Exception ex)
        {
            session.StopForFailure(ex.Message);
            session.Complete(ex.Message);
        }
        finally
        {
            session.Complete(); // idempotent safety net
            await flusher.ConfigureAwait(true);
        }
    }

    private static async Task FlushLoopAsync(SearchSession session)
    {
        while (!session.IsCompleted)
        {
            try { await Task.Delay(session.Limits.BatchInterval).ConfigureAwait(true); }
            catch (OperationCanceledException) { break; }
            session.Flush();
        }
    }

    private async ValueTask ProduceAsync(SearchSession session, WorkQueue queue)
    {
        var query = session.Query;
        var ct = session.Token;
        var folders = query.Roots ?? _workspace.Folders;
        var roots = new RootSet(folders);
        var include = GlobFilter.Parse(query.Include);
        var exclude = GlobFilter.Parse(query.Exclude);
        var settingsExclude = query.UseExcludeSettings
            ? GlobFilter.FromSetting(_config, "files.exclude").Combine(GlobFilter.FromSetting(_config, SearchLimits.ExcludeKey))
            : GlobFilter.Empty;

        bool Accept(Uri uri)
        {
            var rel = roots.ToRelative(uri);
            if (!include.IsEmpty && !include.IsMatch(rel)) return false;
            if (exclude.IsMatch(rel)) return false;
            return !settingsExclude.IsMatch(rel);
        }

        // Selection search: one document, one range.
        if (query.Selection is { } sel)
        {
            if (_documents?.Get(sel.Uri) is { } doc)
            {
                var version = doc.Version;
                var text = doc.GetText(sel.Range);
                await queue.EnqueueAsync(new WorkItem(sel.Uri, text, version, null, sel.Range.Start.Line, sel.Range.Start.Character), ct).ConfigureAwait(true);
            }
            return;
        }

        // Open documents with unsaved changes (and untitled buffers) are searched from memory, never from the stale disk copy.
        var memory = new Dictionary<Uri, (string Text, int Version)>();
        var openDocs = _documents?.Documents.ToArray() ?? [];
        foreach (var doc in openDocs)
        {
            if (!(doc.IsDirty || doc.Uri.Scheme == "untitled")) continue;
            var version = doc.Version;
            memory[doc.Uri] = (doc.GetText(), version);
        }

        if (query.OpenEditorsOnly)
        {
            foreach (var doc in openDocs)
            {
                ct.ThrowIfCancellationRequested();
                if (!Accept(doc.Uri)) continue;
                var item = memory.TryGetValue(doc.Uri, out var m) ? new WorkItem(doc.Uri, m.Text, m.Version) : new WorkItem(doc.Uri);
                if (item.Text is null)
                {
                    if (await PreloadAsync(session, item, ct).ConfigureAwait(true) is not { } ready) continue;
                    item = ready;
                }
                await queue.EnqueueAsync(item, ct).ConfigureAwait(true);
            }
            return;
        }

        // A remote workspace is searched where its files are (server-side Find in Files): only the matches cross the network.
        if (!query.MultiLine && folders.Count > 0 && _textSearch.FirstOrDefault(p => folders.All(f => string.Equals(f.Scheme, p.Scheme, StringComparison.OrdinalIgnoreCase))) is { } remote)
        {
            await ProduceRemoteAsync(session, remote, folders, roots, Accept, memory, queue).ConfigureAwait(true);
            return;
        }

        var request = new FileSourceRequest(folders, roots, query.UseExcludeSettings, query.UseIgnoreFiles, include, exclude, settingsExclude);
        var n = 0;
        await foreach (var uri in _source.EnumerateAsync(request, ct).ConfigureAwait(true))
        {
            if (query.Roots is not null && !roots.Contains(uri)) continue;
            if (!Accept(uri)) continue;
            var item = memory.TryGetValue(uri, out var m) ? new WorkItem(uri, m.Text, m.Version) : new WorkItem(uri);
            if (item.Text is null)
            {
                if (await PreloadAsync(session, item, ct).ConfigureAwait(true) is not { } ready) continue;
                item = ready;
            }
            await queue.EnqueueAsync(item, ct).ConfigureAwait(true);
            // Let the flusher run even when the source yields synchronously (a hot enumeration would starve it).
            if ((++n & 0xFF) == 0) await Task.Yield();
        }
    }

    /// <summary>
    /// Folders a <see cref="ITextSearchProvider"/> searches where they are: documents with unsaved changes under them are scanned here from memory
    /// (the server's disk copy is stale), everything else comes back as matches, filtered with the same include/exclude rules as a local search
    /// and counted against the same result cap.
    /// </summary>
    private async ValueTask ProduceRemoteAsync(SearchSession session, ITextSearchProvider provider, IReadOnlyList<Uri> folders, RootSet roots, Func<Uri, bool> accept,
        Dictionary<Uri, (string Text, int Version)> memory, WorkQueue queue)
    {
        var query = session.Query;
        var ct = session.Token;
        foreach (var (uri, m) in memory)
            if (roots.Contains(uri) && accept(uri)) await queue.EnqueueAsync(new WorkItem(uri, m.Text, m.Version), ct).ConfigureAwait(true);

        var excludes = new List<string>();
        if (query.UseExcludeSettings)
            foreach (var key in new[] { "files.exclude", SearchLimits.ExcludeKey })
                if (_config?.GetRaw(key) is { ValueKind: System.Text.Json.JsonValueKind.Object } obj)
                    excludes.AddRange(obj.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.True).Select(p => p.Name));
        var limits = session.Limits;
        var request = new TextSearchQuery(folders, query.Pattern, query.IsRegex, query.MatchCase, query.WholeWord, excludes, query.UseIgnoreFiles,
            limits.MaxResults, limits.MaxFileSizeBytes);
        await foreach (var batch in provider.SearchAsync(request, ct).ConfigureAwait(true))
        {
            foreach (var file in batch.GroupBy(h => h.Uri))
            {
                if (memory.ContainsKey(file.Key) || !roots.Contains(file.Key) || !accept(file.Key)) continue;
                var matches = new List<SearchMatch>();
                foreach (var hit in file)
                {
                    if (!session.TryReserve()) break; // the cap: the session stops, truncated
                    matches.Add(RemoteMatch(hit, limits.MaxPreviewChars));
                }
                if (matches.Count > 0) session.Report(new WorkItem(file.Key), new ScanResult(ScanStatus.Ok, matches));
                if (session.Token.IsCancellationRequested) return;
            }
        }
    }

    /// <summary>A match found elsewhere, with the preview built like a local one's (leading blanks trimmed, controls as spaces, clipped around the match).</summary>
    internal static SearchMatch RemoteMatch(TextSearchHit hit, int maxPreview)
    {
        maxPreview = Math.Clamp(maxPreview, 40, 480);
        var text = hit.LineText;
        var idx = Math.Clamp(hit.Column - hit.LineTextStart, 0, text.Length);
        var len = Math.Min(hit.Length, text.Length - idx);
        var lead = 0;
        while (lead < idx && char.IsWhiteSpace(text[lead])) lead++;
        var start = idx - lead > 40 || text.Length - lead > maxPreview ? Math.Max(lead, idx - 40) : lead;
        var clipLeft = start > lead || (hit.LineTextStart > 0 && start == 0);
        var end = Math.Min(text.Length, start + maxPreview);
        var clipRight = end < text.Length;
        var sb = new System.Text.StringBuilder(end - start + 2);
        if (clipLeft) sb.Append('…');
        for (var i = start; i < end; i++) sb.Append(text[i] < ' ' ? ' ' : text[i]);
        if (clipRight) sb.Append('…');
        var preview = sb.ToString().TrimEnd();
        var previewStart = (clipLeft ? 1 : 0) + (idx - start);
        var matched = text.Substring(idx, len);
        return new SearchMatch(new TextRange(new TextPosition(hit.Line, hit.Column), new TextPosition(hit.Line, hit.Column + hit.Length)),
            preview, Math.Min(previewStart, preview.Length), Math.Max(0, Math.Min(len, preview.Length - previewStart)), matched.Length > 256 ? matched[..256] : matched);
    }

    /// <summary>Non-file schemes have no local path to stream from: read their bytes through <see cref="IFileSystem"/> (size-checked first).</summary>
    private async ValueTask<WorkItem?> PreloadAsync(SearchSession session, WorkItem item, CancellationToken ct)
    {
        if (item.Uri.IsFile) return item;
        try
        {
            var fs = _workspace.FileSystem;
            var stat = await fs.StatAsync(item.Uri, ct).ConfigureAwait(true);
            if (stat is null) { session.CountUnreadable(); return null; }
            if (stat.Size > session.Limits.MaxFileSizeBytes) { session.CountTooLarge(); return null; }
            return item with { Bytes = await fs.ReadFileAsync(item.Uri, ct).ConfigureAwait(true) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            session.CountUnreadable();
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------- workers

    private void StartWorker(SearchSession session, TextMatcher matcher, WorkQueue queue, int index)
    {
        Interlocked.Increment(ref _runningWorkers);
        var thread = new Thread(() => WorkerMain(session, matcher, queue))
        {
            IsBackground = true,
            Name = $"cscode-search-{session.Id}-{index}",
        };
        try { thread.Priority = ThreadPriority.BelowNormal; }
        catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException) { /* best effort: not settable everywhere */ }
        thread.Start();
    }

    private void WorkerMain(SearchSession session, TextMatcher matcher, WorkQueue queue)
    {
        try
        {
            using var scanner = new FileScanner(session.Limits, matcher);
            var token = session.Token;
            while (queue.TryTake(out var item, token))
            {
                ScanResult result;
                try { result = scanner.Scan(item, session); }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // A bug or an exotic file must cost one file, never the process (an unhandled exception on this thread would).
                    result = new ScanResult(ScanStatus.Unreadable);
                }
                session.Report(item, result);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled while waiting for work
        }
        catch (Exception ex)
        {
            session.StopForFailure("Search worker failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _runningWorkers);
            session.WorkerExited();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _sessions.Values)
        {
            // Complete here rather than waiting for the coordinator, which is about to go away (its continuations would be dropped).
            s.Cancel();
            s.Complete();
        }
        DedicatedThreadScheduler? scheduler;
        lock (_schedulerGate)
        {
            scheduler = _scheduler;
            _scheduler = null;
        }
        scheduler?.Dispose();
    }
}

/// <summary>Bounded hand-off from the coordinator (async producer) to worker threads (blocking consumers).</summary>
internal sealed class WorkQueue(int capacity)
{
    private readonly ConcurrentQueue<WorkItem> _items = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _space = new(capacity, capacity);

    public async ValueTask EnqueueAsync(WorkItem item, CancellationToken ct)
    {
        await _space.WaitAsync(ct).ConfigureAwait(true);
        _items.Enqueue(item);
        _available.Release();
    }

    /// <summary>Blocks the calling (dedicated) thread until an item arrives; false once the queue is complete and drained. Throws on cancellation.</summary>
    public bool TryTake(out WorkItem item, CancellationToken ct)
    {
        _available.Wait(ct);
        if (_items.TryDequeue(out item))
        {
            _space.Release();
            return true;
        }
        return false;
    }

    /// <summary>No more items will be added: wakes <paramref name="consumers"/> blocked workers so they can exit.</summary>
    public void Complete(int consumers) => _available.Release(consumers);
}

/// <summary>A TaskScheduler that runs everything on one dedicated thread, so async code resumes there and never on the shared pool.</summary>
internal sealed class DedicatedThreadScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread _thread;

    public DedicatedThreadScheduler(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var task in _tasks.GetConsumingEnumerable()) TryExecuteTask(task);
    }

    protected override void QueueTask(Task task)
    {
        try { _tasks.Add(task); }
        catch (InvalidOperationException) { /* completed: the engine is being disposed */ }
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) =>
        Thread.CurrentThread == _thread && TryExecuteTask(task);

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

    /// <summary>Lets the thread finish what is queued and exit. Never blocks the caller (which may be the UI thread).</summary>
    public void Dispose() => _tasks.CompleteAdding();
}
