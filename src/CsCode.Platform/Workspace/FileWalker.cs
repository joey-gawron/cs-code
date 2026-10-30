using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;
using CsCode.Core.Text;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;

namespace CsCode.Platform.Workspace;

internal sealed record WalkOptions(
    string? IncludeGlob,
    IReadOnlyList<string> ExcludeGlobs,
    bool UseIgnoreFiles,
    bool FollowSymlinks,
    int Degree,
    int MaxResults);

/// <summary>
/// Streaming, parallel, cancellable directory walk for "find files". Designed for the IO lane's rules:
/// <list type="bullet">
/// <item><b>Cooperative.</b> The walk is a handful of small "pump" work items on the IO lane (≤ 3 ms each, then they re-enqueue themselves), so a
/// huge tree never monopolises a lane thread and other file operations interleave. Parallelism is bounded by <see cref="WalkOptions.Degree"/>.</item>
/// <item><b>Back-pressure.</b> Results go through a bounded channel. When the consumer is slow the walk PARKS (no thread blocked, nothing queued)
/// and resumes when the consumer reads again.</item>
/// <item><b>Memory is O(depth × fan-out), never O(files).</b> Directories wait on a LIFO stack (depth-first), each holding only a lazy enumerator.</item>
/// <item><b>Cancellation</b> is checked per entry; a pump exits within one entry (plus whatever a single readdir takes) of the token firing.</item>
/// <item><b>Symlink-loop safe</b>: a link to a directory is followed only if its real path is not an ancestor's; links are never followed when disabled.</item>
/// <item>Honours <c>files.exclude</c>, nested <c>.gitignore</c> files (with negations), <c>.git/info/exclude</c>; never enters <c>.git</c>/<c>.hg</c>/<c>.svn</c>.</item>
/// </list>
/// </summary>
internal sealed class FileWalker : IDisposable
{
    private const long SliceTicks = 3; // milliseconds, converted below
    private const int MaxIgnoreFileBytes = 1024 * 1024;

    private readonly record struct Entry(string Name, bool IsDir, bool IsLink);

    private sealed class Ancestor(string realPath, Ancestor? parent)
    {
        public string RealPath { get; } = realPath;
        public Ancestor? Parent { get; } = parent;
    }

    private sealed class DirWork
    {
        public required string Path { get; init; }
        public required string Rel { get; init; }
        public required string RealPath { get; init; }
        public required Ancestor? Ancestors { get; init; }
        public IgnoreChain? Chain { get; set; }
        /// <summary>The root's repository matches ignore rules case-insensitively (core.ignorecase), decided once per root.</summary>
        public bool IgnoreCase { get; set; }
        public bool IsRoot { get; init; }
        public bool Started { get; set; }
        public IEnumerator<Entry>? Enumerator { get; set; }
        public Uri? PendingUri { get; set; }
    }

    private readonly IoLane _lane;
    private readonly WalkOptions _options;
    private readonly ExcludeMatcher _excludes;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<Uri> _out = Channel.CreateBounded<Uri>(new BoundedChannelOptions(1024)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object _gate = new();
    private readonly Stack<DirWork> _stack = new();
    private readonly long _sliceStopwatchTicks = Stopwatch.Frequency * SliceTicks / 1000;
    private int _pumps;
    private int _busy;
    private int _reserved;
    private bool _parked;
    private bool _finished;
    private Exception? _fault;
    private int _peakPending;

    public FileWalker(IoLane lane, IReadOnlyList<string> roots, WalkOptions options, CancellationToken ct)
    {
        _lane = lane;
        _options = options;
        _excludes = new ExcludeMatcher("", options.ExcludeGlobs);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var root in roots.Reverse()) // LIFO: the first root is walked first
            _stack.Push(new DirWork { Path = root, Rel = "", RealPath = RealPath(root), Ancestors = null, IsRoot = true });
        if (_stack.Count == 0) { Complete(); return; }

        lock (_gate) SpawnPumpsLocked();
    }

    public ChannelReader<Uri> Reader => _out.Reader;

    /// <summary>Largest number of directories ever waiting at once (diagnostics/tests: shows memory grows with depth × fan-out, not with file count).</summary>
    public int PeakPendingDirectories { get { lock (_gate) return _peakPending; } }

