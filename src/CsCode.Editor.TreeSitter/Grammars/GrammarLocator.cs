using CsCode.Editor.TreeSitter.Native;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>Where one grammar's compiled parser and query directories were found.</summary>
/// <param name="QueryDirectories">Most preferred first; a query file is taken from the first directory that has it.</param>
/// <param name="Source">Human-readable origin ("bundled", the root directory, "nix store").</param>
/// <param name="Bundle">The app's bundle when the parser comes from it: the library is checked against the bundle's digest before it is loaded.</param>
public sealed record GrammarFiles(string Name, string ParserPath, IReadOnlyList<string> QueryDirectories, string Source, TreeSitterBundle? Bundle = null)
{
    /// <summary>The search root the parser was found under (its queries are preferred), or null (Nix store).</summary>
    public string? Root { get; init; }
}

/// <summary>
/// Finds compiled grammars and their queries, read-only. Search order (docs/TREE-SITTER.md, "Distribution"):
/// <list type="number">
/// <item>the app's bundle (<c>&lt;app&gt;/tree-sitter/grammars</c>, built from the pinned sources by <c>tools/treesitter/build-grammars.sh</c>);</item>
/// <item>the user's <c>{UserDataDir}/tree-sitter/grammars</c>;</item>
/// <item><c>CSCODE_TREESITTER_GRAMMARS</c> (a path list);</item>
/// </list>
/// where a grammar is <c>&lt;root&gt;/&lt;name&gt;.so</c> (or <c>&lt;root&gt;/&lt;name&gt;/parser</c>, <c>libtree-sitter-&lt;name&gt;.so</c>…) with queries in
/// <c>&lt;root&gt;/queries/&lt;name&gt;/*.scm</c> or <c>&lt;root&gt;/&lt;name&gt;/queries/*.scm</c>; then, FOR DEVELOPMENT ONLY (an app without a bundle,
/// or <c>CSCODE_TREESITTER_NIX=1</c>: <see cref="TreeSitterBundle.NixStoreFallback"/>), the Nix store: <c>/nix/store/*-tree-sitter-&lt;name&gt;-0.0.0+rev=*</c>
/// (the revisions nvim-treesitter pins) or a versioned package, with queries from the nvim-treesitter plugin in the store before the grammar
/// package's own <c>queries/</c>. Nothing is downloaded, built or installed here.
/// Thread-safe; results are cached. The Nix listing is done once per process (≈ 100k store entries, tens of ms) on the calling lane.
/// </summary>
public sealed class GrammarLocator
{
    private readonly string[] _roots;
    private readonly bool _useNixStore;
    private readonly TreeSitterBundle? _bundle;
    private readonly object _gate = new();
    private readonly Dictionary<string, GrammarFiles?> _grammars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _queryDirs = new(StringComparer.Ordinal);

