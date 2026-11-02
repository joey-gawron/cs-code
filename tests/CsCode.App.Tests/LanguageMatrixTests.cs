using System.Globalization;
using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Editor.Languages;
using CsCode.Editor.TreeSitter;
using CsCode.Lsp.Manager;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// B1 (themes-languages-treesitter brief): the language support matrix in docs/LANGUAGES.md is GENERATED from the real composition root
/// (every module, CSCODE_HOME in a temp dir): detection from the live ILanguageService, highlighting / configuration / snippets from the
/// editor's built-in catalog, servers from the live ILanguageServerManager plus their install hints, debuggers from the live IDebugService.
/// The test fails when the committed section differs, so the document cannot drift. Regenerate with
/// <c>CSCODE_UPDATE_LANGUAGES_MD=1 dotnet test tests/CsCode.App.Tests --filter LanguageMatrixTests</c>.
/// </summary>
public class LanguageMatrixTests
{
    private const string Begin = "<!-- BEGIN GENERATED: LanguageMatrixTests (do not edit by hand) -->";
    private const string End = "<!-- END GENERATED -->";

    private sealed record Snapshot(
        IReadOnlyList<BuiltinLanguageSupportInfo> Languages,
        ILanguageService LanguageService,
        IReadOnlyList<LanguageServerDefinition> Servers,
        IReadOnlyList<DebuggerType> Debuggers);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_support_matrix_in_docs_LANGUAGES_md_matches_the_running_composition()
    {
        await Task.Yield();
        using var home = new TempDirectory("cscode-matrix-");
        var previousHome = Environment.GetEnvironmentVariable("CSCODE_HOME");
        Environment.SetEnvironmentVariable("CSCODE_HOME", home.Path); // never the user's real config/state directories
        try
        {
            string generated;
            await using (var sp = AppServices.Build([]))
            {
                var snapshot = await Task.Run(() => new Snapshot(
                    BuiltinLanguageSupport.Languages,
                    sp.GetRequiredService<ILanguageService>(),
                    sp.GetRequiredService<ILanguageServerManager>().Definitions,
                    sp.GetRequiredService<IDebugService>().DebuggerTypes)).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
                CheckInvariants(snapshot);
                generated = Generate(snapshot);
            }

            var path = Path.Combine(RepoRoot(), "docs", "LANGUAGES.md");
            var current = File.Exists(path) ? (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n") : "";
            var updated = Splice(current, generated);
            if (Environment.GetEnvironmentVariable("CSCODE_UPDATE_LANGUAGES_MD") == "1")
            {
                if (updated != current) await File.WriteAllTextAsync(path, updated, TestContext.Current.CancellationToken);
                return;
            }
            if (updated != current)
            {
                var diff = FirstDifference(current, updated);
                Assert.Fail("docs/LANGUAGES.md is out of date with the code (first difference below). Regenerate it with\n"
                    + "  CSCODE_UPDATE_LANGUAGES_MD=1 dotnet test tests/CsCode.App.Tests --filter LanguageMatrixTests\n" + diff);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CSCODE_HOME", previousHome);
        }
    }

    // ---- invariants the matrix promises ----------------------------------------------------------------------------------------------------

    private static void CheckInvariants(Snapshot s)
    {
        var ids = s.Languages.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        // every built-in server is registered in the composition and serves only languages CsCode detects
        Assert.All(BuiltinLanguageServers.Catalog, b => Assert.Contains(s.Servers, d => d.Id == b.Definition.Id));
        foreach (var b in BuiltinLanguageServers.Catalog)
            foreach (var lang in Languages(b.Definition))
                Assert.True(ids.Contains(lang), $"server {b.Definition.Id} serves '{lang}', which no built-in language detects");
        // every language is detected by something, and highlighted (TextMate or tree-sitter) unless it is plain text
        foreach (var l in s.Languages)
        {
            Assert.True(Detection(l, s.LanguageService) != "-", $"{l.Id} cannot be detected");
            Assert.True(l.Id is "plaintext" || l.GrammarScope is not null || TreeSitterLanguageSupport.IsDefaultFor(l.Id), $"{l.Id} is not highlighted");
        }
        // every language without highlighting still has comment/bracket behaviour of its own (B4), except formats that have no comments
        foreach (var l in s.Languages.Where(l => l.GrammarScope is null && l.Id is not ("plaintext" or "juliamarkdown")))
            Assert.Equal(LanguageConfigurationOrigin.CsCode, l.Configuration);
    }

    // ---- generation ---------------------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Languages(LanguageServerDefinition d) => d.Selector.Filters.Select(f => f.Language).Where(l => l is not null).Select(l => l!);

    private static string Generate(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(Begin).Append('\n');
        sb.Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"{s.Languages.Count} languages, {BuiltinLanguageServers.Catalog.Count} built-in language servers, ")
          .Append(CultureInfo.InvariantCulture, $"{s.Debuggers.Count} built-in debugger(s). ")
          .Append(CultureInfo.InvariantCulture, $"Highlighted: {s.Languages.Count(l => l.GrammarScope is not null || TreeSitterLanguageSupport.IsDefaultFor(l.Id))} ")
          .Append(CultureInfo.InvariantCulture, $"({s.Languages.Count(l => TreeSitterLanguageSupport.IsDefaultFor(l.Id))} of them by tree-sitter by default, ")
          .Append(CultureInfo.InvariantCulture, $"{s.Languages.Count(l => l.GrammarScope is not null && TreeSitterLanguageSupport.GrammarFor(l.Id) is not null)} more with tree-sitter as an option); ")
          .Append(CultureInfo.InvariantCulture, $"with a language server: {s.Languages.Count(l => ServersFor(l.Id).Any())}.\n\n");

        sb.Append("### Support matrix\n\n");
        sb.Append("| Language | Detection | Highlighting | Language configuration | Snippets | Folding | Language server | Formatter | Debugger |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|\n");
        foreach (var l in s.Languages.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id, StringComparer.Ordinal))
        {
            var config = s.LanguageService.GetConfiguration(l.Id);
            var servers = ServersFor(l.Id).ToArray();
            sb.Append("| ").Append(Cell($"{l.DisplayName} `{l.Id}`"))
              .Append(" | ").Append(Cell(Detection(l, s.LanguageService)))
              .Append(" | ").Append(Cell(Highlighting(l)))
              .Append(" | ").Append(Cell(Configuration(l, config)))
              .Append(" | ").Append(Snippets(l))
              .Append(" | ").Append(Cell(Folding(l.Id, config, servers.Length > 0)))
              .Append(" | ").Append(Cell(servers.Length == 0 ? "none built in" : string.Join("; ", servers.Select(Server))))
              .Append(" | ").Append(Cell(Formatter(servers)))
              .Append(" | ").Append(Cell(Debugger(l.Id, s.Debuggers)))
              .Append(" |\n");
        }

        sb.Append("\n### Built-in language servers\n\n");
        sb.Append("| Id | Languages | Executable (alternatives) and arguments | Root markers | Initialization options / settings | Install: Linux | Install: macOS | Install: Windows |\n");
        sb.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (var b in BuiltinLanguageServers.Catalog.OrderBy(b => b.Definition.Id, StringComparer.Ordinal))
        {
            var d = b.Definition;
            var launch = string.Join(" or ", new[] { Launch(d.Command, d.Args) }.Concat(b.Alternatives.Select(a => Launch(a.Command, a.Args))));
            var markers = d.RootMarkers is { Count: > 0 } m
                ? string.Join(" ", m.Select(x => $"`{x}`")) + (b.Roots == RootStrategy.Outermost ? " (outermost)" : " (nearest)")
                : "none: one server for all workspace folders";
            var options = string.Join("; ", new[] { d.InitializationOptions is { } io ? $"init `{io.GetRawText()}`" : null, d.Settings is { } st ? $"settings `{st.GetRawText()}`" : null }
                .Where(x => x is not null));
            sb.Append("| ").Append(Cell($"`{d.Id}`"))
              .Append(" | ").Append(Cell(string.Join(" ", Languages(d).Select(x => $"`{x}`"))))
              .Append(" | ").Append(Cell(launch + (b.Notes is null ? "" : " — " + b.Notes)))
              .Append(" | ").Append(Cell(markers))
              .Append(" | ").Append(Cell(options.Length == 0 ? "-" : options))
              .Append(" | ").Append(Cell(b.Install.Linux))
              .Append(" | ").Append(Cell(b.Install.MacOS))
              .Append(" | ").Append(Cell(b.Install.Windows))
              .Append(" |\n");
        }
        sb.Append('\n').Append(End);
        return sb.ToString();
    }

