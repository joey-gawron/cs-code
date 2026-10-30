using System.Runtime.CompilerServices;
using CsCode.Core.Platform;
using CsCode.Remote.Protocol;

namespace CsCode.Remote.Client;

/// <summary>A text match in a remote file, as the client sees it.</summary>
public sealed record RemoteTextMatch(Uri Uri, int Line, int Column, int Length, string Preview);

/// <summary>
/// Search that runs on the remote: workspace file enumeration (the <see cref="IFileSearchProvider"/> behind quick open and the Find in Files file list,
/// honouring files.exclude and .gitignore there) and full text search (<see cref="SearchTextAsync"/>: the server walks and scans in parallel and
/// only matches cross the network). Both are pull sessions: batches are fetched as the consumer reads, abandoning or cancelling the enumeration
/// cancels the server-side work.
/// </summary>
public sealed class RemoteSearch(IRemoteEndpoint endpoint) : IFileSearchProvider, ITextSearchProvider
{
    public const int BatchSize = 1000;

    /// <summary>
    /// Find in Files for a remote workspace (<see cref="ITextSearchProvider"/>): the server walks the folders (with the query's excludes and ignore
    /// files) and scans them in parallel; matches stream back in batches as they are pulled. Invalid patterns fail with an ArgumentException.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<TextSearchHit>> SearchAsync(TextSearchQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (query.Folders.Count == 0) yield break;
        var (session, mapper) = await endpoint.ResolveAsync(query.Folders[0], ct).ConfigureAwait(false);
        var request = new SearchTextParams(query.Folders.Select(mapper.ToRemotePath).ToArray(), query.Pattern, query.IsRegex, query.MatchCase, query.WholeWord,
            null, query.ExcludeGlobs, query.UseIgnoreFiles, query.MaxResults, query.MaxFileBytes);
        await foreach (var batch in PullAsync(session, RemoteMethods.SearchText, request, ct).ConfigureAwait(false))
            if (batch.Matches is { Count: > 0 } matches)
                yield return matches.Select(m => new TextSearchHit(mapper.FromRemotePath(m.Path), m.Line, m.Column, m.Length, m.Preview, m.PreviewStart)).ToArray();
    }

    public string Scheme => SshUriMapper.Scheme;

    public async IAsyncEnumerable<Uri> FindFilesAsync(Uri folder, FileSearchOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (session, mapper) = await endpoint.ResolveAsync(folder, ct).ConfigureAwait(false);
        var request = new SearchFilesParams(mapper.ToRemotePath(folder), options.IncludeGlob, options.ExcludeGlobs, options.UseIgnoreFiles, options.FollowSymlinks, options.MaxResults);
        await foreach (var batch in PullAsync(session, RemoteMethods.SearchFiles, request, ct).ConfigureAwait(false))
            foreach (var path in batch.Paths ?? [])
                yield return mapper.FromRemotePath(path);
    }

    /// <summary>Text search in <paramref name="folders"/> (ssh: URIs of the connected host). Matches stream in as the server finds them.</summary>
    public async IAsyncEnumerable<RemoteTextMatch> SearchTextAsync(IReadOnlyList<Uri> folders, SearchTextParams query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (folders.Count == 0) yield break;
        var (session, mapper) = await endpoint.ResolveAsync(folders[0], ct).ConfigureAwait(false);
        var request = query with { Folders = folders.Select(mapper.ToRemotePath).ToArray() };
        await foreach (var batch in PullAsync(session, RemoteMethods.SearchText, request, ct).ConfigureAwait(false))
            foreach (var m in batch.Matches ?? [])
                yield return new RemoteTextMatch(mapper.FromRemotePath(m.Path), m.Line, m.Column, m.Length, m.Preview);
    }

    private static async IAsyncEnumerable<SearchBatch> PullAsync<T>(RemoteSession session, string method, T request, [EnumeratorCancellation] CancellationToken ct)
    {
        var peer = session.Peer;
        var started = await peer.InvokeAsync<T, SearchSession>(method, request, ct).ConfigureAwait(false) ?? throw new IOException("Empty answer starting a remote search");
        var finished = false;
        try
        {
            while (true)
            {
                var batch = await peer.InvokeAsync<SearchNextParams, SearchBatch>(RemoteMethods.SearchNext, new SearchNextParams(started.SessionId, BatchSize), ct, Timeout.InfiniteTimeSpan)
                    .ConfigureAwait(false) ?? throw new IOException("Empty search batch");
                if (batch.Error is { } error && batch.Done && batch.Paths is null && batch.Matches is null) throw new IOException("Remote search failed: " + error);
                yield return batch;
                if (batch.Done) { finished = true; yield break; }
            }
        }
        finally
        {
            if (!finished) peer.Fire(RemoteMethods.SearchCancel, started); // abandoned or cancelled: stop the walk on the server
        }
    }
}
