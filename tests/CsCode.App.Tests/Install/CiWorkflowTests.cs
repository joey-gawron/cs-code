using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// The CI workflow's inputs stay true to the repository (.github/workflows/ci.yml, tools/ci/): every test project runs in one of its test
/// groups, every entry of the load-sensitive list names a test that exists (a renamed test would otherwise silently run again in the gating
/// jobs, or a typo exclude nothing), and tools/ci/test-filter.sh turns the list into the filters the jobs use.
/// </summary>
public sealed partial class CiWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<string> LoadSensitive() =>
        [.. File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "ci", "load-sensitive-tests.txt"))
            .Select(l => (l.Contains('#') ? l[..l.IndexOf('#')] : l).Trim()).Where(l => l.Length > 0)];

    [Fact]
    public void Every_test_project_runs_in_a_CI_test_group()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));
        var projects = Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "tests"), "CsCode.*Tests").Select(Path.GetFileName).ToList();
        Assert.NotEmpty(projects);
        foreach (var project in projects) Assert.True(Regex.IsMatch(workflow, $@"tests/{Regex.Escape(project!)}(\s|$)", RegexOptions.Multiline), $"{project} is in no test group of ci.yml");
        Assert.Contains("--blame-hang-timeout", workflow);
        Assert.Contains("-warnaserror", workflow);
        Assert.Contains("tools/package-macos.sh --no-adapters", workflow);
    }

    [Fact]
    public void Every_load_sensitive_entry_names_a_test_that_exists()
    {
        // namespace + class -> method names, from the test sources
        var tests = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (Namespace().Match(text) is not { Success: true } ns) continue;
            var methods = Method().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match c in Class().Matches(text))
                (tests.TryGetValue(ns.Groups[1].Value + "." + c.Groups[1].Value, out var known) ? known : tests[ns.Groups[1].Value + "." + c.Groups[1].Value] = []).UnionWith(methods);
        }
        var entries = LoadSensitive();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var found = tests.Any(t => entry == t.Key || (entry.StartsWith(t.Key + ".", StringComparison.Ordinal) && t.Value.Any(m => m.StartsWith(entry[(t.Key.Length + 1)..], StringComparison.Ordinal))));
            Assert.True(found, $"tools/ci/load-sensitive-tests.txt: '{entry}' names no test class or method in tests/");
        }
    }

    [Fact]
    public async Task The_filter_script_leaves_out_the_soak_and_perf_suites_and_each_listed_test_and_selects_just_those_with_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "bash");
        var entries = LoadSensitive();
        var gating = await RunAsync();
        Assert.Equal(string.Join("&", ["Category!=Soak", "Category!=Perf", .. entries.Select(e => "FullyQualifiedName!~" + e)]), gating);
        var only = await RunAsync("--only");
        Assert.Equal(string.Join("|", entries.Select(e => "FullyQualifiedName~" + e)), only);
    }

    private static async Task<string> RunAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Path.Combine(RepoRoot(), "tools", "ci", "test-filter.sh"));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(p.ExitCode == 0, error);
        return output.Trim();
    }

    [GeneratedRegex(@"^namespace\s+([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex Class();

    [GeneratedRegex(@"(?:public|internal)\s+(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)\s*\(")]
    private static partial Regex Method();
}
