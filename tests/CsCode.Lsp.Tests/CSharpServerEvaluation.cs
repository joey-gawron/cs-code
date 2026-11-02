using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.CSharp;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// The csharp-ls / Roslyn evaluation of docs/CSHARP.md, on CsCode's own solution: each server runs through the built-in definition, the real
/// process factory and CsCode's client as the app runs it (csharp-ls rooted at the nearest project, Roslyn at the solution), on
/// <c>src/CsCode.Testing/Providers/DotNet/DotNetTestProvider.cs</c> opened with three lines added (a type error, a field that breaks the
/// repository's .editorconfig naming rule, and nothing else). It measures time to the first diagnostics, the process tree's memory, completion
/// latency (cold, then 20 warm requests), and records rename, code actions, analyzer and .editorconfig diagnostics, go to a generated
/// implementation and go to library code. Results go to <c>.scratch/eval/csharp-&lt;server&gt;.json</c>. Not a gate: it runs only with
/// <c>CSCODE_CSHARP_EVAL=1</c> (a few minutes and a few GB per server).
/// </summary>
[Trait("Category", "Evaluation")]
public class CSharpServerEvaluation
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Target = "src/CsCode.Testing/Providers/DotNet/DotNetTestProvider.cs";

    /// <summary>csharp-ls as CsCode roots it (the nearest project), csharp-ls rooted at the solution (<c>csharp@solution</c>), Roslyn.</summary>
    public static TheoryData<string> Servers => new() { CSharpServers.CSharpLsId, CSharpServers.CSharpLsId + "@solution", CSharpServers.RoslynId };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Evaluate(string variant)
    {
        var server = variant.Split('@')[0];
        var atSolution = variant.EndsWith("@solution", StringComparison.Ordinal);
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CSCODE_CSHARP_EVAL") == "1", "the C# server evaluation runs only with CSCODE_CSHARP_EVAL=1 (docs/CSHARP.md)");
        if (server == CSharpServers.RoslynId && RealCSharpServerTests.RoslynDll() is null) Assert.Skip("Roslyn is not fetched: tools/fetch-roslyn-ls.sh");
        var repo = LspTestEnv.RepoRoot;
        var results = new Dictionary<string, object?> { ["server"] = variant, ["date"] = DateTime.UtcNow.ToString("u") };
        await using var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 60_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 300_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 300_000);
            c.Set(LspSettingKeys.ShutdownTimeoutMs, 10_000);
            c.Set(LspSettingKeys.ServerChoice, new Dictionary<string, string> { [BuiltinLanguageServers.CSharpGroup] = server });
            var overrides = BuiltinLanguageServers.All.Where(d => d.Id is not (CSharpServers.RoslynId or CSharpServers.CSharpLsId))
                .ToDictionary(d => d.Id, _ => (object)new { enabled = false });
            if (server == CSharpServers.RoslynId)
                overrides[server] = new { command = "dotnet", args = new[] { RealCSharpServerTests.RoslynDll()! }.Concat(BuiltinLanguageServers.RoslynArgs).ToArray() };
            else if (atSolution)
                overrides[server] = new { rootMarkers = new[] { "*.slnx", "*.sln" } }; // the repository root: csharp-ls loads the solution
            c.Set(LspSettingKeys.LanguageServers, overrides);
        }, realServers: true);
        env.Workspace.AddFolder(repo);

        var path = Path.Combine(repo, Target);
        var original = await File.ReadAllTextAsync(path, Ct);
        // three lines after the class's opening brace: a type error (CS0029), a private field named against the .editorconfig rule (IDE1006)
        var anchor = original.IndexOf("    public const string ProviderId", StringComparison.Ordinal);
        var text = original.Insert(anchor, "    private readonly int evalBadName = 0;\n    private int EvalError() { int wrong = \"text\"; return wrong + evalBadName; }\n\n");
        var doc = new InMemoryTextDocument(new Uri(path), "csharp", text);
        var clock = Stopwatch.StartNew();
        using var registration = env.Documents.Register(doc);

        var info = await env.WaitForStateAsync(server, LanguageServerState.Running, TimeSpan.FromMinutes(5));
        results["running_s"] = Seconds(clock);
        results["serverVersion"] = $"{info.ServerName} {info.ServerVersion}";
        var client = Assert.Single(env.Manager.RunningClients(server));
        results["root"] = client.RootUri?.LocalPath;
        if (client.Extension?.State is RoslynSession roslyn)
        {
            await roslyn.Ready.WaitAsync(TimeSpan.FromMinutes(10), Ct);
            results["ready_s"] = Seconds(clock);
        }

        var errorLine = text[..text.IndexOf("int wrong", StringComparison.Ordinal)].Count(c => c == '\n');
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline && !env.Diagnostics.Get(doc.Uri).Any(d => d.Code == "CS0029")) await Task.Delay(100, Ct);
        results["firstDiagnostics_s"] = env.Diagnostics.Get(doc.Uri).Any(d => d.Code == "CS0029") ? Seconds(clock) : null;
        await Task.Delay(TimeSpan.FromSeconds(15), Ct); // let analyzers finish: the counts below are what settles
        var diagnostics = env.Diagnostics.Get(doc.Uri);
        results["diagnostics"] = diagnostics.GroupBy(d => $"{d.Code} ({d.Severity})").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        results["editorconfigNamingIDE1006"] = diagnostics.Where(d => d.Code == "IDE1006").Select(d => d.Severity.ToString()).FirstOrDefault();
        results["memoryAfterReady_MB"] = TreeRssMb(info.ProcessId!.Value);

        // completion after "_info." (members of ConcurrentDictionary): cold, then 20 warm requests
        var completionAt = doc.PositionAt(text.IndexOf("_info.TryGetValue", StringComparison.Ordinal) + "_info.".Length);
        var completion = env.Registry.GetProviders<ICompletionProvider>(doc).First();
        var times = new List<double>();
        int items = 0;
        for (var i = 0; i < 21; i++)
        {
            var sw = Stopwatch.StartNew();
            var list = await completion.ProvideCompletionAsync(doc, completionAt, new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."), Ct);
            times.Add(sw.Elapsed.TotalMilliseconds);
            items = list?.Items.Count ?? 0;
        }
        results["completion_cold_ms"] = Math.Round(times[0]);
        var warm = times.Skip(1).Order().ToArray();
        results["completion_warm_median_ms"] = Math.Round(warm[warm.Length / 2]);
        results["completion_warm_p90_ms"] = Math.Round(warm[(int)(warm.Length * 0.9)]);
        results["completion_items"] = items;

        // code actions on the type error, and refactorings on a method name
        var error = diagnostics.FirstOrDefault(d => d.Code == "CS0029");
        results["codeActions_onCS0029"] = await ActionTitlesAsync(env, doc, error?.Range ?? TextRange.At(errorLine, 30), error is null ? [] : [error]);
        var methodName = text.IndexOf("internal static bool MentionsTests", StringComparison.Ordinal) + "internal static bool ".Length;
        results["codeActions_onMethodName"] = await ActionTitlesAsync(env, doc, new TextRange(doc.PositionAt(methodName), doc.PositionAt(methodName + "MentionsTests".Length)), []);

        // rename of a private field used in several places of the file
        if (env.Registry.GetProviders<IRenameProvider>(doc).FirstOrDefault() is { } rename)
        {
            var at = doc.PositionAt(text.IndexOf("_info = new", StringComparison.Ordinal) + 1);
            var sw = Stopwatch.StartNew();
            var edit = await rename.ProvideRenameAsync(doc, at, "_projects", Ct);
            results["rename_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds);
            results["rename_edits"] = edit?.Changes.Sum(c => c.Value.Count);
        }

        // go to library code (ConcurrentDictionary) and to a source generator's output ([GeneratedRegex] FilterEscape)
        var definitions = env.Registry.GetProviders<IDefinitionProvider>(doc).First();
        var lib = await Timed(() => definitions.ProvideDefinitionAsync(doc, doc.PositionAt(text.IndexOf("ConcurrentDictionary<string, ProjectInfo>", StringComparison.Ordinal) + 3), DefinitionKind.Definition, Ct));
        results["gotoLibrary"] = Describe(lib.Value, env);
        results["gotoLibrary_ms"] = lib.Ms;
        var gen = await Timed(() => definitions.ProvideDefinitionAsync(doc, doc.PositionAt(text.IndexOf("private static partial Regex FilterEscape", StringComparison.Ordinal) + "private static partial Regex ".Length + 2), DefinitionKind.Implementation, Ct));
        results["gotoGenerated"] = Describe(gen.Value, env);

        results["memoryAfterRequests_MB"] = TreeRssMb(info.ProcessId!.Value);
        results["total_s"] = Seconds(clock);

        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        var outDir = Path.Combine(repo, ".scratch", "eval");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"csharp-{variant.Replace('@', '-')}.json"), json, Ct);
        TestContext.Current.SendDiagnosticMessage(json);
    }

    private static double Seconds(Stopwatch sw) => Math.Round(sw.Elapsed.TotalSeconds, 1);

    private static async Task<(T Value, double Ms)> Timed<T>(Func<ValueTask<T>> call)
    {
        var sw = Stopwatch.StartNew();
        var value = await call();
        return (value, Math.Round(sw.Elapsed.TotalMilliseconds));
    }

    private static string Describe(IReadOnlyList<LocationLink>? links, LspTestEnv env) =>
        links is not { Count: > 0 } ? "nothing"
        : string.Join("; ", links.Take(3).Select(l => VirtualDocumentUri.IsVirtual(l.TargetUri)
            ? $"{VirtualDocumentUri.Title(l.TargetUri)} ({env.Manager.External.ServerUriOf(l.TargetUri)?.Split(':')[0]})"
            : Path.GetFileName(l.TargetUri.LocalPath)));

    private static async Task<string[]> ActionTitlesAsync(LspTestEnv env, ITextDocument doc, TextRange range, IReadOnlyList<Diagnostic> diagnostics)
    {
        var titles = new List<string>();
        foreach (var p in env.Registry.GetProviders<ICodeActionProvider>(doc))
            if (await p.ProvideCodeActionsAsync(doc, range, new CodeActionContext(diagnostics), Ct) is { } actions)
                titles.AddRange(actions.Select(a => a.Title));
        return [.. titles];
    }

    /// <summary>Resident memory of a process and all its descendants (ps), in MB.</summary>
    private static double TreeRssMb(int pid)
    {
        var psi = new ProcessStartInfo("ps", "-A -o pid=,ppid=,rss=") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var rows = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(c => c.Length == 3)
            .Select(c => (Pid: int.Parse(c[0]), Parent: int.Parse(c[1]), Kb: long.Parse(c[2]))).ToArray();
        p.WaitForExit();
        var tree = new HashSet<int> { pid };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var r in rows) if (!tree.Contains(r.Pid) && tree.Contains(r.Parent)) { tree.Add(r.Pid); changed = true; }
        }
        return Math.Round(rows.Where(r => tree.Contains(r.Pid)).Sum(r => r.Kb) / 1024.0);
    }
}
