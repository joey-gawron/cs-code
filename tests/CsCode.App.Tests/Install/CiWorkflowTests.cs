using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace CsCode.App.Tests.Install;

/// <summary>
/// The CI workflow's inputs stay true to the repository (.github/workflows/ci.yml, tools/ci/): every test project runs in one of its test
/// groups, every entry of the load-sensitive list names a test method that exists (a renamed test would otherwise silently run again in the
/// gating jobs, a typo exclude nothing, and a class drop the rest of its tests from them), and tools/ci/test-filter.sh turns the list into the
/// filters the jobs use, on each OS only the entries for it ("macos:" and "linux:" lines are one OS's).
/// </summary>
public sealed partial class CiWorkflowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root (CsCode.slnx) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The list's entries: the OS a line is marked for (null: every OS) and the test name.</summary>
    private static IReadOnlyList<(string? Os, string Name)> LoadSensitiveEntries() =>
        [.. File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "ci", "load-sensitive-tests.txt"))
            .Select(l => (l.Contains('#') ? l[..l.IndexOf('#')] : l).Replace(" ", "", StringComparison.Ordinal).Trim()).Where(l => l.Length > 0)
            .Select(l => l.IndexOf(':') is var colon and > 0 ? (l[..colon], l[(colon + 1)..]) : ((string?)null, l))];

    /// <summary>The test names the list leaves out on <paramref name="os"/>, in the list's order.</summary>
    private static IReadOnlyList<string> LoadSensitive(string os) => [.. LoadSensitiveEntries().Where(e => e.Os is null || e.Os == os).Select(e => e.Name)];

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
        var entries = LoadSensitiveEntries();
        Assert.NotEmpty(entries);
        foreach (var (os, entry) in entries)
        {
            Assert.True(os is null or "macos" or "linux", $"tools/ci/load-sensitive-tests.txt: '{os}:' is not an OS mark (macos:, linux:)");
            // A class name alone would leave every test of the class out of the gating runs, on every OS: one test per line.
            Assert.False(tests.ContainsKey(entry), $"tools/ci/load-sensitive-tests.txt: '{entry}' names a whole class; list its load-sensitive tests one by one");
            var found = tests.Any(t => entry.StartsWith(t.Key + ".", StringComparison.Ordinal) && entry.Length > t.Key.Length + 1
                && t.Value.Any(m => m.StartsWith(entry[(t.Key.Length + 1)..], StringComparison.Ordinal)));
            Assert.True(found, $"tools/ci/load-sensitive-tests.txt: '{entry}' names no test method in tests/");
        }
    }

    [Theory]
    [InlineData("linux")]
    [InlineData("macos")]
    public async Task The_filter_script_leaves_out_the_soak_and_perf_suites_and_each_test_listed_for_the_OS_and_selects_just_those_with_only(string os)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "bash");
        var entries = LoadSensitive(os);
        var gating = await RunAsync("--os", os);
        Assert.Equal(string.Join("&", ["Category!=Soak", "Category!=Perf", .. entries.Select(e => "FullyQualifiedName!~" + e)]), gating);
        var only = await RunAsync("--only", "--os", os);
        Assert.Equal(string.Join("|", entries.Select(e => "FullyQualifiedName~" + e)), only);
        // A test marked for the other OS gates here.
        var other = os == "linux" ? "macos" : "linux";
        foreach (var name in LoadSensitiveEntries().Where(e => e.Os == other).Select(e => e.Name)) Assert.DoesNotContain(name, gating, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_filter_script_uses_this_machine_s_OS_by_default()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "bash");
        var os = OperatingSystem.IsMacOS() ? "macos" : "linux";
        Assert.Equal(await RunAsync("--os", os), await RunAsync());
        Assert.Equal(await RunAsync("--only", "--os", os), await RunAsync("--only"));
    }

    [Fact]
    public void The_list_keeps_the_macOS_only_failures_of_known_issues_gating_on_Linux()
    {
        // known-issues names these as macOS failures: on Linux they are real checks (the background-session process harness, the memory
        // toolkit's probes) and must stay in the gating runs there.
        var linux = LoadSensitive("linux");
        Assert.DoesNotContain(linux, e => e.Contains("BackgroundSessionProcessTests", StringComparison.Ordinal));
        Assert.DoesNotContain(linux, e => e.Contains("ToolkitTests", StringComparison.Ordinal));
        Assert.DoesNotContain(linux, e => e.Contains("RealDebuggerEndToEndTests", StringComparison.Ordinal));
        Assert.Contains(LoadSensitive("macos"), e => e.EndsWith("BackgroundSessionProcessTests.Concurrent_launches_of_one_session_start_one_process", StringComparison.Ordinal));
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
