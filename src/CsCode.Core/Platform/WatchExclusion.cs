using System.Text;
using CsCode.Core.Text;

namespace CsCode.Core.Platform;

/// <summary>
/// What a file watch leaves out, as one definition for the watcher and for the modules that ask it for more (<see cref="FileWatchWants"/>):
/// <list type="bullet">
/// <item>Exclude globs, matched against the root-relative path. A directory is excluded <i>as a directory</i> (its contents are left out, so
/// the OS need not watch it) when the glob matches its path or its path with a trailing <c>/</c> (<c>**/node_modules/**</c> matches
/// <c>node_modules/</c>); an event is excluded by its own path alone, so a folder like <c>bin/</c> appearing or going is still reported.</item>
/// <item>Cache directories (<see cref="IsCacheDirectory"/>): a directory holding a valid <c>CACHEDIR.TAG</c> (<see href="https://bford.info/cachedir/"/>;
/// Cargo writes one in every <c>target/</c>, as do pytest, mypy and ruff in their caches) has its contents left out for every subscription,
/// whatever its name — a source folder called <c>target/</c> without the tag stays watched. Except a Python virtual environment (it holds
/// <c>pyvenv.cfg</c>): uv tags the <c>.venv</c> it creates, and language servers (Pyright) resolve imports from it. Found where the watcher
/// walks the disk (the Linux inotify walk, the polling fallback, the scan of a new directory).</item>
/// </list>
/// </summary>
public static class WatchExclusion
{
    /// <summary>
    /// The default of <c>files.watcherExclude</c>, and what a watch excludes when no configuration is at hand: version-control internals and
    /// the build and package output of .NET (bin/obj) and npm (node_modules). Rust, Maven and sbt output (target/) is left out by its cache tag.
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludes { get; } =
        ["**/.git/objects/**", "**/.git/subtree-cache/**", "**/.hg/store/**", "**/node_modules/**", "**/bin/**", "**/obj/**"];

    public const string CacheDirTagName = "CACHEDIR.TAG";

    /// <summary>The file that makes a directory a Python virtual environment (PEP 405).</summary>
    public const string VirtualEnvMarkerName = "pyvenv.cfg";

    private static readonly byte[] CacheDirTagSignature = Encoding.ASCII.GetBytes("Signature: 8a477f597d28d172789f06886806bc55");

    /// <summary>
    /// True when <paramref name="directory"/> is a cache directory a watch leaves out: it holds a valid tag (<see cref="HasCacheDirTag"/>) and
    /// is not a Python virtual environment (no <c>pyvenv.cfg</c>). Reads the disk; never blocks, never throws.
    /// </summary>
    public static bool IsCacheDirectory(string directory) =>
        HasCacheDirTag(directory) && !File.Exists(Path.Join(directory, VirtualEnvMarkerName));

