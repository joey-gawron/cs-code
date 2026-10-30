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
public sealed class RemoteSearch(IRemoteEndpoint endpoint) : IFileSearchProvider
{
    public const int BatchSize = 1000;

    public string Scheme => SshUriMapper.Scheme;

    public async IAsyncEnumerable<Uri> FindFilesAsync(Uri folder, FileSearchOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (session, mapper) = endpoint.Resolve(folder);
        var request = new SearchFilesParams(mapper.ToRemotePath(folder), options.IncludeGlob, options.ExcludeGlobs, options.UseIgnoreFiles, options.FollowSymlinks, options.MaxResults);
        await foreach (var batch in PullAsync(session, RemoteMethods.SearchFiles, request, ct).ConfigureAwait(false))
            foreach (var path in batch.Paths ?? [])
                yield return mapper.FromRemotePath(path);
    }

    /// <summary>Text search in <paramref name="folders"/> (ssh: URIs of the connected host). Matches stream in as the server finds them.</summary>
    public async IAsyncEnumerable<RemoteTextMatch> SearchTextAsync(IReadOnlyList<Uri> folders, SearchTextParams query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (folders.Count == 0) yield break;
        var (session, mapper) = endpoint.Resolve(folders[0]);
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