    private static IEnumerable<BuiltinServer> ServersFor(string languageId) =>
        BuiltinLanguageServers.Catalog.Where(b => Languages(b.Definition).Contains(languageId, StringComparer.Ordinal));

    private static string Launch(string command, IReadOnlyList<string> args) => "`" + string.Join(" ", new[] { command }.Concat(args)) + "`";

    private static string Server(BuiltinServer b) =>
        $"`{b.Definition.Id}`: " + string.Join(" / ", new[] { b.Definition.Command }.Concat(b.Alternatives.Select(a => a.Command))) + (b.EnabledByDefault ? "" : " (off by default)");

    /// <summary>What formats the language: the servers that do (enabled ones first); "none (…)" only when none of them formats.</summary>
    private static string Formatter(BuiltinServer[] servers)
    {
        if (servers.Length == 0) return "none";
        var formatting = servers.Where(b => !b.Formatter.StartsWith("none", StringComparison.Ordinal)).OrderBy(b => b.EnabledByDefault ? 0 : 1).ToArray();
        return string.Join("; ", (formatting.Length > 0 ? formatting : servers).Select(b => b.Formatter));
    }

    /// <summary>Where a language's built-in snippets come from: VS Code's extension, CsCode's own set (language stage 1), or none (user snippets only).</summary>
    private static string Snippets(BuiltinLanguageSupportInfo l) =>
        (l.HasVsCodeSnippets, l.HasOwnSnippets) switch
        {
            (true, true) => "built-in (VS Code, CsCode)",
            (false, true) => "built-in (CsCode)",
            (true, false) => "built-in (VS Code)",
            _ => "user only",
        };

