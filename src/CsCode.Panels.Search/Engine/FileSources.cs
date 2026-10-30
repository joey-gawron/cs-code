using System.Runtime.CompilerServices;
using System.Text;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Search.Engine;

/// <summary>What a file source needs to know to enumerate candidate files.</summary>
public sealed record FileSourceRequest(
    IReadOnlyList<Uri> Roots,
    RootSet RootSet,
    bool UseExcludeSettings,
    bool UseIgnoreFiles,
    GlobFilter Include,
    GlobFilter Exclude,
    GlobFilter SettingsExclude);

/// <summary>
/// Streams the URIs of files that may contain matches. Implementations must honour cancellation promptly and never buffer the whole tree.
/// They are enumerated on the engine's coordinator thread and must await with <c>ConfigureAwait(true)</c>, so that their own work (filtering,
/// ignore files) resumes there: the real file system completes on its IO lane with asynchronous continuations, and <c>ConfigureAwait(false)</c>
/// would move that work to the shared thread pool.
/// </summary>
public interface ISearchFileSource
{
    IAsyncEnumerable<Uri> EnumerateAsync(FileSourceRequest request, CancellationToken ct);
}

/// <summary>
/// The platform's own file enumeration (<see cref="IWorkspaceService.FindFilesAsync"/>), which already applies <c>files.exclude</c> and
/// <c>.gitignore</c>. It walks every workspace folder, so the engine filters by the requested roots afterwards.
/// </summary>
public sealed class WorkspaceFileSource(IWorkspaceService workspace) : ISearchFileSource
{
    public async IAsyncEnumerable<Uri> EnumerateAsync(FileSourceRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var uri in workspace.FindFilesAsync(null, int.MaxValue, ct).ConfigureAwait(true))
            yield return uri;
    }
}

/// <summary>
/// Own directory walker over <see cref="IFileSystem"/>: used when "use exclude settings" or "use ignore files" is switched OFF (the
/// platform enumeration cannot be told to include ignored files). Depth-first, entries in name order (deterministic), one directory
/// listing in flight at a time, never follows directory symlinks (no cycles), always skips VCS metadata folders.
/// </summary>
public sealed class WalkingFileSource(IFileSystem fileSystem) : ISearchFileSource
{
    private static readonly HashSet<string> VcsFolders = new(StringComparer.Ordinal) { ".git", ".svn", ".hg" };

    private sealed record IgnoreNode(string Directory, IgnoreRules Rules, IgnoreNode? Parent);

    private sealed record Frame(Uri Uri, string Rel, IgnoreNode? Ignore);

    public async IAsyncEnumerable<Uri> EnumerateAsync(FileSourceRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var root in request.Roots)
        {
            var rootUri = EnsureSlash(root);
            var ignoreCase = request.UseIgnoreFiles && await IgnoresCaseAsync(rootUri, ct).ConfigureAwait(true);
            var stack = new Stack<Frame>();
            stack.Push(new Frame(rootUri, "/", null));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var frame = stack.Pop();
                IReadOnlyList<DirectoryEntry> entries;
                try { entries = await fileSystem.ReadDirectoryAsync(frame.Uri, ct).ConfigureAwait(true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { continue; }

                var ignore = frame.Ignore;
                if (request.UseIgnoreFiles && entries.FirstOrDefault(static e => e.Name == ".gitignore" && (e.Type & FileType.File) != 0) is { } gi)
                {
                    try
                    {
                        var bytes = await fileSystem.ReadFileAsync(ChildUri(frame.Uri, ".gitignore", false), ct).ConfigureAwait(true);
                        var rules = IgnoreRules.Parse(Encoding.UTF8.GetString(bytes), ignoreCase);
                        if (rules.Count > 0) ignore = new IgnoreNode(frame.Rel, rules, ignore);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable .gitignore ignores nothing */ }
                }

                var subdirs = new List<Frame>();
                foreach (var entry in entries)
                {
                    var isDir = (entry.Type & FileType.Directory) != 0;
                    var isFile = (entry.Type & FileType.File) != 0;
                    if (!isDir && !isFile) continue;                                    // dangling link, socket...
                    if (isDir && (entry.Type & FileType.SymbolicLink) != 0) continue;  // never follow directory links
                    if (isDir && VcsFolders.Contains(entry.Name)) continue;

                    var rel = frame.Rel == "/" ? "/" + entry.Name : frame.Rel + "/" + entry.Name;
                    if (request.UseExcludeSettings && request.SettingsExclude.IsMatch(rel)) continue;
                    if (request.Exclude.IsMatch(rel)) continue;
                    if (ignore is not null && IsIgnored(ignore, rel, isDir)) continue;

                    if (isDir) subdirs.Add(new Frame(ChildUri(frame.Uri, entry.Name, true), rel, ignore));
                    else if (request.Include.IsEmpty || request.Include.IsMatch(rel)) yield return ChildUri(frame.Uri, entry.Name, false);
                }
                for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]); // pop in name order
            }
        }
    }

    /// <summary>
    /// core.ignorecase of the repository at <paramref name="root"/> (its own git config), else the platform's rule for a local folder (a Mac
    /// ignores case) and case-sensitive for any other scheme, whose host's file system is not known here.
    /// </summary>
    private async Task<bool> IgnoresCaseAsync(Uri root, CancellationToken ct)
    {
        var fallback = root.IsFile && GitIgnoreCase.PlatformDefault;
        try
        {
            var config = await fileSystem.ReadFileAsync(ChildUri(ChildUri(root, ".git", true), "config", false), ct).ConfigureAwait(true);
            return GitIgnoreCase.Parse(Encoding.UTF8.GetString(config)) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return fallback; }
    }

    private static bool IsIgnored(IgnoreNode node, string rel, bool isDir)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            var sub = n.Directory == "/" ? rel[1..] : rel[(n.Directory.Length + 1)..];
            if (n.Rules.Evaluate(sub, isDir) is { } verdict) return verdict;
        }
        return false;
    }

    /// <summary>
    /// Child of a directory URI (which ends with '/'), built from the absolute string. <c>new Uri(base, relative)</c> must not be used: for a
    /// base made from a plain file path it re-escapes an already escaped name ("%23" becomes "%2523"), so "#" or "%" in a name broke the file.
    /// </summary>
    internal static Uri ChildUri(Uri directory, string name, bool isDirectory) =>
        new(directory.AbsoluteUri + Uri.EscapeDataString(name) + (isDirectory ? "/" : ""), UriKind.Absolute);

    internal static Uri EnsureSlash(Uri uri)
    {
        var s = uri.AbsoluteUri;
        return s.EndsWith('/') ? uri : new Uri(s + "/");
    }
}

/// <summary>Chooses the platform enumeration when its semantics (excludes + gitignore ON) are what was asked for, the walker otherwise.</summary>
public sealed class DefaultFileSource(IWorkspaceService workspace, ISearchFileSource? workspaceSource = null, ISearchFileSource? walker = null) : ISearchFileSource
{
    private readonly ISearchFileSource _workspace = workspaceSource ?? new WorkspaceFileSource(workspace);
    private readonly ISearchFileSource _walker = walker ?? new WalkingFileSource(workspace.FileSystem);

    public IAsyncEnumerable<Uri> EnumerateAsync(FileSourceRequest request, CancellationToken ct) =>
        request.UseExcludeSettings && request.UseIgnoreFiles ? _workspace.EnumerateAsync(request, ct) : _walker.EnumerateAsync(request, ct);
}
