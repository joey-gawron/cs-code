using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Python;
using CsCode.Core.Text;
using CsCode.Lsp.Runtime;
using CsCode.Lsp.Tests.Harness;
using Xunit;

namespace CsCode.Lsp.Tests;

/// <summary>
/// Language stage 1 against the REAL Python servers: Pyright (<c>pyright-langserver</c>, npm) and Ruff (<c>ruff server</c>) on one document, the
/// selected interpreter reaching Pyright as <c>python.pythonPath</c>, and Ruff's formatting, fix-all and organize-imports actions. Install with
/// <c>tools/fetch-lsp-tools.sh --with-python</c> and <c>tools/fetch-debugpy.sh</c> (the tool venv with pytest); without them every test SKIPS.
/// Ruff is found the way the app finds a project's own copy: in the selected environment's <c>bin</c> (here the tool venv).
/// </summary>
[Trait("Category", "RealTools")]
public class RealPythonServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Venv => Path.Combine(LspTestEnv.ToolsDir, "python", "venv");
    private static string VenvPython => Path.Combine(Venv, "bin", "python");
    private static string Pyright => Path.Combine(LspTestEnv.ToolsDir, "lsp", "node_modules", ".bin", "pyright-langserver");

    private static bool RequireTools()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { Assert.Skip("integration tests are written for unix"); return false; }
        if (!File.Exists(Pyright)) { Assert.Skip("pyright is not installed in .scratch/tools/lsp; run tools/fetch-lsp-tools.sh --with-python"); return false; }
        if (!File.Exists(Path.Combine(Venv, "bin", "ruff"))) { Assert.Skip("ruff is not installed in .scratch/tools/python/venv; run tools/fetch-lsp-tools.sh --with-python"); return false; }
        if (!Directory.EnumerateDirectories(Path.Combine(Venv, "lib")).Any(d => Directory.Exists(Path.Combine(d, "site-packages", "pytest"))))
        { Assert.Skip("pytest is not installed in the tool venv; run tools/fetch-debugpy.sh"); return false; }
        if (ToolResolver.FindOnPath("node", null) is null) { Assert.Skip("node is not on PATH"); return false; }
        return true;
    }

    /// <summary>The interpreter service as the servers see it: one interpreter for every folder, switchable.</summary>
    private sealed class FixedEnvironments : IPythonEnvironmentService
    {
        private readonly Emitter<Uri?> _changed = new();
        public PythonEnvironment? Active { get; private set; }
        public void Select(PythonEnvironment? env) { Active = env; _changed.Fire(null); }
        public PythonEnvironment? GetActive(Uri? resource) => Active;
        public ValueTask<PythonEnvironment?> GetActiveAsync(Uri? resource, CancellationToken ct = default) => ValueTask.FromResult(Active);
        public ValueTask<IReadOnlyList<PythonEnvironment>> DiscoverAsync(Uri? resource, bool refresh = false, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<PythonEnvironment>>(Active is { } a ? [a] : []);
        public ValueTask SetActiveAsync(Uri? resource, string? executable, CancellationToken ct = default) => default;
        public ValueTask<PythonEnvironment?> ProbeAsync(string executable, CancellationToken ct = default) => ValueTask.FromResult<PythonEnvironment?>(null);
        public ValueTask<bool> HasModuleAsync(string executable, string module, CancellationToken ct = default) => ValueTask.FromResult(false);
        public IEventSource<Uri?> ActiveChanged => _changed;
    }

    private static PythonEnvironment ToolVenv => new(VenvPython, PythonEnvironmentKind.Venv, "3", Venv, "venv");

    private static LspTestEnv Create(FixedEnvironments python) => new(c =>
    {
        c.Set(LspSettingKeys.InteractiveTimeoutMs, 30_000);
        c.Set(LspSettingKeys.BackgroundTimeoutMs, 60_000);
        c.Set(LspSettingKeys.InitializeTimeoutMs, 120_000);
    }, realServers: true, python: python);

    private static async Task<T> UntilAsync<T>(Func<Task<T?>> query, string what, Func<T, bool>? accept = null, int seconds = 60) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = await query().Within(TimeSpan.FromSeconds(40));
            if (r is not null && (accept?.Invoke(r) ?? true)) return r;
            await Task.Delay(300, Ct);
        }
        Assert.Fail("no usable answer for: " + what);
        return null!;
    }

    /// <summary>A path-built and a string-built file Uri are not always equal Uri objects: match by path, as the editor does.</summary>
    private static IReadOnlyList<TextEdit> EditsFor(ITextDocument doc, WorkspaceEdit edit) =>
        Assert.Single(edit.Changes, kv => kv.Key.LocalPath == doc.Uri.LocalPath).Value;

    private static string Apply(ITextDocument doc, IReadOnlyList<TextEdit> edits)
    {
        Assert.True(doc.ApplyEdits(edits));
        return doc.GetText();
    }

    [Fact]
    public async Task Pyright_and_Ruff_both_report_on_one_file_and_Pyright_follows_the_selected_interpreter()
    {
        if (!RequireTools()) return;
        var python = new FixedEnvironments();
        python.Select(ToolVenv); // has pytest, and Ruff in its bin
        await using var env = Create(python);
        env.Dir.WriteFile("pyproject.toml", "[project]\nname = \"demo\"\nversion = \"0\"\n");
        var doc = env.Open("main.py", "import os\nimport pytest\n\ncount: int = \"oops\"\nprint(pytest.__name__)\n", "python");
        await env.WaitForStateAsync("ruff", LanguageServerState.Running, TimeSpan.FromSeconds(60));
        await env.WaitForStateAsync("python", LanguageServerState.Running, TimeSpan.FromSeconds(90));

        // Ruff: F401 for the unused os; Pyright: the str assigned to an int
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Any(d => d.Code == "F401"), () => "Ruff's F401\n" + env.Dump(), TimeSpan.FromSeconds(60));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("\"Literal['oops']\"", StringComparison.Ordinal) || d.Message.Contains("is not assignable to declared type \"int\"", StringComparison.Ordinal)),
            () => "Pyright's type error\n" + env.Dump(), TimeSpan.FromSeconds(90));
        Assert.DoesNotContain(env.Diagnostics.Get(doc.Uri), d => d.Message.Contains("\"pytest\" could not be resolved", StringComparison.Ordinal)); // the venv has pytest

        // switch to an interpreter without pytest: Pyright is told (didChangeConfiguration), asks again, and now cannot resolve the import
        python.Select(new PythonEnvironment("/usr/bin/false", PythonEnvironmentKind.Global, "3", "/usr"));
        await TestHelpers.EventuallyAsync(() => env.Diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("\"pytest\" could not be resolved", StringComparison.Ordinal)),
            () => "Pyright re-resolved imports for the newly selected interpreter\n" + env.Dump(), TimeSpan.FromSeconds(90));
        python.Select(ToolVenv);
        await TestHelpers.EventuallyAsync(() => !env.Diagnostics.Get(doc.Uri).Any(d => d.Message.Contains("\"pytest\" could not be resolved", StringComparison.Ordinal)),
            () => "back on the venv with pytest\n" + env.Dump(), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Ruff_formats_and_its_fix_all_and_organize_imports_actions_resolve_to_edits()
    {
        if (!RequireTools()) return;
        var python = new FixedEnvironments();
        python.Select(ToolVenv);
        await using var env = Create(python);
        env.Config.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { ["python"] = new { enabled = false } }); // Ruff alone
        var doc = env.Open("main.py", "import sys\nimport os\nx=f\"hi\"\nprint(x,sys.argv)\n", "python");
        await env.WaitForStateAsync("ruff", LanguageServerState.Running, TimeSpan.FromSeconds(60));
        Assert.Contains(env.Output.ChannelNames, n => n.Contains("Ruff", StringComparison.Ordinal));

        // Ruff registers its features dynamically after initialize; the formatter says it is Ruff's: editor.defaultFormatter = "ruff" picks it
        await TestHelpers.EventuallyAsync(() => env.Registry.GetProviders<IDocumentFormattingProvider>(doc).Count > 0 && env.Registry.GetProviders<ICodeActionProvider>(doc).Count > 0,
            () => "Ruff's formatting and code actions registered\n" + env.Dump(), TimeSpan.FromSeconds(30));
        var formatter = env.Provider<IDocumentFormattingProvider>(doc);
        Assert.Equal("ruff", LanguageProviderSource.IdOf(formatter));

        var actions = env.Provider<ICodeActionProvider>(doc);
        var whole = new TextRange(TextPosition.Zero, doc.PositionAt(doc.Length));
        var fixAll = (await UntilAsync(
            async () => await actions.ProvideCodeActionsAsync(doc, whole, new CodeActionContext([], ["source.fixAll"], CodeActionTriggerKind.Automatic), Ct),
            "source.fixAll", l => l.Any(a => a.Kind == "source.fixAll.ruff"))).First(a => a.Kind == "source.fixAll.ruff");
        var resolved = fixAll.Edit is null ? await actions.ResolveCodeActionAsync(fixAll, Ct) : fixAll;
        Assert.NotNull(resolved.Edit);
        Assert.Equal("import sys\n\nx=\"hi\"\nprint(x,sys.argv)\n", Apply(doc, EditsFor(doc, resolved.Edit)));

        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 0), "import abc\n"), TextEdit.Insert(new TextPosition(3, 0), "print(abc.ABC)\n")]); // unsorted now
        var organize = (await UntilAsync(
            async () => await actions.ProvideCodeActionsAsync(doc, new TextRange(TextPosition.Zero, doc.PositionAt(doc.Length)), new CodeActionContext([], ["source.organizeImports"], CodeActionTriggerKind.Automatic), Ct),
            "source.organizeImports", l => l.Any(a => a.Kind == "source.organizeImports.ruff"))).First(a => a.Kind == "source.organizeImports.ruff");
        var organized = organize.Edit is null ? await actions.ResolveCodeActionAsync(organize, Ct) : organize;
        Assert.StartsWith("import abc\nimport sys\n", Apply(doc, EditsFor(doc, organized.Edit!)));

        var formatted = await UntilAsync(async () => await formatter.FormatDocumentAsync(doc, new FormattingOptions(4, true), Ct), "formatting");
        Assert.Contains("x = \"hi\"\n", Apply(doc, formatted));
        Assert.Contains("print(x, sys.argv)\n", doc.GetText());
    }

    /// <summary>
    /// The comparison docs/PYTHON.md records for choosing the default type checker: what each server offers on the same file. Checked 2026-11-01:
    /// Pyright 1.1.414's own server now has semantic tokens and inlay hints too (they used to be Pylance-only), as basedpyright 1.40.2 does.
    /// </summary>
    [Theory]
    [InlineData("python", "pyright-langserver")]
    [InlineData("basedpyright", "basedpyright-langserver")]
    public async Task What_Pyright_and_basedpyright_offer(string id, string command)
    {
        if (!RequireTools()) return;
        if (!File.Exists(Path.Combine(LspTestEnv.ToolsDir, "lsp", "node_modules", ".bin", command))) { Assert.Skip(command + " is not installed: run tools/fetch-lsp-tools.sh --with-python"); return; }
        var python = new FixedEnvironments();
        python.Select(ToolVenv);
        await using var env = Create(python);
        var other = id == "python" ? "basedpyright" : "python";
        env.Config.Set(LspSettingKeys.LanguageServers, new Dictionary<string, object> { [id] = new { enabled = true }, [other] = new { enabled = false }, ["ruff"] = new { enabled = false } });
        var doc = env.Open("main.py", "def add(a: int, b: int) -> int:\n    return a + b\n\ntotal = add(1, 2)\n", "python");
        await env.WaitForStateAsync(id, LanguageServerState.Running, TimeSpan.FromSeconds(90));
        await Task.Delay(2000, Ct); // dynamic registrations
        var semantic = env.Registry.GetProviders<ISemanticTokensProvider>(doc).Count > 0;
        var inlay = env.Registry.GetProviders<IInlayHintsProvider>(doc).Count > 0;
        IReadOnlyList<InlayHint>? hints = null;
        if (inlay)
            hints = await UntilAsync(async () => await env.Provider<IInlayHintsProvider>(doc).ProvideInlayHintsAsync(doc, new TextRange(TextPosition.Zero, doc.PositionAt(doc.Length)), Ct),
                "inlay hints", h => h.Count > 0, seconds: 30);
        CsCodeTestNote.Report($"[{id}] semantic tokens: {semantic}; inlay hints: {inlay} ({string.Join(", ", hints?.Select(h => h.Label) ?? [])}); formatting: {env.Registry.GetProviders<IDocumentFormattingProvider>(doc).Count > 0}");
        Assert.True(semantic);
        Assert.True(inlay);
        Assert.Contains(hints!, h => h.Label.Contains("int", StringComparison.Ordinal)); // the inferred type of `total`
    }
}

internal static class CsCodeTestNote
{
    public static void Report(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        if (System.Environment.GetEnvironmentVariable("CSCODE_LSP_TEST_NOTES") is { Length: > 0 } path) File.AppendAllText(path, text + "\n");
    }
}
