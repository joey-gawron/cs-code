using CsCode.Editor.TreeSitter.Native;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>Where one grammar's compiled parser and query directories were found.</summary>
/// <param name="QueryDirectories">Most preferred first; a query file is taken from the first directory that has it.</param>
/// <param name="Source">Human-readable origin ("user", "CSCODE_TREESITTER_GRAMMARS", "nix store").</param>
public sealed record GrammarFiles(string Name, string ParserPath, IReadOnlyList<string> QueryDirectories, string Source);

/// <summary>
/// Finds compiled grammars and their queries on the machine, read-only. Nothing is downloaded, built or installed (that is a product
/// decision for the user, docs/TREE-SITTER-EVALUATION.md §Distribution). Search order:
/// <list type="number">
/// <item>the explicit roots (the user's <c>{UserDataDir}/tree-sitter/grammars</c>, then <c>CSCODE_TREESITTER_GRAMMARS</c>, a path list) where a
/// grammar is <c>&lt;root&gt;/&lt;name&gt;/parser</c> (or <c>&lt;name&gt;.so</c>, <c>libtree-sitter-&lt;name&gt;.so</c>) with
/// <c>&lt;root&gt;/&lt;name&gt;/queries/*.scm</c> or <c>&lt;root&gt;/queries/&lt;name&gt;/*.scm</c>;</item>
/// <item>on a Nix machine, <c>/nix/store/*-tree-sitter-&lt;name&gt;-0.0.0+rev=*</c> (the revisions nvim-treesitter pins, which match its queries)
/// or any versioned <c>tree-sitter-&lt;name&gt;-*</c> package, with queries from the nvim-treesitter plugin in the store
/// (<c>runtime/queries/&lt;name&gt;</c>) before the grammar package's own <c>queries/</c>.</item>
/// </list>
/// Thread-safe; results are cached. The Nix listing is done once per process (≈ 100k store entries, tens of ms) on the calling lane.
/// </summary>
public sealed class GrammarLocator
{
    private readonly string[] _roots;
    private readonly bool _useNixStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, GrammarFiles?> _grammars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _queryDirs = new(StringComparer.Ordinal);

    public GrammarLocator(IEnumerable<string> roots, bool useNixStore = true)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        _useNixStore = useNixStore && OperatingSystem.IsLinux();
    }

    /// <summary>The explicit roots: <paramref name="userGrammarDir"/> then each entry of <c>CSCODE_TREESITTER_GRAMMARS</c>.</summary>
    public static IEnumerable<string> DefaultRoots(string? userGrammarDir)
    {
        if (!string.IsNullOrEmpty(userGrammarDir)) yield return userGrammarDir!;
        if (Environment.GetEnvironmentVariable("CSCODE_TREESITTER_GRAMMARS") is { Length: > 0 } env)
            foreach (var p in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return p;
    }

    public GrammarFiles? Find(string grammarName)
    {
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
                if (File.Exists(c)) return new GrammarFiles(name, c, QueryDirectories(name), root);
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
