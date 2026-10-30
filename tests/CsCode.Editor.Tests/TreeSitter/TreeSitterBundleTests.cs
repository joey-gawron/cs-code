using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Editor.Languages;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Native;
using CsCode.TestDoubles;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Grammar distribution (docs/TREE-SITTER.md, "Distribution"): the pinned manifest, the fetch script's verification, the bundle the build
/// script makes and the app ships, the bundle's version gate (platform, ABI range, digests) and the discovery order (the bundle first, then the
/// user's folder, then the environment; the Nix store only for development without a bundle).
/// </summary>
public partial class TreeSitterBundleTests
{
    private static readonly string[] AllowedLicences = ["MIT", "Apache-2.0", "ISC", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "CC0-1.0"];

    /// <summary>Grammars in the manifest only for injections (no CsCode language is coloured by them directly).</summary>
    private static readonly string[] InjectionOnly = ["markdown_inline", "regex", "jsdoc", "hcl", "ocaml_interface"];

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Commit();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    private static JsonElement Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "treesitter", "manifest.json")));
        return doc.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Sources(JsonElement m) => new[] { m.GetProperty("runtime"), m.GetProperty("queries") }.Concat(m.GetProperty("sources").EnumerateArray());

    [Fact]
    public void Every_source_in_the_manifest_is_pinned_licence_checked_and_from_GitHub()
    {
        var m = Manifest();
        var names = new HashSet<string>();
        foreach (var s in Sources(m))
        {
            var name = s.GetProperty("name").GetString()!;
            Assert.True(names.Add(name), $"{name} twice");
            Assert.StartsWith("https://github.com/", s.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.Matches(Sha256(), s.GetProperty("sha256").GetString()!);
            Assert.True(s.GetProperty("size").GetInt64() > 1000, name);
            var commit = s.GetProperty("commit").GetString()!;
            Assert.Matches(Commit(), commit);
            var revision = s.GetProperty("revision").GetString()!;
            // a commit id pins itself; a tag is pinned by the commit id the archive carries (checked by fetch-grammars.sh)
            Assert.True(revision == commit || revision.StartsWith('v'), $"{name}: revision {revision} vs commit {commit}");
            Assert.Contains(s.GetProperty("license").GetString(), AllowedLicences);
            Assert.False(string.IsNullOrEmpty(s.GetProperty("licenseFile").GetString()), name);
        }
        Assert.Equal("Apache-2.0", m.GetProperty("queries").GetProperty("license").GetString()); // nvim-treesitter's queries: NOTICE required
        foreach (var g in m.GetProperty("grammars").EnumerateArray())
            Assert.Contains(g.GetProperty("source").GetString()!, names);
    }

    [Fact]
    public void Every_grammar_CsCode_uses_is_in_the_manifest_and_every_manifest_grammar_is_used()
    {
        var manifest = Manifest().GetProperty("grammars").EnumerateArray().Select(g => g.GetProperty("name").GetString()!).ToHashSet();
        var used = TreeSitterLanguages.Languages.Select(l => TreeSitterLanguages.GrammarFor(l)!).ToHashSet();
        used.Add(TreeSitterLanguages.GrammarFor("ocaml", new Uri("file:///a/b.mli"))!);
        foreach (var g in used) Assert.Contains(g, manifest);
        foreach (var g in manifest) Assert.True(used.Contains(g) || InjectionOnly.Contains(g), $"{g} is in the manifest but nothing uses it");
    }

    /// <summary>The <c>auto</c> default's promise: every language CsCode detects that TextMate cannot colour has a bundled tree-sitter grammar.</summary>
    [Fact]
    public void Every_detected_language_without_a_TextMate_grammar_has_a_tree_sitter_grammar()
    {
        var manifest = Manifest().GetProperty("grammars").EnumerateArray().Select(g => g.GetProperty("name").GetString()!).ToHashSet();
        // languages a file can be detected as (VS Code also declares helper ids such as jsx-tags that no file ever gets)
        var uncoloured = BuiltinLanguages.Catalog.Languages
            .Where(l => l.Extensions.Count + l.Filenames.Count + l.FilenamePatterns.Count > 0 || l.FirstLine is not null)
            .Select(l => l.Id)
            .Where(id => id != "plaintext" && BuiltinLanguages.Catalog.ScopeForLanguage(id) is null)
            .ToArray();
        Assert.True(uncoloured.Length >= 17, string.Join(", ", uncoloured)); // the 17 of docs/LANGUAGES.md (and a few more)
        foreach (var id in uncoloured)
        {
            var grammar = TreeSitterLanguages.GrammarFor(id);
            Assert.True(grammar is not null, $"{id} has neither a TextMate nor a tree-sitter grammar");
            Assert.Contains(grammar, manifest);
        }
    }

    [Fact]
    public void The_NOTICE_names_every_grammar_source_and_the_query_licence()
    {
        var notice = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "treesitter", "NOTICE"));
        foreach (var s in Manifest().GetProperty("sources").EnumerateArray())
        {
            var shortName = s.GetProperty("name").GetString()!["tree-sitter".Length..]; // "-toml": the NOTICE lists them as "-toml"
            Assert.Contains(shortName, notice, StringComparison.Ordinal);
        }
        Assert.Contains("nvim-treesitter", notice, StringComparison.Ordinal);
        Assert.Contains("Apache License, Version 2.0", notice, StringComparison.Ordinal);
        Assert.Contains("MIT License", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fetch_script_verifies_every_pinned_download_and_refuses_a_tampered_one()
    {
        var script = Path.Combine(RepoRoot(), "tools", "treesitter", "fetch-grammars.sh");
        Assert.SkipUnless(File.Exists("/usr/bin/jq") || File.Exists("/bin/jq"), "needs jq");
        var (ok, output) = Run(script, "--offline");
        Assert.SkipUnless(ok || !output.Contains("is missing", StringComparison.Ordinal), "the downloads are not on this machine (tools/treesitter/fetch-grammars.sh)");
        Assert.True(ok, output);
        Assert.Contains("40 source(s) verified", output, StringComparison.Ordinal);

        // a copy of one archive with one byte changed is refused (digest), and nothing is downloaded in its place
        using var dir = new TempDirectory("cscode-ts-fetch-");
        var toml = Manifest().GetProperty("sources").EnumerateArray().First(s => s.GetProperty("name").GetString() == "tree-sitter-toml");
        var rev = toml.GetProperty("revision").GetString()!;
        var downloads = DownloadRoot(output);
        var original = Path.Combine(downloads, "tree-sitter-toml", rev, $"tree-sitter-toml-{rev}.tar.gz");
        var copy = Path.Combine(dir.Path, "tree-sitter-toml", rev, $"tree-sitter-toml-{rev}.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        var bytes = File.ReadAllBytes(original);
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);
        var (tamperedOk, tamperedOutput) = Run(script, "--offline", "--only", "tree-sitter-toml", "--dir", dir.Path);
        Assert.False(tamperedOk, tamperedOutput);
        Assert.Contains("the manifest pins", tamperedOutput, StringComparison.Ordinal);
    }

    private static string DownloadRoot(string fetchOutput)
    {
        const string marker = "downloads in ";
        var line = fetchOutput.Split('\n').First(l => l.Contains(marker, StringComparison.Ordinal));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
    }

    private static (bool Ok, string Output) Run(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--noprofile");
        psi.ArgumentList.Add("--norc");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("SSH_AUTH_SOCK");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(entireProcessTree: true);
            return (false, "timed out");
        }
        return (p.ExitCode == 0, stdout.Result + stderr.Result);
    }

    // ---- the built bundle ----

    [Fact]
    public void The_app_bundle_is_used_first_and_every_file_in_it_matches_its_digest()
    {
        var bundle = RequireBundle();
        Assert.Equal(TreeSitterBundle.CurrentTarget, bundle.Target);
        Assert.Null(bundle.VerifyRuntime());
        RequireRuntime();
        Assert.Equal("bundled", TsRuntime.LoadedFrom);
        Assert.Equal(bundle.RuntimePath, TsRuntime.LoadedPath);
        var manifest = Manifest();
        Assert.Equal(manifest.GetProperty("runtime").GetProperty("revision").GetString(), bundle.RuntimeVersion);
        Assert.Equal(manifest.GetProperty("queries").GetProperty("commit").GetString(), bundle.QueriesCommit);
        foreach (var g in manifest.GetProperty("grammars").EnumerateArray().Select(g => g.GetProperty("name").GetString()!))
        {
            var entry = bundle.Grammar(g);
            Assert.True(entry is not null, $"{g} is missing from the bundle");
            Assert.Null(bundle.VerifyGrammar(g, Path.Combine(bundle.Directory, entry!.File)));
            Assert.InRange(entry.Abi, TsRuntime.MinAbi, TsRuntime.MaxAbi);
        }
        // the production locator finds the bundle first, its queries with it, and leaves the Nix store alone
        var locator = GrammarLocator.CreateDefault(null);
        Assert.False(locator.UsesNixStore);
        Assert.False(TreeSitterBundle.NixStoreFallback);
        var toml = locator.Find("toml")!;
        Assert.Equal("bundled", toml.Source);
        Assert.Same(bundle, toml.Bundle);
        Assert.StartsWith(bundle.GrammarsRoot, toml.QueryDirectories[0], StringComparison.Ordinal);
        // the licences and the NOTICE ship with it
        Assert.True(File.Exists(Path.Combine(bundle.Directory, "NOTICE")));
        Assert.True(File.Exists(Path.Combine(bundle.Directory, "licenses", "nvim-treesitter.txt")));
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(bundle.Directory, "licenses", "tree-sitter-elixir.txt")), StringComparison.Ordinal);
    }

    /// <summary>docs/LANGUAGES.md says which languages fold by the tree from this list: it must be what the bundle really has.</summary>
    [Fact]
    public void The_documented_list_of_grammars_with_folds_matches_the_bundle()
    {
        var bundle = RequireBundle();
        var withFolds = bundle.Grammars.Where(g => File.Exists(Path.Combine(bundle.GrammarsRoot, "queries", g.Name, "folds.scm"))).Select(g => g.Name).ToHashSet();
        Assert.Equal(withFolds.Order(), TreeSitterLanguageSupport.GrammarsWithFolds.Order());
    }

    [Fact]
    public void Every_bundled_grammar_loads_and_its_highlight_query_compiles()
    {
        var bundle = RequireBundle();
        RequireRuntime();
        using var registry = NewRegistry();
        var problems = new List<string>();
        foreach (var g in bundle.Grammars)
        {
            var language = registry.Get(g.Name, out var error);
            if (language is null) problems.Add($"{g.Name}: {error}");
            else if (language.Files?.Source != "bundled") problems.Add($"{g.Name} came from {language.Files?.Source}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_bundle_built_for_another_platform_format_or_ABI_range_is_refused_with_the_reason()
    {
        using var dir = new TempDirectory("cscode-ts-gate-");
        string Write(string target, int format, uint min, uint max)
        {
            var path = Path.Combine(dir.Path, $"{target}-{format}-{min}-{max}");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "bundle.json"), JsonSerializer.Serialize(new
            {
                format, target, abi = new { min, max },
                runtime = new { version = "v0.26.11", commit = new string('a', 40), file = "lib/libtree-sitter.so", sha256 = new string('0', 64) },
                queries = new { source = "nvim-treesitter", commit = new string('b', 40) },
                grammars = Array.Empty<object>(),
            }));
            return path;
        }
        Assert.NotNull(TreeSitterBundle.TryOpen(Write(TreeSitterBundle.CurrentTarget, 1, TsRuntime.MinAbi, TsRuntime.MaxAbi), out var fine));
        Assert.Null(fine);
        Assert.Null(TreeSitterBundle.TryOpen(Write("plan9-mips", 1, TsRuntime.MinAbi, TsRuntime.MaxAbi), out var platform));
        Assert.Contains("built for plan9-mips", platform, StringComparison.Ordinal);
        Assert.Null(TreeSitterBundle.TryOpen(Write(TreeSitterBundle.CurrentTarget, 2, TsRuntime.MinAbi, TsRuntime.MaxAbi), out var format));
        Assert.Contains("format 2", format, StringComparison.Ordinal);
        Assert.Null(TreeSitterBundle.TryOpen(Write(TreeSitterBundle.CurrentTarget, 1, 14, 16), out var abi));
        Assert.Contains("ABI 14-16", abi, StringComparison.Ordinal);
        Assert.Null(TreeSitterBundle.TryOpen(Path.Combine(dir.Path, "nothing"), out var missing));
        Assert.Contains("no tree-sitter bundle", missing, StringComparison.Ordinal);
        // the runtime file of the fine bundle does not exist / match: refused before it is loaded
        Assert.NotNull(TreeSitterBundle.TryOpen(Write(TreeSitterBundle.CurrentTarget, 1, TsRuntime.MinAbi, TsRuntime.MaxAbi), out _)!.VerifyRuntime());
    }

    /// <summary>A copy of the bundle with ONE grammar file damaged: that grammar is refused (digest), the others still load.</summary>
    [Fact]
    public void A_bundled_grammar_whose_file_does_not_match_its_digest_is_refused_and_the_others_still_load()
    {
        var real = RequireBundle();
        RequireRuntime();
        using var dir = new TempDirectory("cscode-ts-corrupt-");
        var copy = CopyBundle(real, dir.Path, ["toml", "json"]);
        var damaged = Path.Combine(copy, "grammars", "toml.so");
        var bytes = File.ReadAllBytes(damaged);
        bytes[bytes.Length / 2] ^= 0x5A; // same size, different content
        File.WriteAllBytes(damaged, bytes);

        var bundle = TreeSitterBundle.TryOpen(copy, out var problem)!;
        Assert.Null(problem);
        using var registry = new GrammarRegistry(new GrammarLocator([bundle.GrammarsRoot], useNixStore: false, bundle), TreeSitterLanguages.RootScopeFor);
        Assert.Null(registry.Get("toml", out var error));
        Assert.Contains("does not match the digest", error, StringComparison.Ordinal);
        Assert.NotNull(registry.Get("json", out var jsonError));
        Assert.Null(jsonError);
    }

    [Fact]
    public void The_bundle_comes_before_the_user_folder_and_each_grammar_keeps_its_own_queries()
    {
        var real = RequireBundle();
        RequireRuntime();
        using var dir = new TempDirectory("cscode-ts-order-");
        var bundle = TreeSitterBundle.TryOpen(CopyBundle(real, Path.Combine(dir.Path, "app"), ["toml"]), out _)!;
        // a user's own TOML grammar (here: the same library) with queries of its own
        var user = Path.Combine(dir.Path, "user");
        Directory.CreateDirectory(Path.Combine(user, "queries", "toml"));
        File.Copy(Path.Combine(bundle.GrammarsRoot, "toml.so"), Path.Combine(user, "toml.so"));
        File.WriteAllText(Path.Combine(user, "queries", "toml", "highlights.scm"), "; the user's\n(comment) @comment\n");

        Assert.Equal([bundle.GrammarsRoot, user], GrammarLocator.DefaultRoots(bundle, user).Take(2));
        var bundledFirst = new GrammarLocator(GrammarLocator.DefaultRoots(bundle, user), useNixStore: false, bundle);
        Assert.Equal("bundled", bundledFirst.Find("toml")!.Source);
        using (var registry = new GrammarRegistry(bundledFirst, TreeSitterLanguages.RootScopeFor))
            Assert.All(registry.Get("toml", out _)!.QueryFiles, f => Assert.StartsWith(bundle.GrammarsRoot, f, StringComparison.Ordinal));

        // without the bundle the user's grammar is used, with ITS queries (not another root's)
        var userOnly = new GrammarLocator([user, bundle.GrammarsRoot], useNixStore: false);
        Assert.Equal(user, userOnly.Find("toml")!.Source);
        Assert.StartsWith(user, userOnly.QueryDirectories("toml", user)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_Nix_store_is_a_development_fallback_only()
    {
        if (Environment.GetEnvironmentVariable("CSCODE_TREESITTER_NIX") is { Length: > 0 }) Assert.Skip("CSCODE_TREESITTER_NIX is set for this run");
        Assert.Equal(TreeSitterBundle.Default is null, TreeSitterBundle.NixStoreFallback);
        Assert.Equal(TreeSitterBundle.NixStoreFallback && OperatingSystem.IsLinux(), GrammarLocator.CreateDefault(null).UsesNixStore);
    }

    /// <summary>A partial copy of <paramref name="bundle"/> (its bundle.json, runtime, the named grammars and their queries) under <paramref name="target"/>.</summary>
    private static string CopyBundle(TreeSitterBundle bundle, string target, string[] grammars)
    {
        Directory.CreateDirectory(Path.Combine(target, "grammars", "queries"));
        Directory.CreateDirectory(Path.Combine(target, "lib"));
        File.Copy(Path.Combine(bundle.Directory, "bundle.json"), Path.Combine(target, "bundle.json"));
        File.Copy(bundle.RuntimePath, Path.Combine(target, "lib", Path.GetFileName(bundle.RuntimePath)));
        foreach (var g in grammars)
        {
            var entry = bundle.Grammar(g)!;
            File.Copy(Path.Combine(bundle.Directory, entry.File), Path.Combine(target, entry.File));
            var queries = Path.Combine(target, "grammars", "queries", g);
            Directory.CreateDirectory(queries);
            foreach (var f in Directory.GetFiles(Path.Combine(bundle.GrammarsRoot, "queries", g))) File.Copy(f, Path.Combine(queries, Path.GetFileName(f)));
        }
        return target;
    }
}