    /// <summary>An unexpected failure on the IO lane (the walk is then complete); null otherwise.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>The consumer read something: if the walk parked because the channel was full, wake it.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_parked || _finished) return;
            _parked = false;
            SpawnPumpsLocked();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            _stack.Clear();
            _parked = false;
        }
        Complete();
    }

    // ---- scheduling --------------------------------------------------------------------------------------------------------------

    private void SpawnPumpsLocked()
    {
        while (_pumps < _options.Degree && _pumps < _stack.Count + _busy && !_parked && !_finished)
        {
            _pumps++;
            _lane.Post(Pump, OnPumpFailed);
        }
    }

    private void OnPumpFailed(Exception ex)
    {
        Interlocked.CompareExchange(ref _fault, ex, null);
        Dispose();
    }

    private void Complete()
    {
        lock (_gate) _finished = true;
        _out.Writer.TryComplete();
    }

    private void Pump()
    {
        var started = Stopwatch.GetTimestamp();
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() - started < _sliceStopwatchTicks)
            {
                DirWork work;
                lock (_gate)
                {
                    if (_stack.Count == 0 || _parked || _finished) break;
                    work = _stack.Pop();
                    _busy++;
                }

                var done = false;
                try { done = Process(work, started, ct); }
                finally
                {
                    lock (_gate)
                    {
                        _busy--;
                        if (!done && !_finished) _stack.Push(work); // time slice over, or parked on a full channel: continue this directory later
                        SpawnPumpsLocked();                         // children may have been discovered: wake idle pumps
                    }
                }
            }
        }
        finally
        {
            bool again, complete;
            lock (_gate)
            {
                _pumps--;
                var stopped = ct.IsCancellationRequested || _finished;
                again = !stopped && !_parked && _stack.Count > 0;
                if (again) _pumps++;
                // done when nothing is queued, nobody is mid-directory and no other pump is alive (a parked walk is NOT done: its work is on the stack)
                complete = stopped ? _pumps == 0 : _stack.Count == 0 && _busy == 0 && _pumps == 0 && !_parked;
            }
            if (again) _lane.Post(Pump, OnPumpFailed);
            else if (complete) Complete();
        }
    }

    // ---- one directory -----------------------------------------------------------------------------------------------------------

    /// <summary>Processes entries of one directory until it is exhausted (true), the time slice ends or the channel is full (false: resume later).</summary>
    private bool Process(DirWork w, long sliceStart, CancellationToken ct)
    {
        if (!w.Started)
        {
            w.Started = true;
            if (_options.UseIgnoreFiles) w.Chain = LoadIgnoreChain(w);
            try
            {
                var enumOptions = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
                w.Enumerator = new FileSystemEnumerable<Entry>(w.Path, static (ref FileSystemEntry e) =>
                    new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), enumOptions).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // unreadable directory: skip it
        }

        if (w.PendingUri is { } pending)
        {
            if (!TryEmit(pending, out var finishedNow)) return Park(w, false);
            w.PendingUri = null;
            if (finishedNow) { w.Enumerator?.Dispose(); return true; }
        }

        var en = w.Enumerator!;
        var sinceCheck = 0;
        while (true)
        {
            if (ct.IsCancellationRequested) { en.Dispose(); return true; }
            if (++sinceCheck >= 32)
            {
                sinceCheck = 0;
                if (Stopwatch.GetTimestamp() - sliceStart >= _sliceStopwatchTicks) return false; // yield the lane thread
            }

            bool has;
            try { has = en.MoveNext(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { has = false; }
            if (!has) { en.Dispose(); return true; }

            var entry = en.Current;
            var rel = w.Rel.Length == 0 ? entry.Name : w.Rel + "/" + entry.Name;

            if (entry.IsDir)
            {
                if (entry.Name is ".git" or ".hg" or ".svn") continue;
                if (IsExcluded(w, rel, isDir: true)) continue;
                var child = MakeChild(w, entry, rel);
                if (child is null) continue;
                lock (_gate) { _stack.Push(child); if (_stack.Count > _peakPending) _peakPending = _stack.Count; }
            }
            else
            {
                if (IsExcluded(w, rel, isDir: false)) continue;
                if (_options.IncludeGlob is { } glob && !Glob.IsMatch(glob, rel)) continue;
                var full = Path.Combine(w.Path, entry.Name);
                if (entry.IsLink && !LinkTargetExists(full)) continue; // dangling link: nothing to open
                var uri = FileUri.FromPath(full);

                if (!TryEmit(uri, out var finishedNow))
                {
                    w.PendingUri = uri;
                    return Park(w, false);
                }
                if (finishedNow) { en.Dispose(); return true; }
            }
        }
    }

    private bool Park(DirWork w, bool done)
    {
        lock (_gate) _parked = true;
        return done; // false: the caller pushes the work back
    }

    /// <summary>Writes one result. False = the channel is full (park). <paramref name="finished"/> = the result limit was reached.</summary>
    private bool TryEmit(Uri uri, out bool finished)
    {
        finished = false;
        var n = Interlocked.Increment(ref _reserved);
        if (n > _options.MaxResults) { Interlocked.Decrement(ref _reserved); finished = true; StopAll(); return true; }
        if (!_out.Writer.TryWrite(uri)) { Interlocked.Decrement(ref _reserved); return false; }
        if (n == _options.MaxResults) { finished = true; StopAll(); }
        return true;
    }

    private void StopAll()
    {
        _cts.Cancel();
        lock (_gate) _stack.Clear();
    }

    private bool IsExcluded(DirWork w, string rel, bool isDir)
    {
        if (!_excludes.IsEmpty && _excludes.IsExcludedRelative(rel)) return true;
        return _options.UseIgnoreFiles && IgnoreChain.IsIgnored(w.Chain, rel, isDir);
    }

    private DirWork? MakeChild(DirWork parent, Entry entry, string rel)
    {
        var path = Path.Combine(parent.Path, entry.Name);
        string real;
        if (!entry.IsLink) real = Path.Combine(parent.RealPath, entry.Name);
        else
        {
            if (!_options.FollowSymlinks) return null;
            // follow a directory link only when it does not lead back into the chain we came down (that would loop forever)
            var target = RealPath(path);
            if (!Directory.Exists(target)) return null;
            for (var a = new Ancestor(parent.RealPath, parent.Ancestors); a is not null; a = a.Parent)
                if (string.Equals(a.RealPath, target, StringComparison.Ordinal) || IsInside(a.RealPath, target)) return null;
            real = target;
        }
        return new DirWork
        {
            Path = path, Rel = rel, RealPath = real, Ancestors = new Ancestor(parent.RealPath, parent.Ancestors), Chain = parent.Chain, IgnoreCase = parent.IgnoreCase,
        };
    }

    // `target` is an ancestor if it equals it or contains it (a link to a parent directory of where we are).
    private static bool IsInside(string deeper, string possibleAncestor) =>
        deeper.StartsWith(possibleAncestor.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="path"/> with every link in it resolved, as realpath(3) gives it. The ancestors' real paths and a link's target must be
    /// compared in the same form: a root reached through a link (on macOS /tmp and /var are links to /private/...) never equalled a target, so a
    /// link back to the root was followed one level before the loop was seen (files-15). A ".." is applied to what is resolved so far, never to
    /// the text: in a target "other/.." where other is a link, it leaves the directory other leads to, not the one holding other. A part that
    /// is not there, cannot be read or loops keeps its unresolved rest. Never throws.
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path); // not GetFullPath: it collapses ".." as text
        var real = Path.GetPathRoot(full) ?? "";
        var hops = 0;
        var rest = new Queue<string>(Parts(full, real.Length));
        while (rest.TryDequeue(out var part))
        {
            if (part == ".") continue;
            if (part == "..") { real = Path.GetDirectoryName(real) ?? real; continue; } // the root's parent is the root
            var next = Path.Combine(real, part);
            string? link;
            try { link = new DirectoryInfo(next).LinkTarget; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { link = null; } // unreadable: taken as it is
            if (link is null) { real = next; continue; }
            if (++hops > 40) return Path.Combine([next, .. rest]); // ELOOP: leave the rest as it is
            // the link's target replaces it: an absolute one from its own root, a relative one from the (resolved) directory holding the link
            var targetRoot = Path.IsPathRooted(link) ? Path.GetPathRoot(link) ?? "" : "";
            rest = new Queue<string>(Parts(link, targetRoot.Length).Concat(rest));
            if (targetRoot.Length > 0) real = targetRoot;
        }
        return real;

        static IEnumerable<string> Parts(string p, int skip) =>
            p[skip..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(x => x.Length > 0);
    }

    private static bool LinkTargetExists(string path)
    {
        try
        {
            var final = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return final is not null && (File.Exists(final.FullName) || Directory.Exists(final.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- ignore files ------------------------------------------------------------------------------------------------------------

    /// <summary>The chain for a directory = what the parent had + this directory's own .gitignore (and, for a root, .git/info/exclude beneath it).</summary>
    private IgnoreChain? LoadIgnoreChain(DirWork w)
    {
        var chain = w.Chain;
        if (w.IsRoot)
        {
            w.IgnoreCase = GitIgnoreFile.RepositoryIgnoresCase(w.Path);
            var info = TryParse(System.IO.Path.Combine(w.Path, ".git", "info", "exclude"), w.IgnoreCase);
            if (info is not null) chain = new IgnoreChain(info, "", chain);
        }
        var file = TryParse(System.IO.Path.Combine(w.Path, ".gitignore"), w.IgnoreCase);
        return file is null ? chain : new IgnoreChain(file, w.Rel, chain);
    }

    private static GitIgnoreFile? TryParse(string path, bool ignoreCase)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxIgnoreFileBytes) return null;
            var file = GitIgnoreFile.Parse(File.ReadAllText(path), ignoreCase);
            return file.RuleCount == 0 ? null : file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