    /// <summary>
    /// True when <paramref name="directory"/> holds a <c>CACHEDIR.TAG</c> that is a regular file starting with the standard signature. Never
    /// throws, and never blocks: a symlink (to a terminal, say) or a FIFO named like the tag is not read — only a regular file at least as
    /// long as the signature is opened (FIFOs and devices report no size).
    /// </summary>
    public static bool HasCacheDirTag(string directory)
    {
        var tag = Path.Join(directory, CacheDirTagName);
        try
        {
            // The common case, asked for every directory a walk meets: one lstat, no exception.
            var info = new FileInfo(tag);
            if (!info.Exists || info.LinkTarget is not null || info.Length < CacheDirTagSignature.Length) return false;
            using var file = new FileStream(tag, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            Span<byte> head = stackalloc byte[CacheDirTagSignature.Length];
            return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.SequenceEqual(CacheDirTagSignature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>The path of <paramref name="fullPath"/> relative to <paramref name="root"/>, with '/' separators ("" for the root itself).</summary>
    public static string Relative(string root, string fullPath)
    {
        root = root.TrimEnd('/', '\\');
        if (fullPath.Length >= root.Length && fullPath.StartsWith(root, StringComparison.Ordinal))
            return fullPath[root.Length..].TrimStart('/', '\\').Replace('\\', '/');
        return Path.GetRelativePath(root.Length == 0 ? "/" : root, fullPath).Replace('\\', '/');
    }

    /// <summary>A root-relative path matched as an entry: the glob must match the path itself. The root ("") is never excluded.</summary>
    public static bool IsExcludedEntry(string relative, IReadOnlyList<string> globs)
    {
        if (relative.Length == 0) return false;
        foreach (var g in globs)
            if (!string.IsNullOrWhiteSpace(g) && Glob.IsMatch(g, relative)) return true;
        return false;
    }

    /// <summary>A root-relative directory whose contents the globs leave out: the glob matches the path or the path with a trailing '/'.</summary>
    public static bool IsExcludedDirectory(string relative, IReadOnlyList<string> globs)
    {
        if (relative.Length == 0) return false;
        var asDir = relative + "/";
        foreach (var g in globs)
            if (!string.IsNullOrWhiteSpace(g) && (Glob.IsMatch(g, relative) || Glob.IsMatch(g, asDir))) return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="directory"/> (at or below <paramref name="root"/>) lies in a part of the tree that a watch with
    /// <paramref name="excludeGlobs"/> leaves out: it or one of its ancestors below the root is excluded as a directory or holds a cache tag.
    /// Such a directory is watched (and its changes reported) only when a subscription asks for it through <see cref="FileWatchWants"/>.
    /// Reads the disk (one stat per ancestor): call it off the UI thread.
    /// </summary>
    public static bool IsInExcludedArea(string root, string directory, IReadOnlyList<string> excludeGlobs)
    {
        root = root.TrimEnd('/', '\\');
        directory = directory.TrimEnd('/', '\\');
        var relative = Relative(root, directory);
        if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal)) return false;
        var segments = relative.Split('/');
        var path = root;
        var rel = "";
        foreach (var segment in segments)
        {
            path = Path.Join(path.Length == 0 ? "/" : path, segment);
            rel = rel.Length == 0 ? segment : rel + "/" + segment;
            if (IsExcludedDirectory(rel, excludeGlobs) || IsCacheDirectory(path)) return true;
        }
        return false;
    }
}

/// <summary>
/// Paths below a watch's root that its subscriber wants watched and reported although its excludes, the default excludes or a cache tag
/// (<see cref="WatchExclusion"/>) leave them out — without a watch (or an OS watch instance) of their own: the watch they are passed to walks
/// down to them and keeps them watched. A <i>directory</i> want covers its direct entries (an Explorer folder that is expanded); a <i>tree</i>
/// want its whole subtree (a language server's <c>RelativePattern</c> base, such as a build script's <c>OUT_DIR</c> under <c>target/</c>).
/// Live: <see cref="SetAsync"/> replaces the wanted paths, and the watch picks them up (and lets go of the ones no longer wanted). Watchers
/// that cannot leave anything out (a <see cref="System.IO.FileSystemWatcher"/>) apply wants to the events they report only; remote watches ignore them.
/// </summary>
public sealed class FileWatchWants
{
    private readonly object _gate = new();
    private Uri[] _directories = [];
    private Uri[] _trees = [];
    private Func<Task>[] _listeners = [];

    public FileWatchWants() { }

    public FileWatchWants(IEnumerable<Uri>? directories, IEnumerable<Uri>? trees)
    {
        _directories = directories?.ToArray() ?? [];
        _trees = trees?.ToArray() ?? [];
    }

    public IReadOnlyList<Uri> Directories => Volatile.Read(ref _directories);
    public IReadOnlyList<Uri> Trees => Volatile.Read(ref _trees);

    /// <summary>Replaces the wanted paths. Completes once every watch using this object has applied them (the OS watches are in place).</summary>
    public Task SetAsync(IEnumerable<Uri> directories, IEnumerable<Uri> trees)
    {
        Func<Task>[] listeners;
        lock (_gate)
        {
            Volatile.Write(ref _directories, directories.ToArray());
            Volatile.Write(ref _trees, trees.ToArray());
            listeners = _listeners;
        }
        return listeners.Length == 0 ? Task.CompletedTask : Task.WhenAll(listeners.Select(static l => l()));
    }

    /// <summary>For watch implementations: <paramref name="applied"/> runs after every change and returns when the change is applied.</summary>
    public IDisposable Subscribe(Func<Task> applied)
    {
        lock (_gate) _listeners = [.. _listeners, applied];
        return new Unsubscriber(this, applied);
    }

    private sealed class Unsubscriber(FileWatchWants owner, Func<Task> applied) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._listeners = owner._listeners.Where(l => !ReferenceEquals(l, applied)).ToArray();
        }
    }
}
