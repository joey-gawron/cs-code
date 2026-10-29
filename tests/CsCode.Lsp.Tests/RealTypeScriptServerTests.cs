using System.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// End-to-end against the REAL typescript-language-server (child process over stdio, real tsserver underneath).
/// Install with tools/fetch-lsp-tools.sh; without it (or without node) every test SKIPS with that reason.
/// </summary>
public class RealTypeScriptServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string TsLs => Path.Combine(LspTestEnv.ToolsDir, "lsp", "node_modules", ".bin", "typescript-language-server");

    private static bool RequireTools()
    {
        if (!File.Exists(TsLs)) { Assert.Skip("typescript-language-server is not installed in .scratch/tools/lsp; run tools/fetch-lsp-tools.sh"); return false; }
        if (ToolResolver.FindOnPath("node", null) is null) { Assert.Skip("node is not on PATH"); return false; }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { Assert.Skip("integration tests are written for unix"); return false; }
        return true;
    }

    private const string UtilTs = """
        export function greet(name: string): string {
          return "hello " + name;
        }

        export const unused = 1;
        """;

    private const string MainTs = """
        import { greet } from "./util";

        const count: number = "oops";
        const message = greet("world");
        console.log(message.toUpperCase(), count);
        """;

    private sealed class Project : IAsyncDisposable
    {
        public required LspTestEnv Env { get; init; }
        public required InMemoryTextDocument Main { get; init; }
        public required InMemoryTextDocument Util { get; init; }
        public ValueTask DisposeAsync() => Env.DisposeAsync();
        public static TextPosition Find(ITextDocument doc, string needle, int offset = 0) => doc.PositionAt(doc.GetText().IndexOf(needle, StringComparison.Ordinal) + offset);
    }

    private static async Task<Project> OpenProjectAsync(string main = MainTs, string util = UtilTs, string compilerOptions = "")
    {
        var env = new LspTestEnv(c =>
        {
            c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
            c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
            c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
        }, realServers: true);
        env.Dir.WriteFile("tsconfig.json", "{\"compilerOptions\":{\"strict\":true,\"target\":\"es2020\",\"module\":\"commonjs\",\"noEmit\":true" + compilerOptions + "}}");
        env.Dir.WriteFile("package.json", """{"name":"cscode-it","version":"1.0.0"}""");
        var u = env.Open("src/util.ts", util, "typescript");
        var m = env.Open("src/main.ts", main, "typescript");
        await env.WaitForStateAsync("typescript", LanguageServerState.Running, TimeSpan.FromSeconds(90));
        return new Project { Env = env, Main = m, Util = u };
    }

    /// <summary>The server loads the project asynchronously: retry a query until it yields something (or the budget runs out).</summary>
    private static async Task<T> UntilAsync<T>(Func<Task<T?>> query, string what, Func<T, bool>? accept = null, int seconds = 60) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = await query().Within(TimeSpan.FromSeconds(40));
            if (r is not null && (accept?.Invoke(r) ?? true)) return r;
            await Task.Delay(300, Ct);
        }
        Assert.Fail("no usable answer from typescript-language-server for: " + what);
        return null!;
    }

    [Fact]
    public async Task Diagnostics_appear_for_a_type_error_and_follow_edits()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var diagnostics = p.Env.Diagnostics;

        await TestHelpers.EventuallyAsync(() => diagnostics.Get(p.Main.Uri).Any(d => d.Code == "2322"), () => "TS2322 diagnostic\n" + p.Env.Dump(), TimeSpan.FromSeconds(60));
        var error = diagnostics.Get(p.Main.Uri).First(d => d.Code == "2322");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("not assignable to type 'number'", error.Message);
        Assert.Equal(2, error.Range.Start.Line); // `const count: number = "oops";`

        // fix it: the diagnostic disappears
        var oops = p.Main.GetText().IndexOf("\"oops\"", StringComparison.Ordinal);
        p.Main.ApplyEdits([new TextEdit(new TextRange(p.Main.PositionAt(oops), p.Main.PositionAt(oops + 6)), "5")]);
        await TestHelpers.EventuallyAsync(() => !diagnostics.Get(p.Main.Uri).Any(d => d.Code == "2322"), () => "diagnostic cleared after the fix\n" + p.Env.Dump(), TimeSpan.FromSeconds(60));

        // break it again: it comes back, with the range following the edit
        p.Main.ApplyEdits([new TextEdit(new TextRange(p.Main.PositionAt(oops), p.Main.PositionAt(oops + 1)), "\"again\"")]);
        await TestHelpers.EventuallyAsync(() => diagnostics.Get(p.Main.Uri).Any(d => d.Code == "2322"), () => "diagnostic back after re-introducing the error\n" + p.Env.Dump(), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Completion_offers_members_of_the_receiver()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        p.Main.ApplyEdits([TextEdit.Insert(p.Main.PositionAt(p.Main.Length), "\nmessage.")]);
        var end = p.Main.PositionAt(p.Main.Length);
        var provider = p.Env.Provider<ICompletionProvider>(p.Main);
        Assert.Contains(".", provider.TriggerCharacters);

        var list = await UntilAsync(
            async () => await provider.ProvideCompletionAsync(p.Main, end, new CompletionContext(CompletionTriggerKind.TriggerCharacter, "."), Ct),
            "completion after `message.`", l => l.Items.Any(i => i.Label == "toUpperCase"));
        var item = list.Items.First(i => i.Label == "toUpperCase");
        Assert.Equal(CompletionItemKind.Method, item.Kind);
        Assert.Contains(list.Items, i => i.Label == "length");

        // resolve fills in documentation lazily
        var resolved = await provider.ResolveCompletionAsync(item, Ct).Within(TimeSpan.FromSeconds(30));
        Assert.Equal("toUpperCase", resolved.Label);
    }

    [Fact]
    public async Task Hover_shows_the_signature_and_signature_help_reports_the_active_parameter()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var greetUse = Project.Find(p.Main, "greet(\"world\")", 2);
        var hover = await UntilAsync(async () => await p.Env.Provider<IHoverProvider>(p.Main).ProvideHoverAsync(p.Main, greetUse, Ct), "hover over greet",
            h => h.Contents.Any(c => c.Value.Contains("greet(name: string): string")));
        Assert.Contains(hover.Contents, c => c.IsMarkdown && c.Value.Contains("greet(name: string): string"));

        var open = Project.Find(p.Main, "greet(\"world\")", 6);
        var help = await UntilAsync(async () => await p.Env.Provider<ISignatureHelpProvider>(p.Main).ProvideSignatureHelpAsync(p.Main, open,
            new SignatureHelpContext(SignatureHelpTriggerKind.TriggerCharacter, "(", false, null), Ct), "signature help inside greet(");
        Assert.Contains("greet", help.Signatures[0].Label);
        Assert.Equal("name: string", help.Signatures[0].Parameters![0].Label);
    }

    [Fact]
    public async Task Definition_and_references_work_across_files()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var use = Project.Find(p.Main, "greet(\"world\")", 1);
        var def = await UntilAsync(async () => await p.Env.Provider<IDefinitionProvider>(p.Main).ProvideDefinitionAsync(p.Main, use, DefinitionKind.Definition, Ct),
            "definition of greet in util.ts", d => d.Any(x => x.TargetUri.AbsolutePath.EndsWith("/src/util.ts"))); // before the project loads, tsserver answers with the import alias
        def = def.Where(x => x.TargetUri.AbsolutePath.EndsWith("/src/util.ts")).ToArray();
        Assert.EndsWith("/src/util.ts", def[0].TargetUri.AbsolutePath);
        Assert.Equal(0, def[0].TargetSelectionRange.Start.Line);
        Assert.Equal("greet", p.Util.GetText(def[0].TargetSelectionRange));

        var declPos = Project.Find(p.Util, "greet", 1);
        var refs = await UntilAsync(async () => await p.Env.Provider<IReferenceProvider>(p.Util).ProvideReferencesAsync(p.Util, declPos, includeDeclaration: true, Ct),
            "references of greet", r => r.Select(x => Path.GetFileName(x.Uri.AbsolutePath)).Distinct().Count() >= 2);
        Assert.Contains(refs, r => r.Uri.AbsolutePath.EndsWith("/src/util.ts"));
        Assert.Contains(refs, r => r.Uri.AbsolutePath.EndsWith("/src/main.ts"));
    }

    [Fact]
    public async Task Rename_returns_a_workspace_edit_touching_both_files_and_prepareRename_validates()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var declPos = Project.Find(p.Util, "greet", 1);
        var rename = p.Env.Provider<IRenameProvider>(p.Util);
        var prepared = await UntilAsync(async () => await rename.PrepareRenameAsync(p.Util, declPos, Ct), "prepareRename on greet");
        Assert.Equal("greet", p.Util.GetText(prepared.Range));

        var edit = await UntilAsync(async () => await rename.ProvideRenameAsync(p.Util, declPos, "welcome", Ct), "rename greet -> welcome", e => e.Changes.Count >= 2);
        Assert.Contains(edit.Changes.Keys, u => u.AbsolutePath.EndsWith("/src/util.ts"));
        Assert.Contains(edit.Changes.Keys, u => u.AbsolutePath.EndsWith("/src/main.ts"));
        Assert.All(edit.Changes.Values.SelectMany(v => v), e => Assert.Equal("welcome", e.NewText));
        // applying it through the navigation double edits the open documents consistently
        Assert.True(await p.Env.Navigation.ApplyWorkspaceEditAsync(edit, "rename", Ct));
        Assert.DoesNotContain("greet", p.Main.GetText());
        Assert.Contains("welcome", p.Util.GetText());
    }

    [Fact]
    public async Task Formatting_produces_edits_that_format_the_document()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var messy = p.Env.Open("src/messy.ts", "const   a  =1 ;\nfunction f( ){return   2}\n", "typescript");
        var fmt = p.Env.Provider<IDocumentFormattingProvider>(messy);
        var edits = await UntilAsync(async () => await fmt.FormatDocumentAsync(messy, new FormattingOptions(2, true), Ct), "format document", e => e.Count > 0);
        Assert.True(messy.ApplyEdits(edits));
        Assert.Contains("const a = 1;", messy.GetText());
        Assert.Contains("function f() {", messy.GetText());
    }

    [Fact]
    public async Task Document_symbols_folding_and_semantic_tokens()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var symbols = await UntilAsync(async () => await p.Env.Provider<IDocumentSymbolProvider>(p.Util).ProvideDocumentSymbolsAsync(p.Util, Ct), "document symbols", s => s.Count >= 2);
        Assert.Contains(symbols, s => s.Name == "greet" && s.Kind == SymbolKind.Function);
        Assert.Contains(symbols, s => s.Name == "unused");

        var folds = await UntilAsync(async () => await p.Env.Provider<IFoldingRangeProvider>(p.Util).ProvideFoldingRangesAsync(p.Util, Ct), "folding ranges", f => f.Count > 0);
        Assert.Equal(0, folds[0].StartLine);

        var tokensProvider = p.Env.Provider<ISemanticTokensProvider>(p.Util);
        Assert.Contains("function", tokensProvider.Legend.TokenTypes);
        var tokens = await UntilAsync(async () => await tokensProvider.ProvideSemanticTokensAsync(p.Util, Ct), "semantic tokens", t => t.Data.Count > 0);
        Assert.Equal(0, tokens.Data.Count % 5);
    }

    [Fact]
    public async Task Code_actions_are_offered_for_diagnostics_and_workspace_symbols_work()
    {
        if (!RequireTools()) return;
        // noUnusedLocals turns the unused import into a diagnostic that has real quick fixes ("Remove import from './util'")
        await using var p = await OpenProjectAsync("import { greet } from \"./util\";\nexport const total = 1;\n", UtilTs, ",\"noUnusedLocals\":true");
        await TestHelpers.EventuallyAsync(() => p.Env.Diagnostics.Get(p.Main.Uri).Any(d => d.Range.Start.Line == 0), () => "unused-import diagnostic\n" + p.Env.Dump(), TimeSpan.FromSeconds(60));
        var diags = p.Env.Diagnostics.Get(p.Main.Uri).Where(d => d.Range.Start.Line == 0).ToArray();
        var actions = await UntilAsync(async () => await p.Env.Provider<ICodeActionProvider>(p.Main).ProvideCodeActionsAsync(p.Main, diags[0].Range,
            new CodeActionContext(diags, ["quickfix"]), Ct), "quick fixes for the unused import", a => a.Count > 0);
        Assert.All(actions, a => Assert.False(string.IsNullOrEmpty(a.Title)));
        var fix = actions.First(a => a.Kind?.StartsWith("quickfix") == true);
        fix = await p.Env.Provider<ICodeActionProvider>(p.Main).ResolveCodeActionAsync(fix, Ct).Within(TimeSpan.FromSeconds(30));
        Assert.True(fix.Edit is { IsEmpty: false } || fix.Command is not null, "the quick fix carries an edit or a command");

        var symbols = await UntilAsync(async () => await p.Env.Registry.GetAll<IWorkspaceSymbolProvider>()[0].ProvideWorkspaceSymbolsAsync("greet", Ct), "workspace symbols", s => s.Count > 0);
        Assert.Contains(symbols, s => s.Name == "greet");
    }

    [Fact]
    public async Task Server_runs_below_normal_priority_including_its_tsserver_child()
    {
        if (!RequireTools()) return;
        if (!OperatingSystem.IsLinux()) { Assert.Skip("nice values are read from /proc"); return; }
        await using var p = await OpenProjectAsync();
        var pid = p.Env.Manager.Servers.Single().ProcessId!.Value;
        Assert.True(NativePriority.GetProcessNice(pid) >= 10, $"language server nice = {NativePriority.GetProcessNice(pid)}");
        await TestHelpers.EventuallyAsync(() => ProcessReaper.Descendants(pid).Count > 0, "tsserver child process started");
        foreach (var child in ProcessReaper.Descendants(pid))
            Assert.True(NativePriority.GetProcessNice(child) >= 10, $"child {child} nice = {NativePriority.GetProcessNice(child)} (started before priority could be lowered?)");
        Assert.Equal(ProcessPriority.BelowNormal, p.Env.Manager.Servers.Count == 1 ? ProcessPriority.BelowNormal : ProcessPriority.Normal);
    }

    [Fact]
    public async Task Killing_the_server_with_SIGKILL_restarts_it_and_features_work_again()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var hover = p.Env.Provider<IHoverProvider>(p.Main);
        var greetUse = Project.Find(p.Main, "greet(\"world\")", 2);
        await UntilAsync(async () => await hover.ProvideHoverAsync(p.Main, greetUse, Ct), "hover before the crash", h => h.Contents.Any(c => c.Value.Contains("greet")));

        var info = p.Env.Manager.Servers.Single();
        var oldPid = info.ProcessId!.Value;
        var orphanCandidates = ProcessReaper.Descendants(oldPid);
        using (var proc = Process.GetProcessById(oldPid)) proc.Kill(entireProcessTree: false); // kill -9 of exactly the server process we started

        var restarted = await p.Env.WaitForAsync(() => p.Env.Manager.Servers.FirstOrDefault(s => s.State == LanguageServerState.Running && s.ProcessId is { } np && np != oldPid),
            () => "server restarted with a new pid\n" + p.Env.Dump(), TimeSpan.FromSeconds(90));
        Assert.NotEqual(oldPid, restarted.ProcessId);

        // the documents were re-opened on the new session and the providers are registered again
        var greetUse2 = Project.Find(p.Main, "greet(\"world\")", 2);
        await UntilAsync(async () => await p.Env.Provider<IHoverProvider>(p.Main).ProvideHoverAsync(p.Main, greetUse2, Ct), "hover after the restart", h => h.Contents.Any(c => c.Value.Contains("greet")));
        await TestHelpers.EventuallyAsync(() => p.Env.Diagnostics.Get(p.Main.Uri).Any(d => d.Code == "2322"), () => "diagnostics again after the restart\n" + p.Env.Dump(), TimeSpan.FromSeconds(60));

        // the orphaned tsserver of the killed server was reaped too
        await TestHelpers.EventuallyAsync(() => orphanCandidates.All(c => !Directory.Exists("/proc/" + c)), () => "orphaned children of the killed server are gone: " + string.Join(",", orphanCandidates.Where(c => Directory.Exists("/proc/" + c))), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task StopAll_leaves_no_child_process_behind()
    {
        if (!RequireTools()) return;
        await using var p = await OpenProjectAsync();
        var pid = p.Env.Manager.Servers.Single().ProcessId!.Value;
        await TestHelpers.EventuallyAsync(() => ProcessReaper.Descendants(pid).Count > 0, "tsserver started");
        var everyone = ProcessReaper.Descendants(pid).Append(pid).ToArray();
        Assert.True(everyone.All(x => Directory.Exists("/proc/" + x)));

        await p.Env.Manager.StopAllAsync(Ct);

        await TestHelpers.EventuallyAsync(() => everyone.All(x => !Directory.Exists("/proc/" + x)), () => "no leftover processes: " + string.Join(",", everyone.Where(x => Directory.Exists("/proc/" + x))), TimeSpan.FromSeconds(15));
        Assert.Equal(LanguageServerState.Stopped, p.Env.Manager.Servers.Single().State);
    }
}
