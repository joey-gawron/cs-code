using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

/// <summary>
/// <c>search/*</c>: workspace file enumeration (quick open, Find in Files) and text search, run next to the files. A search is a session the client
/// pulls batches from (<c>search/next</c>), which is natural back-pressure: a slow client stalls the walk instead of growing a queue.
/// File walks reuse Platform's walker (files.exclude, .gitignore, symlink-loop safety) through a private <see cref="WorkspaceService"/> per session;
/// text is scanned on dedicated threads. Sessions are bounded in number and expire when the client stops pulling.
/// </summary>
internal sealed class SearchHandlers : IDisposable
{
    public const int MaxSessions = 16;
    public const int TextWorkers = 2;
    private const int PreviewChars = 250;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IFileSystem _fs;
    private readonly IFileWatcher _watcher;
    private readonly IoLane _lane;
    private readonly PlatformOptions _options;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;

    public SearchHandlers(IFileSystem fs, IFileWatcher watcher, IoLane lane, PlatformOptions options)
    {
        _fs = fs;
        _watcher = watcher;
        _lane = lane;
        _options = options;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public int ActiveSessions => _sessions.Count;

    private sealed class Session : IDisposable
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly Channel<object> Results = Channel.CreateBounded<object>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public long LastPulled = Environment.TickCount64;
        public int FilesSearched;
        public string? Error;
        public void Dispose() { try { Cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    public void Register(IRpcEndpoint peer)
    {
        peer.OnRequest<SearchFilesParams, SearchSession>(RemoteMethods.SearchFiles, (p, _) => ValueTask.FromResult(Start(s => ProduceFilesAsync(p, s))));
        peer.OnRequest<SearchTextParams, SearchSession>(RemoteMethods.SearchText, (p, _) =>
        {
            var regex = BuildRegex(p); // invalid patterns fail the request itself
            return ValueTask.FromResult(Start(s => ProduceTextAsync(p, regex, s)));
        });
        peer.OnRequest<SearchNextParams, SearchBatch>(RemoteMethods.SearchNext, (p, ct) => NextAsync(p, ct));
        peer.OnRequest<SearchSession, Empty>(RemoteMethods.SearchCancel, (p, _) =>
        {
            if (_sessions.TryRemove(p.SessionId, out var s)) s.Dispose();
            return ValueTask.FromResult(Empty.Value);
        });
    }

    private SearchSession Start(Func<Session, Task> produce)
    {
        if (_sessions.Count >= MaxSessions) throw new RemoteException(RpcErrorCodes.Busy, $"More than {MaxSessions} searches are running");
        var id = Guid.NewGuid().ToString("N");
        var session = new Session();
        _sessions[id] = session;
        _ = RunAsync(session, produce);
        return new SearchSession(id);
    }

    private static async Task RunAsync(Session session, Func<Session, Task> produce)
    {
        try { await produce(session).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { session.Error = ex.Message; }
        finally { session.Results.Writer.TryComplete(); }
    }

    private async ValueTask<SearchBatch> NextAsync(SearchNextParams p, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(p.SessionId, out var s)) return new SearchBatch(null, null, Done: true, Error: "Unknown or expired search");
        Volatile.Write(ref s.LastPulled, Environment.TickCount64);
        var max = Math.Clamp(p.Max, 1, 5000);
        var reader = s.Results.Reader;
        var items = new List<object>(Math.Min(max, 256));
        var more = await reader.WaitToReadAsync(ct).ConfigureAwait(false);
        while (more && items.Count < max && reader.TryRead(out var item)) items.Add(item);
        var done = !more || reader.Completion.IsCompleted && !reader.TryPeek(out _);
        if (done && _sessions.TryRemove(p.SessionId, out var finished)) finished.Dispose();
        return new SearchBatch(
            items.OfType<string>().ToArray() is { Length: > 0 } paths ? paths : null,
            items.OfType<TextSearchMatch>().ToArray() is { Length: > 0 } matches ? matches : null,
            done, s.Error, Volatile.Read(ref s.FilesSearched));
    }

    // ------------------------------------------------------------------------------------------------------------------- files

    private async Task ProduceFilesAsync(SearchFilesParams p, Session s)
    {
        await foreach (var path in WalkAsync(p.Folder, p.IncludeGlob, p.ExcludeGlobs, p.UseIgnoreFiles, p.FollowSymlinks, p.MaxResults, s.Cts.Token).ConfigureAwait(false))
            await s.Results.Writer.WriteAsync(path, s.Cts.Token).ConfigureAwait(false);
    }

    /// <summary>Platform's workspace walk over one folder, with the request's excludes instead of this server's settings.</summary>
    internal async IAsyncEnumerable<string> WalkAsync(string folder, string? include, IReadOnlyList<string> excludes, bool useIgnoreFiles, bool followSymlinks, int max,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var ws = new WorkspaceService(_fs, _watcher, new MemoryState(), new SearchConfiguration(excludes, useIgnoreFiles, followSymlinks), _lane, null, _options);
        await ws.OpenFolderAsync(FileUri.FromDirectoryPath(FileSystemHandlers.Uri(folder).LocalPath), ct).ConfigureAwait(false);
        await foreach (var uri in ws.FindFilesAsync(include, max, ct).ConfigureAwait(false))
            yield return FileUri.ToPath(uri);
    }

    // ------------------------------------------------------------------------------------------------------------------- text

    internal static Regex BuildRegex(SearchTextParams p)
    {
        if (string.IsNullOrEmpty(p.Pattern)) throw new ArgumentException("Empty search pattern");
        var body = p.IsRegex ? p.Pattern : Regex.Escape(p.Pattern);
        if (p.WholeWord) body = @"\b(?:" + body + @")\b";
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (p.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(body, options, TimeSpan.FromSeconds(1)); }
        catch (RegexParseException ex) { throw new ArgumentException("Invalid regular expression: " + ex.Message, ex); }
    }

    private async Task ProduceTextAsync(SearchTextParams p, Regex regex, Session s)
    {
        var ct = s.Cts.Token;
        var files = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
        var found = 0;
        var workers = Enumerable.Range(0, TextWorkers).Select(i => StartWorker(i, files.Reader, regex, p, s, () => Interlocked.Increment(ref found))).ToArray();
        try
        {
            foreach (var folder in p.Folders)
                await foreach (var path in WalkAsync(folder, p.IncludeGlob, p.ExcludeGlobs ?? [], p.UseIgnoreFiles, true, 0, ct).ConfigureAwait(false))
                    await files.Writer.WriteAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            files.Writer.TryComplete();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private static Task StartWorker(int index, ChannelReader<string> files, Regex regex, SearchTextParams p, Session s, Func<int> countMatch)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var ct = s.Cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!files.TryRead(out var path))
                    {
                        if (!files.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) break; // dedicated server thread: blocking is the point
                        continue;
                    }
                    ScanFile(path, regex, p, s, countMatch, ct);
                    Interlocked.Increment(ref s.FilesSearched);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { s.Error ??= ex.Message; }
            finally { done.TrySetResult(); }
        }) { IsBackground = true, Name = $"cscode-server-search-{index}", Priority = ThreadPriority.BelowNormal };
        thread.Start();
        return done.Task;
    }

    private static void ScanFile(string path, Regex regex, SearchTextParams p, Session s, Func<int> countMatch, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > p.MaxFileBytes) return;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0) return; // binary
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.AsSpan(StartsWithBom(bytes) ? 3 : 0)); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }

        var line = 0;
        var start = 0;
        while (start <= text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var end = text.IndexOfAny(['\n', '\r'], start);
            var lineEnd = end < 0 ? text.Length : end;
            var lineText = text.AsSpan(start, lineEnd - start);
            if (lineText.Length > 0)
            {
                List<(int Index, int Length)> matches;
                try { matches = Matches(regex, lineText); }
                catch (RegexMatchTimeoutException) { return; } // pathological pattern on this file: skip it
                foreach (var m in matches)
                {
                    if (countMatch() > p.MaxResults) { s.Cts.Cancel(); return; }
                    var (preview, from) = lineText.Length <= PreviewChars ? (lineText.ToString(), 0) : Preview(lineText, m.Index);
                    s.Results.Writer.WriteAsync(new TextSearchMatch(path, line, m.Index, m.Length, preview, from), ct).AsTask().GetAwaiter().GetResult();
                }
            }
            if (end < 0) break;
            start = end + (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n' ? 2 : 1);
            line++;
        }
    }

    private static List<(int Index, int Length)> Matches(Regex regex, ReadOnlySpan<char> line)
    {
        var list = new List<(int Index, int Length)>();
        foreach (var m in regex.EnumerateMatches(line))
        {
            if (m.Length == 0) continue;
            list.Add((m.Index, m.Length));
            if (list.Count >= 1000) break;
        }
        return list;
    }

    private static (string Text, int From) Preview(ReadOnlySpan<char> line, int at)
    {
        var from = Math.Max(0, at - 40);
        return (line.Slice(from, Math.Min(PreviewChars, line.Length - from)).ToString(), from);
    }

    private static bool StartsWithBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    private void Sweep()
    {
        var now = Environment.TickCount64;
        foreach (var (id, s) in _sessions)
            if (now - Volatile.Read(ref s.LastPulled) > IdleTimeout.TotalMilliseconds && _sessions.TryRemove(id, out _)) s.Dispose();
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var id in _sessions.Keys.ToArray())
            if (_sessions.TryRemove(id, out var s)) s.Dispose();
    }

    /// <summary>The three settings the workspace walk reads, answered from the request.</summary>
    private sealed class SearchConfiguration(IReadOnlyList<string> excludes, bool useIgnoreFiles, bool followSymlinks) : IConfigurationService
    {
        private readonly Emitter<ConfigurationChangedEventArgs> _changed = new();

        public T Get<T>(string key, T defaultValue) => Get<T>(key) is { } v ? v : defaultValue;

        public T? Get<T>(string key)
        {
            object? value = key switch
            {
                SettingKeys.FilesExclude => excludes.ToDictionary(e => e, _ => true),
                "search.useIgnoreFiles" => useIgnoreFiles,
                "search.followSymlinks" => followSymlinks,
                _ => null,
            };
            return value is T t ? t : default;
        }

        public JsonElement? GetRaw(string key) => null;
        public ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default) => ValueTask.CompletedTask;
        public IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties) => Disposable.Empty;
        public IReadOnlyList<ConfigurationProperty> Properties => [];
        public IEventSource<ConfigurationChangedEventArgs> Changed => _changed;
        public string UserSettingsPath => "";
    }

    private sealed class MemoryState : IStateStorage
    {
        public T? Get<T>(string key, StorageScope scope = StorageScope.Application) => default;
        public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application) { }
        public void Remove(string key, StorageScope scope = StorageScope.Application) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