    /// <param name="roots">Searched in order.</param>
    /// <param name="bundle">When one of <paramref name="roots"/> is this bundle's grammar root, grammars found there are labelled "bundled" and verified before use.</param>
    public GrammarLocator(IEnumerable<string> roots, bool useNixStore = true, TreeSitterBundle? bundle = null)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        _useNixStore = useNixStore && OperatingSystem.IsLinux();
        _bundle = bundle;
    }

    /// <summary>The production order: the app's bundle, <paramref name="userGrammarDir"/>, <c>CSCODE_TREESITTER_GRAMMARS</c>; the Nix store only without a bundle.</summary>
    public static GrammarLocator CreateDefault(string? userGrammarDir) =>
        new(DefaultRoots(TreeSitterBundle.Default, userGrammarDir), TreeSitterBundle.NixStoreFallback, TreeSitterBundle.Default);

    /// <summary>The explicit roots: the bundle's grammars, then <paramref name="userGrammarDir"/>, then each entry of <c>CSCODE_TREESITTER_GRAMMARS</c>.</summary>
    public static IEnumerable<string> DefaultRoots(TreeSitterBundle? bundle, string? userGrammarDir)
    {
        if (bundle is not null) yield return bundle.GrammarsRoot;
        if (!string.IsNullOrEmpty(userGrammarDir)) yield return userGrammarDir!;
        if (Environment.GetEnvironmentVariable("CSCODE_TREESITTER_GRAMMARS") is { Length: > 0 } env)
            foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return p;
    }

    /// <summary>The roots searched before the Nix store, in order.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>True when the Nix store is searched after the roots (development only).</summary>
    public bool UsesNixStore => _useNixStore;

    /// <summary>
    /// True when <paramref name="name"/> has the shape of a grammar or query name (<c>c_sharp</c>, <c>markdown_inline</c>, <c>html_tags</c>):
    /// ASCII lower-case letters, digits and underscores, at most 64 of them. Never a path: no separator, no dot, nothing rooted, so a name
    /// built into <c>Path.Combine(root, name, …)</c> stays under that root. Names come from document text too (a Markdown fence, an rst
    /// <c>code-block</c> argument, an HTML <c>&lt;script type&gt;</c>), so every lookup checks this first.
    /// </summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 64) return false;
        foreach (var c in name)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a valid name (<see cref="IsValidName"/>) of a grammar this locator can find: one listed under a
    /// search root (a directory or a library named after it) or, when the Nix store is searched, a grammar package there. The listing is
    /// taken on first use (on the calling lane), so a name the document invents never makes it probe the file system or grow a cache: what a
    /// document can ask for is bounded by what is installed. It is taken again on a miss when a search root changed since (a grammar copied
    /// into the user's folder while CsCode runs): at once for a document's own language (<paramref name="throttled"/> false, the registry's
    /// lookups), at most once a second for names that come from document text (injections).
    /// </summary>
    public bool IsKnown(string name, bool throttled = true)
    {
        if (!IsValidName(name)) return false;
        lock (_gate)
        {
            if (_known is null)
            {
                _knownStamps = RootStamps();
                _rootsCheckedAt = Environment.TickCount64;
                _known = ListKnown();
                return _known.Contains(name);
            }
            if (_known.Contains(name)) return true;
            var now = Environment.TickCount64;
            if (throttled && now - _rootsCheckedAt < 1000) return false;
            _rootsCheckedAt = now;
            var stamps = RootStamps();
            if (stamps.AsSpan().SequenceEqual(_knownStamps)) return false;
            _knownStamps = stamps;
            _known = ListKnown();
            // what was looked for before the change is looked for again: a grammar or its queries may have arrived
            foreach (var missing in _grammars.Where(kv => kv.Value is null).Select(kv => kv.Key).ToArray()) _grammars.Remove(missing);
            _queryDirs.Clear();
            return _known.Contains(name);
        }
    }

    private HashSet<string>? _known;
    private HashSet<string>? _nixKnown; // the Nix store's grammar packages (listed once: ≈ 100k entries)
    private DateTime[] _knownStamps = [];
    private long _rootsCheckedAt;

    /// <summary>The modification time of each search root (adding or removing a grammar library or directory under it changes it).</summary>
    private DateTime[] RootStamps()
    {
        var stamps = new DateTime[_roots.Length];
        for (var i = 0; i < _roots.Length; i++)
        {
            try
            {
                stamps[i] = Directory.GetLastWriteTimeUtc(_roots[i]); // (a missing root: a fixed year-1601 value, so creating it is a change)
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stamps[i] = default;
            }
        }
        return stamps;
    }

    private HashSet<string> ListKnown()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (_bundle is not null)
            foreach (var g in _bundle.Grammars)
                if (IsValidName(g.Name)) known.Add(g.Name);
        foreach (var root in _roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    var file = Path.GetFileName(entry);
                    if (file.EndsWith(LibraryExtension, StringComparison.Ordinal))
                    {
                        file = file[..^LibraryExtension.Length];
                        if (file.StartsWith("libtree-sitter-", StringComparison.Ordinal)) file = file["libtree-sitter-".Length..];
                    }
                    if (IsValidName(file)) known.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an unreadable root contributes nothing
            }
        }
        if (_useNixStore) known.UnionWith(_nixKnown ??= ListNixKnown());
        return known;
    }

    private static HashSet<string> ListNixKnown()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in NixStore.Entries())
        {
            // <hash>-tree-sitter-<name>-<version>, the name possibly dashed ("c-sharp"); the version starts with a digit
            var file = Path.GetFileName(dir);
            var dash = file.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0 || file.EndsWith(".drv", StringComparison.Ordinal)) continue;
            var rest = file.AsSpan(dash + 1);
            if (!rest.StartsWith("tree-sitter-", StringComparison.Ordinal)) continue;
            rest = rest["tree-sitter-".Length..];
            for (var i = 1; i + 1 < rest.Length; i++)
            {
                if (rest[i] != '-' || !char.IsAsciiDigit(rest[i + 1])) continue;
                var name = rest[..i].ToString().Replace('-', '_');
                if (IsValidName(name)) known.Add(name);
                break;
            }
        }
        return known;
    }

    public GrammarFiles? Find(string grammarName)
    {
        if (!IsValidName(grammarName)) return null; // never a path: nothing is probed or cached for it
        lock (_gate)
        {
            if (_grammars.TryGetValue(grammarName, out var cached)) return cached;
            var found = FindUncached(grammarName);
            _grammars[grammarName] = found;
            return found;
        }
    }

    /// <summary>Query directories for <paramref name="name"/> (which need not be a grammar: nvim-treesitter has query-only names like <c>ecma</c>, <c>jsx</c>, <c>html_tags</c>).</summary>
    public IReadOnlyList<string> QueryDirectories(string name)
    {
        if (!IsValidName(name)) return [];
        lock (_gate)
        {
            if (_queryDirs.TryGetValue(name, out var cached)) return cached;
            var dirs = new List<string>();
            foreach (var root in _roots)
            {
                AddIfDir(dirs, Path.Combine(root, name, "queries"));
                AddIfDir(dirs, Path.Combine(root, "queries", name));
            }
            if (_useNixStore)
            {
                foreach (var plugin in NvimTreesitterPlugins()) AddIfDir(dirs, Path.Combine(plugin, "runtime", "queries", name));
                foreach (var pkg in NixGrammarPackages(name)) AddIfDir(dirs, Path.Combine(pkg, "queries"));
            }
            _queryDirs[name] = dirs;
            return dirs;
        }
    }

    /// <summary>
    /// <see cref="QueryDirectories(string)"/> with the directories under <paramref name="preferredRoot"/> first: the queries that ship with a
    /// grammar build are the ones written for it (a user's newer grammar with its own queries must not get the bundle's, and vice versa).
    /// </summary>
    public IReadOnlyList<string> QueryDirectories(string name, string? preferredRoot)
    {
        var dirs = QueryDirectories(name);
        if (preferredRoot is null) return dirs;
        var prefix = preferredRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return dirs.OrderBy(d => d.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1).ToArray(); // stable
    }

    private GrammarFiles? FindUncached(string name)
    {
        foreach (var root in _roots)
        {
            string[] candidates =
            [
                Path.Combine(root, name, "parser"),
                Path.Combine(root, name, name + LibraryExtension),
                Path.Combine(root, name, "libtree-sitter-" + name + LibraryExtension),
                Path.Combine(root, "libtree-sitter-" + name + LibraryExtension),
                Path.Combine(root, name + LibraryExtension),
            ];
            foreach (var c in candidates)
            {
                if (!File.Exists(c)) continue;
                var bundled = _bundle is not null && string.Equals(root, _bundle.GrammarsRoot, StringComparison.Ordinal);
                return new GrammarFiles(name, c, QueryDirectories(name, root), bundled ? "bundled" : root, bundled ? _bundle : null) { Root = root };
            }
        }
        if (_useNixStore)
        {
            foreach (var pkg in NixGrammarPackages(name))
            {
                var parser = Path.Combine(pkg, "parser");
                if (File.Exists(parser)) return new GrammarFiles(name, parser, QueryDirectories(name), "nix store");
            }
        }
        return null;
    }

    private static string LibraryExtension => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    private static void AddIfDir(List<string> dirs, string dir)
    {
        if (!dirs.Contains(dir) && Directory.Exists(dir)) dirs.Add(dir);
    }

    /// <summary>
    /// Store packages of grammar <paramref name="name"/>: the nvim-treesitter-pinned builds ("tree-sitter-&lt;name&gt;-0.0.0+rev=...") first, then
    /// versioned ones (also under the dashed name: "c-sharp"). Sorted for a deterministic choice.
    /// </summary>
    internal static IEnumerable<string> NixGrammarPackages(string name)
    {
        var names = name.Contains('_', StringComparison.Ordinal) ? new[] { name, name.Replace('_', '-') } : [name];
        var pinned = new List<string>();
        var versioned = new List<string>();
        foreach (var dir in NixStore.Entries())
        {
            var file = Path.GetFileName(dir);
            var dash = file.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0 || file.EndsWith(".drv", StringComparison.Ordinal)) continue;
            var rest = file[(dash + 1)..];
            foreach (var n in names)
            {
                var prefix = "tree-sitter-" + n + "-";
                if (!rest.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var version = rest[prefix.Length..];
                if (version.StartsWith("0.0.0+rev=", StringComparison.Ordinal)) pinned.Add(dir);
                else if (version.Length > 0 && char.IsAsciiDigit(version[0])) versioned.Add(dir);
            }
        }
        pinned.Sort(StringComparer.Ordinal);
        versioned.Sort(StringComparer.Ordinal);
        return pinned.Concat(versioned);
    }

    /// <summary>nvim-treesitter plugin directories in the store, newest (by name, which carries the date) first.</summary>
    internal static IEnumerable<string> NvimTreesitterPlugins() =>
        NixStore.Entries()
            .Where(d => Path.GetFileName(d).Contains("-vimplugin-nvim-treesitter-", StringComparison.Ordinal) && !d.EndsWith(".drv", StringComparison.Ordinal))
            .Where(d => Directory.Exists(Path.Combine(d, "runtime", "queries")))
            .OrderByDescending(d => Path.GetFileName(d)[(Path.GetFileName(d).IndexOf('-', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);
}