    /// <summary>Only what really detects the language through the live service (an extension shared with another language is not listed).</summary>
    private static string Detection(BuiltinLanguageSupportInfo l, ILanguageService service)
    {
        const int MaxShown = 8;
        var parts = new List<string>();
        var exts = l.Extensions.Where(e => e.StartsWith('.') && service.DetectLanguageId(new Uri("file:///probe/probe" + e)) == l.Id).ToArray();
        var files = l.Filenames.Where(f => service.DetectLanguageId(new Uri("file:///probe/" + f)) == l.Id).ToArray();
        var all = exts.Concat(files).ToArray();
        parts.AddRange(all.Take(MaxShown).Select(x => $"`{x}`"));
        if (all.Length > MaxShown) parts.Add($"+{all.Length - MaxShown} more");
        if (l.FilenamePatterns.Count > 0) parts.Add("names " + string.Join(" ", l.FilenamePatterns.Take(3).Select(p => $"`{p}`")) + (l.FilenamePatterns.Count > 3 ? " …" : ""));
        if (l.FirstLine is not null) parts.Add($"first line `{l.FirstLine}`");
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }

    private static string Highlighting(BuiltinLanguageSupportInfo l) =>
        (TreeSitterLanguageSupport.GrammarFor(l.Id), l.GrammarScope) switch
        {
            (null, { } scope) => $"TextMate `{scope}` (VS Code `{l.GrammarSource}`)",
            ({ } grammar, { } scope) => $"TextMate `{scope}` (VS Code `{l.GrammarSource}`); tree-sitter `{grammar}` as an option",
            ({ } grammar, null) => $"tree-sitter `{grammar}` (default)",
            _ => "none (plain text)",
        };

    private static string Configuration(BuiltinLanguageSupportInfo l, LanguageConfiguration c)
    {
        var origin = l.Configuration switch
        {
            LanguageConfigurationOrigin.VsCode => "VS Code",
            LanguageConfigurationOrigin.CsCode => "CsCode",
            _ => "defaults",
        };
        var parts = new List<string>();
        if (c.LineComment is { Length: > 0 } line) parts.Add($"`{line}`"); // XSL's VS Code configuration has an empty line comment
        if (c.BlockComment is { } block) parts.Add($"`{block.Open} {block.Close}`");
        if (parts.Count == 0) parts.Add("no comments");
        if (l.Configuration != LanguageConfigurationOrigin.None) parts.Add("pairs");
        else parts.Add("default pairs");
        if (c.IncreaseIndentPattern is not null || c.DecreaseIndentPattern is not null) parts.Add("indent rules");
        if (c.OnEnterRules.Count > 0) parts.Add("on-Enter rules");
        if (c.WordPattern is not null) parts.Add("word pattern");
        return origin + ": " + string.Join(", ", parts);
    }

    private static string Folding(string languageId, LanguageConfiguration c, bool hasServer)
    {
        var parts = new List<string>();
        // a document tree-sitter colours folds by the tree (folds.scm) instead of indentation and markers (docs/TREE-SITTER.md, "Structure")
        var treeFolds = TreeSitterLanguageSupport.GrammarFor(languageId) is { } grammar && TreeSitterLanguageSupport.GrammarsWithFolds.Contains(grammar);
        if (treeFolds && TreeSitterLanguageSupport.IsDefaultFor(languageId)) parts.Add("syntax tree");
        else
        {
            parts.Add(c.OffsideFolding ? "indentation (off-side)" : "indentation");
            if (c.FoldingMarkersStart is not null && c.FoldingMarkersEnd is not null) parts.Add("region markers");
            if (treeFolds) parts.Add("syntax tree with tree-sitter");
        }
        if (hasServer) parts.Add("server ranges");
        return string.Join(", ", parts);
    }

    private static string Debugger(string languageId, IReadOnlyList<DebuggerType> debuggers)
    {
        var matches = debuggers.Where(d => d.Languages.Contains(languageId, StringComparer.Ordinal)).Select(d => $"`{d.Type}` ({d.Label})").ToArray();
        return matches.Length == 0 ? "none built in" : string.Join("; ", matches);
    }

    /// <summary>A table cell: pipes escaped, no line breaks.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    // ---- the document --------------------------------------------------------------------------------------------------------------------

    private static string Splice(string current, string generated)
    {
        var begin = current.IndexOf(Begin, StringComparison.Ordinal);
        var end = current.IndexOf(End, StringComparison.Ordinal);
        if (begin < 0 || end < begin) return current.TrimEnd('\n') + (current.Length > 0 ? "\n\n" : "") + generated + "\n";
        return current[..begin] + generated + current[(end + End.Length)..];
    }

    private static string FirstDifference(string current, string updated)
    {
        var a = current.Split('\n');
        var b = updated.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<end of file>";
            var y = i < b.Length ? b[i] : "<end of file>";
            if (x != y) return $"line {i + 1}\n  committed: {x}\n  generated: {y}";
        }
        return "(no line differs)";
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }
}
